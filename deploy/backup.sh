#!/usr/bin/env bash
# Daily backup of the database (docs/DEPLOY.md): a compressed dump in BACKUP_DIR, old ones removed, and an optional copy to a second place.
# Cron example (every night at 03:10):  10 3 * * * /opt/lifedrop/deploy/backup.sh >> /var/log/lifedrop-backup.log 2>&1
. "$(dirname "$0")/lib.sh"
DB="${LIFEDROP_DB_NAME:-lifedrop}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/lifedrop}"
KEEP_DAYS="${BACKUP_KEEP_DAYS:-14}"
mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"
file="$BACKUP_DIR/$DB-$(date -u +%Y%m%d-%H%M%S).dump"
DUMP_CMD="${DUMP_CMD:-docker compose -f $DEPLOY_DIR/docker-compose.yml exec -T db pg_dump -U postgres -Fc $DB}"
if ! $DUMP_CMD > "$file.part"; then rm -f "$file.part"; echo "$(date -u +%FT%TZ) backup FAILED" >&2; exit 1; fi
# A dump that cannot be listed is not a backup.
LIST_CMD="${LIST_CMD:-docker compose -f $DEPLOY_DIR/docker-compose.yml exec -T db pg_restore --list}"
if [ ! -s "$file.part" ] || ! $LIST_CMD < "$file.part" >/dev/null 2>&1; then rm -f "$file.part"; echo "$(date -u +%FT%TZ) backup is empty or damaged" >&2; exit 1; fi
mv "$file.part" "$file"
echo "$(date -u +%FT%TZ) backup written: $file ($(du -h "$file" | cut -f1))"
find "$BACKUP_DIR" -name "$DB-*.dump" -type f -mtime +"$KEEP_DAYS" -delete
if [ -n "${BACKUP_REMOTE:-}" ]; then
  case "$BACKUP_REMOTE" in
    *:*) if command -v rclone >/dev/null 2>&1 && rclone listremotes | grep -q "^${BACKUP_REMOTE%%:*}:"; then rclone copy "$file" "$BACKUP_REMOTE"; else scp -q "$file" "$BACKUP_REMOTE"; fi ;;
  esac
  echo "$(date -u +%FT%TZ) copied to $BACKUP_REMOTE"
fi
