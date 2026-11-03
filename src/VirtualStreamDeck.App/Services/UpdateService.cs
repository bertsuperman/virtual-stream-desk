using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using VirtualStreamDeck.App.UI;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Update;

namespace VirtualStreamDeck.App.Services;

/// <summary>
/// Checks GitHub for a newer build and installs it with one click (see <see cref="Updater"/>).
/// All members are used on the UI thread; <see cref="Changed"/> is raised there too.
/// </summary>
internal sealed class UpdateService
{
    public enum State { Idle, Checking, UpToDate, Available, Downloading, Restarting, Error }

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly HttpClient Http = Updater.CreateHttpClient();

    private readonly Deck _deck;
    private readonly DispatcherTimer _timer;
    private readonly string? _exe = Environment.ProcessPath;

    public UpdateService(Deck deck)
    {
        _deck = deck;
        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += async (_, _) => await AutoCheckAsync();
    }

    public State Current { get; private set; } = State.Idle;
    public UpdateInfo? Available { get; private set; }
    public int Percent { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Local builds are 0.2.0: they don't look for updates on their own.</summary>
    public static bool IsDevBuild => AppInfo.Version is "0.2.0" or "0.0.0";

    /// <summary>Where the app lives: the .exe on Windows, the .app folder on a Mac (null when run from a build folder).</summary>
    private string? InstallTarget => OperatingSystem.IsMacOS() ? Updater.BundleOf(_exe) : _exe;

    public event Action? Changed;

    public void Start()
    {
        Cleanup();
        _timer.Start();
        // Give the app (and a just-replaced old instance) a moment before going online.
        DispatcherTimer.RunOnce(async () =>
        {
            Cleanup(); // the old instance has exited by now
            await AutoCheckAsync();
        }, TimeSpan.FromSeconds(8));
    }

    private void Cleanup()
    {
        if (InstallTarget is not { } target) return;
        if (OperatingSystem.IsMacOS()) Updater.CleanupMacBundle(target);
        else Updater.Cleanup(target);
    }

    private async Task AutoCheckAsync()
    {
        if (IsDevBuild || !_deck.Config.Settings.CheckForUpdates) return;
        if (Current is State.Checking or State.Downloading or State.Restarting) return;
        await CheckAsync(quiet: true);
    }

    /// <summary>Asks GitHub for the newest build. <paramref name="quiet"/>: network errors are not shown.</summary>
    public async Task CheckAsync(bool quiet = false)
    {
        if (Current is State.Checking or State.Downloading or State.Restarting) return;
        var before = Current;
        Set(State.Checking);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var info = await Updater.CheckAsync(Http, AppInfo.Version, ct: cts.Token);
            // A release without a file for this computer (e.g. Mac build still missing) is not offered.
            if (info != null && Updater.AssetForCurrentPlatform() is { } asset && info.HashOf(asset) == null) info = null;
            Available = info;
            Set(info != null ? State.Available : State.UpToDate);
        }
        catch (Exception ex) when (ex is UpdateException or OperationCanceledException)
        {
            if (quiet)
            {
                Set(before); // background check: stay silent, try again later
                return;
            }
            Error = ex is UpdateException ? ex.Message : L.T("GitHub neodpovedá. Skús to neskôr.", "GitHub is not responding. Try again later.");
            Set(State.Error);
        }
    }

    /// <summary>Asks, downloads, swaps the app and starts the new build (which then replaces this instance).</summary>
    public async Task ConfirmAndInstallAsync(Window? owner)
    {
        if (Available is not { } info || Current is State.Downloading or State.Restarting) return;
        if (InstallTarget is not { } target || Updater.AssetForCurrentPlatform() is not { } asset)
        {
            Error = L.T("Táto kópia sa nedá aktualizovať sama (spustená mimo appky). Stiahni novú verziu ručne.",
                "This copy can't update itself (not started as an app). Download the new version manually.");
            Set(State.Error);
            return;
        }

        bool yes = await Dialogs.ConfirmAsync(owner, L.T("Aktualizácia", "Update"),
            L.T($"Aktualizovať na verziu {info.Version}?\n\nTeraz máš {AppInfo.Version}. Stream Deck sa na pár sekúnd zavrie a sám sa znova spustí. Tlačidlá a nastavenia ostanú.",
                $"Update to version {info.Version}?\n\nYou have {AppInfo.Version}. Stream Deck closes for a few seconds and starts again by itself. Buttons and settings stay."),
            defaultYes: true);
        if (!yes) return;

        Percent = 0;
        Set(State.Downloading);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var progress = new Progress<int>(p =>
            {
                Percent = p;
                Changed?.Invoke();
            });
            if (OperatingSystem.IsMacOS())
            {
                var zip = Path.Combine(Path.GetTempPath(), "VirtualStreamDeck-update.zip");
                await Updater.DownloadAssetAsync(Http, info, asset, zip, progress, ct: cts.Token);
                await Task.Run(() => Updater.InstallMacBundle(zip, target));
            }
            else
            {
                var downloaded = await Updater.DownloadAsync(Http, info, target, progress, ct: cts.Token);
                Updater.Install(downloaded, target);
            }
        }
        catch (Exception ex) when (ex is UpdateException or OperationCanceledException)
        {
            Error = ex is UpdateException ? ex.Message : L.T("Sťahovanie trvalo príliš dlho. Skús to znova.", "The download took too long. Try again.");
            Set(State.Error);
            return;
        }

        Set(State.Restarting);
        try
        {
            // The new build sees a different version and asks this instance to quit (SingleInstance).
            if (OperatingSystem.IsMacOS()) Process.Start("/usr/bin/open", new[] { "-n", target });
            else Process.Start(new ProcessStartInfo(target) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Error = L.T($"Nová verzia je stiahnutá, ale nepodarilo sa ju spustiť ({ex.Message}). Ukonči Stream Deck a spusti ho znova.",
                $"The new version is downloaded but could not be started ({ex.Message}). Quit Stream Deck and start it again.");
            Set(State.Error);
            return;
        }

        // Normally this instance is gone within a few seconds.
        await Task.Delay(TimeSpan.FromSeconds(30));
        if (Current != State.Restarting) return;
        Error = L.T("Nová verzia je pripravená, ale nespustila sa. Ukonči Stream Deck a spusti ho znova.",
            "The new version is ready but did not start. Quit Stream Deck and start it again.");
        Set(State.Error);
    }

    private void Set(State state)
    {
        Current = state;
        if (state != State.Error) Error = null;
        Changed?.Invoke();
    }
}
