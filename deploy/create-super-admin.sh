#!/usr/bin/env bash
# Creates (or resets the password of) a Super Admin in the platform company (docs/PLATFORM.md, docs/DEPLOY.md). Asks for the e-mail and the password;
# the password is hashed here (PBKDF2-SHA256, 210000 rounds, the format the API checks) and never stored or printed in clear text.
. "$(dirname "$0")/lib.sh"
DB="${LIFEDROP_DB_NAME:-lifedrop}"
PLATFORM_TENANT="00000000-0000-0000-0000-00000000f001"
email="${SUPER_ADMIN_EMAIL:-}"
[ -n "$email" ] || read -r -p "Super Admin e-mail: " email
password="${SUPER_ADMIN_PASSWORD:-}"
if [ -z "$password" ]; then read -r -s -p "Password (at least 12 characters): " password; echo; fi
[ "${#password}" -ge 12 ] || { echo "The password needs at least 12 characters." >&2; exit 1; }
case "$email" in *"'"*|*" "*|"") echo "Use a plain e-mail address." >&2; exit 1;; esac
case "$email" in *@*) ;; *) echo "Use a plain e-mail address." >&2; exit 1;; esac
hash="$(PW="$password" python3 -I -c '
import base64, hashlib, os
salt = os.urandom(16)
digest = hashlib.pbkdf2_hmac("sha256", os.environ["PW"].encode(), salt, 210000, 32)
print("pbkdf2-sha256$210000$" + base64.b64encode(salt).decode() + "$" + base64.b64encode(digest).decode())')"
psql_db "$DB" <<SQL
INSERT INTO users (id, tenant_id, email, password_hash)
VALUES (gen_random_uuid(), '$PLATFORM_TENANT', lower('$email'), '$hash')
ON CONFLICT (tenant_id, lower(email)) WHERE email IS NOT NULL DO UPDATE SET password_hash = EXCLUDED.password_hash, is_active = true;
INSERT INTO user_roles (tenant_id, user_id, role_id)
SELECT u.tenant_id, u.id, r.id FROM users u JOIN roles r ON r.tenant_id = u.tenant_id AND r.name = 'SuperAdmin'
WHERE u.tenant_id = '$PLATFORM_TENANT' AND lower(u.email) = lower('$email')
ON CONFLICT DO NOTHING;
SQL
echo "Super Admin ready. Sign in with company code 'platform' and this e-mail."
