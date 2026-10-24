using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace Major.Streaming;

public sealed class ReceiverService : IAsyncDisposable
{
    private readonly string toolsDirectory;
    private readonly CredentialStore credentials;
    private readonly object sync = new();
    private readonly ConcurrentDictionary<Guid, Connection> connections = new();
    private readonly Dictionary<string, List<DateTimeOffset>> failures = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim streamGate = new(1, 1);
    private CancellationTokenSource? lifetime;
    private TcpListener? listener;
    private Task accepting = Task.CompletedTask, stopping = Task.CompletedTask;
    private string pairingSecret = "";
    private string pairingCode = "";
    private DateTimeOffset pairingExpiresAt;
    private TcpClient? streamingClient;
    private bool disposed;

    public ReceiverService(string toolsDirectory, string credentialDirectory)
    {
        this.toolsDirectory = Path.GetFullPath(toolsDirectory);
        credentials = new(credentialDirectory);
        CertificateSha256 = Convert.ToHexString(SHA256.HashData(credentials.Certificate.RawData));
        RefreshPairingCode();
    }
    public string PairingCode { get { lock (sync) return pairingCode; } }
    public DateTimeOffset PairingExpiresAt { get { lock (sync) return pairingExpiresAt; } }
    public string PcName { get; } = Environment.MachineName;
    public string CertificateSha256 { get; }
    public int Port { get; private set; }
    public bool IsListening { get { lock (sync) return listener is not null; } }
    public event Action<string>? StatusChanged;
    public event Action<VideoFrame>? FrameReceived;
    public event Action? PairingChanged;
    public event Action? StreamStopped;

