using System.Diagnostics;
using System.Reflection;
using Major.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

if (args.Length > 0)
{
    switch (args[0])
    {
        case "--fake-sleep": await Task.Delay(30000); return;
        case "--fake-echo": Console.Write(args[1]); return;
        case "--fake-spam":
            await Task.WhenAll(Console.Out.WriteAsync(new string('o', 100000)), Console.Error.WriteAsync(new string('e', 100000)));
            return;
        case "devices":
            Console.Write("List of devices attached\nusb1\tdevice product:pixel model:Pixel_8 device:husky transport_id:1\nunauth\tunauthorized transport_id:2\n192.168.1.3:5555\toffline\n");
            return;
        case "pair":
            var code = Console.ReadLine();
            Console.Write("Successfully paired to " + args[1] + " code=" + code);
            return;
        case "connect":
            Console.Write(args[1].StartsWith("bad", StringComparison.Ordinal) ? "failed to connect" : "connected to " + args[1]);
            return;
        case "disconnect": Console.Write("disconnected " + args[1]); return;
        case "-s":
            if (args[1] == "broken") { Console.Write("not a PNG"); return; }
            if (args[1] == "error") { Console.Error.Write("unauthorized"); Environment.ExitCode = 1; return; }
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
            await Console.OpenStandardOutput().WriteAsync(png);
            return;
    }
    if (args[0].StartsWith("--serial=", StringComparison.Ordinal))
    {
        var serial = args[0][9..];
        if (serial == "instant") { Environment.ExitCode = 1; return; }
        if (serial == "disconnect") { await Task.Delay(150); Environment.ExitCode = 2; return; }
        await Task.Delay(30000);
        return;
    }
}

if (args.Length == 2 && args[0] == "--real-tools")
{
    var paths = new ToolPaths(args[1]);
    paths.Validate();
    Console.WriteLine("REAL BUNDLED TOOLS CHECK — " + DateTimeOffset.Now.ToString("O"));
    var version = await ProcessRunner.RunAsync(paths.Scrcpy, ["--version"], TimeSpan.FromSeconds(10));
    Console.WriteLine(version.Output.Trim());
    var adb = await ProcessRunner.RunAsync(paths.Adb, ["devices", "-l"], TimeSpan.FromSeconds(15));
    Console.WriteLine($"adb devices -l: exit={adb.ExitCode}\n{adb.Output.Trim()}\n{adb.Error.Trim()}");
    if (adb.ExitCode != 0) throw new Exception("Real ADB query failed.");
    var devices = await new DeviceService(paths).GetDevicesAsync();
    Console.WriteLine($"REAL PASS: DeviceService parsed {devices.Count} connected devices (not simulated).");
    var missingSerial = "Major_test_missing_" + Guid.NewGuid().ToString("N");
    var native = new MirrorService(paths);
    var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    native.Log += (serial, line) => Console.WriteLine("scrcpy: " + line);
    native.Exited += (_, code) => completion.TrySetResult(code);
    try
    {
        await native.StartAsync(new(missingSerial, "device", "No-device test", "USB"), new MirrorOptions { Reconnect = true, UseUhidKeyboard = true, Fullscreen = true, TurnScreenOff = true });
        var code = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (code != 1 || native.IsRunning(missingSerial)) throw new Exception("Missing device should exit 1 without reconnect.");
        Console.WriteLine("REAL PASS: bundled scrcpy accepts configured flags, reports missing selected device, exits 1 and releases session without reconnect.");
    }
    finally { await native.StopAllAsync(); }
    var destination = Path.Combine(Path.GetTempPath(), "Major-real-missing-" + Guid.NewGuid().ToString("N") + ".png");
    try
    {
        try { await new DeviceService(paths).ScreenshotAsync(missingSerial, destination); throw new Exception("Expected screenshot failure."); }
        catch (IOException exception) { Console.WriteLine("REAL PASS: missing-device screenshot gives ADB failure: " + exception.Message.Trim()); }
        if (File.Exists(destination) || Directory.GetFiles(Path.GetDirectoryName(destination)!, Path.GetFileName(destination) + ".*.tmp").Length != 0)
            throw new Exception("Screenshot failure left partial output.");
        Console.WriteLine("REAL PASS: failed screenshot leaves no partial image or temporary file.");
    }
    finally { if (File.Exists(destination)) File.Delete(destination); }
    Console.WriteLine(devices.Count == 0 ? "No Android hardware available. Real mirroring/input/disconnect/reconnect not verified." : "Devices exist; test deliberately selected an impossible serial. No real-device mirroring/input performed.");
    return;
}

