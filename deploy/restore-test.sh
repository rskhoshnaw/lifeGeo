#!/usr/bin/env bash
# Proves a backup works (docs/DEPLOY.md): restores the newest dump (or the file given) into a scratch database, checks that the data is there, and drops the scratch database.
# Run it once after the first backup and then every month. It never touches the live database.
. "$(dirname "$0")/lib.sh"
DB="${LIFEDROP_DB_NAME:-lifedrop}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/lifedrop}"
file="${1:-$(ls -1t "$BACKUP_DIR"/$DB-*.dump 2>/dev/null | head -1)}"
[ -n "$file" ] && [ -f "$file" ] || { echo "No backup file found in $BACKUP_DIR." >&2; exit 1; }
SCRATCH="${DB}_restore_test"
RESTORE_CMD="${RESTORE_CMD:-docker compose -f $DEPLOY_DIR/docker-compose.yml exec -T db pg_restore -U postgres -d $SCRATCH --no-owner}"
psql_db postgres -c "DROP DATABASE IF EXISTS $SCRATCH WITH (FORCE);"
psql_db postgres -c "CREATE DATABASE $SCRATCH;"
trap 'psql_db postgres -c "DROP DATABASE IF EXISTS '"$SCRATCH"' WITH (FORCE);" >/dev/null 2>&1 || true' EXIT
[ "${SKIP_POSTGIS:-0}" = 1 ] || psql_db "$SCRATCH" -c "CREATE EXTENSION IF NOT EXISTS postgis;"
$RESTORE_CMD < "$file"
tenants="$(psql_db "$SCRATCH" -c 'select count(*) from tenants')"
migrations="$(psql_db "$SCRATCH" -c 'select count(*) from schema_migrations')"
users="$(psql_db "$SCRATCH" -c 'select count(*) from users')"
echo "Restored $file: $tenants companies, $users users, $migrations migrations."
[ "$tenants" -ge 1 ] && [ "$migrations" -ge 1 ] || { echo "RESTORE CHECK FAILED: the restored database looks empty." >&2; exit 1; }
echo "Restore test passed."
