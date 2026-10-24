using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Major.Streaming;

var root = Option(args, "--root") ?? (args.Length == 1 && !args[0].StartsWith('-') ? args[0] : FindRoot());
var tools = Path.Combine(root, "tools", "scrcpy");
if (args.Contains("--serve", StringComparer.Ordinal))
{
    await ServeAsync(args, tools);
    return;
}
var scratch = Path.Combine(Path.GetTempPath(), "MajorStreamingTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var checks = 0;
void Check(bool condition, string title)
{
    if (!condition) throw new Exception("FAIL: " + title);
    Console.WriteLine("PASS: " + title); checks++;
}
void Throws<T>(Action action, string title) where T : Exception
{
    try { action(); } catch (T) { Check(true, title); return; }
    throw new Exception("FAIL: " + title);
}

try
{
    Check(StreamProtocol.Base32(Encoding.ASCII.GetBytes("foobar")) == "MZXW6YTBOI", "Base32 RFC4648 vector");
    var code = "AAAA-AAAA-AAAA-AAAA-BBBB-BBBB-BBBB-BBBB";
    Check(StreamProtocol.NormalizePairingCode(code.ToLowerInvariant()) == "AAAAAAAAAAAAAAAABBBBBBBBBBBBBBBB", "pair code grouping/case normalization");
    Throws<ArgumentException>(() => StreamProtocol.NormalizePairingCode("1234"), "reject short pairing code");
    Throws<ArgumentException>(() => StreamProtocol.NormalizePairingCode(new string('0', 32)), "reject ambiguous Base32 characters");
    foreach (var dimensions in new[] { (0, 720, 30), (8192, 8192, 60), (1920, 1080, 0), (4096, 4096, 30) })
        Throws<InvalidDataException>(() => StreamProtocol.ValidateConfiguration(dimensions.Item1, dimensions.Item2, dimensions.Item3), "reject out-of-budget video configuration");
    await using (var empty = new MemoryStream(new byte[] { 0, 0, 0, 0 }))
    { try { await StreamProtocol.ReadPayloadAsync(empty, 100, default); throw new Exception("FAIL"); } catch (InvalidDataException) { Check(true, "reject zero-length frame"); } }
    await using (var truncated = new MemoryStream(new byte[] { 0, 0, 0, 5, 1 }))
    { try { await StreamProtocol.ReadPayloadAsync(truncated, 100, default); throw new Exception("FAIL"); } catch (EndOfStreamException) { Check(true, "reject truncated payload"); } }
    await using (var large = new MemoryStream(new byte[] { 127, 255, 255, 255 }))
    { try { await StreamProtocol.ReadPayloadAsync(large, 100, default); throw new Exception("FAIL"); } catch (InvalidDataException) { Check(true, "reject oversized frame before allocation"); } }

    var landscape = SplitAccessUnits(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "landscape.h264")));
    var portrait = SplitAccessUnits(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portrait.h264")));
    Check(landscape.Count == 4 && portrait.Count == 4, "generated Annex B fixtures contain four access units each");
    using (var decoder = new H264Decoder(tools))
    {
        decoder.Reset(320, 240);
        var frames = landscape.SelectMany((packet, index) => decoder.Decode(packet, index * 100_000)).ToList();
        Check(frames.Count == 4 && frames.All(f => f.Width == 320 && f.Height == 240 && f.Bgra.Length == 320 * 240 * 4), "real bundled FFmpeg decodes landscape H264 to BGRA");
        Check(frames[0].Bgra.Where((_, index) => index % 4 == 3).All(a => a == 255), "BGRA output is fully opaque");
        Check(frames[0].Bgra.Min() < 20 && frames[0].Bgra.Max() > 230, "real decoded test pattern has non-flat pixels");
        decoder.Reset(240, 320);
        var rotated = portrait.SelectMany((packet, index) => decoder.Decode(packet, index * 100_000)).ToList();
        Check(rotated.Count == 4 && rotated.All(f => f.Width == 240 && f.Height == 320), "real decoder reset supports portrait rotation");
        decoder.Reset(320, 240);
        var firstVcl = FirstVclOffset(landscape[0]);
        Check(firstVcl > 0 && decoder.Decode(landscape[0].AsSpan(0, firstVcl), 0).Count == 0, "standalone SPS/PPS codec configuration waits for a video slice");
        Check(decoder.Decode(landscape[0].AsSpan(firstVcl), 0).Count == 1, "separate codec configuration and IDR decode as one frame");
        Throws<InvalidDataException>(() => decoder.Decode(new byte[] { 1, 2, 3, 4 }, 0), "reject non-Annex-B payload");
    }

    string token;
    string certificateHash;
    var identityDirectory = Path.Combine(scratch, "identity");
    var status = new List<string>();
    await using (var receiver = new ReceiverService(tools, identityDirectory))
    {
        receiver.StatusChanged += message => { lock (status) status.Add(message); };
        Check((receiver.PairingExpiresAt - DateTimeOffset.UtcNow).TotalSeconds is > 110 and <= 120, "pairing code expires after two minutes");
        var normalized = StreamProtocol.NormalizePairingCode(receiver.PairingCode);
        certificateHash = receiver.CertificateSha256;
        Check(normalized[..16] == StreamProtocol.Base32(Convert.FromHexString(certificateHash)[..10]), "pair code binds the TLS certificate prefix");
        await receiver.StartAsync(0);
        Check(receiver.IsListening && receiver.Port > 0, "real TLS listener starts on an ephemeral test port");

        try
        {
            await using var wrongPin = await OpenAsync(receiver.Port, new string('A', 64));
            throw new Exception("FAIL: wrong TLS pin accepted");
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException) { Check(true, "wrong TLS certificate pin rejected before sending credentials"); }

        await using (var wrong = await OpenAsync(receiver.Port, certificateHash))
        {
            var auth = await AuthAsync(wrong, "pair", new string('A', 16), "");
            Check(!auth.Ok, "wrong one-time secret rejected over real TLS");
        }
        typeof(ReceiverService).GetField("pairingExpiresAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(receiver, DateTimeOffset.UtcNow.AddSeconds(-1));
        await using (var expired = await OpenAsync(receiver.Port, certificateHash))
        { Check(!(await AuthAsync(expired, "pair", normalized[16..], "")).Ok, "synthetically expired deadline rejects the otherwise correct secret"); }
        receiver.RefreshPairingCode(); normalized = StreamProtocol.NormalizePairingCode(receiver.PairingCode);
        await using (var paired = await OpenAsync(receiver.Port, certificateHash))
        {
            var auth = await AuthAsync(paired, "pair", normalized[16..], "");
            Check(auth.Ok && auth.Token?.Length == 43 && auth.PcName == receiver.PcName, "real TLS pairing returns a 256-bit token and PC name");
            token = auth.Token!;
        }
        await WaitForIdle();
        Check(receiver.PairingExpiresAt <= DateTimeOffset.UtcNow, "pairing secret is single-use");
        await using (var reused = await OpenAsync(receiver.Port, certificateHash))
        { Check(!(await AuthAsync(reused, "pair", normalized[16..], "")).Ok, "consumed pair code cannot be reused"); }
        await WaitForIdle();

        var received = new List<VideoFrame>();
        var firstFrames = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rotatedFrames = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FrameReceived += video =>
        {
            received.Add(video);
            if (received.Count(f => f.Width == 320) >= 4) firstFrames.TrySetResult();
            if (received.Count(f => f.Width == 240) >= 4) rotatedFrames.TrySetResult();
        };
        receiver.StreamStopped += () => stopped.TrySetResult();
        await using (var streaming = await OpenAsync(receiver.Port, certificateHash))
        {
            Check((await AuthAsync(streaming, "resume", "", token)).Ok, "token resumes an authenticated TLS connection");
            await SendConfig(streaming, 320, 240);
            foreach (var accessUnit in landscape) await SendVideo(streaming, accessUnit);
            await firstFrames.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(received.Count(f => f.Width == 320) == 4, "TLS to actual FFmpeg to FrameReceived end-to-end landscape");
            await SendConfig(streaming, 240, 320);
            foreach (var accessUnit in portrait) await SendVideo(streaming, accessUnit);
            await rotatedFrames.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(received.Count(f => f.Width == 240) == 4, "configuration change resets decoder over the real network path");
            await StreamProtocol.WritePayloadAsync(streaming.Stream, new byte[] { StreamProtocol.Ping }, default);
            var pong = await StreamProtocol.ReadPayloadAsync(streaming.Stream, 100, default).WaitAsync(TimeSpan.FromSeconds(2));
            Check(pong.SequenceEqual(new byte[] { StreamProtocol.Ping }), "application heartbeat round trip");
            await StreamProtocol.WritePayloadAsync(streaming.Stream, new byte[] { StreamProtocol.Stop }, default);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(true, "explicit STOP clears the active stream");
        }
        await WaitForIdle();
        await using (var invalid = await OpenAsync(receiver.Port, certificateHash))
        {
            Check((await AuthAsync(invalid, "resume", "", token)).Ok, "receiver remains usable after STOP");
            await invalid.Stream.WriteAsync(new byte[] { 127, 255, 255, 255 });
            var response = new byte[1];
            Check(await invalid.Stream.ReadAsync(response).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == 0, "oversized authenticated packet closes the connection");
        }
        await WaitForIdle();
        var pending = new TcpClient(); await pending.ConnectAsync("127.0.0.1", receiver.Port);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var stop1 = receiver.StopAsync(); var stop2 = receiver.StopAsync();
        Check(ReferenceEquals(stop1, stop2), "concurrent Stop calls share the same completion task");
        await Task.WhenAll(stop1, stop2).WaitAsync(TimeSpan.FromSeconds(2));
        pending.Dispose();
        Check(watch.Elapsed < TimeSpan.FromSeconds(2) && !receiver.IsListening, "Stop cancels even a client stalled before TLS");
        var encrypted = File.ReadAllBytes(Path.Combine(identityDirectory, "receiver-identity.dpapi"));
        Check(!Encoding.UTF8.GetString(encrypted).Contains(token, StringComparison.Ordinal), "stored Windows credentials are DPAPI protected and do not contain the token");
        Check(status.All(s => !s.Contains(token, StringComparison.Ordinal) && !s.Contains(normalized[16..], StringComparison.Ordinal)), "status events never reveal credentials");
    }

    await using (var restarted = new ReceiverService(tools, identityDirectory))
    {
        Check(restarted.CertificateSha256 == certificateHash, "Windows DPAPI identity survives receiver recreation");
        await restarted.StartAsync(0);
        await using (var resumed = await OpenAsync(restarted.Port, certificateHash))
        { Check((await AuthAsync(resumed, "resume", "", token)).Ok, "saved token survives receiver recreation"); }
        await WaitForIdle();
        await using var active = await OpenAsync(restarted.Port, certificateHash);
        Check((await AuthAsync(active, "resume", "", token)).Ok, "authenticated session can be explicitly revoked");
        await restarted.RevokeAllAsync();
        var closed = new byte[1];
        Check(await active.Stream.ReadAsync(closed).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == 0, "revocation immediately closes the active TLS stream");
        await using (var revoked = await OpenAsync(restarted.Port, certificateHash))
        { Check(!(await AuthAsync(revoked, "resume", "", token)).Ok, "revocation rejects previously paired tokens"); }
        var wrongSecret = StreamProtocol.NormalizePairingCode(restarted.PairingCode)[16..] == new string('A', 16) ? new string('B', 16) : new string('A', 16);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await using var rejected = await OpenAsync(restarted.Port, certificateHash);
            Check(!(await AuthAsync(rejected, "pair", wrongSecret, "")).Ok, "invalid authentication is counted for rate limiting");
        }
        try { await using var limited = await OpenAsync(restarted.Port, certificateHash); throw new Exception("FAIL: rate limited client accepted"); }
        catch (Exception ex) when (ex is AuthenticationException or IOException) { Check(true, "five recent failures rate-limit the source before another TLS handshake"); }
        await restarted.StopAsync();
    }
    Console.WriteLine($"All {checks} streaming checks passed. Actual TLS and FFmpeg DLL decode; no Android hardware used.");
}
finally
{
    var temporaryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajorStreamingTests")) + Path.DirectorySeparatorChar;
    if (Path.GetFullPath(scratch).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) Directory.Delete(scratch, true);
}

