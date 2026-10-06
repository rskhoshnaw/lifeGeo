-- Least-privilege table grants for the API runtime role (plan step 0.9).
-- Idempotent; apply after migrations with the schema on search_path. The role must stay
-- NOSUPERUSER NOBYPASSRLS so tenant RLS applies. DeploymentRehearsalTests applies this same
-- file (with a temporary role name) and exercises every endpoint group through it.
-- audit_logs: SELECT/INSERT only; the append-only trigger also blocks UPDATE/DELETE/TRUNCATE.
GRANT SELECT ON
    tenants, users, roles, role_permissions, user_roles, devices, refresh_tokens,
    customers, audit_logs, visits, regions, teams, team_members, visit_reports, visit_location_events, visit_status_history, shifts, tracking_points, customer_visit_windows, places, notifications, products, city_prices, customer_discounts, warehouses, order_counters, orders, order_items
TO lifedrop_app;

GRANT INSERT ON
    user_roles, audit_logs, auth_login_audit, devices, refresh_tokens,
    customers, visits, regions, teams, team_members, visit_reports, visit_location_events, visit_status_history, shifts, tracking_points, customer_visit_windows, places, notifications, products, city_prices, customer_discounts, warehouses, order_counters, orders, order_items
TO lifedrop_app;

GRANT UPDATE ON devices, refresh_tokens, visits, teams, team_members, customers, visit_reports, shifts, visit_location_events, places, notifications, products, city_prices, customer_discounts, warehouses, order_counters, orders, order_items TO lifedrop_app;

GRANT DELETE ON team_members, customer_visit_windows TO lifedrop_app;
