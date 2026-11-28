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

# Plan step 5.68: the main tables must have their rows back. Rows came and went since the dump was made, so the numbers are not compared for equality; but a table that has rows in the
# live database and none in the restored one means the dump lost it. Shown side by side so a person can see the size is right.
failed=0
echo "Table                 live  restored"
for table in tenants users customers visits orders order_items payments products warehouses places audit_logs; do
  [ "$(psql_db "$DB" -c "select to_regclass('public.$table') is not null")" = t ] || continue
  live="$(psql_db "$DB" -c "select count(*) from $table")"
  restored="$(psql_db "$SCRATCH" -c "select count(*) from $table" 2>/dev/null || echo missing)"
  printf '%-18s %7s  %8s\n' "$table" "$live" "$restored"
  if [ "$restored" = missing ] || { [ "$live" -gt 0 ] && [ "$restored" -eq 0 ]; }; then echo "RESTORE CHECK FAILED: $table has $live rows in the live database and $restored in the restored one." >&2; failed=1; fi
done
# The map data (PostGIS) and the company separation (row-level security) must have come back too.
psql_db "$SCRATCH" -c 'select postgis_version()' >/dev/null 2>&1 || { echo "RESTORE CHECK FAILED: PostGIS does not work in the restored database." >&2; failed=1; }
live_policies="$(psql_db "$DB" -c "select count(*) from pg_policies where schemaname = 'public'")"
restored_policies="$(psql_db "$SCRATCH" -c "select count(*) from pg_policies where schemaname = 'public'")"
echo "Row-level security policies: live $live_policies, restored $restored_policies"
if [ "$live_policies" -gt 0 ] && [ "$restored_policies" -eq 0 ]; then echo "RESTORE CHECK FAILED: the restored database has no row-level security policies (companies would see each other's data)." >&2; failed=1; fi
[ "$failed" = 0 ] || exit 1
echo "Restore test passed."
