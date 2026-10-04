INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT r.tenant_id, r.id, 'customers.read'
FROM roles r
JOIN tenants t ON r.tenant_id = t.id
WHERE r.name = 'DevAdmin' AND t.code = 'dev'
ON CONFLICT DO NOTHING;

INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT r.tenant_id, r.id, 'customers.write'
FROM roles r
JOIN tenants t ON r.tenant_id = t.id
WHERE r.name = 'DevAdmin' AND t.code = 'dev'
ON CONFLICT DO NOTHING;
