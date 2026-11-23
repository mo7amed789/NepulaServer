using System.Windows.Forms;

namespace NebulaServer.Desktop;

public static class DesktopApplication
{
    private static Action? _showWindow;

    public static Task RunAsync(IServiceProvider services, bool startInBackground, CancellationToken stoppingToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var form = new MainForm(services);
                // Create the dispatcher even for a hidden startup. No web surface is involved.
                _ = form.Handle;
                void Post(Action action)
                {
                    try { if (!form.IsDisposed) form.BeginInvoke(action); }
                    catch (InvalidOperationException) { /* Window is shutting down. */ }
                }
                Volatile.Write(ref _showWindow, () => Post(form.ShowMainWindow));
                using var context = new ApplicationContext();
                form.FormClosed += (_, _) => context.ExitThread();
                using var shutdown = stoppingToken.Register(() => Post(form.Shutdown));
                if (!startInBackground && !stoppingToken.IsCancellationRequested) form.Show();
                Application.Run(context);
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { Volatile.Write(ref _showWindow, null); }
        }) { Name = "Tanzeel desktop", IsBackground = false };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static void RequestShowWindow() => Volatile.Read(ref _showWindow)?.Invoke();
}
