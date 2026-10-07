# Shared by the deploy scripts. Run them from anywhere; they find the repository themselves.
set -euo pipefail
DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$DEPLOY_DIR")"
if [ -f "$DEPLOY_DIR/.env" ]; then set -a; . "$DEPLOY_DIR/.env"; set +a; fi
# psql as the database superuser. PSQL_CMD can be set to another command (used by the checks that run without docker).
PSQL_CMD="${PSQL_CMD:-docker compose -f $DEPLOY_DIR/docker-compose.yml exec -T db psql -v ON_ERROR_STOP=1 -q -t -A -U postgres}"
psql_db() { local db="$1"; shift; $PSQL_CMD -d "$db" "$@"; }
