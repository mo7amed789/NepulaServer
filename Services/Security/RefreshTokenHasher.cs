using System.Security.Cryptography;
using System.Text;

namespace NebulaServer.Services.Security;

public static class RefreshTokenHasher
{
    public static string Hash(string refreshToken, string? salt = null)
    {
        var saltBytes = string.IsNullOrWhiteSpace(salt)
            ? RandomNumberGenerator.GetBytes(32)
            : Convert.FromBase64String(salt);

        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password: Encoding.UTF8.GetBytes(refreshToken),
            salt: saltBytes,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32);
        return $"{Convert.ToBase64String(saltBytes)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string refreshToken, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        var parts = storedHash.Split(':', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        var recomputed = Hash(refreshToken, parts[0]);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(recomputed),
            Encoding.UTF8.GetBytes(storedHash));
    }
}
