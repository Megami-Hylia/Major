namespace Major.Core;

public sealed class ToolPaths
{
    public string Adb { get; }
    public string Scrcpy { get; }

    public ToolPaths(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var tools = Path.Combine(Path.GetFullPath(baseDirectory), "tools", "scrcpy");
        Adb = Path.Combine(tools, "adb.exe");
        Scrcpy = Path.Combine(tools, "scrcpy.exe");
    }

    public void Validate()
    {
        if (!File.Exists(Adb) || !File.Exists(Scrcpy))
            throw new FileNotFoundException("Mancano ADB o scrcpy. Estrai l'intero pacchetto portable oppure esegui scripts/bootstrap.ps1.");
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(Scrcpy)!, "scrcpy-server")))
            throw new FileNotFoundException("Manca scrcpy-server. Ripristina l'intera cartella tools/scrcpy.");
    }
}
