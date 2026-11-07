#!/usr/bin/env bash
# Encrypted off-site backup (plan step 5.26): a fresh dump, encrypted with the owner's PUBLIC key (age), copied to Google Drive with rclone and checked there.
# The server can only encrypt; the private key stays with the owner, so a stolen server, Drive or token gives nobody the data. Steps: docs/BACKUP-OFFSITE.md.
# Cron (every 3 days at 04:15):  15 4 */3 * * /opt/lifedrop/deploy/backup-offsite.sh >> /var/log/lifedrop-offsite.log 2>&1
. "$(dirname "$0")/lib.sh"
DB="${LIFEDROP_DB_NAME:-lifedrop}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/lifedrop}"
KEEP_DAYS="${OFFSITE_KEEP_DAYS:-45}"
fail() { echo "$(date -u +%FT%TZ) offsite backup FAILED: $*" >&2; exit 1; }
[ -n "${AGE_RECIPIENT:-}" ] || fail "AGE_RECIPIENT (the public key, age1...) is not set in deploy/.env"
[ -n "${OFFSITE_RCLONE_REMOTE:-}" ] || fail "OFFSITE_RCLONE_REMOTE (for example gdrive:lifedrop-backups) is not set in deploy/.env"
case "$AGE_RECIPIENT" in age1*) ;; *) fail "AGE_RECIPIENT must be a public key that starts with age1 (never the private key AGE-SECRET-KEY-...)" ;; esac
command -v age >/dev/null 2>&1 || fail "age is not installed (apt install -y age)"
command -v rclone >/dev/null 2>&1 || fail "rclone is not installed (apt install -y rclone)"
# backup.sh copies a plain dump to BACKUP_REMOTE when it is set; that would put the unencrypted data off the server.
[ -z "${BACKUP_REMOTE:-}" ] || echo "WARNING: BACKUP_REMOTE is set, so backup.sh also copies an UNENCRYPTED dump there. Clear it when this script is used." >&2

"$DEPLOY_DIR/backup.sh" || fail "the local backup failed"
dump="$(ls -1t "$BACKUP_DIR/$DB"-*.dump 2>/dev/null | head -1 || true)"
[ -n "$dump" ] && [ -s "$dump" ] || fail "no dump found in $BACKUP_DIR"
enc="$dump.age"
trap 'rm -f "$enc" "$enc.part"' EXIT
age -r "$AGE_RECIPIENT" -o "$enc.part" "$dump" || fail "encryption failed"
[ -s "$enc.part" ] || fail "the encrypted file is empty"
mv "$enc.part" "$enc"
name="$(basename "$enc")"
rclone copyto "$enc" "$OFFSITE_RCLONE_REMOTE/$name" || fail "the upload to $OFFSITE_RCLONE_REMOTE failed"
# A copy that is not there with the same size is no backup.
rclone check "$BACKUP_DIR" "$OFFSITE_RCLONE_REMOTE" --include "$name" --size-only --one-way >/dev/null 2>&1 || fail "the file on $OFFSITE_RCLONE_REMOTE does not match the local one"
echo "$(date -u +%FT%TZ) offsite backup written: $OFFSITE_RCLONE_REMOTE/$name ($(du -h "$enc" | cut -f1), encrypted)"
# Old encrypted copies are removed from the remote after KEEP_DAYS days; the newest one is never touched by age.
rclone delete "$OFFSITE_RCLONE_REMOTE" --min-age "${KEEP_DAYS}d" --include "$DB-*.dump.age" || echo "(removing old copies from the remote failed; the backup itself is done)"
