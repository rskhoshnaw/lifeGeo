# NOT EXECUTED
# Post-Deployment Verification Checklist for `lifedrop_dev` (Customer API Slice)

## Objective
Verify the runtime API correctly utilizes the newly established `lifedrop_app` role without encountering permission regressions, and confirm that Customer Create/Get operations succeed. Do not use real customer data. Use only synthetic test accounts.

## Prerequisite A: Manual Password Setup (Non-Atomic Boundary)
The `02_apply_changes.sql` script creates the `lifedrop_app` role identically to the tested environment but explicitly strips `LOGIN` capability. To complete the deployment securely:

1. Connect to `lifedrop_dev` as the administrative user (e.g., `postgres`) via `psql`.
2. Securely provision a strong, unique password using the interactive prompt:
   ```sql
   \password lifedrop_app
   ```
3. Once the password is mathematically verified and set by PostgreSQL, manually enable API connection access:
   ```sql
   ALTER ROLE lifedrop_app WITH LOGIN;
   ```
*(**Recovery Note**: The boundary between the automated script and manual password setup is non-atomic. If the `\password` step is interrupted or fails, all schema resources and grants remain completely intact, but the API cannot log in yet. Recovery is entirely safe: simply reconnect via `psql` and re-run the `\password` and `ALTER ROLE ... WITH LOGIN;` commands until successful).*

## Prerequisite B: Switch Configuration
Update the API configuration (e.g., `appsettings.Development.json` or Environment Variables) to use the new connection string credentials (`lifedrop_app` and the password you set). Restart the API host to apply the configuration.

## Step 1: Pre-existing Routes (Regression Check)
*Identify and use an authorized development test account that belongs to the `dev` tenant and possesses the `DevAdmin` role. Do not use real credentials or hardcode passwords in any logs. Ensure all verification data is strictly synthetic.*

- [ ] **Login:** `POST /auth/login`
  - *Expectation:* `200 OK` (Proves `SELECT` on `users`, `tenants`, and `INSERT` on `auth_login_audit`)
- [ ] **Token Refresh:** `POST /auth/refresh`
  - *Expectation:* `200 OK` (Proves `SELECT`, `INSERT`, `UPDATE` on `refresh_tokens`)
- [ ] **Device Registration:** `POST /auth/devices/register`
  - *Expectation:* `200 OK` (Proves `SELECT`, `INSERT` on `devices` and `INSERT`, `SELECT` on `audit_logs`)
- [ ] **Role Management:** `POST /users/{synthetic_id}/roles/{synthetic_id}`
  - *Expectation:* `204 NoContent` (Proves `SELECT`, `INSERT` on `user_roles`)

## Step 2: Customer Slice
*Ensure the synthetic test user has the `DevAdmin` role on the `dev` tenant.*

- [ ] **Create Customer:** `POST /customers`
  - *Payload:* `{"name": "Synthetic Clinic", "code": "SYN-100", "type": "clinic"}`
  - *Expectation:* `201 Created`. The location header contains the new customer ID.
- [ ] **Get Customer:** `GET /customers/{id}` (Using the ID from previous step)
  - *Expectation:* `200 OK`. The payload returns the `"Synthetic Clinic"` record.

## Immediate Non-Destructive Rollback Procedure
If any verification step fails, or if existing integrations report database permission errors:

1. **Revert Connection String:** Immediately update the API configuration to use the previous known-good database role (e.g., `postgres` or previous migration user).
2. **Restart API:** Restart the API host to instantly clear pooled connections and revert to the previous operational state.
3. **Re-verify Step 1:** Confirm `POST /auth/login` and device endpoints operate successfully again.
4. **Preserve Data:** Do NOT run `DROP ROLE`, `DROP TABLE customers`, or `REVOKE ALL` at this stage. All tables and records must be preserved for investigation. Complete removal of the role and schema is a separate operational task requiring dependency review.

