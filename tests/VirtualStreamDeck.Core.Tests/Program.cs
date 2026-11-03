#pragma warning disable CA1416 // NativeInput is Windows-only; the test only measures a struct.
using System.Reflection;
using VirtualStreamDeck.Core;

// Tiny test runner without NuGet dependencies. Every public static void method of Tests is a test.
int failed = 0, passed = 0;
foreach (var m in typeof(Tests).GetMethods(BindingFlags.Public | BindingFlags.Static))
{
    try
    {
        var result = m.Invoke(null, null);
        if (result is Task t) t.GetAwaiter().GetResult();
        passed++;
        Console.WriteLine($"  ok    {m.Name}");
    }
    catch (TargetInvocationException ex)
    {
        failed++;
        Console.WriteLine($"  FAIL  {m.Name}: {ex.InnerException?.Message}");
    }
}
Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static class Assert
{
    public static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what ?? "value"}: expected <{expected}> but was <{actual}>");
    }

    public static void True(bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }
}

static class Tests
{
    // ───────── Hotkey ─────────

    public static void Hotkey_ParsesAndFormatsCanonically()
    {
        Assert.Equal("Ctrl+Alt+1", Hotkey.Parse("alt + ctrl + 1").ToString());
        Assert.Equal("Ctrl+Shift+M", Hotkey.Parse("Ctrl+Shift+m").ToString());
        Assert.Equal("F13", Hotkey.Parse("F13").ToString());
        Assert.Equal("NumPad5", Hotkey.Parse("Num5").ToString());
        Assert.Equal("Shift+Win+S", Hotkey.Parse("Win+Shift+S").ToString()); // order: Ctrl, Alt, Shift, Win
        Assert.Equal("Ctrl+Escape", Hotkey.Parse("Control+Esc").ToString());
        Assert.Equal("Ctrl+1", Hotkey.Parse("Ctrl+D1").ToString()); // WPF key name alias
    }

    public static void Hotkey_StoresWin32Values()
    {
        var hk = Hotkey.Parse("Ctrl+Alt+Shift+Win+A");
        Assert.Equal(1 | 2 | 4 | 8, (int)hk.Modifiers, "MOD_* flags");
        Assert.Equal(0x41, hk.VirtualKey, "VK_A");
        Assert.Equal(0x7C, Hotkey.Parse("F13").VirtualKey, "VK_F13");
        Assert.Equal(0x87, Hotkey.Parse("F24").VirtualKey, "VK_F24");
        Assert.Equal(0x65, Hotkey.Parse("NumPad5").VirtualKey, "VK_NUMPAD5");
    }

    public static void Hotkey_RejectsInvalid()
    {
        foreach (var bad in new[] { "", " ", "Ctrl+", "+A", "Ctrl+Shift", "Foo+A", "Ctrl+Bogus", "A+B", "Ctrl+LeftShift" })
            Assert.True(!Hotkey.TryParse(bad, out _), $"'{bad}' should be invalid");
    }

    public static void Hotkey_UnknownKeysRoundTripAsHex()
    {
        var hk = new Hotkey(HotkeyModifiers.Ctrl, 0xFE);
        Assert.Equal("Ctrl+VK_0xFE", hk.ToString());
        Assert.Equal(hk, Hotkey.Parse(hk.ToString()));
    }

    public static void Hotkey_EveryNamedKeyRoundTrips()
    {
        for (int vk = 1; vk < 0xFF; vk++)
        {
            if (KeyNames.IsModifier(vk)) continue;
            var hk = new Hotkey(HotkeyModifiers.Alt, vk);
            Assert.Equal(hk, Hotkey.Parse(hk.ToString()), $"vk 0x{vk:X2}");
        }
    }

    // ───────── Config JSON ─────────

    public static void Config_ReadsTheDocumentedFormat()
    {
        const string json = """
        {
          "version": 1,
          "settings": { "rows": 3, "columns": 5, "buttonSize": 80, "spacing": 8,
                        "alwaysOnTop": true, "showLabels": true, "startWithWindows": false,
                        "window": { "x": 100, "y": 100 } },
          "buttons": [
            { "id": "7f2c", "row": 0, "col": 0, "label": "Discord", "icon": "icons/a3f9c1.png",
              "hotkey": "Ctrl+Alt+1",
              "action": { "type": "open", "target": "C:\\Users\\x\\Discord.lnk", "args": "" } },
            { "id": "91ab", "row": 0, "col": 1, "label": "Mute", "icon": null, "hotkey": null,
              "action": { "keys": "Ctrl+Shift+M", "type": "keys" } },
            { "id": "c04e", "row": 0, "col": 2, "label": "Play/Pause", "icon": null, "hotkey": "F13",
              "action": { "type": "media", "command": "playPause" } }
          ]
        }
        """;
        var c = ConfigStore.Deserialize(json);
        Assert.Equal(3, c.Buttons.Count, "buttons");
        Assert.Equal(100.0, c.Settings.Window!.X, "window.x");
        Assert.True(c.Buttons[0].Action is OpenAction { Target: @"C:\Users\x\Discord.lnk" }, "open action");
        Assert.True(c.Buttons[1].Action is KeysAction { Keys: "Ctrl+Shift+M" }, "keys action (type not first)");
        Assert.True(c.Buttons[2].Action is MediaAction { Command: MediaCommand.PlayPause }, "media action");
    }

