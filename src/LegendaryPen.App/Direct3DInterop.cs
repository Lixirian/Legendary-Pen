using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace LegendaryPen.App;

/// <summary>Accès minimal à Direct3D 11 pour l'enregistrement de l'écran. Les numéros de méthode suivent d3d11.h.</summary>
internal sealed unsafe class Direct3DInterop : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Box
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    private const uint Bgra8 = 87, BindShaderResource = 8, BindRenderTarget = 32;

    private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IidDxgiSurface = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
    private static readonly Guid IidMultithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    private static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IidDxgiAccess = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
    private static readonly Guid IidCaptureInterop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
    private static readonly Guid IidCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint levels, uint levelCount, uint sdk, out nint device, out int level, out nint context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint inspectable);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(nint dxgiSurface, out nint inspectable);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out nint hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    private readonly nint _device;
    private readonly nint _context;
    private readonly nint _multithread;

    public Direct3DInterop()
    {
        const int Hardware = 1, Sdk = 7;
        const uint BgraSupport = 0x20;
        Check(D3D11CreateDevice(0, Hardware, 0, BgraSupport, 0, 0, Sdk, out _device, out _, out _context));

        try
        {
            Check(Marshal.QueryInterface(_device, in IidMultithread, out _multithread));

            // La capture et l'encodeur travaillent chacun sur leur fil, avec ce même périphérique.
            ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(_multithread, 5))(_multithread, 1);
            Check(Marshal.QueryInterface(_device, in IidDxgiDevice, out var dxgi));

            try
            {
                Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                WinRtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                Marshal.Release(inspectable);
            }
            finally
            {
                Marshal.Release(dxgi);
            }
        }
        catch
        {
            ReleaseDevice();
            throw;
        }
    }

    public IDirect3DDevice WinRtDevice { get; } = null!;

    public static GraphicsCaptureItem CreateItemForMonitor(nint monitor)
    {
        const string ClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Check(WindowsCreateString(ClassName, ClassName.Length, out var name));
        nint factory;

        try
        {
            Check(RoGetActivationFactory(name, in IidCaptureInterop, out factory));
        }
        finally
        {
            WindowsDeleteString(name);
        }

        try
        {
            nint abi;
            var iid = IidCaptureItem;

            // IGraphicsCaptureItemInterop : 3 = CreateForWindow, 4 = CreateForMonitor.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Slot(factory, 4))(factory, monitor, &iid, &abi));
            var item = GraphicsCaptureItem.FromAbi(abi);
            Marshal.Release(abi);
            return item;
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    public nint CreateTexture(int width, int height)
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Bgra8,
            SampleCount = 1,
            BindFlags = BindShaderResource | BindRenderTarget,
        };
        nint texture;
        Check(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, nint, nint*, int>)Slot(_device, 5))(_device, &desc, 0, &texture));
        return texture;
    }

    /// <summary>Renvoie la ID3D11Texture2D d'une surface WinRT. L'appelant la libère.</summary>
    public static nint TextureOf(IDirect3DSurface surface)
    {
        var abi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);

        try
        {
            Check(Marshal.QueryInterface(abi, in IidDxgiAccess, out var access));

            try
            {
                nint texture;
                var iid = IidTexture2D;
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(access, 3))(access, &iid, &texture));
                return texture;
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    public static IDirect3DSurface SurfaceOf(nint texture)
    {
        Check(Marshal.QueryInterface(texture, in IidDxgiSurface, out var dxgi));

        try
        {
            Check(CreateDirect3D11SurfaceFromDXGISurface(dxgi, out var inspectable));
            var surface = MarshalInterface<IDirect3DSurface>.FromAbi(inspectable);
            Marshal.Release(inspectable);
            return surface;
        }
        finally
        {
            Marshal.Release(dxgi);
        }
    }

    /// <summary>Copie le coin supérieur gauche de la source : un écran de taille impaire perd sa dernière ligne ou colonne.</summary>
    public void CopyTopLeft(nint destination, nint source, int width, int height)
    {
        var box = new Box { Right = (uint)width, Bottom = (uint)height, Back = 1 };

        // Le fil de l'encodeur peut utiliser le contexte en même temps : la copie se fait sous le verrou du périphérique.
        ((delegate* unmanaged[Stdcall]<nint, void>)Slot(_multithread, 3))(_multithread);

        try
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, Box*, void>)Slot(_context, 46))(
                _context, destination, 0, 0, 0, 0, source, 0, &box);
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, void>)Slot(_multithread, 4))(_multithread);
        }
    }

    /// <summary>Zéro tant que le périphérique est valide. Windows le perd lors d'une réinitialisation du pilote.</summary>
    public int DeviceRemovedReason() =>
        ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_device, 39))(_device);

    public void Dispose()
    {
        WinRtDevice.Dispose();
        ReleaseDevice();
    }

    public static void Release(nint com) => Marshal.Release(com);

    private void ReleaseDevice()
    {
        if (_multithread != 0)
        {
            Marshal.Release(_multithread);
        }

        Marshal.Release(_context);
        Marshal.Release(_device);
    }

    private static nint Slot(nint com, int index) => (*(nint**)com)[index];

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
}
