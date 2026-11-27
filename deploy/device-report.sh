#!/usr/bin/env bash
# What the database knows about the phones of one user (plan step 5.62): read only. Use it when a phone cannot register or log in:
#   /opt/lifedrop/deploy/device-report.sh user@example.com
# Shows every device row (also the deleted ones), the account state and the last device audit entries.
. "$(dirname "$0")/lib.sh"
EMAIL="${1:?usage: device-report.sh <email>}"
DB="${LIFEDROP_DB_NAME:-lifedrop}"
REPORT_CMD="${REPORT_CMD:-docker compose -f $DEPLOY_DIR/docker-compose.yml exec -T db psql -v ON_ERROR_STOP=1 -U postgres}"
$REPORT_CMD -d "$DB" -v email="$EMAIL" <<'SQL'
\echo '--- account'
SELECT u.id, u.email, u.is_active AS user_active, t.code AS company, t.is_active AS company_active FROM users u JOIN tenants t ON t.id = u.tenant_id WHERE lower(u.email) = lower(:'email');
\echo '--- devices (newest first; deleted ones keep a "#deleted-" install id)'
SELECT d.id, d.status, d.platform, d.model, d.os_version, d.device_label, d.install_id, d.registered_at, d.deleted_at
FROM devices d JOIN users u ON u.tenant_id = d.tenant_id AND u.id = d.user_id WHERE lower(u.email) = lower(:'email') ORDER BY d.registered_at DESC;
\echo '--- last device events'
SELECT a.occurred_at, a.action, a.entity_id FROM audit_logs a JOIN users u ON u.tenant_id = a.tenant_id AND u.id = a.actor_id
WHERE lower(u.email) = lower(:'email') AND (a.action LIKE 'device.%' OR a.action LIKE 'auth.%') ORDER BY a.occurred_at DESC LIMIT 20;
SQL
