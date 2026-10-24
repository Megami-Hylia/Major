using System.Buffers.Binary;
using System.Text.Json;

namespace Major.Streaming;

public static class StreamProtocol
{
    public const int DefaultPort = 49977;
    public const int MaxFrameBytes = 8 * 1024 * 1024;
    public const int MaxPixels = 8_500_000;
    public const byte Configure = 1, Video = 2, Stop = 3, Ping = 4;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false };

    public static string Base32(ReadOnlySpan<byte> bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new System.Text.StringBuilder((bytes.Length * 8 + 4) / 5);
        var accumulator = 0; var bits = 0;
        foreach (var value in bytes)
        {
            accumulator = (accumulator << 8) | value; bits += 8;
            while (bits >= 5) { bits -= 5; output.Append(alphabet[(accumulator >> bits) & 31]); }
        }
        if (bits > 0) output.Append(alphabet[(accumulator << (5 - bits)) & 31]);
        return output.ToString();
    }

    public static string NormalizePairingCode(string code)
    {
        var normalized = code.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (normalized.Length != 32 || normalized.Any(c => !(c is >= 'A' and <= 'Z' or >= '2' and <= '7')))
            throw new ArgumentException("Usa il codice temporaneo completo di 32 caratteri mostrato dal PC.");
        return normalized;
    }

    public static void ValidateConfiguration(int width, int height, int fps)
    {
        if (width is < 128 or > 4096 || height is < 128 or > 4096 || (long)width * height > MaxPixels || fps is < 1 or > 120)
            throw new InvalidDataException("Dimensioni o frame rate video fuori limite.");
    }
    public static bool HasAnnexBStartCode(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == 0 && data[1] == 0 && (data[2] == 1 || (data[2] == 0 && data[3] == 1));

    internal static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken token)
    {
        var bytes = await ReadPayloadAsync(stream, 16 * 1024, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("Messaggio di associazione vuoto.");
    }
    internal static async Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await WritePayloadAsync(stream, bytes, token).ConfigureAwait(false);
    }
    internal static async Task<byte[]> ReadPayloadAsync(Stream stream, int limit, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var count = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (count == 0 || count > limit) throw new InvalidDataException("Lunghezza messaggio non valida.");
        var bytes = new byte[(int)count];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }
    internal static async Task WritePayloadAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
