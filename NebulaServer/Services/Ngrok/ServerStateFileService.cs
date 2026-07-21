using System.Text.Json;

namespace NebulaServer.Services.Ngrok;

public sealed class ServerStateFileService
{
    private readonly string _filePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public ServerStateFileService()
    {
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Nebula Server",
            "server.json");
    }

    public async Task SaveAsync(ServerState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        var json = JsonSerializer.Serialize(state, JsonOptions);

        await File.WriteAllTextAsync(_filePath, json);
    }

    public async Task<ServerState?> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return null;

        var json = await File.ReadAllTextAsync(_filePath);

        return JsonSerializer.Deserialize<ServerState>(json);
    }

    public string FilePath => _filePath;
}