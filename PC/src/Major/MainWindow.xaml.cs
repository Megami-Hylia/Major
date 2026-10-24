using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Major.Core;
using Microsoft.Win32;

namespace Major;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, DeviceInfo> activeDevices = new(StringComparer.Ordinal);
    private readonly Queue<string> log = new();
    private readonly DeviceService devices;
    private readonly MirrorService mirror;
    private ReceiverWindow? receiverWindow;
    private AirPlayWindow? airPlayWindow;
    private UserSettings settings = new();
    private bool refreshing, busy, closing, closed;
    private string deviceSignature = "";
    private string lastPollError = "";
    private Task pendingAction = Task.CompletedTask;
    private Task pendingPoll = Task.CompletedTask;
    private Task? systemShutdownTask;
    private bool systemShutdown;

    public MainWindow()
    {
        InitializeComponent();
        var paths = new ToolPaths(AppContext.BaseDirectory);
        devices = new DeviceService(paths);
        mirror = new MirrorService(paths);
        mirror.Log += (serial, message) => Dispatch(() => AddLog(serial + " · " + message));
        mirror.Exited += (serial, code) => Dispatch(() =>
        {
            activeDevices.Remove(serial);
            SetStatus(code == 0 ? "Sessione terminata." : $"La sessione si è interrotta (codice {code}). Consulta il registro e riprova.", code != 0);
            deviceSignature = "";
            _ = RefreshDevicesAsync();
            UpdateSelection();
        });
        timer.Tick += async (_, _) => await RefreshDevicesAsync();
    }

    private void Dispatch(Action action)
    {
        if (closed || closing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => { if (!closed && !closing) action(); });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { settings = UserSettings.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { AddLog("Impostazioni non leggibili, uso i valori predefiniti: " + ex.Message); }
        LoadControls();
        LoadPreferences();
        ApplySidebarLayout();
        if (settings.StartMaximized) WindowState = WindowState.Maximized;
        await RefreshDevicesAsync(true);
        if (closing || closed) return;
        timer.Start();
        if (Environment.GetCommandLineArgs().Contains("--smoke-test"))
        {
            await RunShellSmokeCheckAsync();
            return;
        }
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--shutdown-test"))
        {
            AirPlayClick(this, new RoutedEventArgs());
            await Task.Delay(2500);
            if (closing || closed) return;
            ReceiveClick(this, new RoutedEventArgs());
            await Task.Delay(250);
            if (closing || closed) return;
            // Only the native-message test needs WPF's hidden session-message target.
            var parkingWindow = typeof(Application).GetField("_parkingHwnd", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Application.Current)!;
            var applicationHandle = (IntPtr)parkingWindow.GetType().GetProperty("Handle")!.GetValue(parkingWindow)!;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "shutdown-test-ready.json"),
                System.Text.Json.JsonSerializer.Serialize(new { pid = Environment.ProcessId, hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle.ToInt64(), applicationHwnd = applicationHandle.ToInt64() }));
            return;
        }
        if (args.Contains("--airplay")) AirPlayClick(this, new RoutedEventArgs());
        else if (args.Contains("--receive")) ReceiveClick(this, new RoutedEventArgs());
        else if (args.Contains("--settings")) SettingsClick(this, new RoutedEventArgs());
        else if (settings.StartPage == "AirPlay") AirPlayClick(this, new RoutedEventArgs());
        else if (settings.StartPage == "Android") ReceiveClick(this, new RoutedEventArgs());
    }

    private async Task RunShellSmokeCheckAsync()
    {
        var checks = new List<string>();
        try
        {
            AirPlayClick(this, new RoutedEventArgs());
            await Task.Delay(2500);
            if (Application.Current.Windows.Count != 1) throw new InvalidOperationException("Più finestre principali");
            checks.Add("PASS AirPlay mounted in single main window");
            var originalSidebar = settings.SidebarCollapsed;
            settings.SidebarCollapsed = false; ApplySidebarLayout(); UpdateLayout();
            var navigationButtons = new[] { AdbNavigationButton, AppleNavigationButton, AndroidNavigationButton, WifiNavigationButton, FolderNavigationButton, SettingsNavigationButton };
            var expandedPositions = navigationButtons.Select(button => button.TranslatePoint(new Point(), this).Y).ToArray();
            settings.SidebarCollapsed = true; ApplySidebarLayout();
            UpdateLayout();
            if (NavigationColumn.Width.Value != 64 || NavigationPanel.Visibility != Visibility.Visible || RefreshButton.Visibility != Visibility.Hidden) throw new InvalidOperationException("Compact sidebar missing");
            for (var index = 0; index < navigationButtons.Length; index++)
                if (!navigationButtons[index].IsVisible || Math.Abs(navigationButtons[index].TranslatePoint(new Point(), this).Y - expandedPositions[index]) > 0.5)
                    throw new InvalidOperationException("Compact sidebar moved or removed a navigation action");
            SetViewerFullscreen(true); SetViewerFullscreen(false);
            if (NavigationColumn.Width.Value != 64) throw new InvalidOperationException("Fullscreen lost compact sidebar");
            settings.SidebarCollapsed = false; ApplySidebarLayout();
            if (NavigationColumn.Width.Value != 228 || NavigationPanel.Visibility != Visibility.Visible) throw new InvalidOperationException("Expanded sidebar missing");
            settings.SidebarCollapsed = originalSidebar; ApplySidebarLayout();
            checks.Add("PASS compact sidebar, expansion and fullscreen restoration");
            SetViewerFullscreen(true); SetViewerFullscreen(false);
            checks.Add("PASS shell fullscreen and restoration");
            ReceiveClick(this, new RoutedEventArgs());
            await Task.Delay(1000);
            if (receiverWindow is null || !receiverWindow.IsVisible) throw new InvalidOperationException("Android view missing");
            await receiverWindow.StopReceiverAsync();
            ReceiveClick(this, new RoutedEventArgs());
            await Task.Delay(1000);
            if (receiverWindow?.IsStopped != false) throw new InvalidOperationException("Android restart failed");
            checks.Add("PASS Android view stop and recreation");
            AdbClick(this, new RoutedEventArgs());
            AirPlayClick(this, new RoutedEventArgs());
            if (Application.Current.Windows.Count != 1) throw new InvalidOperationException("Navigation opened a window");
            checks.Add("PASS USB / Android / AirPlay navigation");
            var originalTheme = settings.Theme;
            try
            {
                SettingsClick(this, new RoutedEventArgs());
                foreach (var theme in new[] { "Dark", "Light" })
                {
                    SelectPreference(ThemeCombo, theme);
                    if (ThemeManager.IsDark != (theme == "Dark") || UserSettings.Load().Theme != theme)
                        throw new InvalidOperationException("Theme application or persistence failed");
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    UpdateLayout();
                    var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ShellGrid.ActualWidth, (int)ShellGrid.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    image.Render(ShellGrid);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    using var file = File.Create(Path.Combine(AppContext.BaseDirectory, "settings-" + theme.ToLowerInvariant() + ".png"));
                    encoder.Save(file);
                    if (SettingsPanel.Visibility != Visibility.Visible || Application.Current.Windows.Count != 1)
                        throw new InvalidOperationException("Settings navigation failed");
                    checks.Add("PASS " + theme + " theme live application, persistence and rendered settings");
                }
                ThemeManager.Apply("System");
                if (ThemeManager.Preference != "System") throw new InvalidOperationException("System theme selection failed");
                checks.Add("PASS Windows theme preference");
            }
            finally
            {
                settings.Theme = originalTheme;
                ThemeManager.Apply(originalTheme);
                LoadPreferences(); SaveSettings();
            }
            await airPlayWindow!.CloseReceiverAsync();
            await receiverWindow!.CloseReceiverAsync();
            checks.Add("PASS receiver cleanup");
        }
        catch (Exception ex) { checks.Add("FAIL " + ex); Environment.ExitCode = 1; }
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "shell-smoke-test.txt"), checks);
        Close();
    }

    private async Task RefreshDevicesAsync(bool manual = false)
    {
        if (refreshing || closing) return;
        refreshing = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var query = devices.GetDevicesAsync(lifetime.Token);
            pendingPoll = query;
            var found = (await query).ToList();
            foreach (var active in activeDevices.Values)
                if (mirror.IsRunning(active.Serial) && found.All(d => d.Serial != active.Serial))
                    found.Add(active with { State = "offline" });
            var rows = found.Select(d => new DeviceRow(d, mirror.IsRunning(d.Serial))).ToList();
            var signature = string.Join("\n", rows.Select(d => $"{d.Serial}|{d.Device.State}|{d.Device.Model}|{d.Active}"));
            if (signature != deviceSignature)
            {
                var selected = SelectedDevice?.Serial;
                DevicesList.ItemsSource = rows;
                DevicesList.SelectedItem = rows.FirstOrDefault(d => d.Serial == selected) ?? rows.FirstOrDefault(d => d.Device.IsAuthorized) ?? rows.FirstOrDefault();
                deviceSignature = signature;
            }
            EmptyDevices.Visibility = found.Count == 0 && !settings.SidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;
            DeviceCount.Text = $"{found.Count} dispositivi · {activeDevices.Count(d => mirror.IsRunning(d.Key))} sessioni";
            if (manual) SetStatus(found.Count == 0 ? "Nessun telefono rilevato. Collega un cavo dati o apri la procedura Wi-Fi." : "Elenco dispositivi aggiornato.");
            lastPollError = "";
            UpdateSelection();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DeviceCount.Text = "Rilevamento non disponibile";
            if (lastPollError != ex.Message || manual) { SetStatus(ex.Message, true); AddLog(ex.Message); }
            lastPollError = ex.Message;
        }
        finally { refreshing = false; RefreshButton.IsEnabled = !closing; }
    }

    private DeviceInfo? SelectedDevice => (DevicesList.SelectedItem as DeviceRow)?.Device;
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (StartButton is null) return;
        var device = SelectedDevice;
        var running = device is not null && mirror.IsRunning(device.Serial);
        DeviceTitle.Text = device?.Model ?? "Connessione Android";
        DeviceSubtitle.Text = device is null ? "Collega un telefono per iniziare." : $"{device.Connection} · {device.Serial}";
        StartButton.IsEnabled = !busy && !closing && device?.IsAuthorized == true && !running;
        StopButton.IsEnabled = !closing && running;
        ScreenshotButton.IsEnabled = !busy && !closing && device?.IsAuthorized == true;
        RecordCheck.IsEnabled = !running && !busy;
        if (device is null)
        {
            StateTitle.Text = "In attesa del telefono";
            StateDescription.Text = "Abilita Debug USB nelle opzioni sviluppatore di Android, collega un cavo dati e autorizza questo computer sul telefono.";
        }
        else if (device.State == "unauthorized")
        {
            StateTitle.Text = "Autorizza questo computer";
            StateDescription.Text = "Sblocca il telefono e accetta la richiesta di debug USB con l'impronta RSA. Il rilevamento si aggiorna automaticamente.";
        }
        else if (running)
        {
            StateTitle.Text = device.IsAuthorized ? "Sessione avviata" : "In attesa di riconnessione";
            StateDescription.Text = device.IsAuthorized ? "Usa la finestra del telefono per vedere e controllare Android. Il registro riporta avvio, errori e riconnessioni." : "Ricollega lo stesso telefono. Il motore prova a riconnettersi fino a 10 volte; premi Termina sessione per interrompere.";
        }
        else if (!device.IsAuthorized)
        {
            StateTitle.Text = "Dispositivo non pronto";
            StateDescription.Text = $"ADB segnala: {device.State}. Sblocca il telefono, controlla il cavo dati e riprova. Su Windows può essere necessario il driver USB del produttore.";
        }
        else
        {
            StateTitle.Text = "Pronto per il mirroring";
            StateDescription.Text = "Il telefono è autorizzato. Scegli la qualità e avvia la sessione; puoi aprire più dispositivi in finestre separate.";
        }
    }

    private async Task RunAction(Func<Task> action)
    {
        if (busy || closing) return;
        busy = true; UpdateSelection();
        try { pendingAction = action(); await pendingAction; }
        catch (OperationCanceledException) { if (!closing) SetStatus("Operazione annullata."); }
        catch (Exception ex) { SetStatus(ex.Message, true); AddLog(ex.Message); }
        finally { busy = false; UpdateSelection(); }
    }

    private async void StartClick(object sender, RoutedEventArgs e)
    {
        var device = SelectedDevice;
        if (device is null) return;
        await RunAction(async () =>
        {
            SaveSettings();
            var options = ReadOptions();
            if (RecordCheck.IsChecked == true) options.RecordPath = NewCapturePath("mp4");
            await mirror.StartAsync(device, options, lifetime.Token);
            activeDevices[device.Serial] = device;
            SetStatus(options.RecordPath is null ? "Avvio richiesto. Attendi la finestra del telefono." : "Registrazione richiesta: " + options.RecordPath);
            deviceSignature = "";
            await RefreshDevicesAsync();
        });
    }

    private async void StopClick(object sender, RoutedEventArgs e)
    {
        var device = SelectedDevice;
        if (device is null) return;
        try { await mirror.StopAsync(device.Serial); SetStatus("Sessione terminata. Controlla le eventuali acquisizioni nella cartella scelta."); }
        catch (Exception ex) { SetStatus(ex.Message, true); AddLog(ex.Message); }
        finally { UpdateSelection(); }
    }

    private async void ScreenshotClick(object sender, RoutedEventArgs e)
    {
        var device = SelectedDevice;
        if (device is null) return;
        await RunAction(async () => { var path = NewCapturePath("png"); await devices.ScreenshotAsync(device.Serial, path, lifetime.Token); SetStatus("Screenshot salvato: " + path); });
    }

    private string NewCapturePath(string extension)
    {
        Directory.CreateDirectory(settings.OutputDirectory);
        return Path.Combine(settings.OutputDirectory, "Major-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6] + "." + extension);
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshDevicesAsync(true);
    private void WifiClick(object sender, RoutedEventArgs e)
    {
        var dialog = new WifiWindow(devices) { Owner = this };
        dialog.ShowDialog();
        _ = RefreshDevicesAsync(true);
    }
    private void ShowReceiver(System.Windows.UIElement? view)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        AdbPanel.Visibility = view is null ? Visibility.Visible : Visibility.Collapsed;
        ReceiverPanels.Visibility = view is null ? Visibility.Collapsed : Visibility.Visible;
        foreach (System.Windows.UIElement child in ReceiverPanels.Children) child.Visibility = child == view ? Visibility.Visible : Visibility.Collapsed;
    }
    private bool preferencesReady;
    private void LoadPreferences()
    {
        preferencesReady = false;
        SelectPreference(ThemeCombo, settings.Theme);
        SelectPreference(StartPageCombo, settings.StartPage);
        CompactSidebarCheck.IsChecked = settings.SidebarCollapsed;
        MaximizedCheck.IsChecked = settings.StartMaximized;
        SettingsOutputPath.Text = settings.OutputDirectory;
        preferencesReady = true;
    }
    private static void SelectPreference(ComboBox combo, string tag)
    {
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == tag);
    }
    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        ShowReceiver(null);
        AdbPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
    }
    private void PreferencesChanged(object sender, RoutedEventArgs e)
    {
        if (!preferencesReady) return;
        settings.Theme = (string)((ComboBoxItem)ThemeCombo.SelectedItem).Tag;
        settings.StartPage = (string)((ComboBoxItem)StartPageCombo.SelectedItem).Tag;
        settings.SidebarCollapsed = CompactSidebarCheck.IsChecked == true;
        settings.StartMaximized = MaximizedCheck.IsChecked == true;
        ThemeManager.Apply(settings.Theme);
        ApplySidebarLayout();
        try { SaveSettings(); PreferencesStatus.Text = "Preferenze salvate."; PreferencesStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); }
        catch (Exception ex) { PreferencesStatus.Text = "Salvataggio non riuscito: " + ex.Message; PreferencesStatus.SetResourceReference(TextBlock.ForegroundProperty, "ErrorInk"); }
    }
    private void OpenDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Major");
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) { PreferencesStatus.Text = ex.Message; }
    }
    private void AdbClick(object sender, RoutedEventArgs e) => ShowReceiver(null);
    private async void ReceiveClick(object sender, RoutedEventArgs e)
    {
        if (receiverWindow?.IsStopped == true)
        {
            await receiverWindow.CloseReceiverAsync();
            ReceiverPanels.Children.Remove(receiverWindow); receiverWindow = null;
        }
        if (receiverWindow is null) { receiverWindow = new ReceiverWindow(); ReceiverPanels.Children.Add(receiverWindow); }
        ShowReceiver(receiverWindow);
    }
    private void AirPlayClick(object sender, RoutedEventArgs e)
    {
        if (airPlayWindow is null) { airPlayWindow = new AirPlayWindow(); ReceiverPanels.Children.Add(airPlayWindow); }
        ShowReceiver(airPlayWindow);
    }
    private WindowState previousWindowState;
    private bool viewerFullscreen;
    private void SidebarToggleClick(object sender, RoutedEventArgs e)
    {
        settings.SidebarCollapsed = !settings.SidebarCollapsed;
        var wasReady = preferencesReady; preferencesReady = false;
        CompactSidebarCheck.IsChecked = settings.SidebarCollapsed;
        preferencesReady = wasReady;
        ApplySidebarLayout();
        try { SaveSettings(); } catch (Exception ex) { SetStatus(ex.Message, true); }
    }
    private void ApplySidebarLayout()
    {
        var compact = settings.SidebarCollapsed;
        NavigationPanel.Visibility = viewerFullscreen ? Visibility.Collapsed : Visibility.Visible;
        NavigationPanel.Margin = new Thickness(compact ? 8 : 16, 8, compact ? 8 : 16, 24);
        RefreshButton.Visibility = compact ? Visibility.Hidden : Visibility.Visible;
        DevicesHeading.Visibility = compact ? Visibility.Hidden : Visibility.Visible;
        EmptyDevices.Visibility = !compact && DevicesList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { AdbNavigationButton, AppleNavigationButton, AndroidNavigationButton, WifiNavigationButton, FolderNavigationButton, SettingsNavigationButton })
        {
            button.Content = compact ? button.Tag : button == WifiNavigationButton ? "Collega via _Wi-Fi (ADB)" : button.ToolTip;
            button.Padding = compact ? new Thickness(0) : new Thickness(14, 8, 14, 8);
            button.FontSize = compact ? 10 : 14;
            System.Windows.Automation.AutomationProperties.SetName(button, (string)button.ToolTip);
        }
        NavigationColumn.Width = new GridLength(viewerFullscreen ? 0 : compact ? 64 : 228);
        BrandName.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BrandPanel.Margin = new Thickness(compact ? 18 : 24, 0, 0, 0);
        SidebarToggle.Content = compact ? "›" : "‹";
        var label = compact ? "Espandi barra laterale" : "Riduci barra laterale";
        SidebarToggle.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(SidebarToggle, label);
    }
    public void SetViewerFullscreen(bool fullscreen)
    {
        viewerFullscreen = fullscreen;
        if (fullscreen) { previousWindowState = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized; }
        else { WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize; WindowState = previousWindowState; }
        ApplySidebarLayout();
        HeaderRow.Height = new GridLength(fullscreen ? 0 : 64);
    }
    private void HelpClick(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.ShowDialog();
    private void ChooseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Cartella per screenshot e registrazioni", InitialDirectory = Directory.Exists(settings.OutputDirectory) ? settings.OutputDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dialog.ShowDialog(this) != true) return;
        settings.OutputDirectory = dialog.FolderName;
        OutputPath.Text = settings.OutputDirectory;
        SettingsOutputPath.Text = settings.OutputDirectory;
        try { SaveSettings(); SetStatus("Cartella acquisizioni aggiornata."); } catch (Exception ex) { SetStatus(ex.Message, true); }
    }
    private void OpenFolderClick(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(settings.OutputDirectory); Process.Start(new ProcessStartInfo(settings.OutputDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try { SaveSettings(); SetStatus("Impostazioni salvate. Si applicano alla prossima sessione."); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private static int ComboValue(ComboBox combo) => int.Parse((string)((ComboBoxItem)combo.SelectedItem).Tag, System.Globalization.CultureInfo.InvariantCulture);
    private MirrorOptions ReadOptions() => new()
    {
        MaxSize = ComboValue(SizeCombo), BitRateMbps = ComboValue(BitrateCombo), MaxFps = ComboValue(FpsCombo),
        StayAwake = AwakeCheck.IsChecked == true, TurnScreenOff = ScreenOffCheck.IsChecked == true,
        Fullscreen = FullscreenCheck.IsChecked == true, Reconnect = ReconnectCheck.IsChecked == true,
        UseUhidKeyboard = UhidCheck.IsChecked == true
    };
    private static void SelectValue(ComboBox combo, int value)
    {
        foreach (ComboBoxItem item in combo.Items) if ((string)item.Tag == value.ToString(System.Globalization.CultureInfo.InvariantCulture)) { combo.SelectedItem = item; return; }
    }
    private void LoadControls()
    {
        var options = settings.Options;
        SelectValue(SizeCombo, options.MaxSize); SelectValue(BitrateCombo, options.BitRateMbps); SelectValue(FpsCombo, options.MaxFps);
        AwakeCheck.IsChecked = options.StayAwake; ScreenOffCheck.IsChecked = options.TurnScreenOff;
        FullscreenCheck.IsChecked = options.Fullscreen; ReconnectCheck.IsChecked = options.Reconnect; UhidCheck.IsChecked = options.UseUhidKeyboard;
        OutputPath.Text = settings.OutputDirectory;
    }
    private void SaveSettings() { settings.Options = ReadOptions(); settings.Save(); }
    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorInk" : "Ink");
    }
    private void AddLog(string text)
    {
        // Bound both row count and row length; device-provided diagnostics are data.
        log.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + text[..Math.Min(text.Length, 1800)]);
        while (log.Count > 160) log.Dequeue();
        LogBox.Text = string.Join(Environment.NewLine, log);
        LogBox.ScrollToEnd();
    }
    internal Task PrepareForSystemShutdown()
    {
        if (systemShutdownTask is not null) return systemShutdownTask;
        if (closed) return Task.CompletedTask;
        systemShutdown = closing = closed = true;
        timer.Stop();
        try { SaveSettings(); } catch (Exception ex) { App.RecordShutdownError(ex); }
        try { lifetime.Cancel(); } catch (Exception ex) { App.RecordShutdownError(ex); }
        systemShutdownTask = Task.WhenAll(
            airPlayWindow?.PrepareForSystemShutdown() ?? Task.CompletedTask,
            receiverWindow?.PrepareForSystemShutdown() ?? Task.CompletedTask,
            mirror.StopAllAsync(TimeSpan.FromMilliseconds(1200)));
        return systemShutdownTask;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closed || systemShutdown || Dispatcher.HasShutdownStarted) return;
        e.Cancel = true;
        if (closing) return;
        closing = true; timer.Stop(); lifetime.Cancel(); UpdateSelection();
        SetStatus("Chiusura delle sessioni e finalizzazione delle registrazioni…");
        try { SaveSettings(); } catch { /* Closing must still release owned mirroring sessions. */ }
        try
        {
            if (airPlayWindow is not null)
            {
                try { await airPlayWindow.CloseReceiverAsync(); } catch (Exception ex) { AddLog(ex.Message); }
            }
            if (receiverWindow is not null)
            {
                try { await receiverWindow.CloseReceiverAsync(); } catch (Exception ex) { AddLog(ex.Message); }
            }
            try { await Task.WhenAll(pendingAction, pendingPoll); } catch { /* Cancelled operations have already cleaned up their child processes. */ }
            await mirror.StopAllAsync();
        }
        catch (Exception ex) { App.RecordShutdownError(ex); }
        finally
        {
            closed = true; lifetime.Dispose();
            if (!systemShutdown && !Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(() => { if (!systemShutdown && !Dispatcher.HasShutdownStarted && IsLoaded) Close(); });
        }
    }

    private sealed record DeviceRow(DeviceInfo Device, bool Active)
    {
        public string Serial => Device.Serial;
        public string Model => Device.Model;
        public string StatusLabel => Active ? (Device.IsAuthorized ? "Sessione avviata · " : "Riconnessione · ") + Device.Connection : Device.State switch
        { "device" => "Pronto · " + Device.Connection, "unauthorized" => "Autorizzazione richiesta", "offline" => "Non in linea", _ => Device.State };
    }
}
