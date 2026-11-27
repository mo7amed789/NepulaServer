using System;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using NebulaServer.Models;
using NebulaServer.Services.Jobs;

namespace NebulaServer.Desktop;

internal sealed partial class MainForm
{
    /// <summary>
    /// Desktop counterpart of the API's duplicate guard: the same media is not
    /// queued twice until the user confirms. Returns false to abort the start.
    /// </summary>
    private bool ConfirmDuplicateDownload(JobRequest request)
    {
        if (request.AllowDuplicate)
            return true;

        ExistingDownload? existing;
        try
        {
            existing = _services.GetRequiredService<DuplicateDownloadGuard>().Find(request);
        }
        catch (Exception ex)
        {
            // Never block a download because the history lookup failed.
            Serilog.Log.Warning(ex, "Duplicate check failed for {Url}; allowing the download.", request.Url);
            return true;
        }

        if (existing is null)
            return true;

        var detail = existing.IsActive
            ? $"\"{existing.Title}\" is already downloading on this server ({existing.Progress:0}% done).\n\n" +
              "Starting it again would download the same video twice."
            : $"\"{existing.Title}\" was already downloaded on {existing.CreatedAt.ToLocalTime():g}" +
              (string.IsNullOrWhiteSpace(existing.FileName) ? "." : $" and saved as {existing.FileName}.") +
              "\n\nThe existing file stays in your Downloads list either way.";

        var answer = MessageBox.Show(
            this,
            $"{detail}\n\nDownload it again anyway?",
            existing.IsActive ? "Download already running" : "Already downloaded",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            _status.Text = "Duplicate download canceled; the existing job was left untouched.";
            return false;
        }

        request.AllowDuplicate = true;
        Serilog.Log.Information("User confirmed a repeat download for {Url}.", request.Url);
        return true;
    }
}
