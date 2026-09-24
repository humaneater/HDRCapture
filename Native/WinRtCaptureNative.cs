using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HdrCapture.Native;

internal static class WinRtCaptureNative
{
    public const int DirectXPixelFormatR16G16B16A16Float = 10;
    public const int DirectXPixelFormatB8G8R8A8UIntNormalized = 87;

    private const int SdkVersion = 7;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11UsageStaging = 3;
    private const uint D3D11CpuAccessRead = 0x10000;

    // D3D11_MAP values: WRITE = 1, READ = 2, READ_WRITE = 3.
    private const uint D3D11MapRead = 2;

    public static readonly Guid GraphicsCaptureItemInteropIid =
        new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    public static readonly Guid GraphicsCaptureItemIid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    public static readonly Guid CaptureFramePoolStatics2Iid =
        new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    public static readonly Guid CaptureSession2Iid =
        new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    public static readonly Guid CaptureSession3Iid =
        new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
    public static readonly Guid Direct3DDxgiInterfaceAccessIid =
        new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    public static readonly Guid DxgiDeviceIid =
        new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    public static readonly Guid D3D11Texture2DIid =
        new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    public static nint CreateGraphicsCaptureItem(nint monitor)
    {
        var factory = GetActivationFactory(
            "Windows.Graphics.Capture.GraphicsCaptureItem",
            GraphicsCaptureItemInteropIid);

        try
        {
            var createForMonitor = Com.GetDelegate<CreateForMonitorDelegate>(factory, 4);
            var iid = GraphicsCaptureItemIid;
            Marshal.ThrowExceptionForHR(createForMonitor(factory, monitor, ref iid, out var item));
            return item;
        }
        finally
        {
            Com.Release(factory);
        }
    }

