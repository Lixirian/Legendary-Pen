using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
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

    private const uint WdaNone = 0x00;
    private const uint WdaExcludeFromCapture = 0x11;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const int VkShift = 0x10;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008;
    private const long WsExTransparent = 0x00000020;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;
    private const long WsExLayered = 0x00080000;
    private const long WsExNoActivate = 0x08000000;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorInfoPrimary = 0x00000001;

    private const uint WsPopup = 0x80000000;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint LwaAlpha = 0x00000002;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint PmRemove = 0x0001;
    private const uint QsAllInput = 0x04FF;
    private const uint WaitObject0 = 0;
    private const uint MagnifierFilterExclude = 0;
    private const string MagnifierClass = "Magnifier";
    private const string MagnifierHostClass = "LegendaryPenMagnifierHost";

    private static readonly IntPtr HwndTopmost = new(-1);

    // Conservé ici : Windows garde ce pointeur tant que la classe de fenêtre existe.
    private static readonly WindowProcedure MagnifierHostProcedure = DefWindowProcW;

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

    /// <summary>Copie un écran en pixels physiques, fenêtres transparentes comprises.</summary>
    public static BitmapSource CaptureScreen(PixelRect bounds)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
        var previous = SelectObject(memory, bitmap);

        try
        {
            if (!BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.Left, bounds.Top, SrcCopy | CaptureBlt))
            {
                throw new InvalidOperationException(Loc.T("Windows a refusé la capture de l'écran."));
            }

            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Retire ou rétablit une fenêtre dans les captures d'écran. Renvoie faux si Windows refuse.</summary>
    public static bool SetExcludedFromCapture(IntPtr window, bool excluded)
    {
        return SetWindowDisplayAffinity(window, excluded ? WdaExcludeFromCapture : WdaNone);
    }

    /// <summary>Attend que l'écran ait été recomposé.</summary>
    public static void WaitForComposition() => DwmFlush();

    public static PixelPoint GetPointer()
    {
        GetCursorPos(out var point);
        return new PixelPoint(point.X, point.Y);
    }

    public static PixelRect GetBounds(IntPtr window)
    {
        GetWindowRect(window, out var rect);
        return rect.ToPixelRect();
    }

    public static bool InitializeMagnification() => MagInitialize();

    public static void UninitializeMagnification() => MagUninitialize();

    /// <summary>
    /// Fenêtre ronde et cachée, qui ne prend ni les clics ni le focus, absente de la barre des tâches et d'Alt+Tab.
    /// Renvoie zéro en cas d'échec. Une seule fois par processus : sa classe de fenêtre n'est enregistrée qu'ici.
    /// </summary>
    public static IntPtr CreateMagnifierHost(int diameter)
    {
        var instance = GetModuleHandleW(null);
        var windowClass = new WindowClass
        {
            Procedure = Marshal.GetFunctionPointerForDelegate(MagnifierHostProcedure),
            Instance = instance,
            ClassName = MagnifierHostClass,
        };

        if (RegisterClassW(ref windowClass) == 0)
        {
            return IntPtr.Zero;
        }

        var host = CreateWindowExW(
            (uint)(WsExTopmost | WsExLayered | WsExTransparent | WsExNoActivate | WsExToolWindow),
            MagnifierHostClass,
            "Legendary Pen - Loupe",
            WsPopup,
            0,
            0,
            diameter,
            diameter,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (host == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // CreateEllipticRgn exclut ses bords droit et bas. Une fois acceptée, la région appartient à Windows.
        var region = CreateEllipticRgn(0, 0, diameter + 1, diameter + 1);

        if (!SetLayeredWindowAttributes(host, 0, 255, LwaAlpha) || SetWindowRgn(host, region, true) == 0)
        {
            DeleteObject(region);
            DestroyWindow(host);
            return IntPtr.Zero;
        }

        return host;
    }

    /// <summary>Contrôle de la loupe, qui remplit son hôte. Renvoie zéro en cas d'échec.</summary>
    public static IntPtr CreateMagnifierControl(IntPtr host, int diameter)
    {
        return CreateWindowExW(
            0,
            MagnifierClass,
            string.Empty,
            WsChild | WsVisible,
            0,
            0,
            diameter,
            diameter,
            host,
            IntPtr.Zero,
            GetModuleHandleW(null),
            IntPtr.Zero);
    }

    public static bool SetMagnifierZoom(IntPtr control, float zoom)
    {
        return MagSetWindowTransform(control, [zoom, 0, 0, 0, zoom, 0, 0, 0, 1]);
    }

    /// <summary>Retire une fenêtre du contenu grossi.</summary>
    public static bool ExcludeFromMagnifier(IntPtr control, IntPtr window)
    {
        return MagSetWindowFilterList(control, MagnifierFilterExclude, 1, [window]);
    }

    /// <summary>Change la zone grossie et demande de la redessiner, même si elle n'a pas bougé : l'écran change dessous.</summary>
    public static void RefreshMagnifier(IntPtr control, PixelRect source)
    {
        MagSetWindowSource(control, new NativeRect { Left = source.Left, Top = source.Top, Right = source.Right, Bottom = source.Bottom });
        InvalidateRect(control, IntPtr.Zero, false);
    }

    /// <summary>Place la fenêtre juste sous <paramref name="anchor"/>, ou au premier plan s'il n'y en a pas.</summary>
    public static void PlaceBelow(IntPtr window, PixelPoint position, IntPtr anchor)
    {
        SetWindowPos(window, anchor != IntPtr.Zero ? anchor : HwndTopmost, position.X, position.Y, 0, 0, SwpNoSize | SwpNoActivate);
    }

    public static void ShowWithoutActivating(IntPtr window) => ShowWindow(window, SwShowNoActivate);

    public static void HideWindow(IntPtr window) => ShowWindow(window, SwHide);

    public static void Destroy(IntPtr window) => DestroyWindow(window);

    /// <summary>Traite les messages en attente du fil appelant.</summary>
    public static void PumpMessages()
    {
        while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, PmRemove))
        {
            TranslateMessage(message);
            DispatchMessageW(message);
        }
    }

    /// <summary>Attend un signal, un message ou la fin du délai. Renvoie vrai seulement si un message est arrivé.</summary>
    public static bool WaitForMessageOrSignal(IntPtr signal, uint milliseconds)
    {
        return MsgWaitForMultipleObjects(1, [signal], false, milliseconds, QsAllInput) == WaitObject0 + 1;
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmFlush();

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr deviceContext, IntPtr gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr deviceContext);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

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

    [LibraryImport("Magnification.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MagInitialize();

    [LibraryImport("Magnification.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MagUninitialize();

    [LibraryImport("Magnification.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MagSetWindowSource(IntPtr control, NativeRect source);

    [LibraryImport("Magnification.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MagSetWindowTransform(IntPtr control, float[] matrix);

    [LibraryImport("Magnification.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MagSetWindowFilterList(IntPtr control, uint mode, int count, IntPtr[] windows);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out NativeMessage message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in NativeMessage message);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DispatchMessageW(in NativeMessage message);

    [LibraryImport("user32.dll")]
    private static partial uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds, uint wakeMask);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string? moduleName);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InvalidateRect(IntPtr window, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WindowClass windowClass);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr clip, IntPtr data);

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public IntPtr Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

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
