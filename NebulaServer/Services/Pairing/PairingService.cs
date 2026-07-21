using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using NebulaServer.Models.Pairing;

namespace NebulaServer.Services.Pairing;

public sealed class PairingService : IPairingService
{
    private readonly PairingFileService _fileService;
    private readonly ILogger<PairingService> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);

    private PairingInfo? _pairing;

    public PairingService(
        PairingFileService fileService,
        ILogger<PairingService> logger)
    {
        _fileService = fileService;
        _logger = logger;
    }

    // 1. تمت إضافة CancellationToken هنا ليتطابق مع الواجهة (IPairingService)
    public async Task<PairingInfo> GetPairingInfoAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _pairing != null)
            return _pairing;

        // 2. تمرير المُعامل هنا لضمان إمكانية الإلغاء أثناء الانتظار
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _pairing != null)
                return _pairing;

            try
            {
                // إذا كانت دالة LoadAsync تدعم CancellationToken، يمكنك تغييره إلى:
                // var loaded = await _fileService.LoadAsync(cancellationToken);
                var loaded = await _fileService.LoadAsync();

                if (loaded != null)
                {
                    _pairing = loaded;
                    return _pairing;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load pairing state from {FilePath}.", _fileService.FilePath);

                if (_pairing != null)
                    return _pairing;

                throw;
            }

            if (_pairing != null)
                return _pairing;

            _pairing = CreateNewPairing();

            // إذا كانت دالة SaveAsync تدعم CancellationToken، يمكنك تغييره إلى:
            // await _fileService.SaveAsync(_pairing, cancellationToken);
            await _fileService.SaveAsync(_pairing);

            _logger.LogInformation(
                "Created new pairing state at {FilePath} for server {ServerId}.",
                _fileService.FilePath,
                _pairing.ServerId);

            return _pairing;
        }
        finally
        {
            _sync.Release();
        }
    }

    // إضافة CancellationToken اختياري في حال طُلب مستقبلاً
    public async Task<string> GetPairingKeyAsync()
    {
        var pairing = await GetPairingInfoAsync();
        return pairing.PairingKey;
    }

    public async Task<bool> ValidateKeyAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var pairing = await GetPairingInfoAsync(forceRefresh: true);

        return ConstantTimeEquals(pairing.PairingKey, key);
    }

    public async Task RotateKeyAsync()
    {
        var pairing = await GetPairingInfoAsync();

        pairing.PairingKey = GenerateKey();
        pairing.LastRotationUtc = DateTime.UtcNow;

        await _fileService.SaveAsync(pairing);

        _logger.LogInformation(
            "Rotated pairing key for server {ServerId} and saved to {FilePath}.",
            pairing.ServerId,
            _fileService.FilePath);
    }

    internal static bool ConstantTimeEquals(string expected, string actual)
    {
        if (expected is null || actual is null)
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);

        // استخدام FixedTimeEquals لمنع هجمات التوقيت (Timing Attacks)
        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static PairingInfo CreateNewPairing()
    {
        return new PairingInfo
        {
            ServerId = Guid.NewGuid(),
            PairingKey = GenerateKey(),
            CreatedUtc = DateTime.UtcNow,
            LastRotationUtc = DateTime.UtcNow
        };
    }

    private static string GenerateKey()
    {
        Span<byte> buffer = stackalloc byte[24];

        RandomNumberGenerator.Fill(buffer);

        return "NBL_" + Convert.ToHexString(buffer);
    }
}