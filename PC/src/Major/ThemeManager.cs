using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Major;

internal static class ThemeManager
{
    internal static string Preference { get; private set; } = "System";
    internal static bool IsDark { get; private set; }
    internal static void Apply(string? preference)
    {
        Preference = preference is "Light" or "Dark" ? preference : "System";
        IsDark = Preference == "Dark" || Preference == "System" && SystemUsesDarkTheme();
        var colors = new Dictionary<string, string>
        {
            ["Ink"] = IsDark ? "#F2EFF7" : "#201529",
            ["Muted"] = IsDark ? "#BFB6CA" : "#665B70",
            ["Accent"] = IsDark ? "#BF9BFC" : "#7544B4",
            ["AppBackground"] = IsDark ? "#18151E" : "#F6F3FA",
            ["Surface"] = IsDark ? "#24202D" : "#FFFFFF",
            ["SubtleSurface"] = IsDark ? "#2E2838" : "#F5F1FA",
            ["ControlSurface"] = IsDark ? "#393144" : "#ECE5F4",
            ["SelectedSurface"] = IsDark ? "#443259" : "#E8D9FC",
            ["BorderBrush"] = IsDark ? "#51465F" : "#D7CBDF",
            ["SheetBorder"] = IsDark ? "#3D3449" : "#EAE2F1",
            ["PrimarySurface"] = IsDark ? "#BF9BFC" : "#BF9BFC",
            ["PrimaryInk"] = "#260B43",
            ["ErrorInk"] = IsDark ? "#FFADB0" : "#A51F25"
        };
        foreach (var (key, color) in colors)
            Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
    }
    private static bool SystemUsesDarkTheme()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { return false; }
    }
    internal static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
}
