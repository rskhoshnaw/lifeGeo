using System.Text.Json;
using System.Security.Cryptography;
using LifeDrop.Infrastructure.Auth;
using Npgsql;

var resetAdminPassword = args.Length == 1 && args[0] == "--reset-admin-password";
var generatedFieldPassword = args.Length == 1 && args[0] == "--set-field-password-generated";
var setFieldPassword = generatedFieldPassword ||
    (args.Length == 1 && args[0] == "--set-field-password");
if (args.Length != 0 && !resetAdminPassword && !setFieldPassword)
    throw new ArgumentException("Supported options: --reset-admin-password, --set-field-password, --set-field-password-generated");

var configPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", "..", "src", "LifeDrop.API", "appsettings.Development.local.json"));
if (!File.Exists(configPath))
    throw new InvalidOperationException("Local development settings were not found.");
using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
if (!document.RootElement.GetProperty("ConnectionStrings").TryGetProperty("Bootstrap", out var bootstrapProp))
    throw new InvalidOperationException("Missing 'Bootstrap' connection string in appsettings.Development.local.json. DevBootstrap requires a separate elevated connection.");
var connectionString = bootstrapProp.GetString() ?? throw new InvalidOperationException("Missing 'Bootstrap' connection string.");
var connectionOptions = new NpgsqlConnectionStringBuilder(connectionString);
if (connectionOptions.Database != "lifedrop_dev" || connectionOptions.Host != "127.0.0.1")
    throw new InvalidOperationException("Bootstrap is restricted to local lifedrop_dev.");

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();
Guid? existingTenant = null;
Guid? existingAdmin = null;
Guid? existingField = null;
await using (var check = new NpgsqlCommand("""
    SELECT t.id, a.id, f.id FROM tenants t
    LEFT JOIN users a ON a.tenant_id=t.id AND a.email='admin@lifedrop.local'
    LEFT JOIN users f ON f.tenant_id=t.id AND f.email='field@lifedrop.local'
    WHERE t.code='dev'
    """, connection))
{
    await using var reader = await check.ExecuteReaderAsync();
    if (await reader.ReadAsync())
    {
        existingTenant = reader.GetGuid(0);
        if (!reader.IsDBNull(1)) existingAdmin = reader.GetGuid(1);
        if (!reader.IsDBNull(2)) existingField = reader.GetGuid(2);
    }
}
if (existingTenant.HasValue && !resetAdminPassword && !setFieldPassword)
{
    Console.WriteLine("Development tenant already exists. No changes made. Use --reset-admin-password or --set-field-password if needed.");
    return;
}
if (resetAdminPassword && (!existingTenant.HasValue || !existingAdmin.HasValue))
    throw new InvalidOperationException("Development admin does not exist; no changes made.");
if (setFieldPassword && (!existingTenant.HasValue || !existingField.HasValue))
    throw new InvalidOperationException("Development field user does not exist; no changes made.");

var password = new System.Text.StringBuilder();
if (generatedFieldPassword)
{
    password.Append(Convert.ToBase64String(RandomNumberGenerator.GetBytes(36)));
}
else
{
    Console.Write(setFieldPassword
        ? "Choose a development field user password (12+ characters): "
        : "Choose a development admin password (12+ characters): ");
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace && password.Length > 0) password.Length--;
        else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
    }
    Console.WriteLine();
}
if (password.Length < 12) throw new InvalidOperationException("Password must contain at least 12 characters.");
var hash = PasswordHash.Create(password.ToString());
var generatedPasswordValue = generatedFieldPassword ? password.ToString() : null;
password.Clear();

if (resetAdminPassword || setFieldPassword)
{
    var userToUpdate = resetAdminPassword ? existingAdmin!.Value : existingField!.Value;
    var action = resetAdminPassword ? "dev.admin_password_reset" : "dev.field_password_set";
    await using var resetTransaction = await connection.BeginTransactionAsync();
    await using var update = new NpgsqlCommand("""
        WITH changed AS (
            UPDATE users SET password_hash=@hash
        WHERE tenant_id=@tenant AND id=@user AND is_active
            RETURNING id, tenant_id
        )
        INSERT INTO audit_logs(id,tenant_id,actor_id,action,entity_id,occurred_at)
        SELECT gen_random_uuid(),tenant_id,id,@action,id::text,now()
        FROM changed;
        """, connection, resetTransaction);
    update.Parameters.AddWithValue("hash", hash);
    update.Parameters.AddWithValue("tenant", existingTenant!.Value);
    update.Parameters.AddWithValue("user", userToUpdate);
    update.Parameters.AddWithValue("action", action);
    if (await update.ExecuteNonQueryAsync() != 1)
        throw new InvalidOperationException("Development user is inactive; no changes made.");
    await resetTransaction.CommitAsync();
    if (generatedPasswordValue is not null)
    {
        var passwordPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!,
            "..", "..", "artifacts", "dev-field-password.local.txt"));
        Directory.CreateDirectory(Path.GetDirectoryName(passwordPath)!);
        await File.WriteAllTextAsync(passwordPath, generatedPasswordValue);
        Console.WriteLine($"Generated password saved locally: {passwordPath}");
    }
    Console.WriteLine(resetAdminPassword
        ? "Development admin password updated. Existing database records were preserved."
        : "Development field user password set. Existing database records were preserved.");
    return;
}

await using var transaction = await connection.BeginTransactionAsync();
await using (var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM tenants WHERE code='dev')", connection, transaction))
{
    if ((bool)(await check.ExecuteScalarAsync() ?? false))
    {
        Console.WriteLine("Development tenant was created by another process. No changes made.");
        return;
    }
}

var tenant = Guid.NewGuid();
var admin = Guid.NewGuid();
var target = Guid.NewGuid();
var adminRole = Guid.NewGuid();
var fieldRole = Guid.NewGuid();
await using (var seed = new NpgsqlCommand("""
    INSERT INTO tenants(id,name,code) VALUES (@tenant,'LifeDrop Development','dev');
    INSERT INTO users(id,tenant_id,email,password_hash) VALUES
      (@admin,@tenant,'admin@lifedrop.local',@hash),
      (@target,@tenant,'field@lifedrop.local',NULL);
    INSERT INTO roles(id,tenant_id,name,requires_device_binding) VALUES
      (@admin_role,@tenant,'DevAdmin',false),(@field_role,@tenant,'FieldRep',true);
    INSERT INTO role_permissions(tenant_id,role_id,permission_key)
      VALUES (@tenant,@admin_role,'user.manageroles'),
             (@tenant,@admin_role,'device.approve');
    INSERT INTO user_roles(tenant_id,user_id,role_id)
      VALUES (@tenant,@admin,@admin_role);
    INSERT INTO audit_logs(id,tenant_id,actor_id,action,entity_id,occurred_at) VALUES
      (gen_random_uuid(),@tenant,@admin,'dev.bootstrap',@tenant::text,now());
    """, connection, transaction))
{
    seed.Parameters.AddWithValue("tenant", tenant);
    seed.Parameters.AddWithValue("admin", admin);
    seed.Parameters.AddWithValue("target", target);
    seed.Parameters.AddWithValue("admin_role", adminRole);
    seed.Parameters.AddWithValue("field_role", fieldRole);
    seed.Parameters.AddWithValue("hash", hash);
    await seed.ExecuteNonQueryAsync();
}
await transaction.CommitAsync();
Console.WriteLine("Development tenant: dev");
Console.WriteLine("Admin login: admin@lifedrop.local");
Console.WriteLine($"Target user: {target}");
Console.WriteLine($"Assignable FieldRep role: {fieldRole}");
