using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VirtualStreamDeck.App.Services;
using VirtualStreamDeck.App.UI;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Obs;
using VirtualStreamDeck.Core.Remote;

namespace VirtualStreamDeck.App;

/// <summary>
/// "VirtualStreamDeck --screenshots &lt;dir&gt;": renders the windows with a sample deck to PNG files,
/// without a screen (Avalonia headless + Skia). CI runs it so the UI can be checked on every push.
/// </summary>
internal static class Screenshots
{
    public static int Run(string outputDir)
    {
        App.ScreenshotMode = true;
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        Directory.CreateDirectory(outputDir);
        int failures = 0;
        foreach (var language in new[] { AppLanguage.Sk, AppLanguage.En })
        {
            try
            {
                Render(outputDir, language);
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"[{language}] {ex}");
            }
        }
        Console.WriteLine($"Screenshots in {outputDir}");
        return failures == 0 ? 0 : 1;
    }

    private sealed class NoActions : IActionExecutor
    {
        public Task ExecuteAsync(DeckAction action) => Task.CompletedTask;
    }

    private static void Render(string dir, AppLanguage language)
    {
        L.Set(language);
        string lang = L.Code(language);
        var configDir = Directory.CreateTempSubdirectory("vsd-shots-").FullName;
        var store = new ConfigStore(configDir);
        var deck = new Deck(store, new NoActions());
        deck.Load();
        deck.Update(c =>
        {
            c.Settings.Language = lang;
            c.ActiveProfile.Name = DeckConfig.DefaultProfileName;
            DeckConfig.AddMediaButtons(c.ActiveProfile);
            c.Buttons.Add(new DeckButton { Row = 1, Col = 0, Label = "GitHub", Action = new OpenAction { Target = "https://github.com" } });
            c.Buttons.Add(new DeckButton { Row = 1, Col = 1, Label = "Mute Teams", Hotkey = "Ctrl+Alt+M", Action = new KeysAction { Keys = "Ctrl+Shift+M" } });
            c.Buttons.Add(new DeckButton { Row = 1, Col = 2, Label = "Gaming", Action = new ObsAction { Command = ObsCommand.SwitchScene, Scene = "Gaming" } });
            c.Buttons.Add(new DeckButton { Row = 1, Col = 3, Label = "REC", Action = new ObsAction { Command = ObsCommand.ToggleRecord } });
            c.Buttons.Add(new DeckButton { Row = 1, Col = 4, Label = "Mic", Action = new ObsAction { Command = ObsCommand.ToggleMute, Source = "Mic/Aux" } });
            c.Buttons.Add(new DeckButton { Row = 2, Col = 0, Label = L.T("Dokumenty", "Documents"), Action = new OpenAction { Target = configDir } });
            c.AddMediaProfile();
            c.Settings.Remote.Enabled = true;
            c.Settings.Remote.Token = "Xy7_kP2mQa9LbC4dEfGh1w";
        });

        var obs = new ObsClient();
        var remote = new RemoteServer(deck);
        var updates = new UpdateService(deck);
        using var hotkeys = HotkeyService.Create();
        var main = new MainWindow(deck, obs, remote, updates, hotkeys);
        main.Show();
        Save(main, dir, $"main-{lang}");

        foreach (var section in new[] { SettingsPanel.Profiles, SettingsPanel.Layout, SettingsPanel.General, SettingsPanel.Phone, SettingsPanel.Obs, SettingsPanel.Guide })
        {
            main.OpenSettings(section);
            Save(main, dir, $"panel-{section}-{lang}");
        }
        main.CloseForExit();

        var editor = new ButtonEditorWindow(deck.Config, deck.Config.GetAt(1, 1), 1, 1, new IconService(store), obs);
        editor.Show();
        Save(editor, dir, $"editor-keys-{lang}");
        editor.Close();

        var newButton = new ButtonEditorWindow(deck.Config, null, 2, 4, new IconService(store), obs);
        newButton.Show();
        Save(newButton, dir, $"editor-new-{lang}");
        newButton.Close();

        var confirm = Dialogs.Build(L.T("Ukončiť Stream Deck", "Quit Stream Deck"),
            L.T("Naozaj ukončiť Virtual Stream Deck?", "Quit Virtual Stream Deck?"), L.T("Áno", "Yes"), L.T("Nie", "No"), false);
        confirm.Show();
        Save(confirm, dir, $"dialog-{lang}");
        confirm.Close();

        if (language == AppLanguage.Sk)
        {
            var pick = new LanguageWindow();
            pick.Show();
            Save(pick, dir, "language");
            pick.Close();
        }
        obs.Dispose();
    }

    private static void Save(Window window, string dir, string name)
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        var frame = window.CaptureRenderedFrame();
        if (frame == null) throw new InvalidOperationException($"no frame for {name}");
        frame.Save(Path.Combine(dir, name + ".png"));
        Console.WriteLine($"  {name}.png  {frame.PixelSize.Width}×{frame.PixelSize.Height}");
    }
}