var passed = 0;
var folder = Path.Combine(Path.GetTempPath(), "Major.Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    void Check(bool success, string name)
    {
        if (!success) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
        passed++;
    }
    async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, name); return; }
        throw new Exception("FAIL: " + name + " (no " + typeof(T).Name + ")");
    }
    void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, name); return; }
        throw new Exception("FAIL: " + name + " (no " + typeof(T).Name + ")");
    }

    var list = DeviceService.ParseDevices("* daemon started successfully\r\nList of devices attached\r\nabc device model:Pixel_8 transport_id:1\r\nbad unauthorized\r\n192.168.1.2:5555 offline\r\nemulator-5554 device model:sdk\r\nabc device model:duplicate\nnoise line\n");
    Check(list.Count == 4 && list[0].Model == "Pixel 8" && list[0].IsAuthorized && list[0].Connection == "USB", "parse ADB, model, duplicates, daemon noise");
    Check(!list[1].IsAuthorized && list[2].Connection == "Wi-Fi" && list[3].Connection == "Emulatore", "unauthorized/offline/multiple transports");
    foreach (var endpoint in new[] { "192.168.1.2:12345", "phone.local:65535", "[fe80::1234]:37001", "localhost:1" })
        Check(DeviceService.ValidateEndpoint(" " + endpoint + " ") == endpoint, "endpoint " + endpoint);
    foreach (var endpoint in new[] { "", "-bad:42", "1.2.3.4:0", "1.2.3.4:65536", "[::1]", "host:abc", "host:12 & calc", "host:12\ncalc", "::1:55", "host: 5" })
        Throws<ArgumentException>(() => DeviceService.ValidateEndpoint(endpoint), "reject endpoint " + endpoint.Replace('\n', ' '));
    foreach (var serial in new[] { "", "--evil", "serial\nnext", "white space" })
        Throws<ArgumentException>(() => DeviceService.ValidateSerial(serial), "reject serial");
    var device = new DeviceInfo("usb1", "device", "Pixel 8", "USB");
    var record = Path.Combine(folder, "test with spaces.mkv");
    var options = new MirrorOptions { Fullscreen = true, TurnScreenOff = true, UseUhidKeyboard = true, RecordPath = record };
    var arguments = MirrorService.BuildArguments(device, options);
    Check(arguments.Contains("--serial=usb1") && arguments.Contains("--max-size=1920") && arguments.Contains("--video-bit-rate=8M") && arguments.Contains("--max-fps=60"), "scrcpy explicit serial and video settings");
    Check(arguments.Contains("--record=" + record) && arguments.Contains("--fullscreen") && arguments.Contains("--turn-screen-off") && arguments.Contains("--keyboard=uhid"), "scrcpy recording path and controls flags");
    Throws<ArgumentOutOfRangeException>(() => MirrorService.BuildArguments(device, new MirrorOptions { MaxSize = -1 }), "invalid resolution");
    Throws<ArgumentOutOfRangeException>(() => MirrorService.BuildArguments(device, new MirrorOptions { BitRateMbps = 0 }), "invalid bitrate");
    Throws<ArgumentOutOfRangeException>(() => MirrorService.BuildArguments(device, new MirrorOptions { MaxFps = 999 }), "invalid framerate");
    Throws<ArgumentException>(() => MirrorService.BuildArguments(device, new MirrorOptions { RecordPath = "x.txt" }), "invalid recording extension");
    File.WriteAllText(record, "preserve");
    Throws<IOException>(() => MirrorService.BuildArguments(device, options), "refuse recording overwrite");
    Check(File.ReadAllText(record) == "preserve", "existing recording preserved");

    var executable = Environment.ProcessPath!;
    var prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? new[] { Assembly.GetExecutingAssembly().Location } : Array.Empty<string>();
    Task<ProcessResult> Child(string[] childArgs, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        ProcessRunner.RunAsync(executable, prefix.Concat(childArgs), timeout ?? TimeSpan.FromSeconds(5), cancellationToken);
    var tricky = "spaces and \"quotes\" ; $() ` & < >\nUnicode è 日本語";
    Check((await Child(["--fake-echo", tricky])).Output == tricky, "ArgumentList preserves literal shell metacharacters and Unicode");
    var spam = await Child(["--fake-spam"]);
    Check(spam.Output.Length == 65536 && spam.Error.Length == 65536, "parallel pipe drain with bounded output");
    var timer = Stopwatch.StartNew();
    await ThrowsAsync<TimeoutException>(() => Child(["--fake-sleep"], TimeSpan.FromMilliseconds(150)), "process timeout kills child");
    Check(timer.Elapsed < TimeSpan.FromSeconds(5), "timeout is bounded");
    using (var cancellation = new CancellationTokenSource(150))
        await ThrowsAsync<OperationCanceledException>(() => Child(["--fake-sleep"], cancellationToken: cancellation.Token), "external cancellation kills child");

    // The apphost locates its original test assembly beside adb.exe/scrcpy.exe; no device is touched.
    var tools = Path.Combine(folder, "tools", "scrcpy");
    Directory.CreateDirectory(tools);
    var assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
    foreach (var file in Directory.GetFiles(assemblyFolder)) File.Copy(file, Path.Combine(tools, Path.GetFileName(file)));
    var apphost = Path.Combine(assemblyFolder, "Major.Core.Tests.exe");
    if (OperatingSystem.IsWindows() && File.Exists(apphost))
    {
        File.Copy(apphost, Path.Combine(tools, "adb.exe"));
        File.Copy(apphost, Path.Combine(tools, "scrcpy.exe"));
        File.WriteAllText(Path.Combine(tools, "scrcpy-server"), "test fixture");
        var paths = new ToolPaths(folder);
        paths.Validate();
        var service = new DeviceService(paths);
        Check((await service.GetDevicesAsync()).Count == 3, "DeviceService invokes selected ADB process");
        var paired = await service.PairAsync("phone.local:37001", "123456");
        Check(paired.Contains("Successfully paired", StringComparison.Ordinal) && !paired.Contains("123456", StringComparison.Ordinal), "pairing uses stdin and redacts code");
        await ThrowsAsync<ArgumentException>(() => service.PairAsync("phone.local:37001", "12345"), "pairing validates six digits");
        Check((await service.ConnectAsync("phone.local:37002")).Contains("connected", StringComparison.Ordinal), "wireless connect success");
        await ThrowsAsync<IOException>(() => service.ConnectAsync("bad.test:1"), "ADB zero-exit connection failure is still failure");
        var screenshot = Path.Combine(folder, "screenshot.png");
        await service.ScreenshotAsync("usb1", screenshot);
        Check(File.ReadAllBytes(screenshot).Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "screenshot binary PNG preserved");
        await ThrowsAsync<IOException>(() => service.ScreenshotAsync("usb1", screenshot), "screenshot refuses overwrite");
        var invalidScreenshot = Path.Combine(folder, "invalid.png");
        await ThrowsAsync<IOException>(() => service.ScreenshotAsync("broken", invalidScreenshot), "screenshot rejects non-PNG response");
        Check(!File.Exists(invalidScreenshot) && Directory.GetFiles(folder, "*.tmp").Length == 0, "failed screenshot leaves no partial destination or temp file");
        await ThrowsAsync<IOException>(() => service.ScreenshotAsync("error", invalidScreenshot), "screenshot reports unauthorized ADB failure");

        var mirror = new MirrorService(paths);
        var exits = 0;
        mirror.Exited += (_, _) => Interlocked.Increment(ref exits);
        await mirror.StartAsync(device, new MirrorOptions());
        Check(mirror.IsRunning(device.Serial), "mirror owns launched session");
        await ThrowsAsync<InvalidOperationException>(() => mirror.StartAsync(device, new MirrorOptions()), "duplicate sessions rejected");
        await mirror.StopAsync(device.Serial);
        Check(!mirror.IsRunning(device.Serial) && exits == 1, "stop owns child, cleans session and notifies exactly once");
        var disconnected = device with { Serial = "disconnect" };
        await mirror.StartAsync(disconnected, new MirrorOptions { Reconnect = true });
        await Task.Delay(500);
        Check(mirror.IsRunning(disconnected.Serial), "disconnection waits for bounded reconnect");
        timer.Restart();
        await mirror.StopAllAsync();
        Check(!mirror.IsRunning(disconnected.Serial) && timer.Elapsed < TimeSpan.FromSeconds(3), "reconnect wait is cancellable");
        await mirror.StartAsync(device with { Serial = "instant" }, new MirrorOptions());
        await Task.Delay(300);
        Check(!mirror.IsRunning("instant"), "startup failure is removed without reconnect loop");
        await ThrowsAsync<InvalidOperationException>(() => mirror.StartAsync(device with { State = "unauthorized" }, new MirrorOptions()), "unauthorized mirror rejected before process launch");

        var reentrant = new MirrorService(paths);
        Task? callbackStop = null;
        reentrant.Log += (serial, line) => { if (line.StartsWith("Avvio mirroring", StringComparison.Ordinal)) callbackStop = reentrant.StopAsync(serial); };
        await reentrant.StartAsync(device, new MirrorOptions());
        if (callbackStop is not null) await callbackStop.WaitAsync(TimeSpan.FromSeconds(3));
        Check(callbackStop is not null && !reentrant.IsRunning(device.Serial), "synchronous start-log listener can stop fully published session");

        var concurrent = new MirrorService(paths);
        var many = Enumerable.Range(0, 12).Select(i => device with { Serial = "parallel" + i }).ToArray();
        await Task.WhenAll(many.Select(d => Task.Run(() => concurrent.StartAsync(d, new MirrorOptions()))));
        Check(many.All(d => concurrent.IsRunning(d.Serial)), "concurrent multi-device sessions are tracked independently");
        await Task.WhenAll(many.Select(d => concurrent.StopAsync(d.Serial)).Concat(many.Select(d => concurrent.StopAsync(d.Serial))));
        Check(many.All(d => !concurrent.IsRunning(d.Serial)), "concurrent duplicate stop requests clean every owned session");
    }
    else Console.WriteLine("SKIP: Windows apphost process fixtures require Windows and apphost output.");
    Console.WriteLine($"All {passed} checks passed. No Android hardware used.");
}
finally { Directory.Delete(folder, recursive: true); }
