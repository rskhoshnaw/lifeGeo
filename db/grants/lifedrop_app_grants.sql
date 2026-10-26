-- Least-privilege table grants for the API runtime role (plan step 0.9).
-- Idempotent; apply after migrations with the schema on search_path. The role must stay
-- NOSUPERUSER NOBYPASSRLS so tenant RLS applies. DeploymentRehearsalTests applies this same
-- file (with a temporary role name) and exercises every endpoint group through it.
-- audit_logs: SELECT/INSERT only; the append-only trigger also blocks UPDATE/DELETE/TRUNCATE.
GRANT SELECT ON
    tenants, users, roles, role_permissions, user_roles, devices, refresh_tokens,
    customers, audit_logs, visits, regions, teams, team_members, visit_reports, visit_location_events, visit_status_history, shifts, tracking_points, customer_visit_windows, places, notifications, products, city_prices, customer_discounts, warehouses, order_counters, orders, order_items, warehouse_staff, order_status_history, payment_counters, payments, sample_counters, sample_distributions, visit_report_details, follow_ups, alerts, shift_summaries, payment_history, push_tokens, platform_audit_logs, saved_reports, report_runs, form_fields, visit_custom_answers
TO lifedrop_app;

GRANT INSERT ON
    user_roles, audit_logs, auth_login_audit, devices, refresh_tokens,
    customers, visits, regions, teams, team_members, visit_reports, visit_location_events, visit_status_history, shifts, tracking_points, customer_visit_windows, places, notifications, products, city_prices, customer_discounts, warehouses, order_counters, orders, order_items, warehouse_staff, order_status_history, payment_counters, payments, sample_counters, sample_distributions, visit_report_details, follow_ups, alerts, shift_summaries, payment_history, push_tokens
TO lifedrop_app;

GRANT UPDATE ON devices, refresh_tokens, visits, teams, team_members, customers, visit_reports, shifts, visit_location_events, places, notifications, products, city_prices, customer_discounts, warehouses, order_counters, orders, order_items, payment_counters, payments, sample_counters, sample_distributions, visit_report_details, follow_ups, alerts TO lifedrop_app;

GRANT UPDATE ON push_tokens, saved_reports, form_fields, visit_custom_answers TO lifedrop_app;
GRANT DELETE ON saved_reports, report_runs TO lifedrop_app;
-- Step 3.1: only the platform endpoints (Super Admin of the platform company) create and change companies.
GRANT INSERT ON tenants, users, roles, role_permissions, platform_audit_logs, saved_reports, report_runs, form_fields, visit_custom_answers TO lifedrop_app;
GRANT UPDATE ON tenants TO lifedrop_app;
GRANT DELETE ON push_tokens TO lifedrop_app;
GRANT DELETE ON team_members, customer_visit_windows, warehouse_staff, tracking_points TO lifedrop_app;
-- Step 3.4: message channels (the user links and unlinks only their own channel; the code row is replaced and removed).
GRANT SELECT, INSERT, UPDATE, DELETE ON user_channels, channel_link_codes TO lifedrop_app;
-- Step 4.33: the shift alert settings of a company (read by the scan, changed by the sales manager).
GRANT SELECT, INSERT, UPDATE ON shift_alert_settings TO lifedrop_app;
-- Step 4.37: a manager removes a role from a user (DELETE /users/{id}/roles/{roleId}).
GRANT DELETE ON user_roles TO lifedrop_app;
-- Step 4.38: roles built in the panel (create, change permissions, delete).
GRANT UPDATE ON roles TO lifedrop_app;
GRANT DELETE ON roles, role_permissions TO lifedrop_app;
-- Step 4.39: a manager gives a device a label and deletes (hides) it.
-- (devices already has UPDATE)
-- Step 4.40: product photos (read by their random key without a company; written by managers).
GRANT SELECT, INSERT, UPDATE, DELETE ON product_photos TO lifedrop_app;
-- Step 4.42: a manager ticks "hide prices" on a region.
GRANT UPDATE ON regions TO lifedrop_app;
-- Managers change a user e-mail, password, active flag and city (PUT /users/{id}/...); tenant RLS still applies.
GRANT UPDATE ON users TO lifedrop_app;
-- Step 5.5: sales targets (set and cleared by managers; read for the targets report).
GRANT SELECT, INSERT, UPDATE, DELETE ON sales_targets TO lifedrop_app;
