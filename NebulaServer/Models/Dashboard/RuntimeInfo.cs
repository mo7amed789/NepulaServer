namespace NebulaServer.Models.Dashboard;

public class RuntimeInfo
{
    public bool PythonReady { get; set; }

    public bool NgrokRunning { get; set; }

    public bool FfmpegReady { get; set; }

    public string PythonVersion { get; set; } = "";

    public string YtDlpVersion { get; set; } = "";
}