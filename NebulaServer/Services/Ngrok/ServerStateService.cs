using System.Threading;

namespace NebulaServer.Services.Ngrok;

public sealed class ServerStateService
{
    private readonly Lock _lock = new();

    private readonly ServerState _state = new();

    public ServerState Get()
    {
        lock (_lock)
        {
            return new ServerState
            {
                IsServerRunning = _state.IsServerRunning,
                IsPythonReady = _state.IsPythonReady,
                PythonVersion = _state.PythonVersion,
                IsNgrokRunning = _state.IsNgrokRunning,
                IsConfigured = _state.IsConfigured,
                Version = _state.Version,
                PublicUrl = _state.PublicUrl,
                LocalIp = _state.LocalIp,
                Port = _state.Port,
                DeviceId = _state.DeviceId,
                LastUpdated = _state.LastUpdated
            };
        }
    }

    public void Update(Action<ServerState> update)
    {
        lock (_lock)
        {
            update(_state);
            _state.LastUpdated = DateTime.UtcNow;
        }
    }
}
