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

    // 2. تم تغيير object إلى DownloadJob هنا
    public Task JobProgress(DownloadJob job)
    {
        return _hub.Clients.All.SendAsync(
            "JobProgress",
            job); // تم تغيير اسم المتغير ليطابق النوع
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
}