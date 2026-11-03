using System.Runtime.InteropServices;
using LegendaryPen.Core;

namespace LegendaryPen.App;

public sealed record MonitorArea(string DeviceName, PixelRect Bounds, PixelRect WorkArea, bool IsPrimary);

/// <summary>Regroupe tous les appels Win32 de l'application.</summary>
internal static partial class Win32
{
    public const int WmActivate = 0x0006;
    public const int WmWindowPosChanging = 0x0046;
    public const int WmHotKey = 0x0312;
    public const int WmDpiChanged = 0x02E0;

    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModNoRepeat = 0x4000;

    public const uint VkDelete = 0x2E;
    public const uint VkEscape = 0x1B;
    public const uint VkSpace = 0x20;
    public const uint VkY = 0x59;
    public const uint VkZ = 0x5A;

    private const int VkShift = 0x10;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;
    private const long WsExNoActivate = 0x08000000;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorInfoPrimary = 0x00000001;

    private static readonly IntPtr HwndTopmost = new(-1);

    public static void MakeFloatingToolWindow(IntPtr window)
    {
        SetExStyle(window, GetExStyle(window) | WsExToolWindow | WsExNoActivate);
    }

    /// <summary>Fenêtre qui ne prend pas le focus au clic mais garde son bouton dans la barre des tâches.</summary>
    public static void MakeNonActivatingAppWindow(IntPtr window)
    {
        SetExStyle(window, (GetExStyle(window) | WsExAppWindow | WsExNoActivate) & ~WsExToolWindow);
    }

    /// <summary>
    /// À appeler sur WM_WINDOWPOSCHANGING : si Windows s'apprête à changer l'ordre d'empilement,
    /// la fenêtre est placée juste sous <paramref name="anchor"/>.
    /// </summary>
    public static void KeepBelow(IntPtr windowPos, IntPtr anchor)
    {
        var insertAfterOffset = IntPtr.Size;
        var flagsOffset = (IntPtr.Size * 2) + (sizeof(int) * 4);
        var flags = (uint)Marshal.ReadInt32(windowPos, flagsOffset);

        if ((flags & SwpNoZOrder) == 0)
        {
            Marshal.WriteIntPtr(windowPos, insertAfterOffset, anchor);
        }
    }

    public static void SetNoActivate(IntPtr window, bool enabled)
    {
        var style = GetExStyle(window);
        SetExStyle(window, enabled ? style | WsExNoActivate : style & ~WsExNoActivate);
    }

    /// <summary>L'overlay n'a pas le focus clavier : l'état des touches vient du système.</summary>
    public static bool IsShiftDown() => (GetAsyncKeyState(VkShift) & 0x8000) != 0;

    public static void SetClickThrough(IntPtr window, bool enabled)
    {
        var style = GetExStyle(window);
        var updated = enabled ? style | WsExTransparent : style & ~WsExTransparent;

        if (updated != style)
        {
            SetExStyle(window, updated);
        }
    }

    public static void SetBounds(IntPtr window, PixelRect bounds)
    {
        SetWindowPos(
            window,
            IntPtr.Zero,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SwpNoZOrder | SwpNoActivate);
    }

    public static void MoveTo(IntPtr window, PixelPoint position)
    {
        SetWindowPos(window, IntPtr.Zero, position.X, position.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    public static void BringToTop(IntPtr window)
    {
        SetWindowPos(window, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public static PixelRect GetBounds(IntPtr window)
    {
        GetWindowRect(window, out var rect);
        return rect.ToPixelRect();
    }

    public static IReadOnlyList<MonitorArea> GetMonitors()
    {
        var monitors = new List<MonitorArea>();

        bool Collect(IntPtr monitor, IntPtr deviceContext, IntPtr clip, IntPtr data)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

            if (GetMonitorInfoW(monitor, ref info))
            {
                monitors.Add(new MonitorArea(
                    info.DeviceName,
                    info.Monitor.ToPixelRect(),
                    info.Work.ToPixelRect(),
                    (info.Flags & MonitorInfoPrimary) != 0));
            }

            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero);
        return monitors;
    }

    public static bool RegisterGlobalHotKey(IntPtr window, int id, uint modifiers, uint virtualKey)
    {
        return RegisterHotKey(window, id, modifiers | ModNoRepeat, virtualKey);
    }

    public static void UnregisterGlobalHotKey(IntPtr window, int id)
    {
        UnregisterHotKey(window, id);
    }

    private static long GetExStyle(IntPtr window) => GetWindowLongPtrW(window, GwlExStyle).ToInt64();

    private static void SetExStyle(IntPtr window, long style)
    {
        SetWindowLongPtrW(window, GwlExStyle, new IntPtr(style));
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr clip, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly PixelRect ToPixelRect() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }
}
