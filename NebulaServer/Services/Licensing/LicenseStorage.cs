using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NebulaServer.Models.Licensing;

namespace NebulaServer.Services.Licensing;

public sealed class LicenseStorage : ILicenseStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("NBL1");

    private readonly ILogger<LicenseStorage> _logger;
    private readonly string _licenseDirectory;
    private readonly string _licenseFile;

    public LicenseStorage(ILogger<LicenseStorage> logger)
    {
        _logger = logger;

        _licenseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Nebula Server");

        _licenseFile = Path.Combine(_licenseDirectory, "license.json");
    }

    public bool Exists() => File.Exists(_licenseFile);

    public async Task<LicenseInfo?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!Exists())
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_licenseFile);
            var licenseBytes = await ReadAllBytesAsync(stream, cancellationToken).ConfigureAwait(false);

            if (licenseBytes.Length >= Magic.Length && licenseBytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                var payload = ProtectedData.Unprotect(
                    licenseBytes[Magic.Length..],
                    optionalEntropy: null,
                    scope: DataProtectionScope.LocalMachine);

                return JsonSerializer.Deserialize<LicenseInfo>(payload, JsonOptions);
            }

            var text = Encoding.UTF8.GetString(licenseBytes);
            return JsonSerializer.Deserialize<LicenseInfo>(text, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load license.");
            return null;
        }
    }

    public async Task SaveAsync(
        LicenseInfo license,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_licenseDirectory);

        var payload = JsonSerializer.SerializeToUtf8Bytes(license, JsonOptions);

        var protectedBytes = ProtectedData.Protect(
            payload,
            optionalEntropy: null,
            scope: DataProtectionScope.LocalMachine);

        var tempFile = _licenseFile + ".tmp";

        var data = new byte[Magic.Length + protectedBytes.Length];

        Buffer.BlockCopy(Magic, 0, data, 0, Magic.Length);
        Buffer.BlockCopy(protectedBytes, 0, data, Magic.Length, protectedBytes.Length);

        await File.WriteAllBytesAsync(tempFile, data, cancellationToken).ConfigureAwait(false);

        // تحسين: النقل الذري الآمن الذي يدعم الكتابة الفوقية مباشرة (متوفر في .NET Core 3.0 وما فوق)
        try
        {
            _logger.LogInformation("Temp Exists: {Exists}", File.Exists(tempFile));
            _logger.LogInformation("License Exists: {Exists}", File.Exists(_licenseFile));
            if (File.Exists(_licenseFile))
            {
                File.SetAttributes(_licenseFile, FileAttributes.Normal);
                File.Delete(_licenseFile);
            }

            File.Move(tempFile, _licenseFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move temporary license file to final destination.");
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
            throw;
        }
    }

    // تم إضافة cancellationToken لتتطابق مع الـ Interface
    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (!Exists())
            return Task.CompletedTask;

        File.SetAttributes(_licenseFile, FileAttributes.Normal);
        File.Delete(_licenseFile);

        return Task.CompletedTask;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }
}