using System.Security.Cryptography;
using System.Text;
using NebulaServer.Models.Licensing;
using NebulaServer.Settings;

namespace NebulaServer.Services.Licensing;

public sealed class LicenseCrypto
{
    public bool HasVerificationKey => !string.IsNullOrWhiteSpace(LicensingSecurity.LicenseSigningPublicKeyPem);

    public string ComputePayload(LicenseInfo license)
    {
        var signedAt = license.SignedAtUtc?.ToUniversalTime().ToString("O") ?? string.Empty;
        var graceUntil = license.OfflineGraceUntilUtc?.ToUniversalTime().ToString("O") ?? string.Empty;

        return string.Join("|",
            license.LicenseKey,
            license.MachineHash,
            license.DeviceName,
            license.NebulaVersion,
            license.ServerId,
            license.ExpiresAt.ToUniversalTime().ToString("O"),
            signedAt,
            graceUntil,
            license.Activated);
    }

    public bool Verify(LicenseInfo license)
    {
        if (!HasVerificationKey)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(license.Signature) || string.IsNullOrWhiteSpace(license.SignatureAlgorithm))
        {
            return false;
        }

        try
        {
            var payload = Encoding.UTF8.GetBytes(ComputePayload(license));

            // =========================================================
            // كود التشخيص الذي طلبته (تمت إضافته هنا)
            // =========================================================
            Console.WriteLine("========== VERIFY PAYLOAD ==========");
            Console.WriteLine(ComputePayload(license));
            Console.WriteLine("====================================");
            // =========================================================

            var signature = Convert.FromBase64String(license.Signature);

            return license.SignatureAlgorithm.Trim().ToUpperInvariant() switch
            {
                "RSA-SHA256" => VerifyRsa(payload, signature),
                _ => false
            };
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private bool VerifyRsa(byte[] payload, byte[] signature)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(LicensingSecurity.LicenseSigningPublicKeyPem);
        return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}