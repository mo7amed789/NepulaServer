namespace NebulaServer.Models.Jobs;

public enum TransferState
{
    Queued = 0,
    Ready = 1,
    Transferring = 2,
    Completed = 3,
    Failed = 4
}
