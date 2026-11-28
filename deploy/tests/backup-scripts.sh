#!/usr/bin/env bash
# Tests of the backup scripts against a REAL PostgreSQL + PostGIS (plan step 5.68), no docker: builds the real schema with setup-db.sh, then runs backup.sh, restore-test.sh,
# backup-status.sh and backup-offsite.sh (with stand-ins for age and rclone) and checks what they say and do, including that damage is noticed.
# Needs: a server on PGHOST (default localhost) where PGUSER (default postgres, password PGPASSWORD) may create databases; psql, pg_dump, pg_restore.
#   PGPASSWORD=temp_root deploy/tests/backup-scripts.sh
# With PG_DOCKER_CONTAINER=<container of the database> psql, pg_dump and pg_restore run inside it with docker exec, as on the real server (CI does this: the runner's own client tools
# are newer than the database, and a newer pg_dump writes settings an older server does not know).
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY="$(dirname "$HERE")"
export PGHOST="${PGHOST:-localhost}" PGUSER="${PGUSER:-postgres}"
: "${PGPASSWORD:?set PGPASSWORD}"
export LIFEDROP_DB_NAME="lifedrop_bktest" APP_DB_PASSWORD="bk-test-pass"
WORK="$(mktemp -d)"
export BACKUP_DIR="$WORK/backups" BACKUP_KEEP_DAYS=14
if [ -n "${PG_DOCKER_CONTAINER:-}" ]; then
  IN="docker exec -i $PG_DOCKER_CONTAINER"
  export PSQL_CMD="$IN psql -U postgres -v ON_ERROR_STOP=1 -q -t -A"
  export DUMP_CMD="$IN pg_dump -U postgres -Fc $LIFEDROP_DB_NAME"
  export LIST_CMD="$IN pg_restore --list"
  export RESTORE_CMD="$IN pg_restore -U postgres -d ${LIFEDROP_DB_NAME}_restore_test --no-owner"
  pgdump() { $IN pg_dump -U postgres "$@"; }
  pgrestore_file() { $IN pg_restore -U postgres -d "$1" --no-owner < "$2"; }   # database, file
else
  export PSQL_CMD="psql -v ON_ERROR_STOP=1 -q -t -A"
  export DUMP_CMD="pg_dump -Fc $LIFEDROP_DB_NAME"
  export LIST_CMD="pg_restore --list"
  export RESTORE_CMD="pg_restore -d ${LIFEDROP_DB_NAME}_restore_test --no-owner"
  pgdump() { pg_dump "$@"; }
  pgrestore_file() { pg_restore -d "$1" --no-owner "$2"; }
fi
# deploy/.env is not read here: lib.sh only sources it when it exists, and a developer machine has none.
[ ! -f "$DEPLOY/.env" ] || { echo "deploy/.env exists: this test must not run on a real server" >&2; exit 2; }

# Host tools must be of the server's own major version (see the top); inside the container they are by definition.
if [ -z "${PG_DOCKER_CONTAINER:-}" ]; then
  server_major="$(psql -d postgres -Atc 'show server_version_num' | cut -c1-2)"
  [ ! -x "/usr/lib/postgresql/$server_major/bin/pg_dump" ] || export PATH="/usr/lib/postgresql/$server_major/bin:$PATH"
  client_major="$(pg_dump --version | grep -o '[0-9][0-9]*' | head -1)"
  [ "$client_major" = "$server_major" ] || { echo "pg_dump is version $client_major but the server is version $server_major: install postgresql-client-$server_major or set PG_DOCKER_CONTAINER" >&2; exit 2; }
fi

pass=0
ok() { pass=$((pass + 1)); echo "ok   $*"; }
die() { echo "FAIL $*" >&2; exit 1; }
expect_fail() { local what="$1"; shift; if "$@" >"$WORK/out" 2>&1; then cat "$WORK/out" >&2; die "$what: expected a failure but it passed"; fi; }
has() { grep -q -- "$1" "$WORK/out" || { cat "$WORK/out" >&2; die "output does not say: $1"; }; }
sql() { psql -v ON_ERROR_STOP=1 -q -t -A -d "$1" -c "$2"; }
cleanup() { sql postgres "DROP DATABASE IF EXISTS ${LIFEDROP_DB_NAME} WITH (FORCE)" >/dev/null 2>&1 || true; sql postgres "DROP DATABASE IF EXISTS ${LIFEDROP_DB_NAME}_restore_test WITH (FORCE)" >/dev/null 2>&1 || true; rm -rf "$WORK"; }
trap cleanup EXIT
sql postgres "DROP DATABASE IF EXISTS ${LIFEDROP_DB_NAME} WITH (FORCE)" >/dev/null

