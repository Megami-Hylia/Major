using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Major.AirPlay;

if (args.Contains("-wid")) { await FakeHelper.RunAsync(args); return; }
if (args.FirstOrDefault() == "--descendant") { File.WriteAllText(args[1], Environment.ProcessId.ToString()); await Task.Delay(Timeout.Infinite); return; }
if (args.FirstOrDefault() == "--job-owner")
{
    var hwnd = Native.CreateWindow();
    await using var service = new AirPlayService(args[1], args[2]);
    await service.StartAsync(hwnd, "Job lifetime test");
    File.WriteAllText(args[3], Environment.ProcessId.ToString());
    await Task.Delay(Timeout.Infinite); return;
}

int checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL " + description);
    Console.WriteLine("PASS " + description); checks++;
}
async Task WaitAsync(Func<bool> predicate, int milliseconds = 5000)
{
    var until = Environment.TickCount64 + milliseconds;
    while (!predicate()) { if (Environment.TickCount64 > until) throw new TimeoutException("Condition did not become true"); await Task.Delay(20); }
}
async Task ThrowsAsync<T>(Func<Task> action, string description) where T : Exception
{
    try { await action(); } catch (T) { Check(true, description); return; }
    Check(false, description);
}
bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
bool Alive(int pid) { try { return !Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { return false; } }
string nonce = new('a', 32);
Check(AirPlayEvents.Parse("MAJOR_AIRPLAY\t" + nonce + "\tpin\t1234", nonce, out var pin) == "pin" && pin == "1234", "PIN marker routed separately");
Check(AirPlayEvents.Parse("MAJOR_AIRPLAY\t" + new string('b', 32) + "\tpin\t1234", nonce, out _) is null, "wrong nonce rejects forged marker");
Check(AirPlayEvents.Parse("MAJOR_AIRPLAY\t" + nonce + "\tpin\t12345", nonce, out _) == "invalid", "invalid PIN length rejected");
Check(AirPlayEvents.Parse("MAJOR_AIRPLAY\t" + nonce + "\tstreaming\textra", nonce, out _) == "invalid", "unexpected event suffix rejected");
Check(!AirPlayEvents.Sanitize("device 1234", nonce, null).Contains("1234"), "four digit secrets removed before PIN arrival");
Check(!AirPlayEvents.Sanitize("password=secret", nonce, null).Contains("secret"), "password diagnostics omitted");
Check(!AirPlayEvents.Sanitize("MAJOR_AIRPLAY\t" + nonce + "\tpin\t1234", nonce, null).Contains(nonce), "marker and nonce omitted from diagnostics");
Check(!AirPlayEvents.Sanitize("a\u001bb\tfinish", nonce, null).Any(char.IsControl), "terminal control characters removed");
Check(AirPlayEvents.Sanitize(new string('z', 5000), nonce, null).Length == AirPlayEvents.MaxLineLength, "diagnostic length bounded");
Check(Throws<ArgumentException>(() => AirPlayService.ValidateName("\nunsafe")), "receiver control characters rejected");
Check(Throws<ArgumentException>(() => AirPlayService.ValidateName("-pw")), "receiver option-like name rejected");
Check(Throws<ArgumentException>(() => AirPlayService.ValidateName(new string('è', 26))), "receiver byte length bounded for mDNS");
AirPlayService.ValidateName("Major – iPad"); Check(true, "Unicode receiver name accepted");

var temp = Path.Combine(Path.GetTempPath(), "MajorAirPlayTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var tools = Path.Combine(temp, "tools"); Directory.CreateDirectory(tools);
foreach (var path in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(path, Path.Combine(tools, Path.GetFileName(path)), true);
File.Copy(Environment.ProcessPath!, Path.Combine(tools, "uxplay.exe"), true);
var identity = Path.Combine(temp, "session"); Directory.CreateDirectory(identity);
IntPtr window = Native.CreateWindow();
try
{
    await using (var service = new AirPlayService(tools, identity))
    {
        var start = service.CreateStartInfo(Path.Combine(tools, "uxplay.exe"), "empty", window, "Major \" Test", nonce);
        var arguments = start.ArgumentList.ToArray();
        Check(arguments.Contains("Major \" Test") && !start.UseShellExecute, "name passed as literal argument without shell");
        Check(arguments.Contains("-wid") && arguments.Contains(window.ToInt64().ToString()), "real HWND sent to native overlay");
        Check(string.Join(" ", arguments).Contains("-p tcp 7000 -p udp 6000"), "fixed TCP and UDP port ranges");
        Check(arguments.Contains("d3d11videosink") && arguments.Contains("wasapisink") && !arguments.Contains("0"), "video and audio sinks both enabled");
        Check(arguments[Array.IndexOf(arguments, "-pw") + 1].StartsWith('-') && !arguments.Contains("-reg") && !arguments.Contains("-key"), "random PIN per access without pairing database");
        Check(start.Environment["GST_PLUGIN_SCANNER"]!.StartsWith(tools) && start.Environment["GST_REGISTRY"]!.StartsWith(identity), "bundled plugin scanner and isolated registry");
        await ThrowsAsync<ArgumentException>(() => service.StartAsync(IntPtr.Zero, "test"), "invalid HWND rejected");
        var diagnostics = new ConcurrentQueue<string>(); var states = new ConcurrentQueue<AirPlayState>();
        service.DiagnosticLog += diagnostics.Enqueue; service.StatusChanged += status => states.Enqueue(status.State);
        service.DiagnosticLog += _ => throw new Exception("subscriber test");
        service.PinChanged += _ => throw new Exception("subscriber test");
        File.WriteAllText(Path.Combine(identity, "fake-mode"), "normal");
        await service.StartAsync(window, "Major \" Test");
        await WaitAsync(() => service.State == AirPlayState.Streaming);
        Check(service.IsRunning && states.Contains(AirPlayState.Authenticating) && states.Contains(AirPlayState.Connected), "helper lifecycle ready PIN connected decoded video");
        Check(service.CurrentPin is null, "PIN cleared once device connected");
        Check(diagnostics.All(line => !line.Contains("1234") && !line.Contains("MAJOR_AIRPLAY") && !line.Contains("secret")), "diagnostics never expose PIN nonce or password");
        var received = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(identity, "fake-args.json")))!;
        Check(received.Contains("Major \" Test"), "helper receives quoted name unchanged");
        var stop = service.StopAsync(); var stop2 = service.StopAsync();
        await Task.WhenAll(stop, stop2);
        Check(service.State == AirPlayState.Stopped && !service.IsRunning, "concurrent stop completes without active process");
        Check(File.Exists(Path.Combine(identity, "fake-stopped")), "stdin STOP permits graceful native cleanup");
        File.WriteAllText(Path.Combine(identity, "fake-mode"), "delayed");
        var starting = service.StartAsync(window, "Restart test");
        await WaitAsync(() => service.IsRunning);
        await service.StopAsync();
        await ThrowsAsync<OperationCanceledException>(() => starting, "stop during startup cancels readiness");
        Check(!service.IsRunning && service.CurrentPin is null && service.State == AirPlayState.Stopped, "startup cancellation leaves no stale state");
        File.WriteAllText(Path.Combine(identity, "fake-mode"), "exit");
        await ThrowsAsync<IOException>(() => service.StartAsync(window, "Failure test"), "helper early exit reported as failure");
        Check(!service.IsRunning && service.State == AirPlayState.Faulted, "early exit cleaned up");
        File.WriteAllText(Path.Combine(identity, "fake-mode"), "normal");
        using var cancel = new CancellationTokenSource();
        await service.StartAsync(window, "Cancel lifetime", cancel.Token); cancel.Cancel();
        await WaitAsync(() => !service.IsRunning);
        Check(service.State == AirPlayState.Stopped, "lifetime cancellation stops running receiver");
        File.WriteAllText(Path.Combine(identity, "fake-mode"), "disconnect");
        await service.StartAsync(window, "Disconnect test");
        await WaitAsync(() => states.Contains(AirPlayState.Streaming));
        await Task.Delay(100);
        Check(service.State == AirPlayState.Ready && service.CurrentPin is null, "disconnect returns to ready and clears PIN");
        await service.StopAsync();
        File.WriteAllText(Path.Combine(identity, "fake-mode"), "pin-disconnect");
        await service.StartAsync(window, "PIN while reconnecting");
        await Task.Delay(150);
        Check(service.State == AirPlayState.Authenticating && service.CurrentPin == "1234", "pending authentication PIN survives temporary RTSP disconnect");
        await service.StopAsync();
        Check(service.CurrentPin is null, "explicit stop clears pending authentication PIN");
        File.WriteAllText(Path.Combine(identity, "fake-mode"), "flood"); diagnostics.Clear();
        await service.StartAsync(window, "Flood test");
        await WaitAsync(() => diagnostics.Count > 0);
        await service.StopAsync();
        Check(diagnostics.Count <= 40 && diagnostics.All(line => line.Length <= AirPlayEvents.MaxLineLength), "oversized and rapid log output bounded");
        File.WriteAllText(Path.Combine(identity, "fake-mode"), "descendant");
        await service.StartAsync(window, "Child tree test");
        var descendantPath = Path.Combine(identity, "fake-descendant");
        await WaitAsync(() => File.Exists(descendantPath));
        int child = int.Parse(File.ReadAllText(descendantPath));
        Check(Alive(child), "helper descendant fixture running");
        await service.StopAsync(); await WaitAsync(() => !Alive(child));
        Check(true, "job closes helper descendant on stop");
        File.WriteAllText(Path.Combine(identity, "fake-mode"), "late-events");
        await service.StartAsync(window, "Late events");
        await service.StopAsync();
        Check(service.State == AirPlayState.Stopped && service.CurrentPin is null, "events emitted during shutdown cannot restore stale PIN or state");
        await service.DisposeAsync();
        await ThrowsAsync<ObjectDisposedException>(() => service.StartAsync(window, "Disposed test"), "disposed receiver cannot restart");
    }
    var ownerIdentity = Path.Combine(temp, "owner-session"); Directory.CreateDirectory(ownerIdentity);
    File.WriteAllText(Path.Combine(ownerIdentity, "fake-mode"), "descendant");
    var ownerReady = Path.Combine(ownerIdentity, "owner-ready");
    var ownerStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var value in new[] { "--job-owner", tools, ownerIdentity, ownerReady }) ownerStart.ArgumentList.Add(value);
    using (var owner = Process.Start(ownerStart)!)
    {
        await WaitAsync(() => File.Exists(ownerReady) && File.Exists(Path.Combine(ownerIdentity, "fake-descendant")));
        int helper = int.Parse(File.ReadAllText(Path.Combine(ownerIdentity, "fake-pid")));
        int child = int.Parse(File.ReadAllText(Path.Combine(ownerIdentity, "fake-descendant")));
        owner.Kill(); await owner.WaitForExitAsync();
        await WaitAsync(() => !Alive(helper) && !Alive(child));
        Check(true, "parent crash closes job and complete helper process tree");
    }
    if (args.Contains("--real"))
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "tools", "airplay"))) current = current.Parent;
        if (current is null) throw new DirectoryNotFoundException("Real AirPlay bundle not found");
        await using var real = new AirPlayService(Path.Combine(current.FullName, "tools", "airplay"), Path.Combine(temp, "real"));
        real.DiagnosticLog += line => Console.WriteLine("NATIVE " + line);
        await real.StartAsync(window, "Major integration test");
        Check(real.State == AirPlayState.Ready && real.IsRunning, "real Windows helper initializes video audio and native mDNS");
        Check(Native.HasResponsiveReceiverChild(window), "embedded D3D11 child belongs to receiver process and has a live message pump");
        var discovery = await NativeNetworkProbe.DiscoverAsync("_airplay._tcp.local", "Major integration test._airplay._tcp.local");
        var instance = discovery.Single(r => r.Type == 12 && r.Name == "_airplay._tcp.local" && r.Target == "Major integration test._airplay._tcp.local").Target;
        Check(instance == "Major integration test._airplay._tcp.local", "native mDNS PTR advertises exact receiver name");
        Check(discovery.Any(r => r.Name == instance && r.Type == 33 && r.Port == 7001), "native mDNS SRV advertises RTSP TCP port 7001");
        var text = discovery.Single(r => r.Name == instance && r.Type == 16).Text;
        Check(text.GetValueOrDefault("pw") == "true", "native mDNS TXT requires password authentication");
        uint features = Convert.ToUInt32(text["features"].Split(',')[0][2..], 16);
        Check((features & (1u << 7)) != 0 && (features & (1u << 9)) != 0, "native mDNS advertises video mirroring and audio");
        Check((features & ((1u << 0) | (1u << 1) | (1u << 2) | (1u << 4) | (1u << 5) | (1u << 13))) == 0,
            "native mDNS omits unsupported HLS photo slideshow DRM video features");
        var info = await NativeNetworkProbe.RequestAsync("GET", "/info");
        Check(info.Headers.StartsWith("RTSP/1.0 200") && info.Body.AsSpan().StartsWith("bplist00"u8), "real RTSP info returns binary plist");
        var setup = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "auth-setup.bplist"));
        var unauthorized = await NativeNetworkProbe.RequestAsync("SETUP", "/stream", setup);
        Check(unauthorized.Headers.StartsWith("RTSP/1.0 401") && unauthorized.Headers.Contains("WWW-Authenticate: Digest"), "unauthenticated SETUP challenged before mirroring starts");
        await Task.Delay(150);
        Check(real.CurrentPin is { Length: 4 } && real.State == AirPlayState.Authenticating, "real Digest PIN remains visible after challenge socket closes");
        var missingDevice = await NativeNetworkProbe.RequestAsync("SETUP", "/stream", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "auth-no-device.bplist")));
        Check(missingDevice.Headers.StartsWith("RTSP/1.0 400"), "SETUP missing device identifier rejected without native crash");
        var malformed = await NativeNetworkProbe.RequestAsync("SETUP", "/stream", setup, "Digest response=\"00000000000000000000000000000000\"");
        Check(malformed.Headers.StartsWith("RTSP/1.0 401"), "Digest without nonce rejected without native crash");
        string challengeNonce = Regex.Match(malformed.Headers, "nonce=\"([0-9a-fA-F]{32})\"").Groups[1].Value;
        var missingTokens = await NativeNetworkProbe.RequestAsync("SETUP", "/stream", setup,
            $"Digest nonce=\"{challengeNonce}\", response=\"00000000000000000000000000000000\"");
        Check(missingTokens.Headers.StartsWith("RTSP/1.0 401"), "Digest missing username realm URI rejected without native crash");
        challengeNonce = Regex.Match(missingTokens.Headers, "nonce=\"([0-9a-fA-F]{32})\"").Groups[1].Value;
        var wrongDigest = await NativeNetworkProbe.RequestAsync("SETUP", "/stream", setup,
            $"Digest username=\"AirPlay\", realm=\"raop\", nonce=\"{challengeNonce}\", uri=\"/stream\", response=\"00000000000000000000000000000000\"");
        Check(wrongDigest.Headers.StartsWith("RTSP/1.0 401"), "incorrect Digest response refused");
        Check(real.State != AirPlayState.Streaming, "discovery and unauthenticated SETUP cannot claim video streaming");
        await real.StopAsync();
        Check(!real.IsRunning && real.State == AirPlayState.Stopped, "real Windows helper stops without orphan");
    }
    Console.WriteLine($"{checks} PASS; {(args.Contains("--real") ? "real helper checked" : "native helper and Apple device not tested by this invocation")}");
}
finally
{
    Native.DestroyWindow(window);
    try { Directory.Delete(temp, true); } catch (IOException) { }
}

