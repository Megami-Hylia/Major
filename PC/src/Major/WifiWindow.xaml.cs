using System.Windows;
using Major.Core;

namespace Major;

public partial class WifiWindow : Window
{
    private readonly DeviceService devices;
    private readonly CancellationTokenSource lifetime = new();
    private bool closed;
    public WifiWindow(DeviceService devices) { this.devices = devices; InitializeComponent(); }
    private async Task RunAction(Func<Task<string>> action)
    {
        PairButton.IsEnabled = ConnectButton.IsEnabled = false;
        ResultText.Text = "Operazione in corso…";
        try { var result = await action(); if (!closed) ResultText.Text = result; }
        catch (OperationCanceledException) { if (!closed) ResultText.Text = "Operazione annullata."; }
        catch (Exception ex)
        {
            var message = ex.Message.Split('\r', '\n')[0];
            var suffix = message.LastIndexOf(" (", StringComparison.Ordinal);
            if (ex is ArgumentException { ParamName: not null } && suffix >= 0) message = message[..suffix];
            if (!closed) ResultText.Text = message;
        }
        finally { if (!closed) PairButton.IsEnabled = ConnectButton.IsEnabled = true; }
    }
    private async void PairClick(object sender, RoutedEventArgs e)
    {
        var code = PairCode.Password;
        PairCode.Clear();
        await RunAction(async () =>
        {
            await devices.PairAsync(PairEndpoint.Text.Trim(), code, lifetime.Token);
            return "Associazione riuscita. Ora usa l'indirizzo della schermata principale di Debug wireless per connetterti.";
        });
    }
    private async void ConnectClick(object sender, RoutedEventArgs e) => await RunAction(async () =>
    {
        await devices.ConnectAsync(ConnectEndpoint.Text.Trim(), lifetime.Token);
        return "Connessione riuscita. Chiudi questa finestra e seleziona il telefono nell'elenco dispositivi.";
    });
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnClosed(object? sender, EventArgs e) { closed = true; lifetime.Cancel(); }
}
