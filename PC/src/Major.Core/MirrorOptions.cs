namespace Major.Core;

public sealed class MirrorOptions
{
    public int MaxSize { get; set; } = 1920;
    public int BitRateMbps { get; set; } = 8;
    public int MaxFps { get; set; } = 60;
    public bool StayAwake { get; set; } = true;
    public bool TurnScreenOff { get; set; }
    public bool Fullscreen { get; set; }
    public bool Reconnect { get; set; } = true;
    public bool UseUhidKeyboard { get; set; }
    public string? RecordPath { get; set; }

    internal MirrorOptions Snapshot() => (MirrorOptions)MemberwiseClone();

    internal void Validate()
    {
        if (MaxSize != 0 && MaxSize is < 128 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(MaxSize), "La dimensione deve essere 0 (originale) oppure tra 128 e 8192 px.");
        if (BitRateMbps is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(BitRateMbps), "Il bitrate deve essere tra 1 e 100 Mbps.");
        if (MaxFps is < 1 or > 240)
            throw new ArgumentOutOfRangeException(nameof(MaxFps), "Il frame rate deve essere tra 1 e 240 fps.");
        if (RecordPath is not null)
        {
            var extension = Path.GetExtension(RecordPath).ToLowerInvariant();
            if (extension is not ".mkv" and not ".mp4")
                throw new ArgumentException("La registrazione richiede un file .mkv o .mp4.", nameof(RecordPath));
            if (File.Exists(RecordPath))
                throw new IOException("Il file di registrazione esiste già. Scegli un nuovo nome.");
        }
    }
}
