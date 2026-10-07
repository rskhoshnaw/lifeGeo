#!/usr/bin/env bash
# Creates the database, the API's restricted login and applies every migration that has not been applied yet, then the grants (docs/DEPLOY.md).
# Safe to run again after every update: finished migrations are skipped (table schema_migrations). Needs: docker compose up -d db
. "$(dirname "$0")/lib.sh"
: "${APP_DB_PASSWORD:?set APP_DB_PASSWORD in deploy/.env}"
MIGRATIONS_DIR="${MIGRATIONS_DIR:-$ROOT_DIR/db/migrations}"
GRANTS_FILE="${GRANTS_FILE:-$ROOT_DIR/db/grants/lifedrop_app_grants.sql}"
DB="${LIFEDROP_DB_NAME:-lifedrop}"
case "$APP_DB_PASSWORD" in *"'"*) echo "APP_DB_PASSWORD must not contain a quote character." >&2; exit 1;; esac

echo "Waiting for the database..."
for _ in $(seq 1 30); do psql_db postgres -c 'select 1' >/dev/null 2>&1 && break; sleep 2; done
psql_db postgres -c 'select 1' >/dev/null

# The API's login: not a superuser and without BYPASSRLS, so the company row-level security always applies.
psql_db postgres -c "DO \$\$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'lifedrop_app') THEN
    CREATE ROLE lifedrop_app WITH LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
  END IF;
END \$\$;"
psql_db postgres -c "ALTER ROLE lifedrop_app WITH PASSWORD '$APP_DB_PASSWORD';"
if [ "$(psql_db postgres -c "select 1 from pg_database where datname = '$DB'")" != "1" ]; then
  psql_db postgres -c "CREATE DATABASE $DB;"
fi
[ "${SKIP_POSTGIS:-0}" = 1 ] || psql_db "$DB" -c "CREATE EXTENSION IF NOT EXISTS postgis;"   # SKIP_POSTGIS only for checks on a machine without PostGIS
psql_db "$DB" -c "GRANT USAGE ON SCHEMA public TO lifedrop_app;"
psql_db "$DB" -c "CREATE TABLE IF NOT EXISTS schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());"

applied=0
for file in $(ls "$MIGRATIONS_DIR"/*.sql | sort); do
  name="$(basename "$file")"
  if [ "$(psql_db "$DB" -c "select 1 from schema_migrations where name = '$name'")" = "1" ]; then continue; fi
  echo "  applying $name"
  # One transaction per migration with its record: a failed migration leaves nothing half done.
  { echo "BEGIN;"; cat "$file"; echo; echo "INSERT INTO schema_migrations (name) VALUES ('$name');"; echo "COMMIT;"; } | $PSQL_CMD -d "$DB"
  applied=$((applied + 1))
done
echo "  grants"
$PSQL_CMD -d "$DB" < "$GRANTS_FILE"
echo "Done. $applied migration(s) applied."
