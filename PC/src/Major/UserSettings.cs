using System.IO;
using System.Text.Json;
using Major.Core;

namespace Major;

public sealed class UserSettings
{
    public MirrorOptions Options { get; set; } = new();
    public bool SidebarCollapsed { get; set; }
    public string Theme { get; set; } = "System";
    public string StartPage { get; set; } = "Usb";
    public bool StartMaximized { get; set; }
    public string OutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Major");
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Major", "settings.json");
    public static UserSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new();
        settings.Options ??= new();
        if (settings.Theme is not ("System" or "Light" or "Dark")) settings.Theme = "System";
        if (settings.StartPage is not ("Usb" or "AirPlay" or "Android")) settings.StartPage = "Usb";
        settings.Options.RecordPath = null; // A recording path is session state, never a preference.
        if (string.IsNullOrWhiteSpace(settings.OutputDirectory)) settings.OutputDirectory = new UserSettings().OutputDirectory;
        return settings;
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var pending = FilePath + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(pending, FilePath, true);
    }
}
