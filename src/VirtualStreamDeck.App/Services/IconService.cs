using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VirtualStreamDeck.Core;

namespace VirtualStreamDeck.App.Services;

/// <summary>Creates PNG icons in the icons folder and loads them for display.</summary>
internal sealed class IconService
{
    private const int IconSize = 256;

    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".webp", ".icns" };

    private readonly ConfigStore _store;
    private readonly Dictionary<string, Bitmap?> _cache = new();

    public IconService(ConfigStore store) => _store = store;

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) && !path.EndsWith(".icns", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Makes an icon from any path: image files are imported as they are,
    /// programs / shortcuts / apps / files / folders use their system icon.
    /// Returns the path to store in config ("icons/xyz.png") or null.
    /// </summary>
    public string? CreateFrom(string path) =>
        IsImageFile(path) ? ImportImage(path) ?? SystemIcon(path) : SystemIcon(path);

    public string? ImportImage(string imagePath)
    {
        try
        {
            using var fs = File.OpenRead(imagePath);
            return Save(new Bitmap(fs));
        }
        catch
        {
            return null;
        }
    }

    private string? SystemIcon(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return null;
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsShellIcon(path);
            if (OperatingSystem.IsMacOS()) return MacIcon(path);
        }
        catch
        {
            // no icon then
        }
        return null;
    }

    /// <summary>
    /// macOS: Quick Look renders the Finder icon of any app, file or folder (qlmanage ships with macOS).
    /// </summary>
    private string? MacIcon(string path)
    {
        var tmp = Directory.CreateTempSubdirectory("vsd-icon-").FullName;
        try
        {
            if (!ShellHelper.Run("/usr/bin/qlmanage", "-t", "-s", IconSize.ToString(), "-o", tmp, path)) return null;
            var png = Directory.GetFiles(tmp, "*.png").FirstOrDefault();
            return png == null ? null : ImportImage(png);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* ignore */ }
        }
    }

    [SupportedOSPlatform("windows")]
    private string? WindowsShellIcon(string path)
    {
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(Win32.IShellItemImageFactory).GUID;
            Win32.SHCreateItemFromParsingName(Path.GetFullPath(path), IntPtr.Zero, ref iid, out var factory);
            var size = new Win32.SIZE { cx = IconSize, cy = IconSize };
            int hr = factory.GetImage(size, Win32.SIIGBF_ICONONLY | Win32.SIIGBF_BIGGERSIZEOK, out hbitmap);
            Marshal.ReleaseComObject(factory);
            if (hr != 0 || hbitmap == IntPtr.Zero) return null;
            return FromHBitmap(hbitmap) is { } bmp ? Save(bmp) : null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) Win32.DeleteObject(hbitmap);
        }
    }

    /// <summary>Copies the 32-bit DIB pixels (premultiplied BGRA, possibly bottom-up) into a bitmap.</summary>
    [SupportedOSPlatform("windows")]
    private static unsafe Bitmap? FromHBitmap(IntPtr hbitmap)
    {
        if (Win32.GetObject(hbitmap, Marshal.SizeOf<Win32.DIBSECTION>(), out var dib) == 0 ||
            dib.dsBm.bmBitsPixel != 32 || dib.dsBm.bmBits == IntPtr.Zero)
            return null;

        int width = dib.dsBm.bmWidth;
        int height = Math.Abs(dib.dsBm.bmHeight);
        int stride = dib.dsBm.bmWidthBytes;
        var pixels = new byte[stride * height];
        Marshal.Copy(dib.dsBm.bmBits, pixels, 0, pixels.Length);

        if (dib.dsBmih.biHeight > 0) // bottom-up
        {
            var flipped = new byte[pixels.Length];
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(pixels, y * stride, flipped, (height - 1 - y) * stride, stride);
            pixels = flipped;
        }

        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha)
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        fixed (byte* p = pixels)
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, (IntPtr)p, new PixelSize(width, height), new Vector(96, 96), stride);
    }

    private string Save(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        int longest = Math.Max(size.Width, size.Height);
        if (longest > IconSize)
        {
            double scale = (double)IconSize / longest;
            var scaled = bitmap.CreateScaledBitmap(
                new PixelSize(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale))),
                BitmapInterpolationMode.HighQuality);
            bitmap.Dispose();
            bitmap = scaled;
        }

        Directory.CreateDirectory(_store.IconsDirectory);
        var fileName = Guid.NewGuid().ToString("N")[..12] + ".png";
        bitmap.Save(Path.Combine(_store.IconsDirectory, fileName));
        bitmap.Dispose();
        return ConfigStore.IconRelativePath(fileName);
    }

    /// <summary>Loads an icon (cached; icon files never change, new icons get new names).</summary>
    public Bitmap? Load(string? relativePath)
    {
        var full = _store.ResolveIconPath(relativePath);
        if (full == null) return null;
        if (_cache.TryGetValue(full, out var cached)) return cached;
        Bitmap? bmp = null;
        try
        {
            using var fs = File.OpenRead(full);
            bmp = new Bitmap(fs);
        }
        catch
        {
            // unreadable: show the default glyph
        }
        _cache[full] = bmp;
        return bmp;
    }
}
