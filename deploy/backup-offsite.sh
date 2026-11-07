#!/usr/bin/env bash
# Encrypted off-site backup (plan steps 5.26 and 5.27): a fresh dump of the database AND the server settings (deploy/.env and the Firebase key), both encrypted with the
# owner's PUBLIC keys (age), copied to Google Drive with rclone and checked there. The server can only encrypt; the private keys stay with the owner, so a stolen server,
# Drive or token gives nobody the data. With two keys (AGE_RECIPIENT="age1... age1...") either private key opens the files, so one lost key does not lose the backups.
# Steps: docs/BACKUP-OFFSITE.md. Cron (every 3 days at 04:15):  15 4 */3 * * /opt/lifedrop/deploy/backup-offsite.sh >> /var/log/lifedrop-offsite.log 2>&1
. "$(dirname "$0")/lib.sh"
DB="${LIFEDROP_DB_NAME:-lifedrop}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/lifedrop}"
KEEP_DAYS="${OFFSITE_KEEP_DAYS:-45}"
fail() { echo "$(date -u +%FT%TZ) offsite backup FAILED: $*" >&2; exit 1; }
[ -n "${AGE_RECIPIENT:-}" ] || fail "AGE_RECIPIENT (the public key, age1...; two keys separated by a space are better) is not set in deploy/.env"
[ -n "${OFFSITE_RCLONE_REMOTE:-}" ] || fail "OFFSITE_RCLONE_REMOTE (for example gdrive:lifedrop-backups) is not set in deploy/.env"
recipients=()
for key in ${AGE_RECIPIENT//,/ }; do
  case "$key" in age1*) recipients+=(-r "$key") ;; *) fail "every key in AGE_RECIPIENT must be a public key that starts with age1 (never the private key AGE-SECRET-KEY-...)" ;; esac
done
[ "${#recipients[@]}" -ge 2 ] || fail "AGE_RECIPIENT has no key"
[ "${#recipients[@]}" -ge 4 ] || echo "WARNING: only one key in AGE_RECIPIENT. If its private key is lost, nobody can open the backups; add a second key (docs/BACKUP-OFFSITE.md)." >&2
command -v age >/dev/null 2>&1 || fail "age is not installed (apt install -y age)"
command -v rclone >/dev/null 2>&1 || fail "rclone is not installed (apt install -y rclone)"
# backup.sh copies a plain dump to BACKUP_REMOTE when it is set; that would put the unencrypted data off the server.
[ -z "${BACKUP_REMOTE:-}" ] || echo "WARNING: BACKUP_REMOTE is set, so backup.sh also copies an UNENCRYPTED dump there. Clear it when this script is used." >&2

# Encrypts stdin or a file to a file in BACKUP_DIR, uploads it, checks it there and removes the local encrypted file. $1 = output name, $2 = input file ("-" = stdin)
send() {
  local rc=0
  send_one "$@" || rc=$?
  rm -f "$BACKUP_DIR/$1" "$BACKUP_DIR/$1.part"
  return $rc
}
send_one() {
  local name="$1" input="$2" enc="$BACKUP_DIR/$1"
  if [ "$input" = "-" ]; then age "${recipients[@]}" -o "$enc.part" || return 1; else age "${recipients[@]}" -o "$enc.part" "$input" || return 1; fi
  [ -s "$enc.part" ] || { echo "the encrypted $name is empty" >&2; return 1; }
  mv "$enc.part" "$enc"
  rclone copyto "$enc" "$OFFSITE_RCLONE_REMOTE/$name" || { echo "the upload of $name to $OFFSITE_RCLONE_REMOTE failed" >&2; return 1; }
  # A copy that is not there with the same size is no backup.
  rclone check "$BACKUP_DIR" "$OFFSITE_RCLONE_REMOTE" --include "$name" --size-only --one-way >/dev/null 2>&1 || { echo "$name on $OFFSITE_RCLONE_REMOTE does not match the local one" >&2; return 1; }
  echo "$(date -u +%FT%TZ) offsite backup written: $OFFSITE_RCLONE_REMOTE/$name ($(du -h "$enc" | cut -f1), encrypted)"
}

"$DEPLOY_DIR/backup.sh" || fail "the local backup failed"
dump="$(ls -1t "$BACKUP_DIR/$DB"-*.dump 2>/dev/null | head -1 || true)"
[ -n "$dump" ] && [ -s "$dump" ] || fail "no dump found in $BACKUP_DIR"
send "$(basename "$dump").age" "$dump" || fail "the database copy failed"

# The settings the server needs to start again (passwords, domains, tokens, the Firebase key): without them a dump alone does not bring the server back. Packed and encrypted in
# one go, so they never exist unencrypted outside the server.
files=()
for f in .env secrets/service-account.json; do [ -f "$DEPLOY_DIR/$f" ] && files+=("$f"); done
if [ "${#files[@]}" -gt 0 ]; then
  tar -C "$DEPLOY_DIR" -cf - "${files[@]}" | send "$DB-config-$(date -u +%Y%m%d-%H%M%S).tar.age" - || fail "the settings copy failed (the database copy above is done)"
else
  echo "WARNING: no deploy/.env and no Firebase key found; nothing to save of the settings." >&2
fi

# Old encrypted copies are removed from the remote after KEEP_DAYS days.
rclone delete "$OFFSITE_RCLONE_REMOTE" --min-age "${KEEP_DAYS}d" --include "$DB-*.dump.age" --include "$DB-config-*.tar.age" || echo "(removing old copies from the remote failed; the backup itself is done)"
