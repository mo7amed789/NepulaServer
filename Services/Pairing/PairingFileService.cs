using System.Text.Json;
using Microsoft.Extensions.Logging;
using NebulaServer.Helpers;
using NebulaServer.Models.Pairing;

namespace NebulaServer.Services.Pairing;

public sealed class PairingFileService
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly ILogger<PairingFileService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public PairingFileService(ILogger<PairingFileService> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(RuntimePaths.Data, "pairing.json");
    }

    public async Task<PairingInfo?> LoadAsync()
    {
        await _sync.WaitAsync();
        try
        {
            if (!File.Exists(_filePath))
                return null;

            var json = await File.ReadAllTextAsync(_filePath);

            try
            {
                return JsonSerializer.Deserialize<PairingInfo>(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize pairing state from {FilePath}.", _filePath);
                throw;
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task SaveAsync(PairingInfo pairing)
    {
        await _sync.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            var json = JsonSerializer.Serialize(pairing, JsonOptions);

            await File.WriteAllTextAsync(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save pairing state to {FilePath}.", _filePath);
            throw;
        }
        finally
        {
            _sync.Release();
        }
    }

    public bool Exists()
    {
        return File.Exists(_filePath);
    }

    public string FilePath => _filePath;
}
