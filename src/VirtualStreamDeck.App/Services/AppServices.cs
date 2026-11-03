using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using VirtualStreamDeck.Core.Update;

namespace VirtualStreamDeck.App.Services;

internal static class AppInfo
{
    public const string ProductName = "Virtual Stream Deck";

    /// <summary>e.g. "0.2.14" (CI sets it from the build number; local builds are "0.2.0").</summary>
    public static string Version { get; } = ReadVersion();

    public static bool IsMac => OperatingSystem.IsMacOS();
    public static bool IsWindows => OperatingSystem.IsWindows();

    private static string ReadVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "0.0.0";
        int plus = v.IndexOf('+'); // strip "+<git sha>"
        return plus >= 0 ? v[..plus] : v;
    }
}

/// <summary>"Start with Windows / at login": HKCU\…\Run on Windows, a LaunchAgent on a Mac.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VirtualStreamDeck";
    private const string MacAgentLabel = "sk.bertsuperman.virtualstreamdeck";

    private static string MacAgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", MacAgentLabel + ".plist");

    public static void Set(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsWindows()) SetWindows(enabled);
            else if (OperatingSystem.IsMacOS()) SetMac(enabled);
        }
        catch
        {
            // locked down by policy / no permission: the checkbox simply has no effect then
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled && Environment.ProcessPath is { } exe)
            key.SetValue(ValueName, $"\"{exe}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static void SetMac(bool enabled)
    {
        var bundle = Updater.BundleOf(Environment.ProcessPath);
        if (!enabled || bundle == null)
        {
            if (File.Exists(MacAgentPath)) File.Delete(MacAgentPath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(MacAgentPath)!);
        File.WriteAllText(MacAgentPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{MacAgentLabel}</string>
              <key>ProgramArguments</key>
              <array><string>/usr/bin/open</string><string>-a</string><string>{System.Security.SecurityElement.Escape(bundle)}</string></array>
              <key>RunAtLoad</key><true/>
            </dict>
            </plist>
            """);
    }
}

/// <summary>
/// Only one instance runs.
/// - Starting the same build again just shows the running window.
/// - Starting a different build (newer download, other path) replaces the running one,
///   so "I downloaded the new version but still see the old one" can't happen.
/// Windows: named mutex + events (what the WPF builds 0.2.x listen to as well). macOS: a local socket.
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\VirtualStreamDeck.Instance";
    private const string ShowEventName = @"Local\VirtualStreamDeck.Show";
    private const string ExitEventName = @"Local\VirtualStreamDeck.Exit";
    private const string PipeName = "VirtualStreamDeck.Instance";

    private static readonly string InfoPath = Path.Combine(Path.GetTempPath(), "VirtualStreamDeck.instance");

    private static Mutex? _mutex;
    private static Action? _onShow, _onExit;

    /// <summary>Returns false if another instance of the same build is already running (it was asked to show itself).</summary>
    public static bool TryAcquire(Action onShowRequest, Action onExitRequest)
    {
        _onShow = onShowRequest;
        _onExit = onExitRequest;
        return OperatingSystem.IsWindows() ? TryAcquireWindows() : TryAcquireUnix();
    }

    private static bool IsSameBuildRunning()
    {
        var running = ReadInfo();
        return running != null &&
               running.Value.Version == AppInfo.Version &&
               string.Equals(running.Value.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) &&
               IsAlive(running.Value.Pid);
    }

    // ───────────── Windows ─────────────

    private static bool TryAcquireWindows()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            if (IsSameBuildRunning() || !TakeOverWindows())
            {
                Signal(ShowEventName);
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
        }

        WriteInfo();
        Listen(new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName), () => _onShow?.Invoke());
        Listen(new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName), () => _onExit?.Invoke());
        return true;
    }

    private static bool TakeOverWindows()
    {
        Signal(ExitEventName);
        if (WaitForMutex(TimeSpan.FromSeconds(4))) return true;
        KillOthers(); // builds before 0.2 don't listen for the exit request
        return WaitForMutex(TimeSpan.FromSeconds(3));
    }

    private static bool WaitForMutex(TimeSpan timeout)
    {
        try
        {
            return _mutex!.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true; // previous owner exited without releasing: we own it now
        }
    }

    private static void Signal(string name)
    {
        try
        {
            using var e = EventWaitHandle.OpenExisting(name);
            e.Set();
        }
        catch
        {
            // the other instance is not listening (yet / anymore)
        }
    }

    private static void Listen(EventWaitHandle handle, Action callback)
    {
        new Thread(() =>
        {
            while (handle.WaitOne()) callback();
        }) { IsBackground = true, Name = "SingleInstance" }.Start();
    }

    // ───────────── macOS (and Linux): local socket ─────────────

    private static bool TryAcquireUnix()
    {
        if (Send("ping"))
        {
            if (IsSameBuildRunning())
            {
                Send("show");
                return false;
            }
            // Another build: ask it to quit, then take its place.
            Send("exit");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && Send("ping")) Thread.Sleep(150);
            if (Send("ping")) KillOthers();
        }

        WriteInfo();
        new Thread(ServeUnix) { IsBackground = true, Name = "SingleInstance" }.Start();
        return true;
    }

    private static void ServeUnix()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                server.WaitForConnection();
                using var reader = new StreamReader(server);
                var command = reader.ReadLine();
                if (command == "show") _onShow?.Invoke();
                else if (command == "exit") _onExit?.Invoke();
            }
            catch
            {
                Thread.Sleep(200);
            }
        }
    }

    private static bool Send(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(300);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ───────────── shared ─────────────

    public static void Release()
    {
        try
        {
            if (ReadInfo()?.Pid == Environment.ProcessId) File.Delete(InfoPath);
        }
        catch
        {
            // ignore
        }
    }

    private static void KillOthers()
    {
        foreach (var p in Process.GetProcesses().Where(p => p.Id != Environment.ProcessId &&
                                                            p.ProcessName.StartsWith("VirtualStreamDeck", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch
            {
                // already gone or not ours
            }
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteInfo()
    {
        try
        {
            File.WriteAllLines(InfoPath, new[] { Environment.ProcessId.ToString(), AppInfo.Version, Environment.ProcessPath ?? "" });
        }
        catch
        {
            // not critical: a later launch will simply take over
        }
    }

    private static (int Pid, string Version, string Path)? ReadInfo()
    {
        try
        {
            var lines = File.ReadAllLines(InfoPath);
            if (lines.Length >= 3 && int.TryParse(lines[0], out int pid)) return (pid, lines[1], lines[2]);
        }
        catch
        {
            // missing or unreadable
        }
        return null;
    }
}

internal static class ShellHelper
{
    public static void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* ignore */ }
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("/usr/bin/open", new[] { path });
            else
                Process.Start("xdg-open", new[] { path });
        }
        catch
        {
            // ignore
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("/usr/bin/open", new[] { url });
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Runs a program and waits (for small tools like qlmanage); returns false on any failure.</summary>
    public static bool Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            return p.WaitForExit(15000) && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static Task Delay(int ms) => Task.Delay(ms);
}