static string FindRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null) { if (Directory.Exists(Path.Combine(directory.FullName, "tools", "scrcpy"))) return directory.FullName; directory = directory.Parent; }
    throw new DirectoryNotFoundException("Passa la cartella Major come argomento.");
}
static string? Option(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}
static async Task ServeAsync(string[] arguments, string toolsDirectory)
{
    var directory = Option(arguments, "--identity") ?? throw new ArgumentException("Passa --identity con una cartella privata per le credenziali di prova.");
    var infoPath = Option(arguments, "--info") ?? throw new ArgumentException("Passa --info con un file privato nell'area work, mai nei deliverable.");
    var statsPath = Option(arguments, "--stats") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(infoPath))!, "receiver-stats.json");
    var port = int.Parse(Option(arguments, "--port") ?? StreamProtocol.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(infoPath))!);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statsPath))!);
    await using var receiver = new ReceiverService(toolsDirectory, directory);
    var sync = new object();
    long count = 0; var stopped = 0; var width = 0; var height = 0; var status = "Inizializzazione";
    var lastWrite = DateTimeOffset.MinValue;
    void SaveInfo()
    {
        // Contains an active one-time secret: callers must keep this file in private work storage.
        var info = new { pairingCode = receiver.PairingCode, certificateSha256 = receiver.CertificateSha256, port = receiver.Port, pcName = receiver.PcName, expiresAt = receiver.PairingExpiresAt };
        lock (sync) File.WriteAllText(infoPath, JsonSerializer.Serialize(info));
    }
    void SaveStats(bool force)
    {
        var listening = receiver.IsListening;
        var activePort = receiver.Port;
        lock (sync)
        {
            if (!force && DateTimeOffset.UtcNow - lastWrite < TimeSpan.FromSeconds(1)) return;
            lastWrite = DateTimeOffset.UtcNow;
            var snapshot = JsonSerializer.Serialize(new { listening, port = activePort, frames = count, width, height, streamStops = stopped, status, observedAt = lastWrite });
            var temporary = statsPath + ".tmp"; File.WriteAllText(temporary, snapshot); File.Move(temporary, statsPath, true);
        }
    }
    receiver.PairingChanged += SaveInfo;
    receiver.StatusChanged += text => { lock (sync) status = text; SaveStats(true); };
    receiver.FrameReceived += frame => { lock (sync) { count++; width = frame.Width; height = frame.Height; } SaveStats(false); };
    receiver.StreamStopped += () => { lock (sync) stopped++; SaveStats(true); };
    try
    {
        await receiver.StartAsync(port); SaveInfo(); SaveStats(true);
        Console.WriteLine("Receiver ready. Private pairing info saved; commands: stop, start, cycle, code, quit.");
        string? command;
        while ((command = await Console.In.ReadLineAsync()) is not null)
        {
            switch (command.Trim().ToLowerInvariant())
            {
                case "stop": await receiver.StopAsync(); SaveStats(true); Console.WriteLine("Receiver stopped."); break;
                case "start": await receiver.StartAsync(port); SaveInfo(); SaveStats(true); Console.WriteLine("Receiver restarted."); break;
                case "cycle":
                    await receiver.StopAsync(); SaveStats(true); await Task.Delay(1500); await receiver.StartAsync(port); SaveInfo(); SaveStats(true); Console.WriteLine("Receiver cycled."); break;
                case "code": receiver.RefreshPairingCode(); SaveInfo(); Console.WriteLine("Private pairing code renewed."); break;
                case "quit": await receiver.StopAsync(); SaveStats(true); return;
                default: Console.WriteLine("Commands: stop, start, cycle, code, quit."); break;
            }
        }
    }
    finally
    {
        await receiver.StopAsync(); SaveStats(true);
        // Do not leave a reusable pairing code in an unattended diagnostic artifact.
        File.WriteAllText(infoPath, JsonSerializer.Serialize(new { closed = true, port }));
    }
}
static List<byte[]> SplitAccessUnits(byte[] bytes)
{
    var starts = new List<int>();
    for (var i = 0; i + 4 < bytes.Length; i++)
    {
        if (bytes[i] != 0 || bytes[i + 1] != 0) continue;
        var prefix = bytes[i + 2] == 1 ? 3 : bytes[i + 2] == 0 && bytes[i + 3] == 1 ? 4 : 0;
        if (prefix == 0) continue;
        if ((bytes[i + prefix] & 31) == 9) starts.Add(i);
        i += prefix;
    }
    return starts.Select((start, index) => bytes[start..(index + 1 < starts.Count ? starts[index + 1] : bytes.Length)]).ToList();
}
static int FirstVclOffset(byte[] bytes)
{
    for (var i = 0; i + 4 < bytes.Length; i++)
    {
        if (bytes[i] != 0 || bytes[i + 1] != 0) continue;
        var prefix = bytes[i + 2] == 1 ? 3 : bytes[i + 2] == 0 && bytes[i + 3] == 1 ? 4 : 0;
        if (prefix == 0) continue;
        if ((bytes[i + prefix] & 31) is >= 1 and <= 5) return i;
        i += prefix;
    }
    return -1;
}
static async Task<TestConnection> OpenAsync(int port, string expectedHash)
{
    var client = new TcpClient { NoDelay = true }; await client.ConnectAsync("127.0.0.1", port);
    var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), Convert.FromHexString(expectedHash)));
    try
    {
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "Major", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck });
        return new(client, ssl);
    }
    catch { ssl.Dispose(); client.Dispose(); throw; }
}
static async Task<AuthResult> AuthAsync(TestConnection connection, string kind, string secret, string token)
{
    await StreamProtocol.WriteJsonAsync(connection.Stream, new { version = 1, kind, secret, token, device = "Original synthetic stream fixture" }, default);
    return await StreamProtocol.ReadJsonAsync<AuthResult>(connection.Stream, default).WaitAsync(TimeSpan.FromSeconds(3));
}
static async Task SendConfig(TestConnection connection, int width, int height)
{
    var payload = new byte[13]; payload[0] = StreamProtocol.Configure;
    BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(1), width); BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(5), height); BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(9), 10);
    await StreamProtocol.WritePayloadAsync(connection.Stream, payload, default);
}
static async Task SendVideo(TestConnection connection, byte[] annexB)
{
    var payload = new byte[13 + annexB.Length]; payload[0] = StreamProtocol.Video;
    BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1), 100_000); BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(9), 1); annexB.CopyTo(payload, 13);
    await StreamProtocol.WritePayloadAsync(connection.Stream, payload, default);
}
static Task WaitForIdle() => Task.Delay(100); // Network EOF cleanup, bounded below normal UI polling.
internal sealed record AuthResult(bool Ok, string? PcName, string? Token, string? Error);
internal sealed record TestConnection(TcpClient Client, SslStream Stream) : IAsyncDisposable
{
    public ValueTask DisposeAsync() { Stream.Dispose(); Client.Dispose(); return ValueTask.CompletedTask; }
}
