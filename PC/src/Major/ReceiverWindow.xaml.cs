using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Major.Streaming;

namespace Major;

public partial class ReceiverWindow : System.Windows.Controls.UserControl
{
    private ReceiverService? receiver;
    private VideoFrame? latestFrame;
    private readonly object videoSync = new();
    private long videoRevision;
    private WriteableBitmap? bitmap;
    private readonly DispatcherTimer paintTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer expiryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool stopped, closed;
    public bool IsStopped => stopped;
    private Task? stopTask;
    private long frameCount;
    public ReceiverWindow()
    {
        InitializeComponent();
        paintTimer.Tick += (_, _) => RenderLatestFrame();
        expiryTimer.Tick += (_, _) => UpdatePairing();
    }
    private bool initialized;
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        initialized = true;
        try
        {
            receiver = new ReceiverService(Path.Combine(AppContext.BaseDirectory, "tools", "scrcpy"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Major", "receiver"));
            receiver.StatusChanged += message => Dispatch(() => { ReceiverStatus.Text = message; });
            receiver.FrameReceived += frame =>
            {
                lock (videoSync) { if (stopped) return; latestFrame = frame; videoRevision++; }
            };
            receiver.StreamStopped += () =>
            {
                long revision;
                lock (videoSync) { latestFrame = null; revision = ++videoRevision; }
                Dispatch(() =>
                {
                    lock (videoSync) { if (videoRevision != revision) return; }
                    VideoImage.Source = null; bitmap = null;
                    WaitingPanel.Visibility = Visibility.Visible;
                    VideoInfo.Text = "Nessun video in trasmissione";
                });
            };
            receiver.PairingChanged += () => Dispatch(UpdatePairing);
            PcNameText.Text = receiver.PcName;
            var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.Address.ToString()).Distinct().ToArray();
            AddressCombo.ItemsSource = addresses.Length == 0 ? new[] { "Nessun indirizzo LAN" } : addresses;
            AddressCombo.SelectedIndex = 0;
            await receiver.StartAsync();
            UpdatePairing(); paintTimer.Start(); expiryTimer.Start();
        }
        catch (Exception ex) { ReceiverStatus.Text = "Impossibile avviare il ricevitore: " + ex.Message; NewCodeButton.IsEnabled = false; }
    }
    private void Dispatch(Action action)
    {
        if (!closed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { if (!closed) action(); });
    }
    private void UpdatePairing()
    {
        if (receiver is null || stopped) return;
        var groups = receiver.PairingCode.Split('-');
        PairCodeBox.Text = groups.Length == 8 ? string.Join('-', groups.Take(4)) + Environment.NewLine + string.Join('-', groups.Skip(4)) : receiver.PairingCode;
        var seconds = Math.Max(0, (int)(receiver.PairingExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        PairExpiryText.Text = seconds > 0 ? $"Scade tra {seconds / 60}:{seconds % 60:00}" : "Codice scaduto o già usato. Generane uno nuovo.";
    }
    private void RenderLatestFrame()
    {
        VideoFrame? frame;
        lock (videoSync) { frame = latestFrame; latestFrame = null; }
        if (frame is null || stopped) return;
        if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
        {
            bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            VideoImage.Source = bitmap;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Width * 4, 0);
        WaitingPanel.Visibility = Visibility.Collapsed;
        VideoInfo.Text = $"{frame.Width} × {frame.Height} · {++frameCount} fotogrammi visualizzati";
    }
    private void NewCodeClick(object sender, RoutedEventArgs e)
    {
        try { receiver?.RefreshPairingCode(); UpdatePairing(); } catch (Exception ex) { ReceiverStatus.Text = ex.Message; }
    }
    private async void RevokeClick(object sender, RoutedEventArgs e)
    {
        if (receiver is null) return;
        try { await receiver.RevokeAllAsync(); UpdatePairing(); ReceiverStatus.Text = "Associazioni revocate. Usa un nuovo codice per collegare un telefono."; }
        catch (Exception ex) { ReceiverStatus.Text = ex.Message; }
    }
    private async void StopClick(object sender, RoutedEventArgs e)
    {
        try { await StopReceiverAsync(); } catch (Exception ex) { ReceiverStatus.Text = ex.Message; }
    }
    public Task StopReceiverAsync() => stopTask ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        lock (videoSync) { stopped = true; latestFrame = null; videoRevision++; }
        paintTimer.Stop(); expiryTimer.Stop();
        NewCodeButton.IsEnabled = RevokeButton.IsEnabled = StopReceiverButton.IsEnabled = false;
        PairCodeBox.Clear(); PairExpiryText.Text = "Ricevitore spento";
        VideoImage.Source = null; bitmap = null;
        WaitingPanel.Visibility = Visibility.Visible; VideoInfo.Text = "Ricezione interrotta";
        if (receiver is not null) await receiver.DisposeAsync();
        lock (videoSync) latestFrame = null;
        ReceiverStatus.Text = "Ricezione interrotta. Seleziona di nuovo Android Wi-Fi per riattivare la ricezione.";
    }
    public async Task CloseReceiverAsync()
    {
        try { await StopReceiverAsync(); } finally { closed = true; }
    }
    internal Task PrepareForSystemShutdown()
    {
        if (closed) return Task.CompletedTask;
        closed = true;
        lock (videoSync) { stopped = true; latestFrame = null; videoRevision++; }
        paintTimer.Stop(); expiryTimer.Stop();
        return receiver?.DisposeAsync().AsTask() ?? Task.CompletedTask;
    }

}
