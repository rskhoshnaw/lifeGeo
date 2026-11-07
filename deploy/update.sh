#!/usr/bin/env bash
# One command to update the server (docs/DEPLOY-FIRST-SERVER.md): new code, backup, database changes, rebuild, then waits until the API is healthy.
# Run it on the server:  /opt/lifedrop/deploy/update.sh   Any step that fails stops the update and says which one; nothing after it runs.
. "$(dirname "$0")/lib.sh"
COMPOSE="docker compose -f $DEPLOY_DIR/docker-compose.yml"
step() { echo; echo "==> $*"; }
fail() { echo; echo "UPDATE STOPPED: $*" >&2; exit 1; }

step "1/5 New code (git pull)"
git -C "$ROOT_DIR" pull --ff-only || fail "git pull failed (local changes on the server, or no connection to GitHub). Nothing was changed."
step "2/5 Backup of the database"
"$DEPLOY_DIR/backup.sh" || fail "backup failed. The database and the running server were not touched."
step "3/5 Database changes (migrations)"
"$DEPLOY_DIR/setup-db.sh" || fail "setup-db failed. The backup from step 2 is safe; the running server is still the old one."
step "4/5 Rebuild and restart"
$COMPOSE up -d --build || fail "docker compose failed. See the messages above."

step "5/5 Waiting for the API to be healthy (up to 2 minutes)"
status="starting"
for _ in $(seq 1 24); do
  id="$($COMPOSE ps -q api 2>/dev/null || true)"
  status="$([ -n "$id" ] && docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$id" 2>/dev/null || echo missing)"
  [ "$status" = "healthy" ] && break
  sleep 5
done
$COMPOSE ps
[ "$status" = "healthy" ] || fail "the API is '$status', not healthy. Look at it with:  docker compose -f $DEPLOY_DIR/docker-compose.yml logs --tail 80 api"
# Old images of earlier builds (no longer used by any container) fill the disk; only dangling ones are removed, never an image a container uses.
step "Cleaning old images"
docker image prune -f || echo "(cleaning old images failed; the update itself is done)"
echo
echo "UPDATE DONE: the API is healthy ($(git -C "$ROOT_DIR" log -1 --format='%h %s'))."
