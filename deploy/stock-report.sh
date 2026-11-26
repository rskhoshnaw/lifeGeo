#!/usr/bin/env bash
# Report of the stock migration (plan step 5.53): what moved to the warehouses, what could not, which products nobody counted. Runs the SQL inside the database container,
# so no psql is needed on the server:  /opt/lifedrop/deploy/stock-report.sh
. "$(dirname "$0")/lib.sh"
DB="${LIFEDROP_DB_NAME:-lifedrop}"
REPORT_CMD="${REPORT_CMD:-docker compose -f $DEPLOY_DIR/docker-compose.yml exec -T db psql -v ON_ERROR_STOP=1 -U postgres}"
$REPORT_CMD -d "$DB" < "$ROOT_DIR/scripts/db/stock-migration-report.sql"
