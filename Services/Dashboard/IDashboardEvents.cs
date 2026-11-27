using System.Threading.Tasks;
using NebulaServer.Models;

namespace NebulaServer.Services;

public interface IDashboardEvents
{
    Task JobProgress(DownloadJob job);
    Task DownloadProgressChanged(NebulaServer.Models.Jobs.DownloadProgress progress);
}
