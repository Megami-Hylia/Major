using System.Text;
using System.Text.RegularExpressions;

namespace Major.AirPlay;

public enum AirPlayState { Stopped, Starting, Ready, Authenticating, Connected, Streaming, Stopping, Faulted }
public sealed record AirPlayStatus(AirPlayState State, string Message);

internal static partial class AirPlayEvents
{
    public const int MaxLineLength = 2048;
    public static string? Parse(string line, string nonce, out string? pin)
    {
        pin = null;
        var prefix = "MAJOR_AIRPLAY\t" + nonce + "\t";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var payload = line[prefix.Length..];
        if (payload.StartsWith("pin\t", StringComparison.Ordinal))
        {
            var value = payload[4..];
            if (value.Length != 4 || value.Any(c => c is < '0' or > '9')) return "invalid";
            pin = value; return "pin";
        }
        return payload is "ready" or "connected" or "streaming" or "disconnected" ? payload : "invalid";
    }
    public static string Sanitize(string line, string nonce, string? pin)
    {
        if (line.Contains("MAJOR_AIRPLAY", StringComparison.Ordinal) ||
            line.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
            PinLabel().IsMatch(line)) return "[Messaggio di autenticazione omesso]";
        var clean = new StringBuilder(Math.Min(line.Length, MaxLineLength));
        foreach (char c in line.Take(MaxLineLength)) if (!char.IsControl(c)) clean.Append(c);
        var value = clean.ToString().Replace(nonce, "[evento]", StringComparison.Ordinal);
        if (pin is not null) value = value.Replace(pin, "[PIN]", StringComparison.Ordinal);
        // Device-controlled names can contain a PIN emitted just before its marker arrives.
        return FourDigits().Replace(value, "[numero]");
    }
    [GeneratedRegex(@"\bpin\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PinLabel();
    [GeneratedRegex(@"(?<!\d)\d{4}(?!\d)", RegexOptions.CultureInvariant)] private static partial Regex FourDigits();
}
