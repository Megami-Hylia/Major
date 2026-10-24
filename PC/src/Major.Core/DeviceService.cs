using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Major.Core;

public sealed class DeviceService(ToolPaths paths)
{
    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(paths.Adb, ["devices", "-l"], TimeSpan.FromSeconds(12), cancellationToken);
        EnsureSuccess(result);
        return ParseDevices(result.Output);
    }

    public async Task<string> PairAsync(string endpoint, string code, CancellationToken cancellationToken = default)
    {
        endpoint = ValidateEndpoint(endpoint);
        if (!Regex.IsMatch(code, @"\A[0-9]{6}\z"))
            throw new ArgumentException("Inserisci il codice di associazione di 6 cifre visualizzato sul telefono.", nameof(code));
        // Keep the transient pairing code out of the process command line and logs.
        var result = await ProcessRunner.RunAsync(paths.Adb, ["pair", endpoint], TimeSpan.FromSeconds(45), cancellationToken, code + "\n");
        result = result with { Output = result.Output.Replace(code, "******", StringComparison.Ordinal), Error = result.Error.Replace(code, "******", StringComparison.Ordinal) };
        EnsureSuccess(result);
        if (!result.Output.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
            throw new IOException(Describe(result, "Associazione non riuscita. Verifica porta e codice sul telefono."));
        return result.Output.Trim();
    }

    public async Task<string> ConnectAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        endpoint = ValidateEndpoint(endpoint);
        var result = await ProcessRunner.RunAsync(paths.Adb, ["connect", endpoint], TimeSpan.FromSeconds(20), cancellationToken);
        EnsureSuccess(result);
        if (!result.Output.Contains("connected to", StringComparison.OrdinalIgnoreCase))
            throw new IOException(Describe(result, "Connessione non riuscita. Usa la porta di connessione, diversa da quella di associazione."));
        return result.Output.Trim();
    }

    public async Task<string> DisconnectAsync(string serial, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        var endpoint = ValidateEndpoint(serial);
        var result = await ProcessRunner.RunAsync(paths.Adb, ["disconnect", endpoint], TimeSpan.FromSeconds(12), cancellationToken);
        EnsureSuccess(result);
        return result.Output.Trim();
    }

    public async Task ScreenshotAsync(string serial, string path, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        var destination = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(destination), ".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Lo screenshot richiede un file .png.", nameof(path));
        if (File.Exists(destination)) throw new IOException("Il file esiste già. Scegli un nuovo nome.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var process = new Process { StartInfo = ProcessRunner.CreateInfo(paths.Adb, ["-s", serial, "exec-out", "screencap", "-p"]) };
        Task<string>? errorTask = null;
        try
        {
            process.Start();
            errorTask = ProcessRunner.ReadBoundedAsync(process.StandardError, 16 * 1024);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, deadline.Token)) != 0)
                {
                    total += read;
                    if (total > 128 * 1024 * 1024) throw new IOException("Screenshot oltre il limite di 128 MiB.");
                    await file.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
                }
            }
            await process.WaitForExitAsync(deadline.Token);
            var error = await errorTask;
            if (process.ExitCode != 0) throw new IOException("Screenshot non riuscito: " + error.Trim());
            var signature = new byte[8];
            using (var check = File.OpenRead(temporary))
            {
                if (check.Read(signature) != 8 || !signature.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                    throw new IOException("ADB non ha restituito un'immagine PNG valida.");
            }
            File.Move(temporary, destination);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Screenshot scaduto. Controlla che il dispositivo sia ancora collegato.");
        }
        finally
        {
            ProcessRunner.Kill(process);
            if (errorTask is not null) await errorTask;
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static IReadOnlyList<DeviceInfo> ParseDevices(string output)
    {
        var devices = new List<DeviceInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*')) continue;
            var columns = Regex.Split(line, @"\s+");
            if (columns.Length < 2 || columns[1] is not ("device" or "unauthorized" or "offline" or "recovery" or "sideload" or "bootloader" or "no")) continue;
            var model = columns.Skip(2).FirstOrDefault(x => x.StartsWith("model:", StringComparison.Ordinal))?[6..].Replace('_', ' ');
            var state = columns[1] == "no" ? "no permissions" : columns[1];
            var connection = columns[0].StartsWith("emulator-", StringComparison.Ordinal) ? "Emulatore" :
                columns[0].Contains(':') || columns[0].Contains("_adb-tls-", StringComparison.Ordinal) ? "Wi-Fi" : "USB";
            devices.Add(new(columns[0], state, string.IsNullOrWhiteSpace(model) ? "Android" : model, connection));
        }
        return devices.DistinctBy(x => x.Serial).ToArray();
    }

    internal static string ValidateEndpoint(string value)
    {
        value = value.Trim();
        string host, portText;
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end <= 1 || end + 2 >= value.Length || value[end + 1] != ':') throw InvalidEndpoint();
            host = value[1..end];
            portText = value[(end + 2)..];
            if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) throw InvalidEndpoint();
        }
        else
        {
            var colon = value.LastIndexOf(':');
            if (colon <= 0 || value[..colon].Contains(':')) throw InvalidEndpoint();
            host = value[..colon];
            portText = value[(colon + 1)..];
            if (host.Length > 253 || !Regex.IsMatch(host, @"\A[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?\z") || Uri.CheckHostName(host) == UriHostNameType.Unknown) throw InvalidEndpoint();
        }
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535) throw InvalidEndpoint();
        return value;
    }

    internal static void ValidateSerial(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || serial.Length > 512 || serial.StartsWith('-') || serial.Any(char.IsWhiteSpace) || serial.Any(char.IsControl))
            throw new ArgumentException("Identificativo dispositivo non valido.", nameof(serial));
    }

    private static ArgumentException InvalidEndpoint() => new("Indirizzo non valido. Usa IP:porta, nome:porta oppure [IPv6]:porta.", "endpoint");
    private static void EnsureSuccess(ProcessResult result)
    {
        if (result.ExitCode != 0) throw new IOException(Describe(result, "ADB ha restituito un errore."));
    }
    private static string Describe(ProcessResult result, string fallback)
    {
        var text = (result.Error + "\n" + result.Output).Trim();
        return text.Length == 0 ? fallback : text[..Math.Min(text.Length, 2000)];
    }
}
