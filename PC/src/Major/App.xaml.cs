using System.Windows;
namespace Major;
public partial class App : Application
{
    internal bool SessionIsEnding { get; private set; }
    private Task? sessionCleanup;
    protected override void OnStartup(StartupEventArgs e)
    {
        LegacySettings.Migrate();
        try { ThemeManager.Apply(UserSettings.Load().Theme); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { ThemeManager.Apply("System"); }
        Activated += (_, _) => { foreach (Window window in Windows) ThemeManager.ApplyTitleBar(window); };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        base.OnStartup(e);
    }
    private void SystemPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (SessionIsEnding || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => { if (!SessionIsEnding && !Dispatcher.HasShutdownStarted && ThemeManager.Preference == "System") ThemeManager.Apply("System"); });
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        SessionIsEnding = true;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        try
        {
            // Windows owns this shutdown: stop services without waiting on dispatcher continuations.
            if (MainWindow is MainWindow window)
            {
                sessionCleanup = window.PrepareForSystemShutdown();
                if (!sessionCleanup.Wait(TimeSpan.FromSeconds(3)))
                    _ = sessionCleanup.ContinueWith(task => RecordShutdownError(task.Exception!), TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (Exception ex) { RecordShutdownError(ex); }
        e.Cancel = false;
        base.OnSessionEnding(e);
    }
    internal static void RecordShutdownError(Exception error)
    {
        try
        {
            var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Major");
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "last-shutdown-error.txt"), DateTimeOffset.Now + Environment.NewLine + error);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        if (Environment.GetCommandLineArgs().Contains("--shutdown-test"))
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "shutdown-test-result.json"),
                System.Text.Json.JsonSerializer.Serialize(new { pid = Environment.ProcessId, sessionEnding = SessionIsEnding, cleanupCompleted = sessionCleanup?.IsCompletedSuccessfully == true, exitCode = e.ApplicationExitCode }));
        base.OnExit(e);
    }
}
