using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Major.AirPlay;

public sealed class AirPlayService : IAsyncDisposable
{
    private readonly string toolsDirectory, sessionDirectory;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object sync = new();
    private Session? session;
    private AirPlayState state;
    private string? currentPin;
    private bool disposed;
    public AirPlayState State { get { lock (sync) return state; } }
    public bool IsRunning { get { lock (sync) return session is not null; } }
    public string? CurrentPin { get { lock (sync) return currentPin; } }
    public event Action<AirPlayStatus>? StatusChanged;
    public event Action<string>? DiagnosticLog;
    public event Action<string?>? PinChanged;

    public AirPlayService(string toolsDirectory, string sessionDirectory)
    {
        this.toolsDirectory = Path.GetFullPath(toolsDirectory);
        this.sessionDirectory = Path.GetFullPath(sessionDirectory);
    }

    public async Task StartAsync(IntPtr videoWindowHandle, string receiverName, CancellationToken cancellationToken = default)
    {
        ValidateWindow(videoWindowHandle);
        ValidateName(receiverName);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        Session? run = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (sync) if (session is not null) throw new InvalidOperationException("Il ricevitore AirPlay è già attivo.");
            var executable = Path.Combine(toolsDirectory, "uxplay.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Il componente AirPlay Windows manca. Ripristina la cartella tools/airplay della distribuzione.", executable);
            Directory.CreateDirectory(sessionDirectory);
            var config = Path.Combine(sessionDirectory, "major-empty.uxplayrc");
            File.WriteAllText(config, "# Configurazione intenzionalmente vuota, gestita da Major.\n", new UTF8Encoding(false));
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var start = CreateStartInfo(executable, config, videoWindowHandle, receiverName, nonce);
            var process = new Process { StartInfo = start };
            run = new Session(process, WindowsJob.Create(), nonce);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                session = run;
            }
            SetStatus(AirPlayState.Starting, "Avvio del ricevitore AirPlay…");
            if (!process.Start()) throw new IOException("Il componente AirPlay non si è avviato.");
            run.Started = true;
            run.Job.Assign(process.SafeHandle);
            run.Output = PumpAsync(run, process.StandardOutput, true);
            run.Errors = PumpAsync(run, process.StandardError, false);
            _ = MonitorAsync(run);
            process.StandardInput.NewLine = "\n";
            await process.StandardInput.WriteLineAsync("START").ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.StopRequested.Token);
            await run.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20), linked.Token).ConfigureAwait(false);
            run.LifetimeRegistration = cancellationToken.Register(() => { _ = StopAsync(); });
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            if (run is not null)
            {
                await CleanupAsync(run).ConfigureAwait(false);
                bool stopped = ex is OperationCanceledException;
                SetStatus(stopped ? AirPlayState.Stopped : AirPlayState.Faulted,
                    stopped ? "Ricevitore AirPlay spento." : "Avvio AirPlay non riuscito: " + SafeError(ex));
            }
            throw;
        }
        finally { lifecycle.Release(); }
    }

    internal ProcessStartInfo CreateStartInfo(string executable, string config, IntPtr handle, string name, string nonce)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = sessionDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        string[] arguments = ["-rc", config, "-n", name, "-nh", "-wid", unchecked((ulong)handle.ToInt64()).ToString(CultureInfo.InvariantCulture),
            "-pw", "-p", "tcp", "7000", "-p", "udp", "6000", "-vs", "d3d11videosink", "-as", "wasapisink", "-nofreeze"];
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PATH"] = Path.Combine(toolsDirectory, "bin") + Path.PathSeparator + toolsDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["GST_PLUGIN_PATH"] = Path.Combine(toolsDirectory, "lib", "gstreamer-1.0");
        start.Environment["GST_PLUGIN_SYSTEM_PATH"] = Path.Combine(toolsDirectory, "lib", "gstreamer-1.0");
        start.Environment["GST_PLUGIN_SCANNER"] = Path.Combine(toolsDirectory, "libexec", "gstreamer-1.0", "gst-plugin-scanner.exe");
        start.Environment["GST_REGISTRY"] = Path.Combine(sessionDirectory, "gstreamer-registry.bin");
        start.Environment["GST_PLUGIN_PATH_1_0"] = start.Environment["GST_PLUGIN_PATH"];
        start.Environment["GST_PLUGIN_SYSTEM_PATH_1_0"] = start.Environment["GST_PLUGIN_SYSTEM_PATH"];
        start.Environment["GST_PLUGIN_SCANNER_1_0"] = start.Environment["GST_PLUGIN_SCANNER"];
        start.Environment["GST_REGISTRY_1_0"] = start.Environment["GST_REGISTRY"];
        start.Environment["GST_DEBUG"] = "0";
        start.Environment.Remove("GST_DEBUG_FILE");
        start.Environment["MAJOR_EVENT_NONCE"] = nonce;
        return start;
    }

    public Task StopAsync()
    {
        lock (sync)
        {
            if (session is null) return Task.CompletedTask;
            session.StopRequested.Cancel();
            return session.StopTask ??= StopSerializedAsync(session);
        }
    }
    private async Task StopSerializedAsync(Session run)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync) if (!ReferenceEquals(session, run)) return;
            SetStatus(AirPlayState.Stopping, "Interruzione della ricezione AirPlay…");
            await CleanupAsync(run).ConfigureAwait(false);
            SetStatus(AirPlayState.Stopped, "Ricevitore AirPlay spento.");
        }
        finally { lifecycle.Release(); }
    }
    private async Task CleanupAsync(Session run)
    {
        run.StopRequested.Cancel();
        run.LifetimeRegistration.Unregister();
        if (run.Started)
        {
            try
            {
                if (!run.Process.HasExited)
                {
                    await run.Process.StandardInput.WriteLineAsync("STOP").ConfigureAwait(false);
                    await run.Process.StandardInput.FlushAsync().ConfigureAwait(false);
                    await run.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or Win32Exception) { }
            try { if (!run.Process.HasExited) run.Process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        }
        // Closing the job also terminates scanner descendants if the main helper already exited.
        run.Job.Dispose();
        if (run.Started)
        {
            try { await run.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
            try { await Task.WhenAll(run.Output, run.Errors).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException) { }
        }
        run.Process.Dispose();
        lock (sync) if (ReferenceEquals(session, run)) session = null;
        SetPin(null);
    }

    private async Task MonitorAsync(Session run)
    {
        try
        {
            await run.Process.WaitForExitAsync().ConfigureAwait(false);
            // Drain the marker and useful bounded diagnostics before reporting an early exit.
            await Task.WhenAll(run.Output, run.Errors).ConfigureAwait(false);
            int exitCode = run.Process.ExitCode;
            run.Ready.TrySetException(new IOException($"Il componente AirPlay si è chiuso (codice {exitCode}). Consulta la diagnostica."));
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (sync) if (!ReferenceEquals(session, run) || run.StopRequested.IsCancellationRequested) return;
                await CleanupAsync(run).ConfigureAwait(false);
                SetStatus(AirPlayState.Faulted, $"Il ricevitore AirPlay si è chiuso (codice {exitCode}). Riavvialo o consulta la diagnostica.");
            }
            finally { lifecycle.Release(); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException) { }
    }

    private async Task PumpAsync(Session run, StreamReader reader, bool trustedEvents)
    {
        var buffer = new char[1024];
        var line = new StringBuilder(AirPlayEvents.MaxLineLength);
        bool overflow = false;
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                for (int i = 0; i < count; i++)
                {
                    char c = buffer[i];
                    if (c == '\n')
                    {
                        if (!overflow) ProcessLine(run, line.ToString().TrimEnd('\r'), trustedEvents);
                        else EmitDiagnostic(run, "[Riga diagnostica troppo lunga omessa]");
                        line.Clear(); overflow = false;
                    }
                    else if (line.Length < AirPlayEvents.MaxLineLength) line.Append(c);
                    else overflow = true;
                }
            }
            if (line.Length > 0 && !overflow) ProcessLine(run, line.ToString(), trustedEvents);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    private void ProcessLine(Session run, string line, bool trustedEvents)
    {
        lock (sync)
        {
        if (!ReferenceEquals(session, run) || run.StopRequested.IsCancellationRequested) return;
        string? pin = null;
        string? kind = trustedEvents ? AirPlayEvents.Parse(line, run.Nonce, out pin) : null;
        switch (kind)
        {
            case "ready":
                SetStatus(AirPlayState.Ready, "Ricevitore AirPlay pronto sulla rete locale."); run.Ready.TrySetResult(); break;
            case "pin": SetPin(pin); SetStatus(AirPlayState.Authenticating, "Inserisci sul dispositivo il codice mostrato sul PC."); break;
            case "connected": SetPin(null); SetStatus(AirPlayState.Connected, "Dispositivo Apple collegato, in attesa del video."); break;
            case "streaming": SetPin(null); SetStatus(AirPlayState.Streaming, "Video AirPlay in ricezione."); break;
            case "disconnected":
                // iOS can close the challenge socket while its password prompt is open.
                // UxPlay retains the random password until authentication or a new challenge.
                if (currentPin is not null) SetStatus(AirPlayState.Authenticating, "Inserisci sull'iPad o iPhone il codice ancora mostrato sul PC.");
                else SetStatus(AirPlayState.Ready, "Trasmissione terminata. Il ricevitore resta disponibile.");
                break;
            case "invalid": EmitDiagnostic(run, "[Evento del componente AirPlay non valido]"); break;
            default: if (!string.IsNullOrWhiteSpace(line)) EmitDiagnostic(run, AirPlayEvents.Sanitize(line, run.Nonce, CurrentPin)); break;
        }
        }
    }
    private void EmitDiagnostic(Session run, string message)
    {
        lock (sync)
        {
            if (!ReferenceEquals(session, run) || run.StopRequested.IsCancellationRequested) return;
            var now = Environment.TickCount64;
            if (now - run.LogWindow >= 1000) { run.LogWindow = now; run.LogCount = 0; }
            if (++run.LogCount > 40) return;
        }
        Invoke(DiagnosticLog, message);
    }
    private void SetPin(string? pin) { lock (sync) currentPin = pin; Invoke(PinChanged, pin); }
    private void SetStatus(AirPlayState value, string message) { lock (sync) state = value; Invoke(StatusChanged, new(value, message)); }
    private static void Invoke<T>(Action<T>? handler, T value)
    {
        if (handler is null) return;
        foreach (Action<T> subscriber in handler.GetInvocationList())
            try { subscriber(value); } catch (Exception) { /* A UI subscriber cannot strand the native receiver. */ }
    }
    private static string SafeError(Exception error) => error switch
    {
        TimeoutException => "Il componente non ha confermato l'avvio entro 20 secondi.",
        Win32Exception => "Windows non ha avviato o isolato il componente nativo.",
        _ => error.Message
    };
    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Any(char.IsControl) || Encoding.UTF8.GetByteCount(name) > 50 || name.StartsWith('-'))
            throw new ArgumentException("Usa un nome da 1 a 50 byte UTF-8, senza caratteri di controllo o trattino iniziale.", nameof(name));
    }
    private static void ValidateWindow(IntPtr window)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("AirPlay richiede Windows.");
        if (window == IntPtr.Zero || !IsWindow(window)) throw new ArgumentException("La finestra video non è valida.", nameof(window));
        _ = GetWindowThreadProcessId(window, out uint process);
        if (process != Environment.ProcessId) throw new ArgumentException("La finestra video deve appartenere a Major.", nameof(window));
    }
    public async ValueTask DisposeAsync()
    {
        lock (sync) { disposed = true; session?.StopRequested.Cancel(); }
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            disposed = true;
            Session? run;
            lock (sync) { run = session; run?.StopRequested.Cancel(); }
            if (run is not null) await CleanupAsync(run).ConfigureAwait(false);
            SetStatus(AirPlayState.Stopped, "Ricevitore AirPlay spento.");
        }
        finally { lifecycle.Release(); }
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    private sealed class Session(Process process, WindowsJob job, string nonce)
    {
        public Process Process { get; } = process;
        public WindowsJob Job { get; } = job;
        public string Nonce { get; } = nonce;
        public bool Started;
        public readonly CancellationTokenSource StopRequested = new();
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Output = Task.CompletedTask, Errors = Task.CompletedTask;
        public Task? StopTask;
        public CancellationTokenRegistration LifetimeRegistration;
        public long LogWindow;
        public int LogCount;
    }
}
