-- NOT EXECUTED
-- Preflight script for Customer API Slice Deployment

\set ON_ERROR_STOP on

SELECT '--- DATABASE IDENTITY ---' AS section;
SELECT current_database() AS target_database;

SELECT '--- POSTGIS EXTENSION STATUS ---' AS section;
-- Check if PostGIS is installed
SELECT extname, extversion FROM pg_extension WHERE extname = 'postgis';
-- Also check if it's available to be installed
SELECT name, default_version, installed_version FROM pg_available_extensions WHERE name = 'postgis';

SELECT '--- CUSTOMERS TABLE & RLS STATUS ---' AS section;
SELECT tablename, rowsecurity 
FROM pg_tables 
WHERE tablename = 'customers';

SELECT '--- DEV TENANT COUNT ---' AS section;
SELECT COUNT(*) AS dev_tenant_count 
FROM tenants 
WHERE code = 'dev';

SELECT '--- RUNTIME ROLE ATTRIBUTES (lifedrop_app) ---' AS section;
SELECT rolname, rolsuper, rolbypassrls, rolcreatedb, rolcreaterole 
FROM pg_roles 
WHERE rolname = 'lifedrop_app';

SELECT '--- EXISTING CUSTOMER PERMISSIONS (DevAdmin) ---' AS section;
SELECT rp.permission_key, COUNT(*) AS grant_count
FROM role_permissions rp
JOIN roles r ON rp.role_id = r.id
JOIN tenants t ON rp.tenant_id = t.id
WHERE t.code = 'dev' AND r.name = 'DevAdmin' AND rp.permission_key IN ('customers.read', 'customers.write')
GROUP BY rp.permission_key;