internal static class FakeHelper
{
    public static async Task RunAsync(string[] arguments)
    {
        var cwd = Environment.CurrentDirectory;
        File.WriteAllText(Path.Combine(cwd, "fake-pid"), Environment.ProcessId.ToString());
        File.WriteAllText(Path.Combine(cwd, "fake-args.json"), JsonSerializer.Serialize(arguments));
        string nonce = Environment.GetEnvironmentVariable("MAJOR_EVENT_NONCE")!;
        void Emit(string value) { Console.WriteLine("MAJOR_AIRPLAY\t" + nonce + "\t" + value); Console.Out.Flush(); }
        var mode = File.ReadAllText(Path.Combine(cwd, "fake-mode"));
        if (await Console.In.ReadLineAsync() != "START") { Environment.ExitCode = 1; return; }
        if (mode == "exit") { Environment.ExitCode = 7; return; }
        if (mode == "delayed") { await Task.Delay(1000); }
        if (mode == "descendant")
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--descendant"); start.ArgumentList.Add(Path.Combine(cwd, "fake-descendant"));
            _ = Process.Start(start);
        }
        Emit("ready");
        if (mode == "pin-disconnect") { Emit("pin\t1234"); Emit("disconnected"); }
        if (mode is "normal" or "disconnect")
        {
            Emit("pin\t1234"); Console.WriteLine("device 1234"); Console.Error.WriteLine("password=secret");
            Console.WriteLine("MAJOR_AIRPLAY\tforged\tpin\t9876");
            Emit("connected"); Emit("streaming");
        }
        if (mode == "disconnect") { await Task.Delay(40); Emit("disconnected"); }
        if (mode == "flood")
        {
            Console.WriteLine(new string('x', 100000));
            for (int i = 0; i < 100; i++) Console.WriteLine("bounded log " + i);
        }
        while (await Console.In.ReadLineAsync() is string command)
            if (command == "STOP")
            {
                if (mode == "late-events") { Emit("pin\t9999"); Emit("streaming"); }
                File.WriteAllText(Path.Combine(cwd, "fake-stopped"), "yes"); return;
            }
    }
}
internal static class Native
{
    public static bool HasResponsiveReceiverChild(IntPtr parent)
    {
        bool found = false;
        EnumChildren callback = (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint owner);
            if (owner != Environment.ProcessId && SendMessageTimeout(child, 0, IntPtr.Zero, IntPtr.Zero, 2, 1000, out _) != IntPtr.Zero) found = true;
            return true;
        };
        EnumChildWindows(parent, callback, IntPtr.Zero);
        return found;
    }
    public static IntPtr CreateWindow()
    {
        var handle = CreateWindowEx(0, "STATIC", "Major test host", 0, 0, 0, 640, 480, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (handle == IntPtr.Zero) throw new Exception("Cannot create test HWND");
        return handle;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyWindow(IntPtr window);
    private delegate bool EnumChildren(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(IntPtr parent, EnumChildren callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
