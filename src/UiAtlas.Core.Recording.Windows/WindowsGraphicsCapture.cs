using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static unsafe class WindowsGraphicsCapture
{
    private static readonly Guid CaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid CaptureItemInteropId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid FramePoolStatics2Id = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    private static readonly Guid CaptureSession2Id = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    private static readonly Guid DxgiDeviceId = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid DxgiSurfaceAccessId = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid Texture2DId = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid ClosableId = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    private const uint BgraDeviceSupport = 0x20;
    private const uint CpuRead = 0x20000;
    private const uint D3D11SdkVersion = 7;
    private const uint Bgra8Unorm = 87;

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public static Task<byte[]> CaptureWindowPngAsync(nint hwnd, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(hwnd, nint.Zero);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        return Task.Run(() => CaptureWindowPng(hwnd, timeout, cancellationToken), cancellationToken);
    }

    private static byte[] CaptureWindowPng(nint hwnd, TimeSpan timeout, CancellationToken cancellationToken)
    {
        nint item = 0;
        nint nativeDevice = 0;
        nint nativeContext = 0;
        nint projectedDevice = 0;
        nint framePool = 0;
        nint session = 0;
        nint frame = 0;
        try
        {
            item = CreateCaptureItem(hwnd);
            var size = GetCaptureItemSize(item);
            ValidateSize(size);

            CreateNativeDevice(out nativeDevice, out nativeContext);
            projectedDevice = CreateDirect3DDevice(nativeDevice);
            framePool = CreateFramePool(projectedDevice, size);
            session = CreateCaptureSession(framePool, item);
            DisableCursorCapture(session);
            StartCapture(session);

            var timer = Stopwatch.StartNew();
            while (frame == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                frame = TryGetNextFrame(framePool);
                if (frame != 0) break;
                if (timer.Elapsed >= timeout) throw new TimeoutException("Timed out waiting for a window capture frame.");
                Thread.Sleep(10);
            }

            var contentSize = GetFrameContentSize(frame);
            ValidateSize(contentSize);
            return CopyFrameToPng(frame, nativeDevice, nativeContext, contentSize);
        }
        finally
        {
            CloseAndRelease(ref frame);
            CloseAndRelease(ref session);
            CloseAndRelease(ref framePool);
            Release(ref projectedDevice);
            Release(ref nativeContext);
            Release(ref nativeDevice);
            Release(ref item);
        }
    }

    private static nint CreateCaptureItem(nint hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, checked((uint)className.Length), out var classHandle));
        try
        {
            var interopId = CaptureItemInteropId;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classHandle, in interopId, out var factory));
            try
            {
                var itemId = CaptureItemId;
                nint item = 0;
                var method = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)GetMethod(factory, 3);
                Marshal.ThrowExceptionForHR(method(factory, hwnd, &itemId, &item));
                return item != 0 ? item : throw new InvalidOperationException("Window capture item creation returned no item.");
            }
            finally
            {
                Release(ref factory);
            }
        }
        finally
        {
            WindowsDeleteString(classHandle);
        }
    }

    private static SizeInt32 GetCaptureItemSize(nint item)
    {
        var size = default(SizeInt32);
        var method = (delegate* unmanaged[Stdcall]<nint, SizeInt32*, int>)GetMethod(item, 7);
        Marshal.ThrowExceptionForHR(method(item, &size));
        return size;
    }

    private static void CreateNativeDevice(out nint device, out nint context)
    {
        var result = D3D11CreateDevice(0, DriverType.Hardware, 0, BgraDeviceSupport, 0, 0,
            D3D11SdkVersion, out device, out _, out context);
        if (result < 0)
        {
            Release(ref context);
            Release(ref device);
            result = D3D11CreateDevice(0, DriverType.Warp, 0, BgraDeviceSupport, 0, 0,
                D3D11SdkVersion, out device, out _, out context);
        }

        if (result < 0)
        {
            Release(ref context);
            Release(ref device);
            Marshal.ThrowExceptionForHR(result);
        }

        if (device == 0 || context == 0)
        {
            Release(ref context);
            Release(ref device);
            throw new InvalidOperationException("Graphics device creation returned an incomplete device.");
        }
    }

    private static nint CreateDirect3DDevice(nint nativeDevice)
    {
        var dxgiId = DxgiDeviceId;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(nativeDevice, in dxgiId, out var dxgiDevice));
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var projectedDevice));
            return projectedDevice != 0 ? projectedDevice : throw new InvalidOperationException("Graphics device projection returned no device.");
        }
        finally
        {
            Release(ref dxgiDevice);
        }
    }

    private static nint CreateFramePool(nint device, SizeInt32 size)
    {
        const string className = "Windows.Graphics.Capture.Direct3D11CaptureFramePool";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, checked((uint)className.Length), out var classHandle));
        try
        {
            var staticsId = FramePoolStatics2Id;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classHandle, in staticsId, out var statics));
            try
            {
                nint pool = 0;
                var method = (delegate* unmanaged[Stdcall]<nint, nint, uint, int, SizeInt32, nint*, int>)GetMethod(statics, 6);
                Marshal.ThrowExceptionForHR(method(statics, device, Bgra8Unorm, 1, size, &pool));
                return pool != 0 ? pool : throw new InvalidOperationException("Frame-pool creation returned no pool.");
            }
            finally
            {
                Release(ref statics);
            }
        }
        finally
        {
            WindowsDeleteString(classHandle);
        }
    }

    private static nint CreateCaptureSession(nint framePool, nint item)
    {
        nint session = 0;
        var method = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)GetMethod(framePool, 10);
        Marshal.ThrowExceptionForHR(method(framePool, item, &session));
        return session != 0 ? session : throw new InvalidOperationException("Capture-session creation returned no session.");
    }

    private static void DisableCursorCapture(nint session)
    {
        var session2Id = CaptureSession2Id;
        if (Marshal.QueryInterface(session, in session2Id, out var session2) < 0) return;
        try
        {
            var method = (delegate* unmanaged[Stdcall]<nint, byte, int>)GetMethod(session2, 7);
            Marshal.ThrowExceptionForHR(method(session2, 0));
        }
        finally
        {
            Release(ref session2);
        }
    }

    private static void StartCapture(nint session)
    {
        var method = (delegate* unmanaged[Stdcall]<nint, int>)GetMethod(session, 6);
        Marshal.ThrowExceptionForHR(method(session));
    }

    private static nint TryGetNextFrame(nint framePool)
    {
        nint frame = 0;
        var method = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetMethod(framePool, 7);
        Marshal.ThrowExceptionForHR(method(framePool, &frame));
        return frame;
    }

    private static SizeInt32 GetFrameContentSize(nint frame)
    {
        var size = default(SizeInt32);
        var method = (delegate* unmanaged[Stdcall]<nint, SizeInt32*, int>)GetMethod(frame, 8);
        Marshal.ThrowExceptionForHR(method(frame, &size));
        return size;
    }

    private static byte[] CopyFrameToPng(nint frame, nint device, nint context, SizeInt32 contentSize)
    {
        nint surface = 0;
        nint access = 0;
        nint texture = 0;
        nint staging = 0;
        try
        {
            var getSurface = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetMethod(frame, 6);
            Marshal.ThrowExceptionForHR(getSurface(frame, &surface));
            if (surface == 0) throw new InvalidOperationException("Capture frame returned no surface.");

            var accessId = DxgiSurfaceAccessId;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface, in accessId, out access));
            var textureId = Texture2DId;
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)GetMethod(access, 3);
            Marshal.ThrowExceptionForHR(getInterface(access, &textureId, &texture));
            if (texture == 0) throw new InvalidOperationException("Capture surface returned no texture.");

            var description = default(Texture2DDescription);
            var getDescription = (delegate* unmanaged[Stdcall]<nint, Texture2DDescription*, void>)GetMethod(texture, 10);
            getDescription(texture, &description);
            if (description.Format != Bgra8Unorm || description.Sample.Count != 1)
                throw new InvalidOperationException("Capture texture has an unsupported pixel format.");

            var width = Math.Min(description.Width, checked((uint)contentSize.Width));
            var height = Math.Min(description.Height, checked((uint)contentSize.Height));
            var stagingDescription = description with
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Usage = 3,
                BindFlags = 0,
                CpuAccessFlags = CpuRead,
                MiscFlags = 0
            };

            var createTexture = (delegate* unmanaged[Stdcall]<nint, Texture2DDescription*, nint, nint*, int>)GetMethod(device, 5);
            Marshal.ThrowExceptionForHR(createTexture(device, &stagingDescription, 0, &staging));
            if (staging == 0) throw new InvalidOperationException("Staging texture creation returned no texture.");

            var copyResource = (delegate* unmanaged[Stdcall]<nint, nint, nint, void>)GetMethod(context, 47);
            copyResource(context, staging, texture);

            var mapped = default(MappedSubresource);
            var map = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)GetMethod(context, 14);
            Marshal.ThrowExceptionForHR(map(context, staging, 0, 1, 0, &mapped));
            try
            {
                if (mapped.Data == 0 || mapped.RowPitch < width * 4)
                    throw new InvalidOperationException("Mapped capture texture has an invalid layout.");
                var stride = checked((int)width * 4);
                var pixels = new byte[checked(stride * (int)height)];
                for (var row = 0; row < height; row++)
                    Marshal.Copy(mapped.Data + checked((nint)(row * mapped.RowPitch)), pixels, checked((int)row * stride), stride);
                return EncodePng(pixels, checked((int)width), checked((int)height), stride);
            }
            finally
            {
                var unmap = (delegate* unmanaged[Stdcall]<nint, nint, uint, void>)GetMethod(context, 15);
                unmap(context, staging, 0);
            }
        }
        finally
        {
            Release(ref staging);
            Release(ref texture);
            Release(ref access);
            Release(ref surface);
        }
    }

    private static byte[] EncodePng(byte[] pixels, int width, int height, int stride)
    {
        ForceOpaqueAlpha(pixels, width, height, stride);
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        if (stream.Length > 16 * 1024 * 1024) throw new InvalidOperationException("Encoded frame exceeds quota.");
        return stream.ToArray();
    }

    internal static void ForceOpaqueAlpha(byte[] pixels, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || stride < checked(width * 4) ||
            pixels.Length < checked(stride * height))
            throw new ArgumentOutOfRangeException(nameof(pixels), "The BGRA capture buffer dimensions are invalid.");

        // A captured HWND is rectangular evidence, not an image with intentional
        // transparency. Some owner-drawn Office dialogs leave alpha at zero for
        // otherwise valid RGB pixels (notably tab captions, list text and colour
        // swatches). PNG decoders then hide those pixels even though capture got
        // them. Discard the undefined WGC alpha before persisting the screenshot.
        for (var row = 0; row < height; row++)
        for (var offset = checked(row * stride + 3); offset < checked(row * stride + width * 4); offset += 4)
            pixels[offset] = byte.MaxValue;
    }

    private static void ValidateSize(SizeInt32 size)
    {
        if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > 16_000_000)
            throw new InvalidOperationException("Capture surface dimensions are invalid or too large.");
    }

    private static nint GetMethod(nint instance, int slot)
    {
        if (instance == 0) throw new ArgumentOutOfRangeException(nameof(instance));
        return ((nint**)instance)[0][slot];
    }

    private static void CloseAndRelease(ref nint value)
    {
        if (value == 0) return;
        var closableId = ClosableId;
        if (Marshal.QueryInterface(value, in closableId, out var closable) >= 0)
        {
            try
            {
                var close = (delegate* unmanaged[Stdcall]<nint, int>)GetMethod(closable, 6);
                _ = close(closable);
            }
            finally
            {
                Release(ref closable);
            }
        }
        Release(ref value);
    }

    private static void Release(ref nint value)
    {
        if (value == 0) return;
        Marshal.Release(value);
        value = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SizeInt32(int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SampleDescription(uint Count, uint Quality);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Texture2DDescription(
        uint Width,
        uint Height,
        uint MipLevels,
        uint ArraySize,
        uint Format,
        SampleDescription Sample,
        uint Usage,
        uint BindFlags,
        uint CpuAccessFlags,
        uint MiscFlags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MappedSubresource(nint Data, uint RowPitch, uint DepthPitch);

    private enum DriverType : uint { Hardware = 1, Warp = 5 }

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string value, uint length, out nint handle);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint handle);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint className, in Guid interfaceId, out nint factory);

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3D11CreateDevice(nint adapter, DriverType driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out uint featureLevel, out nint immediateContext);

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
