# Platform: companies and Super Admin (step 3.1)

- The platform company (`code = platform`, fixed id `00000000-0000-0000-0000-00000000f001`) is created by migration 0038 with the role `SuperAdmin`. Only its users reach `/platform/*`, and only with the permission (`platform.tenants.manage`, `platform.tenants.access`). A role with those keys in an ordinary company gives nothing.
- **First Super Admin (production):** no user is seeded. Insert one in the platform company with a PBKDF2 hash (same format as `users.password_hash`) and give it the `SuperAdmin` role; see the dev seed (`db/local/seed_dev.sql`, `root@platform.local`, company code `platform`) for the exact rows. Use a long unique password and keep the account to one or two people.
- Create a company: `POST /platform/tenants` (creates default roles and the first account, which gets both CompanyManager and SalesManager; CompanyManager alone is oversight only and sees no live map, devices or user management). Deactivate: `PUT /platform/tenants/{id}` with `isActive:false` and a reason; its users can no longer sign in and open sessions stop.
- Cross-company reading is explicit: `GET /platform/tenants/{id}/audit-logs?reason=...` (reason at least 10 characters). The reason, the actor and the company go to `platform_audit_logs` in the same transaction as the read. That table cannot be changed or deleted.
- Not in this step: reading business data (customers, orders) of another company, a panel screen for these routes, and a way for a company manager to see who from the platform looked at their audit log.

## Invitation links and QR (step 4.7)

The Companies tab has an **Invite** button for each company (not for the platform company). It shows two links and a QR code; the links carry only the public company code, never a password or a token.

| Link | Form | Use |
|---|---|---|
| Panel | `https://<panel address>/?company=<code>` | opens the sign-in with the company code filled (still editable) |
| App | `lifedrop://join/<code>` (this is the text in the QR code) | in the app, paste it into the company field: the code is taken out of it (`CompanyJoinLink`) |

Built in step 4.7 (final part): Android intent filter for `lifedrop://join/<code>` (`[IntentFilter]` on `MainActivity`, handled in `OnCreate`/`OnNewIntent`) and in-app QR scanner (`QrScanPage` with `ZXing.Net.MAUI.Controls`). Needs a real phone to verify.
