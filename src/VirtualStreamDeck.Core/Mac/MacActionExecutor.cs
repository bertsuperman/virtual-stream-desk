using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using VirtualStreamDeck.Core.Obs;

namespace VirtualStreamDeck.Core.Mac;

[SupportedOSPlatform("macos")]
public sealed class MacActionExecutor : IActionExecutor
{
    private readonly ObsClient _obs;

    public MacActionExecutor(ObsClient obs) => _obs = obs;

    public Task ExecuteAsync(DeckAction action)
    {
        switch (action)
        {
            case OpenAction open:
                Open(open);
                return Task.CompletedTask;
            case KeysAction keys:
                if (!Hotkey.TryParse(keys.Keys, out var hk))
                    throw new InvalidOperationException(L.T($"Neplatná klávesová skratka: '{keys.Keys}'.", $"Invalid key combination: '{keys.Keys}'."));
                var code = MacKeys.ToMac(hk.VirtualKey)
                           ?? throw new InvalidOperationException(L.T(
                               $"Kláves {KeyNames.GetName(hk.VirtualKey)} na Macu neexistuje. Vyber iný.",
                               $"The key {KeyNames.GetName(hk.VirtualKey)} does not exist on a Mac. Pick another one."));
                RequireAccessibility();
                MacNative.PostKey(code, MacKeys.CgFlags(hk.Modifiers));
                return Task.CompletedTask;
            case MediaAction media:
                RequireAccessibility();
                MacNative.PostMediaKey(media.Command switch
                {
                    MediaCommand.Next => MacNative.NxNext,
                    MediaCommand.Prev => MacNative.NxPrevious,
                    MediaCommand.VolumeUp => MacNative.NxSoundUp,
                    MediaCommand.VolumeDown => MacNative.NxSoundDown,
                    MediaCommand.Mute => MacNative.NxMute,
                    _ => MacNative.NxPlay, // PlayPause; a Mac has no separate Stop key, play/pause pauses
                });
                return Task.CompletedTask;
            case ObsAction obs:
                return _obs.ExecuteAsync(obs);
            default:
                throw new InvalidOperationException(L.T("Neznámy typ akcie.", "Unknown action type."));
        }
    }

    private static void RequireAccessibility()
    {
        if (MacNative.CheckTrustedAndPrompt()) return;
        throw new InvalidOperationException(L.T(
            "Mac zatiaľ nepovolil posielanie kláves. Povoľ Stream Deck v Nastavenia systému → Súkromie a bezpečnosť → Prístupnosť a skús znova.",
            "macOS does not allow sending keys yet. Allow Stream Deck in System Settings → Privacy & Security → Accessibility and try again."));
    }

    /// <summary>Opens an app, file, folder or URL with /usr/bin/open (apps can get arguments).</summary>
    private static void Open(OpenAction action)
    {
        var target = ExpandHome(action.Target.Trim().Trim('"'));
        if (target.Length == 0) throw new InvalidOperationException(L.T("Nie je zadané, čo sa má otvoriť.", "Nothing to open is set."));

        var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false, RedirectStandardError = true };
        bool isApp = target.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".app/", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(action.Args) && isApp)
        {
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(target);
            psi.ArgumentList.Add("--args");
            foreach (var arg in SplitArgs(action.Args)) psi.ArgumentList.Add(arg);
        }
        else
        {
            psi.ArgumentList.Add(target);
        }

        try
        {
            using var p = Process.Start(psi)!;
            if (p.WaitForExit(3000) && p.ExitCode != 0)
            {
                var err = p.StandardError.ReadToEnd().Trim();
                throw new InvalidOperationException(L.T($"Nedá sa otvoriť „{target}“: {err}", $"Cannot open \"{target}\": {err}"));
            }
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(L.T($"Nedá sa otvoriť „{target}“: {ex.Message}", $"Cannot open \"{target}\": {ex.Message}"));
        }
    }

    internal static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..]
            : path;

    /// <summary>Splits "a \"b c\" d" into a, b c, d.</summary>
    internal static List<string> SplitArgs(string args)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (char ch in args)
        {
            if (ch == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (any) result.Add(current.ToString());
                current.Clear();
                any = false;
                continue;
            }
            current.Append(ch);
            any = true;
        }
        if (any) result.Add(current.ToString());
        return result;
    }
}