    public static void Config_RoundTripsAndWritesReadableJson()
    {
        var c = new DeckConfig();
        c.Buttons.Add(new DeckButton { Id = "a", Label = "Spotify – hudba", Action = new MediaAction { Command = MediaCommand.VolumeUp } });
        c.Buttons.Add(new DeckButton { Id = "b", Col = 1, Action = new OpenAction { Target = "https://example.com" } });
        var json = ConfigStore.Serialize(c);

        Assert.True(json.Contains("\"type\": \"media\""), "type discriminator");
        Assert.True(json.Contains("\"command\": \"volumeUp\""), "enum as camelCase string");
        Assert.True(json.Contains("Spotify – hudba"), "non-ASCII not escaped");
        Assert.True(!json.Contains("isUrl") && !json.Contains("virtualKey"), "computed properties not serialized");

        var back = ConfigStore.Deserialize(json);
        Assert.Equal(ConfigStore.Serialize(back), json, "round trip");
    }

    public static void OpenAction_DetectsUrls()
    {
        Assert.True(new OpenAction { Target = "https://youtube.com" }.IsUrl, "https");
        Assert.True(new OpenAction { Target = "steam://rungameid/570" }.IsUrl, "custom scheme");
        Assert.True(!new OpenAction { Target = @"C:\Windows\notepad.exe" }.IsUrl, "path");
        Assert.True(!new OpenAction { Target = "notepad" }.IsUrl, "bare name");
    }

    // ───────── Grid rules ─────────

    public static void Grid_ShrinkingHidesButDoesNotDelete()
    {
        var c = new DeckConfig();
        c.Buttons.Add(new DeckButton { Id = "far", Row = 2, Col = 4 });
        c.Settings.Rows = 2;
        Assert.Equal(1, c.Buttons.Count, "still stored");
        Assert.True(!c.IsVisible(c.Buttons[0]), "hidden");
        c.Settings.Rows = 3;
        Assert.True(c.IsVisible(c.Buttons[0]), "visible again");
    }

    public static void Grid_MoveSwapsButtons()
    {
        var c = new DeckConfig();
        c.Buttons.Add(new DeckButton { Id = "a", Row = 0, Col = 0 });
        c.Buttons.Add(new DeckButton { Id = "b", Row = 1, Col = 1 });
        c.Move("a", 1, 1);
        Assert.Equal((1, 1), (c.FindById("a")!.Row, c.FindById("a")!.Col), "a moved");
        Assert.Equal((0, 0), (c.FindById("b")!.Row, c.FindById("b")!.Col), "b swapped");
        c.Move("a", 2, 3);
        Assert.Equal((2, 3), (c.FindById("a")!.Row, c.FindById("a")!.Col), "move to empty");
    }

    public static void Grid_UpsertReplacesSameIdAndSameCell()
    {
        var c = new DeckConfig();
        c.Buttons.Add(new DeckButton { Id = "a", Row = 0, Col = 0, Label = "old" });
        c.Buttons.Add(new DeckButton { Id = "b", Row = 0, Col = 1 });
        c.Upsert(new DeckButton { Id = "a", Row = 0, Col = 1, Label = "new" });
        Assert.Equal(1, c.Buttons.Count, "b on target cell replaced, old a removed");
        Assert.Equal("new", c.Buttons[0].Label);
    }

    public static void Grid_FindEmptyCellWrapsAndFillsUp()
    {
        var c = new DeckConfig { Settings = { Rows = 2, Columns = 2 } };
        Assert.Equal((0, 0), c.FindEmptyCell()!.Value, "first");
        c.Buttons.Add(new DeckButton { Row = 1, Col = 1 });
        Assert.Equal((0, 0), c.FindEmptyCell(1, 1)!.Value, "wraps around");
        c.Buttons.Add(new DeckButton { Row = 0, Col = 0 });
        c.Buttons.Add(new DeckButton { Row = 0, Col = 1 });
        Assert.Equal((1, 0), c.FindEmptyCell(0, 1)!.Value, "skips taken");
        c.Buttons.Add(new DeckButton { Row = 1, Col = 0 });
        Assert.True(c.FindEmptyCell() == null, "full");
    }

    public static void Grid_DetectsHotkeyConflicts()
    {
        var c = new DeckConfig();
        c.Buttons.Add(new DeckButton { Id = "a", Hotkey = "Ctrl+Alt+1" });
        Assert.Equal("a", c.FindHotkeyConflict(Hotkey.Parse("alt+ctrl+1"), "b")?.Id, "conflict");
        Assert.True(c.FindHotkeyConflict(Hotkey.Parse("Ctrl+Alt+1"), "a") == null, "own hotkey is fine");
        Assert.True(c.FindHotkeyConflict(Hotkey.Parse("Ctrl+Alt+2"), null) == null, "different");
    }

