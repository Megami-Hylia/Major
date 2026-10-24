using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Major.AirPlay;

namespace Major;

public partial class AirPlayWindow : System.Windows.Controls.UserControl
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Queue<string> diagnostics = new();
    private AirPlayService? receiver;
    private Task? stopTask;
    private bool busy, closing, closed, fullscreen;
    private static string PreferencesPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Major", "airplay-ui.json");

    public AirPlayWindow()
    {
        InitializeComponent();
        ReceiverNameBox.Text = "Major - " + Environment.MachineName;
        try
        {
            if (File.Exists(PreferencesPath))
            {
                var preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(PreferencesPath));
                if (!string.IsNullOrWhiteSpace(preferences?.Name) && preferences.Name.Length <= 50) ReceiverNameBox.Text = preferences.Name;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { Log("Preferenze non caricate: " + ex.Message); }
    }

    private bool initialized;
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        initialized = true;
        ComponentDispatcher.ThreadFilterMessage += FilterNativeKey;
        try
        {
        receiver = new AirPlayService(Path.Combine(AppContext.BaseDirectory, "tools", "airplay"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Major", "airplay-session"));
        receiver.StatusChanged += status => Dispatch(() => UpdateStatus(status));
        receiver.PinChanged += pin => Dispatch(() =>
        {
            PinText.Text = pin ?? "—";
            PinCompact.Text = pin is null ? "" : "Codice: " + pin;
            PinToolbar.Visibility = pin is null ? Visibility.Collapsed : Visibility.Visible;
            PinHint.Text = pin is null ? "Il codice appare quando iPhone o iPad richiede l'accesso." : "Inserisci questo codice sul dispositivo che vuoi collegare.";
        });
        receiver.DiagnosticLog += message => Dispatch(() => Log(message));
        await StartReceiverAsync();
        }
        catch (Exception ex) { ReceiverStatus.Text = "Ricevitore non disponibile: " + ex.Message; Log(ex.Message); DiagnosticsPanel.IsExpanded = true; }
    }

    private void Dispatch(Action action)
    {
        if (!closed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { if (!closed) action(); });
    }
    private void Log(string message)
    {
        // The service strips PINs; bound device-controlled log volume in the UI as well.
        diagnostics.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + message[..Math.Min(message.Length, 1200)]);
        while (diagnostics.Count > 100) diagnostics.Dequeue();
        DiagnosticsBox.Text = string.Join(Environment.NewLine, diagnostics);
        DiagnosticsBox.ScrollToEnd();
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(PreferencesPath)!, "airplay-session");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "diagnostics.txt"), DiagnosticsBox.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void UpdateStatus(AirPlayStatus status)
    {
        ReceiverStatus.Text = status.Message;
        WriteStatusSnapshot(status);
        VideoCaption.Text = status.State switch
        {
            AirPlayState.Ready => $"Scegli «{ReceiverNameBox.Text.Trim()}» in Duplica schermo",
            AirPlayState.Authenticating => "Autorizza il dispositivo con il codice sul PC",
            AirPlayState.Connected => "Dispositivo collegato · in attesa del video",
            AirPlayState.Streaming => "Schermo Apple in ricezione · audio dagli altoparlanti del PC",
            AirPlayState.Starting => "Avvio e annuncio sulla rete locale…",
            AirPlayState.Stopping => "Interruzione della ricezione…",
            AirPlayState.Faulted => "Ricezione non disponibile · apri la diagnostica",
            _ => "Ricevitore spento"
        };
        if (status.State == AirPlayState.Faulted) DiagnosticsPanel.IsExpanded = true;
        UpdateControls();
    }
    private void WriteStatusSnapshot(AirPlayStatus status)
    {
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(PreferencesPath)!, "airplay-session");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "status.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new
            {
                state = status.State.ToString(), message = status.Message,
                name = ReceiverNameBox.Text.Trim(), processId = Environment.ProcessId,
                pinVisible = receiver?.CurrentPin is not null,
                observedAt = DateTimeOffset.UtcNow
            }));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* UI remains the primary status surface. */ }
    }
    private void UpdateControls()
    {
        var running = receiver?.IsRunning == true;
        StartButton.IsEnabled = !busy && !closing && !running;
        ReceiverNameBox.IsEnabled = !busy && !closing && !running;
        StopButton.IsEnabled = !closing && (busy || running);
    }
    private async Task StartReceiverAsync()
    {
        if (busy || closing || receiver is null || receiver.IsRunning) return;
        busy = true; stopTask = null; UpdateControls();
        try
        {
            if (VideoHost.VideoHandle == IntPtr.Zero) throw new InvalidOperationException("Il viewer video non è pronto. Riapri questa finestra.");
            var name = ReceiverNameBox.Text.Trim();
            await receiver.StartAsync(VideoHost.VideoHandle, name, lifetime.Token);
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
            var pending = PreferencesPath + ".tmp";
            File.WriteAllText(pending, JsonSerializer.Serialize(new Preferences(name)));
            File.Move(pending, PreferencesPath, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ReceiverStatus.Text = ex.Message;
            VideoCaption.Text = "Avvio non riuscito";
            Log(ex.Message); DiagnosticsPanel.IsExpanded = true;
        }
        finally { busy = false; UpdateControls(); }
    }
    private async void StartClick(object sender, RoutedEventArgs e) => await StartReceiverAsync();
    private async void StopClick(object sender, RoutedEventArgs e)
    {
        try { await StopReceiverAsync(); } catch (Exception ex) { ReceiverStatus.Text = ex.Message; Log(ex.Message); }
    }
    public Task StopReceiverAsync() => stopTask ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        if (receiver is not null) await receiver.StopAsync();
        VideoHost.ClearVideo();
        PinText.Text = "—";
        PinCompact.Text = ""; PinToolbar.Visibility = Visibility.Collapsed;
        VideoCaption.Text = "Ricevitore spento";
        WriteStatusSnapshot(new AirPlayStatus(AirPlayState.Stopped, "Ricevitore AirPlay spento."));
        UpdateControls();
    }
    private void FullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat) return;
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && fullscreen) { ToggleFullscreen(); e.Handled = true; }
    }
    private void FilterNativeKey(ref MSG message, ref bool handled)
    {
        if (!IsVisible || Window.GetWindow(this)?.IsActive != true || message.message != 0x100 || (message.lParam.ToInt64() & (1L << 30)) != 0) return;
        var key = message.wParam.ToInt32();
        if (key == 0x7A || (key == 0x1B && fullscreen)) { ToggleFullscreen(); handled = true; }
    }
    private bool detailsCollapsed;
    private void DetailsToggleClick(object sender, RoutedEventArgs e)
    {
        detailsCollapsed = !detailsCollapsed;
        ApplyDetailsLayout();
    }
    private void ApplyDetailsLayout()
    {
        var hidden = fullscreen || detailsCollapsed;
        Sidebar.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = new GridLength(hidden ? 0 : 300);
        GutterColumn.Width = new GridLength(hidden ? 0 : 24);
        DetailsToggle.Content = detailsCollapsed ? "Mostra dettagli" : "Nascondi dettagli";
        DetailsToggle.IsEnabled = !fullscreen;
    }
    private void ToggleFullscreen()
    {
        fullscreen = !fullscreen;
        ((MainWindow)Window.GetWindow(this)).SetViewerFullscreen(fullscreen);
        TitlePanel.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        ApplyDetailsLayout();
        PageGrid.Margin = new Thickness(fullscreen ? 8 : 24);
        DiagnosticsPanel.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        FullscreenButton.Content = fullscreen ? "Esci · Esc / F11" : "Schermo intero · F11";
    }
    public async Task CloseReceiverAsync()
    {
        if (closed) return;
        closing = true; lifetime.Cancel(); UpdateControls();
        try { await StopReceiverAsync(); if (receiver is not null) await receiver.DisposeAsync(); }
        finally { ComponentDispatcher.ThreadFilterMessage -= FilterNativeKey; lifetime.Dispose(); closed = true; }
    }
    internal Task PrepareForSystemShutdown()
    {
        if (closed) return Task.CompletedTask;
        closing = closed = true;
        ComponentDispatcher.ThreadFilterMessage -= FilterNativeKey;
        lifetime.Cancel();
        return receiver?.DisposeAsync().AsTask() ?? Task.CompletedTask;
    }

    private sealed record Preferences(string Name);
}
