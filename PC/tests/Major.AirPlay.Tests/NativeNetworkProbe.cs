using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

internal static class NativeNetworkProbe
{
    internal sealed record Record(string Name, ushort Type, byte[] Data, string? Target, ushort Port, Dictionary<string, string> Text);
    public static async Task<List<Record>> DiscoverAsync(string service, string expectedInstance)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Distinct().ToArray();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tasks = addresses.Select(QueryAsync).ToList();
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks); tasks.Remove(done);
            try { var records = await done; if (records.Any(r => r.Name == service && r.Type == 12 && r.Target == expectedInstance)) { cancellation.Cancel(); return records; } }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException) { }
        }
        throw new IOException("No native mDNS reply observed on active IPv4 interfaces");

        async Task<List<Record>> QueryAsync(IPAddress address)
        {
            using var client = new UdpClient(new IPEndPoint(address, 0));
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            using var query = new MemoryStream();
            query.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            foreach (var label in service.Split('.')) { var bytes = Encoding.UTF8.GetBytes(label); query.WriteByte((byte)bytes.Length); query.Write(bytes); }
            query.Write(new byte[] { 0, 0, 12, 128, 1 });
            await client.SendAsync(query.ToArray(), new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), cancellation.Token);
            while (true)
            {
                var response = await client.ReceiveAsync(cancellation.Token);
                var records = Parse(response.Buffer);
                if (records.Any(r => r.Name == service && r.Type == 12 && r.Target == expectedInstance)) return records;
            }
        }
    }
    public static List<Record> Parse(byte[] bytes)
    {
        if (bytes.Length < 12 || (bytes[2] & 128) == 0) throw new IOException("Invalid DNS response");
        int questions = U16(4), answers = U16(6) + U16(8) + U16(10), offset = 12;
        for (int i = 0; i < questions; i++) { _ = Name(ref offset); offset += 4; }
        var records = new List<Record>();
        for (int i = 0; i < answers; i++)
        {
            string name = Name(ref offset); ushort type = U16(offset); int length = U16(offset + 8); offset += 10;
            if (offset + length > bytes.Length) throw new IOException("Truncated DNS record");
            var data = bytes.AsSpan(offset, length).ToArray(); string? target = null; ushort port = 0;
            var text = new Dictionary<string, string>(StringComparer.Ordinal);
            if (type == 12) { int position = offset; target = Name(ref position); }
            if (type == 33 && length >= 6) { port = U16(offset + 4); int position = offset + 6; target = Name(ref position); }
            if (type == 16)
            {
                for (int p = 0; p < data.Length;)
                {
                    int size = data[p++]; if (p + size > data.Length) throw new IOException("Truncated DNS TXT");
                    var entry = Encoding.UTF8.GetString(data, p, size); p += size;
                    int split = entry.IndexOf('='); if (split > 0) text[entry[..split]] = entry[(split + 1)..];
                }
            }
            records.Add(new(name, type, data, target, port, text)); offset += length;
        }
        return records;
        ushort U16(int position) => position + 2 <= bytes.Length ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position)) : throw new IOException("Truncated DNS field");
        string Name(ref int position)
        {
            var labels = new List<string>(); int cursor = position, after = -1;
            for (int limit = 0; limit < 128; limit++)
            {
                if (cursor >= bytes.Length) throw new IOException("Truncated DNS name");
                int size = bytes[cursor++];
                if ((size & 192) == 192)
                {
                    if (cursor >= bytes.Length) throw new IOException("Truncated DNS pointer");
                    after = after < 0 ? cursor + 1 : after; cursor = ((size & 63) << 8) | bytes[cursor]; continue;
                }
                if (size == 0) { position = after < 0 ? cursor : after; return string.Join('.', labels); }
                if (size > 63 || cursor + size > bytes.Length) throw new IOException("Invalid DNS label");
                labels.Add(Encoding.UTF8.GetString(bytes, cursor, size)); cursor += size;
            }
            throw new IOException("DNS compression loop");
        }
    }
    public static async Task<(string Headers, byte[] Body)> RequestAsync(string method, string path, byte[]? body = null, string? authorization = null)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, 7001, cancellation.Token);
        var stream = client.GetStream(); body ??= [];
        var header = $"{method} {path} RTSP/1.0\r\nCSeq: 1\r\nUser-Agent: MajorIntegrationTest\r\nContent-Type: application/x-apple-binary-plist\r\nContent-Length: {body.Length}\r\n";
        if (authorization is not null) header += "Authorization: " + authorization + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header + "\r\n"), cancellation.Token);
        await stream.WriteAsync(body, cancellation.Token);
        using var bytes = new MemoryStream(); var one = new byte[1];
        while (bytes.Length < 16384)
        {
            if (await stream.ReadAsync(one, cancellation.Token) == 0) throw new IOException("RTSP reply ended before headers");
            bytes.WriteByte(one[0]);
            if (bytes.Length >= 4 && Encoding.ASCII.GetString(bytes.GetBuffer(), (int)bytes.Length - 4, 4) == "\r\n\r\n") break;
        }
        var headers = Encoding.ASCII.GetString(bytes.ToArray());
        int length = 0;
        foreach (string line in headers.Split("\r\n")) if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
        if (length < 0 || length > 1024 * 1024) throw new IOException("RTSP body too large");
        var responseBody = new byte[length]; await stream.ReadExactlyAsync(responseBody, cancellation.Token);
        return (headers, responseBody);
    }
}