# The real schema, then a little data in the main tables.
"$DEPLOY/setup-db.sh" >"$WORK/setup.log" 2>&1 || { cat "$WORK/setup.log" >&2; die "setup-db.sh"; }
sql "$LIFEDROP_DB_NAME" "
  INSERT INTO tenants (id, name, code) VALUES ('11111111-1111-1111-1111-111111111111', 'T1', 'T1');
  SELECT set_config('app.current_tenant', '11111111-1111-1111-1111-111111111111', false);
  INSERT INTO users (id, tenant_id, email) VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'u@t1');
  INSERT INTO products (id, tenant_id, code, name, unit, currency, base_price) VALUES (gen_random_uuid(), '11111111-1111-1111-1111-111111111111', 'P1', 'Product', 'box', 'IQD', 1000);" >/dev/null
ok "the real schema and some data are in place"

# 1. backup.sh makes a dump and records the good copy
"$DEPLOY/backup.sh" >"$WORK/out" 2>&1 || { cat "$WORK/out" >&2; die "backup.sh failed"; }
dump="$(ls -1 "$BACKUP_DIR"/${LIFEDROP_DB_NAME}-*.dump | head -1)"
[ -s "$dump" ] && [ -f "$BACKUP_DIR/last-local-ok" ] && [ ! -f "$BACKUP_DIR/last-local-failed" ] || die "backup.sh left no dump or no status"
ok "backup.sh writes a dump and records it"

# 2. restore-test.sh passes on a good dump and shows the tables
"$DEPLOY/restore-test.sh" >"$WORK/out" 2>&1 || { cat "$WORK/out" >&2; die "restore-test.sh failed on a good dump"; }
has "Restore test passed" ; has "products"; has "Row-level security policies"
ok "restore-test.sh passes on a good dump and lists the tables"

# 3. restore-test.sh notices a dump that lost a table's rows (the live database has them, the dump does not)
cp "$dump" "$WORK/good.dump"
sql postgres "DROP DATABASE IF EXISTS ${LIFEDROP_DB_NAME}_lost WITH (FORCE)" >/dev/null
sql postgres "CREATE DATABASE ${LIFEDROP_DB_NAME}_lost" >/dev/null
sql "${LIFEDROP_DB_NAME}_lost" "CREATE EXTENSION IF NOT EXISTS postgis" >/dev/null
pgrestore_file "${LIFEDROP_DB_NAME}_lost" "$WORK/good.dump" >/dev/null 2>&1 || true
sql "${LIFEDROP_DB_NAME}_lost" "SELECT set_config('app.current_tenant', '11111111-1111-1111-1111-111111111111', false); DELETE FROM products;" >/dev/null 2>&1 || sql "${LIFEDROP_DB_NAME}_lost" "TRUNCATE products CASCADE" >/dev/null
pgdump -Fc "${LIFEDROP_DB_NAME}_lost" > "$WORK/lost.dump"
sql postgres "DROP DATABASE ${LIFEDROP_DB_NAME}_lost WITH (FORCE)" >/dev/null
expect_fail "a dump without the products" "$DEPLOY/restore-test.sh" "$WORK/lost.dump"
has "RESTORE CHECK FAILED: products has 1 rows in the live database and 0 in the restored one"
ok "restore-test.sh fails when a table lost its rows"

# 4. restore-test.sh fails on a damaged file
head -c 2000 "$WORK/good.dump" > "$WORK/damaged.dump"
expect_fail "a damaged dump" "$DEPLOY/restore-test.sh" "$WORK/damaged.dump"
ok "restore-test.sh fails on a damaged dump"

# 5. backup.sh refuses a dump command that fails and says so in the status
good_dump_cmd="$DUMP_CMD"
export DUMP_CMD="false"
expect_fail "a failing dump" "$DEPLOY/backup.sh"
[ -f "$BACKUP_DIR/last-local-failed" ] || die "no failure status after a failed backup"
export DUMP_CMD="$good_dump_cmd"
ok "a failed backup is recorded"