    public static GraphicsDevice CreateGraphicsDevice()
    {
        var result = D3D11CreateDevice(
            0,
            1,
            0,
            D3D11CreateDeviceBgraSupport,
            0,
            0,
            SdkVersion,
            out var device,
            out _,
            out var context);
        Marshal.ThrowExceptionForHR(result);

        nint dxgiDevice = 0;
        nint graphicsDevice = 0;
        try
        {
            var iid = DxgiDeviceIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out dxgiDevice));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out graphicsDevice));
            return new GraphicsDevice(device, context, graphicsDevice);
        }
        catch
        {
            Com.Release(graphicsDevice);
            Com.Release(dxgiDevice);
            Com.Release(context);
            Com.Release(device);
            throw;
        }
        finally
        {
            Com.Release(dxgiDevice);
        }
    }

    public static int GetItemWidth(nint item)
    {
        var getSize = Com.GetDelegate<GetSizeDelegate>(item, 7);
        Marshal.ThrowExceptionForHR(getSize(item, out var size));
        return size.Width;
    }

    public static int GetItemHeight(nint item)
    {
        var getSize = Com.GetDelegate<GetSizeDelegate>(item, 7);
        Marshal.ThrowExceptionForHR(getSize(item, out var size));
        return size.Height;
    }

    public static nint CreateFramePool(nint graphicsDevice, int pixelFormat, int width, int height)
    {
        var factory = GetActivationFactory(
            "Windows.Graphics.Capture.Direct3D11CaptureFramePool",
            CaptureFramePoolStatics2Iid);
        try
        {
            var create = Com.GetDelegate<CreateFramePoolDelegate>(factory, 6);
            var size = new SizeInt32 { Width = width, Height = height };
            Marshal.ThrowExceptionForHR(
                create(factory, graphicsDevice, pixelFormat, 2, size, out var framePool));
            return framePool;
        }
        finally
        {
            Com.Release(factory);
        }
    }

    public static nint CreateCaptureSession(nint framePool, nint item)
    {
        var create = Com.GetDelegate<CreateCaptureSessionDelegate>(framePool, 10);
        Marshal.ThrowExceptionForHR(create(framePool, item, out var session));
        return session;
    }

    public static void ConfigureSession(nint session, bool includeCursor, bool hideBorder)
    {
        var cursorIid = CaptureSession2Iid;
        if (Marshal.QueryInterface(session, in cursorIid, out var session2) == 0)
        {
            try
            {
                var setCursor = Com.GetDelegate<SetBooleanDelegate>(session2, 7);
                Marshal.ThrowExceptionForHR(setCursor(session2, includeCursor ? (byte)1 : (byte)0));
            }
            finally
            {
                Com.Release(session2);
            }
        }

        if (!hideBorder)
        {
            return;
        }

        var borderIid = CaptureSession3Iid;
        if (Marshal.QueryInterface(session, in borderIid, out var session3) == 0)
        {
            try
            {
                var setBorder = Com.GetDelegate<SetBooleanDelegate>(session3, 7);
                _ = setBorder(session3, 0);
            }
            finally
            {
                Com.Release(session3);
            }
        }
    }

    public static void StartCapture(nint session)
    {
        var start = Com.GetDelegate<StartCaptureDelegate>(session, 6);
        Marshal.ThrowExceptionForHR(start(session));
    }

    public static nint TryGetNextFrame(nint framePool, TimeSpan timeout)
    {
        var tryGetFrame = Com.GetDelegate<TryGetNextFrameDelegate>(framePool, 7);
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var result = tryGetFrame(framePool, out var frame);
            if (result == 0 && frame != 0)
            {
                return frame;
            }

            Thread.Sleep(2);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException("Windows Graphics Capture did not provide a frame before the timeout.");
    }

    public static nint GetFrameSurface(nint frame)
    {
        var getSurface = Com.GetDelegate<GetFrameSurfaceDelegate>(frame, 6);
        Marshal.ThrowExceptionForHR(getSurface(frame, out var surface));
        return surface;
    }

    public static nint GetD3DTextureFromSurface(nint surface)
    {
        var accessIid = Direct3DDxgiInterfaceAccessIid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface, in accessIid, out var access));
        try
        {
            var getInterface = Com.GetDelegate<GetDxgiInterfaceDelegate>(access, 3);
            var textureIid = D3D11Texture2DIid;
            Marshal.ThrowExceptionForHR(getInterface(access, ref textureIid, out var texture));
            return texture;
        }
        finally
        {
            Com.Release(access);
        }
    }

    public static nint CreateStagingTexture(nint device, int pixelFormat, int width, int height)
    {
        var description = new D3D11Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = (uint)pixelFormat,
            SampleDescription = new DxgiSampleDescription { Count = 1, Quality = 0 },
            Usage = D3D11UsageStaging,
            BindFlags = 0,
            CpuAccessFlags = D3D11CpuAccessRead,
            MiscFlags = 0
        };

        var createTexture = Com.GetDelegate<CreateTexture2DDelegate>(device, 5);
        Marshal.ThrowExceptionForHR(
            createTexture(device, ref description, 0, out var texture));
        return texture;
    }

    public static D3D11Texture2DDescription GetTextureDescription(nint texture)
    {
        var getDescription = Com.GetDelegate<GetTexture2DDescriptionDelegate>(texture, 10);
        getDescription(texture, out var description);
        return description;
    }

    public static nint GetContextDevice(nint context)
    {
        var getDevice = Com.GetDelegate<GetDeviceDelegate>(context, 3);
        getDevice(context, out var device);
        return device;
    }

    public static void CopyResource(nint context, nint destination, nint source)
    {
        var rawContext = (ID3D11DeviceContextRaw)Marshal.GetObjectForIUnknown(context);
        try
        {
            rawContext.CopyResource(destination, source);
        }
        finally
        {
            Marshal.ReleaseComObject(rawContext);
        }
    }

    public static D3D11MappedSubresource MapRead(nint context, nint resource)
    {
        var rawContext = (ID3D11DeviceContextRaw)Marshal.GetObjectForIUnknown(context);
        try
        {
            Marshal.ThrowExceptionForHR(
                rawContext.Map(resource, 0, D3D11MapRead, 0, out var mapped));
            return mapped;
        }
        finally
        {
            Marshal.ReleaseComObject(rawContext);
        }
    }

    public static int TryMapRead(nint context, nint resource, out D3D11MappedSubresource mapped)
    {
        var rawContext = (ID3D11DeviceContextRaw)Marshal.GetObjectForIUnknown(context);
        try
        {
            return rawContext.Map(resource, 0, D3D11MapRead, 0, out mapped);
        }
        finally
        {
            Marshal.ReleaseComObject(rawContext);
        }
    }

    public static int ProbeMapRead()
    {
        using var device = CreateGraphicsDevice();
        var texture = CreateStagingTexture(device.Device, 28, 1, 1);
        try
        {
            var result = TryMapRead(device.Context, texture, out _);
            if (result == 0)
            {
                Unmap(device.Context, texture);
            }

            return result;
        }
        finally
        {
            Com.Release(texture);
        }
    }

    public static void Unmap(nint context, nint resource)
    {
        var rawContext = (ID3D11DeviceContextRaw)Marshal.GetObjectForIUnknown(context);
        try
        {
            rawContext.Unmap(resource, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(rawContext);
        }
    }

    public static byte[] ReadTexture(
        nint context,
        nint stagingTexture,
        int width,
        int height,
        int bytesPerPixel)
    {
        var mapped = MapRead(context, stagingTexture);
        try
        {
            var rowBytes = checked(width * bytesPerPixel);
            var pixels = new byte[checked(rowBytes * height)];
            for (var row = 0; row < height; row++)
            {
                var source = IntPtr.Add(mapped.Data, checked((int)(row * mapped.RowPitch)));
                Marshal.Copy(source, pixels, row * rowBytes, rowBytes);
            }

            return pixels;
        }
        finally
        {
            Unmap(context, stagingTexture);
        }
    }

    private static nint GetActivationFactory(string className, Guid iid)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length, out var hstring));
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iid, out var factory));
            return factory;
        }
        finally
        {
            _ = WindowsDeleteString(hstring);
        }
    }

    public sealed class GraphicsDevice : IDisposable
    {
        private nint _device;
        private nint _context;
        private nint _graphicsDevice;

        internal GraphicsDevice(nint device, nint context, nint graphicsDevice)
        {
            _device = device;
            _context = context;
            _graphicsDevice = graphicsDevice;
        }

        public nint Device => _device;

        public nint Context => _context;

        public nint GraphicsDevicePointer => _graphicsDevice;

        public void Dispose()
        {
            Com.Release(_graphicsDevice);
            Com.Release(_context);
            Com.Release(_device);
            _graphicsDevice = 0;
            _context = 0;
            _device = 0;
        }
    }

    public struct SizeInt32
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiSampleDescription
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11Texture2DDescription
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public DxgiSampleDescription SampleDescription;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11MappedSubresource
    {
        public nint Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateForMonitorDelegate(nint instance, nint monitor, ref Guid iid, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetSizeDelegate(nint instance, out SizeInt32 size);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateFramePoolDelegate(
        nint instance,
        nint graphicsDevice,
        int pixelFormat,
        int numberOfBuffers,
        SizeInt32 size,
        out nint result);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateCaptureSessionDelegate(nint instance, nint item, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetBooleanDelegate(nint instance, byte value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int StartCaptureDelegate(nint instance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int TryGetNextFrameDelegate(nint instance, out nint frame);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetFrameSurfaceDelegate(nint instance, out nint surface);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetDxgiInterfaceDelegate(nint instance, ref Guid iid, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateTexture2DDelegate(
        nint instance,
        ref D3D11Texture2DDescription description,
        nint initialData,
        out nint texture);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GetTexture2DDescriptionDelegate(
        nint instance,
        out D3D11Texture2DDescription description);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GetDeviceDelegate(nint instance, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int MapDelegate(
        nint instance,
        nint resource,
        uint subresource,
        uint mapType,
        uint mapFlags,
        nint mapped);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void UnmapDelegate(nint instance, nint resource, uint subresource);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void CopyResourceDelegate(nint instance, nint destination, nint source);

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out nint device,
        out int featureLevel,
        out nint immediateContext);

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        nint dxgiDevice,
        out nint graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
    private static extern int WindowsCreateString(
        string sourceString,
        uint length,
        out nint hstring);

    [DllImport("combase.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int RoGetActivationFactory(
        nint activatableClassId,
        ref Guid iid,
        out nint factory);
}

internal static class Com
{
    public static T GetDelegate<T>(nint instance, int vtableIndex)
        where T : Delegate
    {
        if (instance == 0)
        {
            throw new ArgumentException("COM pointer is null.", nameof(instance));
        }

        var vtable = Marshal.ReadIntPtr(instance);
        var function = Marshal.ReadIntPtr(vtable, vtableIndex * IntPtr.Size);
        if (function == 0)
        {
            throw new InvalidOperationException($"COM vtable entry {vtableIndex} is null.");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    public static void Release(nint instance)
    {
        if (instance == 0)
        {
            return;
        }

        try
        {
            Marshal.Release(instance);
        }
        catch (ArgumentException)
        {
        }
    }
}
