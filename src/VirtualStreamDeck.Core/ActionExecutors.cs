using System.Diagnostics;
using VirtualStreamDeck.Core.Mac;
using VirtualStreamDeck.Core.Obs;
using VirtualStreamDeck.Core.Windows;

namespace VirtualStreamDeck.Core;

public static class ActionExecutors
{
    /// <summary>The executor for the current operating system.</summary>
    public static IActionExecutor ForCurrentPlatform(ObsClient obs)
    {
        if (OperatingSystem.IsWindows()) return new WindowsActionExecutor(obs);
        if (OperatingSystem.IsMacOS()) return new MacActionExecutor(obs);
        return new BasicActionExecutor(obs);
    }

    /// <summary>Elsewhere (Linux, for development): open things and control OBS; no key input.</summary>
    private sealed class BasicActionExecutor(ObsClient obs) : IActionExecutor
    {
        public Task ExecuteAsync(DeckAction action)
        {
            switch (action)
            {
                case OpenAction open:
                    using (Process.Start(new ProcessStartInfo("xdg-open", open.Target) { UseShellExecute = false })) { }
                    return Task.CompletedTask;
                case ObsAction o:
                    return obs.ExecuteAsync(o);
                default:
                    throw new PlatformNotSupportedException(L.T("Táto akcia tu nie je podporovaná.", "This action is not supported here."));
            }
        }
    }
}