    public void RefreshPairingCode()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pairingSecret = StreamProtocol.Base32(RandomNumberGenerator.GetBytes(10));
            var raw = StreamProtocol.Base32(Convert.FromHexString(CertificateSha256)[..10]) + pairingSecret;
            pairingCode = string.Join('-', Enumerable.Range(0, 8).Select(i => raw.Substring(i * 4, 4)));
            pairingExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        }
        Emit(PairingChanged);
    }

    public Task StartAsync(int port = StreamProtocol.DefaultPort)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (listener is not null) return Task.CompletedTask;
            if (!stopping.IsCompleted) throw new InvalidOperationException("Attendi la chiusura del ricevitore.");
            if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            using (var decoderCheck = new H264Decoder(toolsDirectory)) { }
            listener = new TcpListener(IPAddress.Any, port);
            try { listener.Start(4); }
            catch { listener = null; throw; }
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            lifetime = new();
            accepting = AcceptAsync(listener, lifetime.Token);
        }
        EmitStatus("In attesa del telefono. Associa con il codice temporaneo.");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (sync)
        {
            if (listener is null) return stopping;
            lifetime!.Cancel(); listener.Stop(); listener = null;
            foreach (var connection in connections.Values) connection.Client.Dispose();
            stopping = CompleteStopAsync(accepting, lifetime);
            lifetime = null;
            return stopping;
        }
    }
    private async Task CompleteStopAsync(Task acceptTask, CancellationTokenSource cancellation)
    {
        try
        {
            try { await acceptTask.ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
            var pending = connections.Values.ToArray();
            foreach (var connection in pending) connection.Client.Dispose();
            await Task.WhenAll(pending.Select(c => c.Completion)).ConfigureAwait(false);
        }
        finally { cancellation.Dispose(); Emit(StreamStopped); EmitStatus("Ricevitore arrestato."); }
    }
    public async Task RevokeAllAsync()
    {
        Task[] pending;
        lock (sync)
        {
            credentials.RevokeAll();
            RefreshPairingCode();
            foreach (var connection in connections.Values) connection.Client.Dispose();
            pending = connections.Values.Select(c => c.Completion).ToArray();
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
        Emit(StreamStopped); EmitStatus("Associazioni revocate. Usa il nuovo codice temporaneo.");
    }

    private async Task AcceptAsync(TcpListener server, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await server.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or SocketException) { break; }
            if (connections.Count >= 4) { client.Dispose(); continue; }
            client.NoDelay = true;
            var id = Guid.NewGuid(); var connection = new Connection(client);
            connections[id] = connection;
            connection.Completion = HandleAsync(id, client, cancellationToken);
        }
    }
    private async Task HandleAsync(Guid id, TcpClient client, CancellationToken cancellationToken)
    {
        var ownsStream = false; var authenticated = false;
        var finalStatus = "In attesa del telefono. La condivisione è terminata.";
        var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        try
        {
            if (IsRateLimited(address)) return;
            using var ssl = new SslStream(client.GetStream(), false);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = credentials.Certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false, CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
                }, deadline.Token).ConfigureAwait(false);
                var request = await StreamProtocol.ReadJsonAsync<AuthRequest>(ssl, deadline.Token).ConfigureAwait(false);
                ownsStream = streamGate.Wait(0);
                if (!ownsStream) { await StreamProtocol.WriteJsonAsync(ssl, new AuthResponse(false, null, null, "Ricevitore occupato da un'altra connessione."), deadline.Token).ConfigureAwait(false); return; }
                string? token;
                lock (sync)
                {
                    token = Authenticate(request);
                    if (token is not null) streamingClient = client;
                }
                if (token is null)
                {
                    RegisterFailure(address);
                    await StreamProtocol.WriteJsonAsync(ssl, new AuthResponse(false, null, null, "Associazione non valida o scaduta. Richiedi un nuovo codice al PC."), deadline.Token).ConfigureAwait(false);
                    return;
                }
                authenticated = true;
                await StreamProtocol.WriteJsonAsync(ssl, new AuthResponse(true, PcName, token, null), deadline.Token).ConfigureAwait(false);
                EmitStatus("Telefono autenticato. In attesa della condivisione esplicita.");
            }
            using var decoder = new H264Decoder(toolsDirectory);
            var configured = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                var message = await StreamProtocol.ReadPayloadAsync(ssl, StreamProtocol.MaxFrameBytes, deadline.Token).ConfigureAwait(false);
                switch (message[0])
                {
                    case StreamProtocol.Configure:
                        if (message.Length != 13) throw new InvalidDataException("Configurazione video non valida.");
                        var width = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(1));
                        var height = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(5));
                        var fps = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(9));
                        StreamProtocol.ValidateConfiguration(width, height, fps);
                        decoder.Reset(width, height); configured = true;
                        EmitStatus($"Ricezione {width} × {height} · massimo {fps} fps.");
                        break;
                    case StreamProtocol.Video:
                        if (!configured || message.Length < 17) throw new InvalidDataException("Video senza configurazione valida.");
                        var pts = BinaryPrimitives.ReadInt64BigEndian(message.AsSpan(1));
                        var flags = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(9));
                        if (pts < 0 || (flags & ~3) != 0) throw new InvalidDataException("Metadati video non validi.");
                        foreach (var frame in decoder.Decode(message.AsSpan(13), pts)) EmitFrame(frame);
                        break;
                    case StreamProtocol.Stop:
                        if (message.Length != 1) throw new InvalidDataException("Messaggio STOP non valido.");
                        return;
                    case StreamProtocol.Ping:
                        if (message.Length != 1) throw new InvalidDataException("Messaggio PING non valido.");
                        await StreamProtocol.WritePayloadAsync(ssl, new byte[] { StreamProtocol.Ping }, deadline.Token).ConfigureAwait(false);
                        break;
                    default: throw new InvalidDataException("Tipo messaggio sconosciuto.");
                }
            }
        }
        catch (EndOfStreamException) { }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException or System.Text.Json.JsonException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException or CryptographicException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                finalStatus = authenticated
                    ? ex is InvalidDataException or NotSupportedException ? "Video non supportato o non valido. Prova una qualità inferiore e riavvia la condivisione." : "Trasmissione interrotta. Controlla rete e condivisione sul telefono."
                    : "Connessione non autorizzata o messaggio non valido.";
                if (!authenticated) EmitStatus(finalStatus);
            }
        }
        finally
        {
            client.Dispose();
            if (ownsStream)
            {
                lock (sync) if (ReferenceEquals(streamingClient, client)) streamingClient = null;
                if (authenticated) { Emit(StreamStopped); EmitStatus(finalStatus); }
                streamGate.Release();
            }
            connections.TryRemove(id, out _);
        }
    }
    private string? Authenticate(AuthRequest request)
    {
        if (request.Version != 1 || request.Device is null || request.Device.Length > 100 || request.Device.Any(char.IsControl)) return null;
        if (request.Kind == "resume") return credentials.ContainsToken(request.Token) ? request.Token : null;
        if (request.Kind != "pair" || request.Secret is null || request.Secret.Length != 16) return null;
        string? token;
        lock (sync)
        {
            var given = System.Text.Encoding.ASCII.GetBytes(request.Secret);
            var expected = System.Text.Encoding.ASCII.GetBytes(pairingSecret);
            if (DateTimeOffset.UtcNow >= pairingExpiresAt || !CryptographicOperations.FixedTimeEquals(given, expected)) return null;
            token = credentials.AddToken(request.Device);
            pairingSecret = ""; pairingExpiresAt = DateTimeOffset.MinValue;
        }
        Emit(PairingChanged);
        return token;
    }
    private bool IsRateLimited(string address)
    {
        lock (sync)
        {
            if (!failures.TryGetValue(address, out var list)) return false;
            list.RemoveAll(t => t < DateTimeOffset.UtcNow.AddMinutes(-1)); return list.Count >= 5;
        }
    }
    private void RegisterFailure(string address)
    {
        lock (sync)
        {
            if (failures.Count >= 256 && !failures.ContainsKey(address)) failures.Clear();
            if (!failures.TryGetValue(address, out var list)) failures[address] = list = [];
            list.Add(DateTimeOffset.UtcNow);
        }
    }
    private void EmitStatus(string status) { foreach (var handler in StatusChanged?.GetInvocationList() ?? []) { try { ((Action<string>)handler)(status); } catch { } } }
    private void EmitFrame(VideoFrame frame) { foreach (var handler in FrameReceived?.GetInvocationList() ?? []) { try { ((Action<VideoFrame>)handler)(frame); } catch { } } }
    private static void Emit(Action? action) { foreach (var handler in action?.GetInvocationList() ?? []) { try { ((Action)handler)(); } catch { } } }
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (sync) { if (disposed) return; disposed = true; credentials.Certificate.Dispose(); streamGate.Dispose(); }
    }
    private sealed class Connection(TcpClient client) { public TcpClient Client { get; } = client; public Task Completion { get; set; } = Task.CompletedTask; }
    private sealed record AuthRequest(int Version, string? Kind, string? Secret, string? Token, string? Device);
    private sealed record AuthResponse(bool Ok, string? PcName, string? Token, string? Error);
}