# 6. backup-status.sh: failure newer than the good copy is a problem; a fresh good copy clears it; an old one is a problem
set +e; "$DEPLOY/backup-status.sh" >"$WORK/out" 2>&1; rc=$?; set -e
[ "$rc" = 1 ] && has "the last try failed" || die "backup-status.sh should report the failed try (rc=$rc)"
"$DEPLOY/backup.sh" >"$WORK/out" 2>&1 || { cat "$WORK/out" >&2; die "backup.sh failed after the failed try"; }
"$DEPLOY/backup-status.sh" >"$WORK/out" 2>&1 || { cat "$WORK/out" >&2; die "backup-status.sh should be fine after a good backup"; }
has "ok       local dump"; has "off-site copy is not set up"
echo "$(( $(date +%s) - 3 * 86400 )) old copy" > "$BACKUP_DIR/last-local-ok"
expect_fail "an old copy" "$DEPLOY/backup-status.sh"
has "hours old"
rm -f "$BACKUP_DIR/last-local-ok"
expect_fail "no copy" "$DEPLOY/backup-status.sh"
has "no good copy has been made yet"
"$DEPLOY/backup.sh" >"$WORK/out" 2>&1 || { cat "$WORK/out" >&2; die "backup.sh failed"; }
ok "backup-status.sh reports failures, old copies and missing copies"

# 7. backup-offsite.sh with stand-ins for age and rclone: a good run is recorded, a failing upload is not counted as good
mkdir -p "$WORK/bin" "$WORK/remote"
cat > "$WORK/bin/age" <<'STUB'
#!/usr/bin/env bash
out=""; in=""; while [ $# -gt 0 ]; do case "$1" in -r) shift ;; -o) out="$2"; shift ;; *) in="$1" ;; esac; shift; done
if [ -n "$in" ]; then cat "$in" > "$out"; else cat > "$out"; fi
STUB
cat > "$WORK/bin/rclone" <<'STUB'
#!/usr/bin/env bash
[ -z "${RCLONE_FAIL:-}" ] || { echo "rclone: forced failure" >&2; exit 1; }
case "$1" in
  copyto) mkdir -p "$REMOTE_DIR"; cp "$2" "$REMOTE_DIR/$(basename "$3")" ;;
  check) for f in "$REMOTE_DIR"/*; do [ -e "$f" ] || exit 1; done ;;
  delete) : ;;
  listremotes) echo "fake:" ;;
esac
STUB
chmod +x "$WORK/bin/age" "$WORK/bin/rclone"
export PATH="$WORK/bin:$PATH" REMOTE_DIR="$WORK/remote" AGE_RECIPIENT="age1aaaaaaaaaa age1bbbbbbbbbb" OFFSITE_RCLONE_REMOTE="fake:lifedrop"
rm -f "$BACKUP_DIR"/last-*
"$DEPLOY/backup-offsite.sh" >"$WORK/out" 2>&1 || { cat "$WORK/out" >&2; die "backup-offsite.sh failed with the stand-ins"; }
[ -f "$BACKUP_DIR/last-offsite-ok" ] && [ ! -f "$BACKUP_DIR/last-offsite-failed" ] || die "the good off-site run left no status"
ls "$WORK/remote" | grep -q "dump.age" || die "no encrypted dump on the remote"
"$DEPLOY/backup-status.sh" >"$WORK/out" 2>&1 || { cat "$WORK/out" >&2; die "backup-status.sh should be fine with both copies"; }
has "ok       off-site"
ok "a good off-site run is recorded and counted"
rm -f "$BACKUP_DIR/last-offsite-ok"
RCLONE_FAIL=1 expect_fail "a failing upload" "$DEPLOY/backup-offsite.sh"
[ -f "$BACKUP_DIR/last-offsite-failed" ] && [ ! -f "$BACKUP_DIR/last-offsite-ok" ] || die "a failed upload must be recorded as failed and not as good"
expect_fail "status after a failed upload" "$DEPLOY/backup-status.sh"
has "off-site"; has "the last try failed"
ok "a failed upload is recorded and backup-status.sh reports it"

echo "All $pass checks passed."
