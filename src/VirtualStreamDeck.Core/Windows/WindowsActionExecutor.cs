using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using VirtualStreamDeck.Core.Obs;

namespace VirtualStreamDeck.Core.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsActionExecutor : IActionExecutor
{
    private readonly ObsClient _obs;

    public WindowsActionExecutor(ObsClient obs) => _obs = obs;

    public Task ExecuteAsync(DeckAction action)
    {
        switch (action)
        {
            case OpenAction open:
                Open(open);
                return Task.CompletedTask;
            case KeysAction keys:
                if (!Hotkey.TryParse(keys.Keys, out var hk))
                    throw new InvalidOperationException($"Neplatná klávesová skratka: '{keys.Keys}'.");
                NativeInput.SendChord(hk);
                return Task.CompletedTask;
            case MediaAction media:
                NativeInput.TapKey(media.VirtualKey);
                return Task.CompletedTask;
            case ObsAction obs:
                return _obs.ExecuteAsync(obs);
            default:
                throw new InvalidOperationException("Neznámy typ akcie.");
        }
    }

    private static void Open(OpenAction action)
    {
        var target = Environment.ExpandEnvironmentVariables(action.Target.Trim().Trim('"'));
        if (target.Length == 0) throw new InvalidOperationException("Nie je zadané, čo sa má otvoriť.");

        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(action.Args))
            psi.Arguments = Environment.ExpandEnvironmentVariables(action.Args);
        if (File.Exists(target))
            psi.WorkingDirectory = Path.GetDirectoryName(target) ?? "";

        try
        {
            using var _ = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Nedá sa otvoriť „{target}“: {ex.Message}");
        }
    }
}
