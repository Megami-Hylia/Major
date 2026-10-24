using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Major;

// Container for the receiver-owned D3D11 child; no capture/copy of an external window.
public sealed class AirPlayVideoHost : HwndHost
{
    private const string ClassName = "MajorAirPlayCanvas";
    private static readonly WindowProc Procedure = DefWindowProc;
    private static readonly object ClassLock = new();
    private static bool registered;
    public IntPtr VideoHandle { get; private set; }

    public void ClearVideo()
    {
        if (VideoHandle == IntPtr.Zero) return;
        InvalidateRect(VideoHandle, IntPtr.Zero, true);
        UpdateWindow(VideoHandle);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        lock (ClassLock)
        {
            if (!registered)
            {
                var windowClass = new WindowClass
                {
                    Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = GetModuleHandle(null),
                    Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
                    Background = GetStockObject(4), Cursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)), Name = ClassName
                };
                if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                registered = true;
            }
        }
        VideoHandle = CreateWindowEx(0, ClassName, "Schermo iPhone/iPad", 0x56000000,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (VideoHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new HandleRef(this, VideoHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        VideoHandle = IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? Menu, Name;
        public IntPtr SmallIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InvalidateRect(IntPtr window, IntPtr rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursor);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
