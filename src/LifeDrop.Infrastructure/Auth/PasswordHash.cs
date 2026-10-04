using System.Security.Cryptography;

namespace LifeDrop.Infrastructure.Auth;

public static class PasswordHash
{
    private const int Iterations = 210_000;
    private const int SaltLength = 16;
    private const int KeyLength = 32;

    public static string Create(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations,
            HashAlgorithmName.SHA256, KeyLength);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encoded)) return false;
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" ||
            !int.TryParse(parts[1], out var iterations) || iterations < Iterations || iterations > 1_000_000)
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length != SaltLength || expected.Length != KeyLength) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations,
                HashAlgorithmName.SHA256, KeyLength);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}
