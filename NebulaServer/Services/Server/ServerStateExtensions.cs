using NebulaServer.Services.Ngrok;

public static class ServerStateExtensions
{
    public static ServerState Snapshot(
        this ServerStateService service)
    {
        return service.Get();
    }
}   