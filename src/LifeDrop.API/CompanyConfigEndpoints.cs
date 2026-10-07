using System.Text.RegularExpressions;
using Npgsql;

namespace LifeDrop.API;

// Company directory for the mobile app (plan step 4.2). The app asks "which server belongs to company code X?" before sign-in, so the
// server address is never built into the app and a company can move servers without a new app version. The answer holds only public
// facts: the company name, the API address (CompanyDirectory:<code>:ApiBaseUrl, default = this server) and the support phone.
// Nothing here is a secret and the token still decides every later request; an unknown or inactive code is a plain 404.
public static partial class CompanyConfigEndpoints
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{1,39}$")]
    private static partial Regex CodePattern();

    public static void MapCompanyConfigEndpoints(this WebApplication app)
    {
        app.MapGet("/company-config/{code}", async (string code, NpgsqlDataSource dataSource, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            var normalized = (code ?? "").Trim().ToLowerInvariant();
            if (!CodePattern().IsMatch(normalized)) return Results.NotFound();

            await using var command = dataSource.CreateCommand("SELECT code, name FROM tenants WHERE lower(code)=@code AND is_active");
            command.Parameters.AddWithValue("code", normalized);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return Results.NotFound();
            var tenantCode = reader.GetString(0);
            var name = reader.GetString(1);

            var apiBaseUrl = configuration[$"CompanyDirectory:{tenantCode}:ApiBaseUrl"];
            if (!string.IsNullOrWhiteSpace(apiBaseUrl) &&
                !(Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
                apiBaseUrl = null; // a bad entry must not point the app at an insecure address
            var supportPhone = configuration["Branding:SupportPhone"];
            return Results.Ok(new CompanyConfigResponse(tenantCode, name, string.IsNullOrWhiteSpace(apiBaseUrl) ? null : apiBaseUrl.TrimEnd('/'),
                string.IsNullOrWhiteSpace(supportPhone) ? null : supportPhone));
        }).ExcludeFromDescription();
    }

    public sealed record CompanyConfigResponse(string TenantCode, string Name, string? ApiBaseUrl, string? SupportPhone);
}
