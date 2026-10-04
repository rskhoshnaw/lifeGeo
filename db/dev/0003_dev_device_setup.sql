-- Development data only. Apply after 0003_sessions_devices.sql and DevBootstrap.
DO $$ BEGIN
    IF current_database() <> 'lifedrop_dev' THEN
        RAISE EXCEPTION 'Refusing to change a non-development database';
    END IF;
END $$;

UPDATE roles r SET requires_device_binding=true
FROM tenants t WHERE r.tenant_id=t.id AND t.code='dev' AND r.name='FieldRep';

INSERT INTO role_permissions(tenant_id,role_id,permission_key)
SELECT r.tenant_id,r.id,'device.approve'
FROM roles r JOIN tenants t ON t.id=r.tenant_id
WHERE t.code='dev' AND r.name='DevAdmin'
ON CONFLICT DO NOTHING;

INSERT INTO audit_logs(id,tenant_id,actor_id,action,entity_id,occurred_at)
SELECT gen_random_uuid(),t.id,u.id,'dev.device_setup',t.id::text,now()
FROM tenants t JOIN users u ON u.tenant_id=t.id
WHERE t.code='dev' AND u.email='admin@lifedrop.local';
