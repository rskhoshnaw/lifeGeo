# EXECUTED: Deployment Plan for `lifedrop_dev` (Customer API Slice)

## Status
- **Execution Date**: 2026-10-03
- **Environment**: Development (`lifedrop_dev`)
- **Result**: `02_apply_changes.sql` successfully committed. `lifedrop_app` role created and enabled for login. The API successfully started without SUPERUSER/BYPASSRLS under this restricted role. A synthetic customer POST/GET verification succeeded. After a subsequent `lifedrop_app` password reset, the API restarted successfully under the restricted role (customer routes were not re-verified post-reset).
- **Tooling**: `DevBootstrap` was securely separated to `ConnectionStrings:Bootstrap` and builds successfully, but was not run for verification. The `postgres` password rotation is unconfirmed.
## Objective
This plan provisions the `customers` slice and introduces a non-superuser `lifedrop_app` role with strictly minimal privileges required solely by the API's runtime operations. The `LifeDrop.DevBootstrap` tool requires elevated initialization privileges and is deliberately separated from the API runtime identity.

## 1. Privileges Assessment

Based on a targeted source analysis of `src/LifeDrop.Infrastructure`, the exact DML operations were identified for the runtime API. No `DELETE` operations exist in the runtime codebase.

### Minimum Required Privileges for `lifedrop_app` (API Runtime)
| Table / Object | SELECT | INSERT | UPDATE | DELETE | Originating Code Context |
|----------------|:---:|:---:|:---:|:---:|------------------|
| `tenants` | ✓ | | | | Select (API isolation checks) |
| `users` | ✓ | | | | Select (API authentication) |
| `roles` | ✓ | | | | Select (API role validation) |
| `role_permissions` | ✓ | | | | Select (API permission validation) |
| `user_roles` | ✓ | ✓ | | | Select, Insert (API Role Assignment) |
| `audit_logs` | ✓ | ✓ | | | Select, Insert (RETURNING clause by API) |
| `auth_login_audit` | | ✓ | | | Insert (Append-only by API Login) |
| `devices` | ✓ | ✓ | ✓ | | Select, Insert, Update (API Device Auth) |
| `refresh_tokens` | ✓ | ✓ | ✓ | | Select, Insert, Update (API Session Rotation) |
| `customers` | ✓ | ✓ | | | Select, Insert (API Customers Endpoint) |

### DevBootstrap-Only Operational Identity (NOT granted to `lifedrop_app`)
The `LifeDrop.DevBootstrap` tool performs tenant and developer-user seeding. These operations should be executed via a separate operational identity or manual bootstrap step. They require:
- **INSERT**: `tenants`, `users`, `roles`, `role_permissions`
- **UPDATE**: `users` (Password resets)

### PostGIS & Schema Dependencies
| Object | Privilege | Notes |
|--------|-----------|-------|
| `public` schema | `USAGE` | Required to access the schema itself. |

**Open Verification Items (PostGIS)**: The `customers` table utilizes `geography(Point, 4326)` and `GIST` indexes. While PostGIS catalog views (`spatial_ref_sys`, `geometry_columns`, `geography_columns`) exist, current source analysis cannot definitively prove the API executes queries requiring explicit `SELECT` grants against them. These are withheld from `lifedrop_app` pending runtime verification.

**Migration Ownership Principle**: All schemas, tables, and PostGIS extensions must be created and owned by a dedicated DDL / superuser role (e.g., `postgres`), not `lifedrop_app`. The `lifedrop_app` role is exclusively for runtime DML.

---

## 2. Deployment Order

Execute this sequence as the administrative / migration role (`postgres`), **not** `lifedrop_app`.

### Phase A: Preflight Checks
Execute `scripts/deploy_customer_api/01_preflight.sql` to verify database identity, PostGIS extension status, customers table presence, dev tenant count, and existing permissions.

### Phase B: Applying Changes
*Note: Schema verification via migration is distinct from real API runtime behavior. The script establishes structure and role grants; Phase C and D verify runtime operation.*

**Step 1:** Execute `scripts/deploy_customer_api/02_apply_changes.sql`
This combined script safely applies the `customers` schema (0004) and `dev` admin permissions (0005). It also creates the `lifedrop_app` role securely without login privileges and applies the targeted runtime grants.

**Reference of key actions inside the script:**
```sql
CREATE ROLE lifedrop_app WITH NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;

GRANT USAGE ON SCHEMA public TO lifedrop_app;

-- Provide SELECT privileges
GRANT SELECT ON tenants, users, roles, role_permissions, user_roles, 
                devices, refresh_tokens, customers, audit_logs TO lifedrop_app;

-- Provide INSERT privileges
GRANT INSERT ON user_roles, audit_logs, auth_login_audit, 
                devices, refresh_tokens, customers TO lifedrop_app;

-- Provide UPDATE privileges
GRANT UPDATE ON devices, refresh_tokens TO lifedrop_app;
```

*(Note: Explicitly excluded `DELETE` globally. `audit_logs` is explicitly restricted to `INSERT` and `SELECT` only).*

### Phase C: Provisioning `lifedrop_app` Login
**Step 2:** Manual Password Setup (Non-Atomic Boundary)
As mandated by `scripts/deploy_customer_api/03_verification_checklist.md`, the role was explicitly created with `NOLOGIN` to prevent hardcoded passwords. To complete the deployment securely:

1. Connect via `psql`.
2. Set a strong password securely:
   ```sql
   \password lifedrop_app
   ```
3. Manually enable login:
   ```sql
   ALTER ROLE lifedrop_app WITH LOGIN;
   ```

### Phase D: Verification Queries
**Query:**
```sql
SELECT has_table_privilege('lifedrop_app', 'audit_logs', 'UPDATE');
SELECT has_table_privilege('lifedrop_app', 'customers', 'SELECT');
```
**Expected Results:**
- `has_table_privilege('lifedrop_app', 'audit_logs', 'UPDATE')` -> `false`
- `has_table_privilege('lifedrop_app', 'customers', 'SELECT')` -> `true`

---

## 3. Rollback Plan

This rollback procedure is strictly non-destructive. It preserves `customers`, `audit_logs`, and all existing data.

### Immediate Non-Destructive Rollback
If the API exhibits degradation or permission failures:
1. **Restore Configuration**: Revert the API's connection string configuration back to the previous stable identity (e.g., `postgres`).
2. **Restart API**: Restart the API instances to immediately flush connection pools and resume operations using the prior valid identity.
3. **Verify Routes**: Confirm that existing, unaffected API routes (e.g., authentication, device registration) continue to function correctly.

### Post-Deployment Clean-up (Requires Separate Review)
Do not immediately issue `DROP`, `DELETE`, or `REVOKE ALL` commands. Removal of the newly introduced `lifedrop_app` role, the `0004` tables (`customers`), or `0005` grants must be treated as a separately reviewed cleanup action that:
- Actively compares the current database state against pre-deployment records.
- Verifies dependency chains (e.g., ensuring no live users rely on the customer permissions before removing them, never assuming permissions were entirely absent beforehand).
- Issues targeted, granular `REVOKE` and `DROP` commands only once dependencies are verified.




