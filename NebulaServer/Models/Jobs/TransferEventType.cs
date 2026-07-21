namespace NebulaServer.Models.Jobs;

public enum TransferEventType
{
    ItemReady = 0,
    ItemCompleted = 1,
    JobCompleted = 2,
    JobFailed = 3,
    JobCanceled = 4
}
