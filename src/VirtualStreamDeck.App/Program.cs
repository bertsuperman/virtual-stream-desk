using System;
using Avalonia;
using Avalonia.Threading;
using VirtualStreamDeck.App.Services;

namespace VirtualStreamDeck.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // CI: render every window to PNG files without a screen.
        if (args.Length >= 2 && args[0] == "--screenshots") return Screenshots.Run(args[1]);

        // Same build launched again: just show the running window.
        // A different build (e.g. a newer download or update): the running one quits and this one takes over.
        if (!SingleInstance.TryAcquire(
                onShowRequest: () => Dispatcher.UIThread.Post(() => App.Instance?.ShowDeck()),
                onExitRequest: () => Dispatcher.UIThread.Post(() => App.Instance?.ExitApp())))
            return 0;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    // Also used by the Avalonia designer / previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .LogToTrace();
}
