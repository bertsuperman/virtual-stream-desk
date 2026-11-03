using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VirtualStreamDeck.Core.Mac;

/// <summary>Minimal bindings to CoreGraphics, CoreFoundation, ApplicationServices and the Objective-C runtime.</summary>
[SupportedOSPlatform("macos")]
public static class MacNative
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private const uint kCGHIDEventTap = 0;

    // ───────────── CoreGraphics ─────────────

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [DllImport(CoreGraphics)]
    private static extern void CGEventSetFlags(IntPtr evt, ulong flags);

    [DllImport(CoreGraphics)]
    private static extern void CGEventPost(uint tap, IntPtr evt);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count,
        IntPtr keyCallBacks, IntPtr valueCallBacks);

    // ───────────── Accessibility ─────────────

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrusted();

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

    // ───────────── Objective-C runtime ─────────────

    [DllImport(ObjC)] public static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] public static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] public static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(ObjC)] public static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector, nuint arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SendBool(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern int SendInt(IntPtr receiver, IntPtr selector);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X, Y;
    }

    // +[NSEvent otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:]
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendOtherEvent(IntPtr cls, IntPtr sel, nuint type, CGPoint location, nuint modifierFlags,
        double timestamp, nint windowNumber, IntPtr context, short subtype, nint data1, nint data2);

    public static IntPtr Sel(string name) => sel_registerName(name);
    public static IntPtr Class(string name) => objc_getClass(name);

    // ───────────── keys ─────────────

    /// <summary>Taps a key with modifiers (flags on both the down and the up event).</summary>
    public static void PostKey(ushort macKeyCode, ulong flags)
    {
        foreach (bool down in new[] { true, false })
        {
            var evt = CGEventCreateKeyboardEvent(IntPtr.Zero, macKeyCode, down);
            if (evt == IntPtr.Zero) throw new InvalidOperationException("CGEventCreateKeyboardEvent failed");
            CGEventSetFlags(evt, flags);
            CGEventPost(kCGHIDEventTap, evt);
            CFRelease(evt);
        }
    }

    // NX_KEYTYPE_* from IOKit/hidsystem/ev_keymap.h
    public const int NxSoundUp = 0, NxSoundDown = 1, NxMute = 7, NxPlay = 16, NxNext = 17, NxPrevious = 18;

    /// <summary>Media / volume keys are "system defined" events (NSEventTypeSystemDefined, subtype 8).</summary>
    public static void PostMediaKey(int nxKeyType)
    {
        EnsureAppKit();
        var pool = objc_autoreleasePoolPush();
        try
        {
            foreach (bool down in new[] { true, false })
            {
                int state = down ? 0xA : 0xB;
                var nsEvent = SendOtherEvent(Class("NSEvent"),
                    Sel("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
                    14, default, (nuint)(state << 8), 0, 0, IntPtr.Zero, 8, (nxKeyType << 16) | (state << 8), -1);
                if (nsEvent == IntPtr.Zero) throw new InvalidOperationException("NSEvent otherEventWithType failed");
                var cgEvent = Send(nsEvent, Sel("CGEvent"));
                CGEventPost(kCGHIDEventTap, cgEvent);
            }
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static bool _appKitLoaded;

    private static void EnsureAppKit()
    {
        if (_appKitLoaded) return;
        NativeLibrary.Load(AppKit);
        _appKitLoaded = true;
    }

    // ───────────── Accessibility permission ─────────────

    public static bool IsTrusted => AXIsProcessTrusted();

    /// <summary>
    /// Asks macOS to show its "allow accessibility" prompt (once; later calls just return the state).
    /// Sending keys needs this permission.
    /// </summary>
    public static bool CheckTrustedAndPrompt()
    {
        if (AXIsProcessTrusted()) return true;
        try
        {
            var ax = NativeLibrary.Load(ApplicationServices);
            var cf = NativeLibrary.Load(CoreFoundation);
            var promptKey = Marshal.ReadIntPtr(NativeLibrary.GetExport(ax, "kAXTrustedCheckOptionPrompt"));
            var trueValue = Marshal.ReadIntPtr(NativeLibrary.GetExport(cf, "kCFBooleanTrue"));
            var dict = CFDictionaryCreate(IntPtr.Zero, new[] { promptKey }, new[] { trueValue }, 1,
                NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"));
            try
            {
                return AXIsProcessTrustedWithOptions(dict);
            }
            finally
            {
                CFRelease(dict);
            }
        }
        catch
        {
            return false;
        }
    }
}
