using System.Buffers.Binary;
using System.Runtime.InteropServices;
using HdrCapture.Capture;

namespace HdrCapture.Imaging;

/// <summary>
/// Publishes the LDR image to the clipboard as a registered "PNG" format and as CF_DIBV5,
/// which is what Photoshop, browsers and the usual image editors read.
/// The DIB is filled directly in the memory block that is handed to the clipboard, so the
/// pixels are never copied.
/// </summary>
internal static class ClipboardWriter
{
    private const uint CfDibV5 = 17;
    private const int DibV5HeaderSize = 124;
    private const uint GmemMoveable = 0x0002;
    private const int MaxAttempts = 12;

    private static readonly uint PngClipboardFormat = RegisterClipboardFormat("PNG");

    public static unsafe void SetImage(LinearImage image, double exposureEv)
    {
        var bgra = LdrConverter.ToBgra(image, exposureEv);
        SetImage(new BgraImage(image.Width, image.Height, bgra));
    }

    public static unsafe void SetImage(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var width = image.Width;
        var height = image.Height;
        var imageBytes = checked(width * height * 4);
        var dibSize = DibV5HeaderSize + imageBytes;

        var dibHandle = GlobalAlloc(GmemMoveable, (nuint)dibSize);
        if (dibHandle == 0)
        {
            throw new OutOfMemoryException("无法为剪贴板分配图像内存。");
        }

        var pointer = GlobalLock(dibHandle);
        if (pointer == 0)
        {
            GlobalFree(dibHandle);
            throw new OutOfMemoryException("无法锁定剪贴板图像内存。");
        }

        try
        {
            WriteDibV5Header(pointer, width, height, imageBytes);
            var pixels = pointer + DibV5HeaderSize;
            CopyBottomUp(image.Pixels, pixels, width * 4, height);

            using var png = new MemoryStream(image.EncodePng(), writable: false);

            GlobalUnlock(dibHandle);
            Publish(png, dibHandle);
            return;
        }
        catch
        {
            GlobalUnlock(dibHandle);
            GlobalFree(dibHandle);
            throw;
        }
    }

    private static unsafe void CopyBottomUp(
        ReadOnlySpan<byte> source,
        nint destination,
        int stride,
        int height)
    {
        for (var row = 0; row < height; row++)
        {
            var sourceRow = source.Slice(row * stride, stride);
            var destinationRow = new Span<byte>(
                (void*)(destination + ((height - 1 - row) * stride)),
                stride);
            sourceRow.CopyTo(destinationRow);
        }
    }

    private static unsafe void WriteDibV5Header(nint destination, int width, int height, int imageBytes)
    {
        Span<byte> header = stackalloc byte[DibV5HeaderSize];
        header.Clear();

        BinaryPrimitives.WriteInt32LittleEndian(header[0..], DibV5HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 3); // BI_BITFIELDS
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], imageBytes);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], 3780);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], 3780);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..], 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(header[52..], 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], 0x73524742); // LCS_sRGB
        BinaryPrimitives.WriteUInt32LittleEndian(header[108..], 4); // LCS_GM_IMAGES

        header.CopyTo(new Span<byte>((void*)destination, DibV5HeaderSize));
    }

    private static unsafe void Publish(Stream png, nint dibHandle)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (OpenClipboard(0))
            {
                break;
            }

            if (attempt >= MaxAttempts)
            {
                throw new InvalidOperationException("剪贴板正被其他程序占用，无法写入。");
            }

            Thread.Sleep(30);
        }

        var handedOver = false;
        try
        {
            if (!EmptyClipboard())
            {
                throw new InvalidOperationException("无法清空剪贴板。");
            }

            PublishStream(PngClipboardFormat, png);
            if (SetClipboardData(CfDibV5, dibHandle) == 0)
            {
                throw new InvalidOperationException("写入 CF_DIBV5 剪贴板格式失败。");
            }

            handedOver = true;
        }
        finally
        {
            CloseClipboard();
            if (!handedOver)
            {
                GlobalFree(dibHandle);
            }
        }
    }

    private static unsafe void PublishStream(uint format, Stream data)
    {
        if (data.Length > int.MaxValue)
        {
            throw new InvalidOperationException("PNG 数据过大，无法写入剪贴板。");
        }

        var handle = GlobalAlloc(GmemMoveable, (nuint)Math.Max(data.Length, 1));
        if (handle == 0)
        {
            throw new OutOfMemoryException("无法为剪贴板分配内存。");
        }

        var pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            GlobalFree(handle);
            throw new OutOfMemoryException("无法锁定剪贴板内存。");
        }

        try
        {
            using var destination = new UnmanagedMemoryStream(
                (byte*)pointer,
                data.Length,
                data.Length,
                FileAccess.Write);
            data.CopyTo(destination, 1 << 20);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        if (SetClipboardData(format, handle) == 0)
        {
            GlobalFree(handle);
            throw new InvalidOperationException($"写入剪贴板格式 {format} 失败。");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint memory);
}