    public static void Settings_AreClamped()
    {
        var s = new DeckSettings { Rows = 0, Columns = 99, ButtonSize = 5, Spacing = -3 };
        s.Normalize();
        Assert.Equal((1, 12, 40, 0), (s.Rows, s.Columns, s.ButtonSize, s.Spacing));
    }

    // ───────── ConfigStore (file system) ─────────

    public static void Store_SaveAndLoad()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        Assert.Equal(0, store.Load().Buttons.Count, "missing file = empty deck");

        var c = new DeckConfig();
        c.Buttons.Add(new DeckButton { Label = "x", Action = new KeysAction { Keys = "Ctrl+C" } });
        store.Save(c);
        Assert.True(!File.Exists(store.ConfigPath + ".tmp"), "temp file cleaned up");
        Assert.Equal("x", store.Load().Buttons[0].Label);
        store.Save(c); // overwrite works
    }

    public static void Store_BacksUpBrokenConfig()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        File.WriteAllText(store.ConfigPath, "{ this is not json");
        var c = store.Load();
        Assert.Equal(0, c.Buttons.Count, "fresh config");
        Assert.True(store.LastLoadWarning != null, "warning set");
        Assert.True(Directory.GetFiles(dir.Path, "config.broken-*.json").Length == 1, "backup written");
    }

    public static void Store_UnknownActionTypeIsTreatedAsBroken()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        File.WriteAllText(store.ConfigPath, """{ "buttons": [ { "action": { "type": "teleport" } } ] }""");
        store.Load();
        Assert.True(store.LastLoadWarning != null, "warning set");
    }

    public static void Store_PortableModeWhenConfigNextToExe()
    {
        using var exe = new TempDir();
        Assert.True(ConfigStore.ResolveDirectory(exe.Path, "/appdata").EndsWith("VirtualStreamDeck"), "appdata");
        File.WriteAllText(Path.Combine(exe.Path, "config.json"), "{}");
        Assert.Equal(exe.Path, ConfigStore.ResolveDirectory(exe.Path, "/appdata"), "portable");
    }

    public static void Store_CleanupRemovesOnlyUnusedIcons()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        Directory.CreateDirectory(store.IconsDirectory);
        File.WriteAllText(Path.Combine(store.IconsDirectory, "used.png"), "");
        File.WriteAllText(Path.Combine(store.IconsDirectory, "orphan.png"), "");
        var c = new DeckConfig();
        c.Buttons.Add(new DeckButton { Icon = ConfigStore.IconRelativePath("used.png"), Row = 9 }); // hidden still counts
        Assert.Equal(1, store.CleanupIcons(c), "deleted");
        Assert.True(File.Exists(Path.Combine(store.IconsDirectory, "used.png")), "used kept");
        Assert.True(store.ResolveIconPath("icons/used.png") != null, "resolves");
        Assert.True(store.ResolveIconPath("icons/orphan.png") == null, "missing => null");
    }

    // ───────── Deck ─────────

    public static async Task Deck_PressRunsActionAndReportsErrors()
    {
        using var dir = new TempDir();
        var exec = new FakeExecutor();
        var deck = new Deck(new ConfigStore(dir.Path), exec);
        deck.Load();
        int changes = 0;
        deck.Changed += (_, _) => changes++;

        deck.Update(c =>
        {
            c.Buttons.Add(new DeckButton { Id = "ok", Action = new MediaAction() });
            c.Buttons.Add(new DeckButton { Id = "bad", Col = 1, Action = new KeysAction { Keys = "boom" } });
            c.Buttons.Add(new DeckButton { Id = "hidden", Row = 50, Action = new MediaAction() });
            c.Buttons.Add(new DeckButton { Id = "empty", Col = 2 });
        });
        Assert.Equal(1, changes, "changed raised");
        deck.Update(c => c.Settings.Window = new WindowPosition(), notify: false);
        Assert.Equal(1, changes, "silent update");

        Assert.True((await deck.PressAsync("ok")).Success, "ok");
        Assert.Equal(1, exec.Count, "executed");

        var bad = await deck.PressAsync("bad");
        Assert.True(!bad.Success && bad.Error == "boom", "error surfaced");

        Assert.True(!(await deck.PressAsync("hidden")).Success, "hidden button can't be pressed");
        Assert.True(!(await deck.PressAsync("nope")).Success, "unknown id");
        Assert.True((await deck.PressAsync("empty")).Success, "no action = no-op");
        Assert.Equal(2, exec.Count, "executed");

        Assert.True(File.Exists(deck.Store.ConfigPath), "saved");
    }

    public static void NativeInput_StructHasWin32Size()
    {
        // SendInput fails unless cbSize matches the native INPUT struct exactly.
        int expected = IntPtr.Size == 8 ? 40 : 28;
        Assert.Equal(expected, VirtualStreamDeck.Core.Windows.NativeInput.InputStructSize, "sizeof(INPUT)");
    }

    private sealed class FakeExecutor : IActionExecutor
    {
        public int Count;
        public void Execute(DeckAction action)
        {
            Count++;
            if (action is KeysAction k) throw new InvalidOperationException(k.Keys);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("vsd-test-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
