using Microsoft.AspNetCore.Hosting;

namespace LifeDrop.Database.Tests;

// Gives each API test host its own JWT settings so tests do not depend on local
// config files or on environment variables set by other test classes.
internal static class TestHostSettings
{
    public const string JwtIssuer = "lifedrop-test";
    public const string JwtAudience = "lifedrop-api-test";
    public const string JwtSigningKey = "lifedrop-isolated-test-signing-key-32-bytes-minimum";

    public static void UseTestJwt(IWebHostBuilder webBuilder)
    {
        webBuilder.UseSetting("Jwt:Issuer", JwtIssuer);
        webBuilder.UseSetting("Jwt:Audience", JwtAudience);
        webBuilder.UseSetting("Jwt:SigningKey", JwtSigningKey);
    }
}
