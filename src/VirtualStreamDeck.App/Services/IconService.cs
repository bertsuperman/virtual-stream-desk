using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VirtualStreamDeck.Core;

namespace VirtualStreamDeck.App.Services;

/// <summary>Creates PNG icons in the icons folder and loads them for display.</summary>
internal sealed class IconService
{
    private const int IconSize = 256;

    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".tif", ".tiff", ".webp" };

    private readonly ConfigStore _store;

    public IconService(ConfigStore store) => _store = store;

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Makes an icon from any path: image files are imported as they are,
    /// programs / shortcuts / files / folders use their Windows icon.
    /// Returns the path to store in config ("icons/xyz.png") or null.
    /// </summary>
    public string? CreateFrom(string path) =>
        IsImageFile(path) ? ImportImage(path) ?? ExtractShellIcon(path) : ExtractShellIcon(path);

    public string? ImportImage(string imagePath)
    {
        try
        {
            var decoder = BitmapDecoder.Create(
                new Uri(Path.GetFullPath(imagePath)),
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreImageCache,
                BitmapCacheOption.OnLoad);

            BitmapSource frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            int longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
            if (longest > IconSize)
            {
                double scale = (double)IconSize / longest;
                frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }
            return SavePng(frame);
        }
        catch
        {
            return null;
        }
    }

    public string? ExtractShellIcon(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return null;

        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(Win32.IShellItemImageFactory).GUID;
            Win32.SHCreateItemFromParsingName(Path.GetFullPath(path), IntPtr.Zero, ref iid, out var factory);
            var size = new Win32.SIZE { cx = IconSize, cy = IconSize };
            int hr = factory.GetImage(size, Win32.SIIGBF_ICONONLY | Win32.SIIGBF_BIGGERSIZEOK, out hbitmap);
            Marshal.ReleaseComObject(factory);
            if (hr != 0 || hbitmap == IntPtr.Zero) return ExtractAssociatedIcon(path);

            return SavePng(FromHBitmap(hbitmap));
        }
        catch
        {
            return ExtractAssociatedIcon(path);
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) Win32.DeleteObject(hbitmap);
        }
    }

    /// <summary>Fallback: the classic 32x32 icon.</summary>
    private string? ExtractAssociatedIcon(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            return SavePng(src);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Imaging.CreateBitmapSourceFromHBitmap drops the alpha channel (black background),
    /// so copy the 32-bit DIB pixels ourselves.
    /// </summary>
    private static BitmapSource FromHBitmap(IntPtr hbitmap)
    {
        if (Win32.GetObject(hbitmap, Marshal.SizeOf<Win32.DIBSECTION>(), out var dib) == 0 ||
            dib.dsBm.bmBitsPixel != 32 || dib.dsBm.bmBits == IntPtr.Zero)
        {
            return Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }

        int width = dib.dsBm.bmWidth;
        int height = Math.Abs(dib.dsBm.bmHeight);
        int stride = dib.dsBm.bmWidthBytes;
        var pixels = new byte[stride * height];
        Marshal.Copy(dib.dsBm.bmBits, pixels, 0, pixels.Length);

        // Positive biHeight = bottom-up DIB.
        if (dib.dsBmih.biHeight > 0)
        {
            var flipped = new byte[pixels.Length];
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(pixels, y * stride, flipped, (height - 1 - y) * stride, stride);
            pixels = flipped;
        }

        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { hasAlpha = true; break; }
        }
        if (!hasAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }

        var bmp = BitmapSource.Create(width, height, 96, 96,
            hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    private string SavePng(BitmapSource source)
    {
        Directory.CreateDirectory(_store.IconsDirectory);
        var fileName = Guid.NewGuid().ToString("N")[..12] + ".png";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (var fs = File.Create(Path.Combine(_store.IconsDirectory, fileName)))
            encoder.Save(fs);
        return ConfigStore.IconRelativePath(fileName);
    }

    /// <summary>Loads an icon without locking the file.</summary>
    public BitmapSource? Load(string? relativePath)
    {
        var full = _store.ResolveIconPath(relativePath);
        if (full == null) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.UriSource = new Uri(full);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }
}
