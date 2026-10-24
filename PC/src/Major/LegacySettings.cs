using System.IO;
namespace Major;
internal static class LegacySettings
{
    internal static void Migrate()
    {
        // Historical name is required only to preserve existing credentials and preferences.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var old = Path.Combine(local, "FiloMirror");
        var current = Path.Combine(local, "Major");
        if (!Directory.Exists(old) || Directory.Exists(current)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(current, Path.GetRelativePath(old, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, false);
            }
            var preferences = Path.Combine(current, "airplay-ui.json");
            if (File.Exists(preferences)) File.WriteAllText(preferences, File.ReadAllText(preferences).Replace("Filo Mirror", "Major"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Existing files remain intact. */ }
    }
}
