using NebulaServer.Services.Server;

public static class ServerStateExtensions
{
    public static ServerState Snapshot(
        this ServerStateService service)
    {
        return service.Get();
    }
}   
