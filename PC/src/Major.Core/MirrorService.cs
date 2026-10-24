using System.Diagnostics;

namespace Major.Core;

public sealed class MirrorService(ToolPaths paths)
{
    private readonly object sync = new();
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    public event Action<string, string>? Log;
    public event Action<string, int>? Exited;

    public bool IsRunning(string serial)
    {
        lock (sync) return sessions.ContainsKey(serial);
    }

    public Task StartAsync(DeviceInfo device, MirrorOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        DeviceService.ValidateSerial(device.Serial);
        if (!device.IsAuthorized) throw new InvalidOperationException("Sblocca il telefono e autorizza questo computer prima di avviare il mirroring.");
        paths.Validate();
        var snapshot = options.Snapshot();
        snapshot.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (sessions.ContainsKey(device.Serial)) throw new InvalidOperationException("Questo dispositivo ha già una sessione attiva.");
            // The start token covers launching; StopAsync owns the ongoing native session lifetime.
            var session = new Session(device, snapshot);
            sessions.Add(device.Serial, session);
            try
            {
                session.Process = StartProcess(device, snapshot);
                session.Completion = SuperviseAsync(session);
            }
            catch
            {
                sessions.Remove(device.Serial);
                session.Cancellation.Dispose();
                throw;
            }
        }
        EmitStartLog(device, snapshot);
        return Task.CompletedTask;
    }

    public async Task StopAsync(string serial, TimeSpan? gracePeriod = null)
    {
        Session? session;
        Process? process;
        lock (sync)
        {
            if (!sessions.TryGetValue(serial, out session)) return;
            session.Cancellation.Cancel();
            process = session.Process;
        }
        if (process is not null)
        {
            try
            {
                // SDL's close event lets scrcpy finalize MKV/MP4 before the bounded fallback.
                if (!process.HasExited && process.CloseMainWindow())
                    await process.WaitForExitAsync().WaitAsync(gracePeriod ?? TimeSpan.FromSeconds(6)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { }
            try
            {
                if (!process.HasExited && session.Options.RecordPath is not null)
                    EmitLog(serial, "Chiusura forzata: verifica la registrazione, che potrebbe non essere stata finalizzata.");
            }
            catch (InvalidOperationException) { }
            ProcessRunner.Kill(process);
        }
        await session.Completion.ConfigureAwait(false);
    }

    public async Task StopAllAsync(TimeSpan? gracePeriod = null)
    {
        string[] serials;
        lock (sync) serials = sessions.Keys.ToArray();
        await Task.WhenAll(serials.Select(serial => StopAsync(serial, gracePeriod))).ConfigureAwait(false);
    }

    internal static IReadOnlyList<string> BuildArguments(DeviceInfo device, MirrorOptions options)
    {
        DeviceService.ValidateSerial(device.Serial);
        options.Validate();
        var arguments = new List<string>
        {
            "--serial=" + device.Serial,
            "--window-title=Major · " + device.Model,
            "--max-size=" + options.MaxSize,
            "--video-bit-rate=" + options.BitRateMbps + "M",
            "--max-fps=" + options.MaxFps,
            "--no-audio"
        };
        if (options.StayAwake) arguments.Add("--stay-awake");
        if (options.TurnScreenOff) arguments.Add("--turn-screen-off");
        if (options.Fullscreen) arguments.Add("--fullscreen");
        if (options.UseUhidKeyboard) arguments.Add("--keyboard=uhid");
        if (options.RecordPath is not null) arguments.Add("--record=" + Path.GetFullPath(options.RecordPath));
        return arguments;
    }

    private Process StartProcess(DeviceInfo device, MirrorOptions options)
    {
        var arguments = BuildArguments(device, options);
        if (options.RecordPath is not null) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.RecordPath))!);
        var info = ProcessRunner.CreateInfo(paths.Scrcpy, arguments);
        info.Environment["ADB"] = paths.Adb;
        info.Environment["SCRCPY_SERVER_PATH"] = Path.Combine(Path.GetDirectoryName(paths.Scrcpy)!, "scrcpy-server");
        var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) EmitLog(device.Serial, args.Data); };
        process.ErrorDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) EmitLog(device.Serial, args.Data); };
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch { ProcessRunner.Kill(process); process.Dispose(); throw; }
    }

    private async Task SuperviseAsync(Session session)
    {
        var exitCode = 0;
        var retries = 0;
        try
        {
            while (true)
            {
                var process = session.Process!;
                // Cancellation is handled by StopAsync so the native window can close gracefully.
                await process.WaitForExitAsync().ConfigureAwait(false);
                process.WaitForExit(); // Drain asynchronous log readers after process exit.
                exitCode = process.ExitCode;
                lock (sync) session.Process = null;
                process.Dispose();
                if (session.Cancellation.IsCancellationRequested || exitCode != 2 || !session.Options.Reconnect) break;
                var reconnected = false;
                while (++retries <= 10)
                {
                    EmitLog(session.Device.Serial, $"Dispositivo scollegato. Tentativo di riconnessione {retries}/10…");
                    await Task.Delay(TimeSpan.FromSeconds(3), session.Cancellation.Token).ConfigureAwait(false);
                    try
                    {
                        var devices = await new DeviceService(paths).GetDevicesAsync(session.Cancellation.Token).ConfigureAwait(false);
                        if (!devices.Any(d => d.Serial == session.Device.Serial && d.IsAuthorized))
                        {
                            if (session.Device.Connection == "Wi-Fi")
                            {
                                try { await new DeviceService(paths).ConnectAsync(session.Device.Serial, session.Cancellation.Token).ConfigureAwait(false); }
                                catch (ArgumentException) { } // mDNS serials are not explicit network endpoints.
                            }
                            devices = await new DeviceService(paths).GetDevicesAsync(session.Cancellation.Token).ConfigureAwait(false);
                            if (!devices.Any(d => d.Serial == session.Device.Serial && d.IsAuthorized)) continue;
                        }
                        var nextOptions = session.Options.Snapshot();
                        if (nextOptions.RecordPath is not null)
                            nextOptions.RecordPath = ReconnectRecordPath(nextOptions.RecordPath, retries);
                        lock (sync)
                        {
                            session.Cancellation.Token.ThrowIfCancellationRequested();
                            session.Process = StartProcess(session.Device, nextOptions);
                        }
                        EmitStartLog(session.Device, nextOptions);
                        reconnected = true;
                        break;
                    }
                    catch (Exception exception) when (exception is IOException or TimeoutException)
                    {
                        EmitLog(session.Device.Serial, "Riconnessione: " + exception.Message);
                    }
                }
                if (!reconnected)
                {
                    EmitLog(session.Device.Serial, "Riconnessione terminata. Collega il dispositivo e premi Apri schermo per riprovare.");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            exitCode = -1;
            EmitLog(session.Device.Serial, "Sessione terminata: " + exception.Message);
        }
        finally
        {
            lock (sync)
            {
                if (session.Process is not null)
                {
                    ProcessRunner.Kill(session.Process);
                    session.Process.Dispose();
                    session.Process = null;
                }
                if (sessions.TryGetValue(session.Device.Serial, out var current) && ReferenceEquals(current, session))
                    sessions.Remove(session.Device.Serial);
            }
            session.Cancellation.Dispose();
            EmitExited(session.Device.Serial, exitCode);
        }
    }

    private static string ReconnectRecordPath(string original, int attempt)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(original))!;
        var name = Path.GetFileNameWithoutExtension(original);
        var extension = Path.GetExtension(original);
        var path = Path.Combine(folder, $"{name}-reconnect-{attempt}{extension}");
        return File.Exists(path) ? Path.Combine(folder, $"{name}-reconnect-{attempt}-{Guid.NewGuid():N}{extension}") : path;
    }

    private void EmitLog(string serial, string text)
    {
        var line = text[..Math.Min(text.Length, 2000)];
        foreach (var handler in Log?.GetInvocationList() ?? [])
        {
            try { ((Action<string, string>)handler)(serial, line); }
            catch (Exception) { /* UI listeners must not interrupt process cleanup. */ }
        }
    }

    private void EmitStartLog(DeviceInfo device, MirrorOptions options) =>
        EmitLog(device.Serial, "Avvio mirroring" + (options.RecordPath is null ? "." : " con registrazione: " + options.RecordPath));

    private void EmitExited(string serial, int code)
    {
        foreach (var handler in Exited?.GetInvocationList() ?? [])
        {
            try { ((Action<string, int>)handler)(serial, code); }
            catch (Exception) { }
        }
    }

    private sealed class Session(DeviceInfo device, MirrorOptions options)
    {
        public DeviceInfo Device { get; } = device;
        public MirrorOptions Options { get; } = options;
        public CancellationTokenSource Cancellation { get; } = new();
        public Process? Process { get; set; }
        public Task Completion { get; set; } = Task.CompletedTask;
    }
}
