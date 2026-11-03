using Microsoft.AspNetCore.SignalR;
using NebulaServer.Hubs;
using NebulaServer.Services;
using NebulaServer.Models; // 1. أضف هذا السطر للوصول إلى كلاس DownloadJob

namespace NebulaServer.Services.Dashboard;

public sealed class DashboardEventService : IDashboardEvents
{
    private readonly IHubContext<DashboardHub> _hub;

    public DashboardEventService(
        IHubContext<DashboardHub> hub)
    {
        _hub = hub;
    }

    public Task RefreshDashboard()
    {
        return _hub.Clients.All.SendAsync("DashboardUpdated");
    }

    public Task JobProgress(DownloadJob job)
    {
        return _hub.Clients.All.SendAsync(
            "JobProgress",
            new NebulaServer.Models.JobStatusResponse
            {
                JobId = job.JobId,
                State = job.State.ToString(),
                ProgressPercentage = job.ProgressPercentage,
                Speed = job.Speed,
                Eta = job.Eta,
                Message = job.Message,
                OutputFileName = job.OutputFileName,
                CompletedItems = job.CompletedItems.Values.OrderBy(item => item.Index).ToList(),
                ManifestVersion = job.ManifestVersion,
                EventSequence = job.EventSequence,
                CompletedCount = job.CompletedItems.Values.Count(item => item.State == NebulaServer.Models.Jobs.TransferState.Completed),
                TotalCount = job.CompletedItems.Count,
                ThumbnailUrl = job.ThumbnailUrl
            });
    }

    // ملاحظة: إذا كانت الواجهة لديك تحتوي أيضاً على دوال أخرى بنفس المشكلة، 
    // يجب عليك تغيير object إلى النوع الصحيح هنا أيضاً.
    public Task JobCompleted(object job)
    {
        return _hub.Clients.All.SendAsync(
            "JobCompleted",
            job);
    }

    public Task ServerStateChanged(object state)
    {
        return _hub.Clients.All.SendAsync(
            "ServerStateChanged",
            state);
    }

    public Task DownloadProgressChanged(NebulaServer.Models.Jobs.DownloadProgress progress)
    {
        return _hub.Clients.All.SendAsync(
            "DownloadProgress",
            progress);
    }
}