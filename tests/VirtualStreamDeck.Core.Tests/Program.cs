#pragma warning disable CA1416 // NativeInput is Windows-only; the test only measures a struct.
using System.Reflection;
using System.Text.Json.Nodes;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Obs;
using VirtualStreamDeck.Core.Update;
using VirtualStreamDeck.Core.Mac;

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
    catch (Exception ex)
    {
        failed++;
        var inner = ex is TargetInvocationException t ? t.InnerException ?? ex : ex;
        Console.WriteLine($"  FAIL  {m.Name}: {inner.Message}");
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
        Assert.Equal(1, c.Profiles.Count, "v1 config migrated into one profile");
        Assert.Equal("Hlavný", c.ActiveProfile.Name, "profile name");
        Assert.Equal((3, 5), (c.Rows, c.Columns), "rows/columns moved into the profile");
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
        Assert.True(!json.Contains("isUrl") && !json.Contains("virtualKey") && !json.Contains("visibleButtons") &&
                    !json.Contains("activeProfile\"") && !json.Contains("allButtons"), "computed properties not serialized");

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
        c.Rows = 2;
        Assert.Equal(1, c.Buttons.Count, "still stored");
        Assert.True(!c.IsVisible(c.Buttons[0]), "hidden");
        c.Rows = 3;
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
        var c = new DeckConfig();
        c.Rows = 2;
        c.Columns = 2;
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
        var c = new DeckConfig { Settings = { ButtonSize = 5, Spacing = -3 } };
        c.Rows = 0;
        c.Columns = 99;
        c.Normalize();
        Assert.Equal((1, 12, 40, 0), (c.Rows, c.Columns, c.Settings.ButtonSize, c.Settings.Spacing));
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

    // ───────── Profiles ─────────

    public static void Profiles_MigratedConfigIsWrittenInNewFormat()
    {
        var c = ConfigStore.Deserialize("""{ "version": 2, "settings": { "rows": 2, "columns": 4 }, "buttons": [ { "id": "a", "label": "X" } ] }""");
        var json = ConfigStore.Serialize(c);
        Assert.True(json.Contains("\"profiles\"") && json.Contains("\"activeProfileId\""), "profiles written");
        Assert.True(!json.Contains("\"rows\": 2,\n    \"columns\"") , "no legacy rows in settings");
        var back = ConfigStore.Deserialize(json);
        Assert.Equal((2, 4, "X"), (back.Rows, back.Columns, back.Buttons[0].Label), "round trip");
        Assert.Equal(DeckConfig.CurrentVersion, back.Version, "version bumped");
        Assert.True(!json.Contains("\"buttons\": [\n    {\n      \"id\": \"a\"") || json.IndexOf("\"buttons\"") > json.IndexOf("\"profiles\""), "buttons only inside profiles");
    }

    public static void Profiles_AddDuplicateRemoveSwitch()
    {
        var c = new DeckConfig();
        c.Normalize();
        c.Buttons.Add(new DeckButton { Id = "a", Label = "A", Icon = "icons/a.png", Hotkey = "F13" });
        var main = c.ActiveProfile;

        var dup = c.DuplicateProfile(main.Id, c.UniqueProfileName(main.Name));
        Assert.Equal("Hlavný 2", dup.Name, "unique name");
        Assert.Equal(1, dup.Buttons.Count, "buttons copied");
        Assert.True(dup.Buttons[0].Id != "a" && dup.Buttons[0].Label == "A", "new ids, same content");
        dup.Buttons[0].Label = "changed";
        Assert.Equal("A", main.Buttons[0].Label, "deep copy");

        var empty = c.AddProfile("Hry");
        Assert.Equal(0, empty.Buttons.Count, "new profile is empty");
        c.ActiveProfileId = empty.Id;
        Assert.Equal(0, c.Buttons.Count, "Buttons follows the active profile");
        Assert.True(c.FindHotkeyConflict(Hotkey.Parse("F13"), null) == null, "hotkeys are per profile");
        Assert.Equal(2, c.AllButtons.Count(), "all buttons across profiles");

        Assert.True(c.RemoveProfile(empty.Id), "removed");
        Assert.Equal(main.Id, c.ActiveProfile.Id, "active falls back to the first profile");
        Assert.True(c.RemoveProfile(dup.Id), "removed dup");
        Assert.True(!c.RemoveProfile(main.Id), "last profile stays");
    }

    public static async Task Profiles_ProfileActionSwitches()
    {
        using var dir = new TempDir();
        var deck = new Deck(new ConfigStore(dir.Path), new FakeExecutor());
        deck.Load();
        string gamesId = "";
        deck.Update(c =>
        {
            var games = c.AddProfile("Hry");
            gamesId = games.Id;
            c.Buttons.Add(new DeckButton { Id = "go", Action = new ProfileAction { ProfileId = games.Id } });
            c.Buttons.Add(new DeckButton { Id = "dead", Col = 1, Action = new ProfileAction { ProfileId = "missing" } });
        });
        int changes = 0;
        deck.Changed += (_, _) => changes++;

        Assert.True(!(await deck.PressAsync("dead")).Success, "missing profile reported");
        Assert.True((await deck.PressAsync("go")).Success, "switch ok");
        Assert.Equal(gamesId, deck.Config.ActiveProfile.Id, "switched");
        Assert.Equal(1, changes, "Changed raised");
        Assert.True(deck.SwitchProfile(gamesId) && changes == 1, "switching to the active profile is a no-op");

        var reloaded = new ConfigStore(dir.Path).Load();
        Assert.Equal(gamesId, reloaded.ActiveProfileId, "active profile persisted");
    }

    public static void Store_CleanupKeepsIconsOfOtherProfiles()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        Directory.CreateDirectory(store.IconsDirectory);
        File.WriteAllText(Path.Combine(store.IconsDirectory, "other.png"), "");
        var c = new DeckConfig();
        c.Normalize();
        c.AddProfile("B").Buttons.Add(new DeckButton { Icon = "icons/other.png" });
        Assert.Equal(0, store.CleanupIcons(c), "icon of an inactive profile kept");
    }

    // ───────── QR + remote ─────────

    public static void Qr_PicksVersionBySize()
    {
        Assert.Equal(1, VirtualStreamDeck.Core.Remote.QrCode.Encode("hello").Version, "short");
        var url = VirtualStreamDeck.Core.Remote.RemoteServer.BuildUrl("192.168.100.200", 8765, RemoteSettings.NewToken());
        var qr = VirtualStreamDeck.Core.Remote.QrCode.Encode(url);
        Assert.True(qr.Version <= 4 && qr.Size == qr.Version * 4 + 17, $"typical URL fits v4 (got v{qr.Version})");
        Assert.True(qr[0, 0] && qr[6, 6] && !qr[7, 7], "finder pattern in the corner");
        Assert.Equal(22, RemoteSettings.NewToken().Length, "token length");
    }

    public static async Task Remote_ServesAppAndRequiresToken()
    {
        using var env = await RemoteEnv.StartAsync();
        var http = env.Http;
        var html = await http.GetStringAsync("/");
        Assert.True(html.Contains("<title>Stream Deck</title>"), "app served without token");
        Assert.Equal(200, (int)(await http.GetAsync("/app.png")).StatusCode, "app icon");
        Assert.Equal(200, (int)(await http.GetAsync("/manifest.webmanifest")).StatusCode, "manifest");
        Assert.True(!(await http.GetStringAsync("/manifest.webmanifest")).Contains("start_url"), "manifest without token: page address");
        Assert.True(!(await http.GetStringAsync("/manifest.webmanifest?t=wrong")).Contains("start_url"), "wrong token not echoed");
        var manifest = await http.GetStringAsync("/manifest.webmanifest?t=" + env.Token);
        Assert.True(manifest.Contains($"\"start_url\": \"/?t={env.Token}\""), "home-screen icon starts with the token");
        System.Text.Json.JsonDocument.Parse(manifest).Dispose(); // still valid JSON
        Assert.True((await http.GetStringAsync("/?t=" + env.Token)).Contains($"href=\"/manifest.webmanifest?t={env.Token}\""),
            "page links the manifest with the token from the start");
        Assert.True((await http.GetStringAsync("/?t=wrong")).Contains("href=\"/manifest.webmanifest\""), "no wrong token in the page");
        Assert.Equal(401, (int)(await http.GetAsync("/api/ping")).StatusCode, "no token");
        Assert.Equal(401, (int)(await http.GetAsync("/api/ping?t=wrong")).StatusCode, "wrong token");
        Assert.Equal(200, (int)(await http.GetAsync("/api/ping?t=" + env.Token)).StatusCode, "token ok");
        Assert.Equal(404, (int)(await http.GetAsync("/nope")).StatusCode, "404");

        File.WriteAllBytes(Path.Combine(env.Store.IconsDirectory, "abc.png"), new byte[] { 1, 2, 3 });
        Assert.Equal(401, (int)(await http.GetAsync("/icons/abc.png")).StatusCode, "icon needs token");
        Assert.Equal(3, (await http.GetByteArrayAsync("/icons/abc.png?t=" + env.Token)).Length, "icon served");
        Assert.Equal(404, (int)(await http.GetAsync("/icons/..%2Fconfig.json?t=" + env.Token)).StatusCode, "no path traversal");
        Assert.Equal(404, (int)(await http.GetAsync("/icons/../config.json?t=" + env.Token)).StatusCode, "no path traversal 2");
    }

    public static async Task Remote_WebSocketPressAndLiveUpdates()
    {
        using var env = await RemoteEnv.StartAsync();
        string? pressedOnDesktop = null;
        env.Server.ButtonPressed += id => pressedOnDesktop = id;

        using var ws = new System.Net.WebSockets.ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{env.Server.Port}/ws?t={env.Token}"), CancellationToken.None);
        var deckMsg = await RemoteEnv.ReceiveAsync(ws);
        Assert.Equal("deck", deckMsg["type"]!.GetValue<string>(), "deck on connect");
        Assert.Equal(2, deckMsg["buttons"]!.AsArray().Count, "hidden button not sent");
        Assert.Equal("🔊", deckMsg["buttons"]![0]!["emoji"]!.GetValue<string>(), "emoji for media");
        Assert.Equal(1, env.Server.ClientCount, "client counted");

        await RemoteEnv.SendAsync(ws, """{"type":"press","id":"vol","seq":7}""");
        var result = await RemoteEnv.ReceiveAsync(ws);
        Assert.True(result["type"]!.GetValue<string>() == "result" && result["ok"]!.GetValue<bool>() && result["seq"]!.GetValue<int>() == 7, "press ok");
        Assert.Equal(1, env.Executor.Count, "executed on the PC");
        Assert.Equal("vol", pressedOnDesktop, "desktop notified");

        await RemoteEnv.SendAsync(ws, """{"type":"press","id":"bad","seq":8}""");
        var bad = await RemoteEnv.ReceiveAsync(ws);
        Assert.True(!bad["ok"]!.GetValue<bool>() && bad["error"]!.GetValue<string>() == "boom", "error forwarded");

        // A change on the PC is pushed to the phone.
        env.Deck.Update(c => c.Buttons.Add(new DeckButton { Id = "new", Col = 2, Label = "Nové" }));
        var updated = await RemoteEnv.ReceiveAsync(ws);
        Assert.Equal(3, updated["buttons"]!.AsArray().Count, "live update");

        // Switching profile from the phone.
        string other = env.Deck.Read(c => c.AddProfile("Druhý").Id);
        await RemoteEnv.SendAsync(ws, $$"""{"type":"profile","id":"{{other}}"}""");
        var switched = await RemoteEnv.ReceiveAsync(ws);
        Assert.Equal(other, switched["activeProfile"]!.GetValue<string>(), "profile switched");
        Assert.Equal(2, switched["profiles"]!.AsArray().Count, "profiles listed");

        // New QR code: old phones are kicked out.
        env.Server.SetToken(RemoteSettings.NewToken());
        for (int i = 0; i < 50 && env.Server.ClientCount > 0; i++) await Task.Delay(20);
        Assert.Equal(0, env.Server.ClientCount, "disconnected after token change");
        Assert.Equal(401, (int)(await env.Http.GetAsync("/api/ping?t=" + env.Token)).StatusCode, "old token rejected");
    }

    public static async Task Remote_RejectsWebSocketWithoutToken()
    {
        using var env = await RemoteEnv.StartAsync();
        using var ws = new System.Net.WebSockets.ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{env.Server.Port}/ws?t=nope"), CancellationToken.None);
            throw new Exception("should not connect");
        }
        catch (System.Net.WebSockets.WebSocketException) { }
        Assert.Equal(0, env.Server.ClientCount, "no client");
    }

    public static void Remote_PortInUseIsReported()
    {
        var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
        blocker.Start();
        int port = ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port;
        try
        {
            using var dir = new TempDir();
            var deck = new Deck(new ConfigStore(dir.Path), new FakeExecutor());
            deck.Load();
            using var server = new VirtualStreamDeck.Core.Remote.RemoteServer(deck);
            try { server.Start(port, "x"); throw new Exception("should fail"); }
            catch (VirtualStreamDeck.Core.Remote.RemoteException ex) { Assert.True(ex.Message.Contains("iná aplikácia"), ex.Message); }
        }
        finally { blocker.Stop(); }
    }

    private sealed class RemoteEnv : IDisposable
    {
        public required TempDir Dir { get; init; }
        public required ConfigStore Store { get; init; }
        public required Deck Deck { get; init; }
        public required FakeExecutor Executor { get; init; }
        public required VirtualStreamDeck.Core.Remote.RemoteServer Server { get; init; }
        public required HttpClient Http { get; init; }
        public required string Token { get; init; }

        public static Task<RemoteEnv> StartAsync()
        {
            var dir = new TempDir();
            var store = new ConfigStore(dir.Path);
            Directory.CreateDirectory(store.IconsDirectory);
            var exec = new FakeExecutor();
            var deck = new Deck(store, exec);
            deck.Load();
            deck.Update(c =>
            {
                c.Buttons.Add(new DeckButton { Id = "vol", Label = "Hlasitosť", Action = new MediaAction { Command = MediaCommand.VolumeUp } });
                c.Buttons.Add(new DeckButton { Id = "bad", Col = 1, Action = new KeysAction { Keys = "boom" } });
                c.Buttons.Add(new DeckButton { Id = "hidden", Row = 9, Action = new MediaAction() });
            });
            var server = new VirtualStreamDeck.Core.Remote.RemoteServer(deck);
            var token = RemoteSettings.NewToken();
            server.Start(0, token);
            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
            return Task.FromResult(new RemoteEnv { Dir = dir, Store = store, Deck = deck, Executor = exec, Server = server, Http = http, Token = token });
        }

        public static async Task<JsonNode> ReceiveAsync(System.Net.WebSockets.ClientWebSocket ws)
        {
            var buffer = new byte[64 * 1024];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var r = await ws.ReceiveAsync(buffer, cts.Token);
            return JsonNode.Parse(buffer.AsSpan(0, r.Count))!;
        }

        public static Task SendAsync(System.Net.WebSockets.ClientWebSocket ws, string json) =>
            ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(json), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);

        public void Dispose()
        {
            Http.Dispose();
            Server.Dispose();
            Dir.Dispose();
        }
    }

    // ───────── OBS ─────────

    public static void Obs_AuthenticationMatchesProtocolExample()
    {
        // Example from the obs-websocket protocol documentation.
        Assert.Equal("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=",
            ObsClient.ComputeAuthentication("supersecretpassword",
                "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=",
                "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY="));
    }

    public static void Obs_ActionAndSettingsRoundTrip()
    {
        var c = new DeckConfig();
        c.Settings.Obs = new ObsSettings { Host = "  ", Port = 0, Password = "x" };
        c.Settings.Normalize();
        Assert.Equal(("localhost", 4455), (c.Settings.Obs.Host, c.Settings.Obs.Port), "obs settings normalized");
        c.Buttons.Add(new DeckButton { Action = new ObsAction { Command = ObsCommand.ToggleSource, Source = "Webcam" } });
        var json = ConfigStore.Serialize(c);
        Assert.True(json.Contains("\"type\": \"obs\"") && json.Contains("\"command\": \"toggleSource\""), "json");
        Assert.True(ConfigStore.Deserialize(json).Buttons[0].Action is ObsAction { Command: ObsCommand.ToggleSource, Source: "Webcam", Scene: null }, "back");
        Assert.True(ConfigStore.Deserialize("{\"settings\":{}}").Settings.Obs.Port == 4455, "old config without obs section");
    }

    public static async Task Obs_SwitchSceneAgainstFakeServer()
    {
        using var server = new FakeObsServer("heslo");
        using var obs = new ObsClient();
        obs.Configure(new ObsSettings { Host = "127.0.0.1", Port = server.Port, Password = "heslo" });

        await obs.ExecuteAsync(new ObsAction { Command = ObsCommand.SwitchScene, Scene = "Pauza" });
        await obs.ExecuteAsync(new ObsAction { Command = ObsCommand.ToggleRecord });
        Assert.True(server.Requests.TryDequeue(out var r1) && r1.Type == "SetCurrentProgramScene" &&
                    r1.Data?["sceneName"]?.GetValue<string>() == "Pauza", "scene request");
        Assert.True(server.Requests.TryDequeue(out var r2) && r2.Type == "ToggleRecord" && r2.Data == null, "no requestData when not needed");
        Assert.Equal(1, server.Connections, "one connection reused");
    }

    public static async Task Obs_ToggleSourceReadsStateFirst()
    {
        using var server = new FakeObsServer("p");
        server.Handler = (type, _) => type switch
        {
            "GetCurrentProgramScene" => (new JsonObject { ["currentProgramSceneName"] = "Gaming", ["sceneName"] = "Gaming" }, null),
            "GetSceneItemId" => (new JsonObject { ["sceneItemId"] = 7 }, null),
            "GetSceneItemEnabled" => (new JsonObject { ["sceneItemEnabled"] = true }, null),
            _ => (null, null),
        };
        using var obs = new ObsClient();
        obs.Configure(new ObsSettings { Host = "127.0.0.1", Port = server.Port, Password = "p" });
        await obs.ExecuteAsync(new ObsAction { Command = ObsCommand.ToggleSource, Source = "Webcam" });

        var types = server.Requests.Select(r => r.Type).ToArray();
        Assert.Equal("GetCurrentProgramScene,GetSceneItemId,GetSceneItemEnabled,SetSceneItemEnabled", string.Join(",", types));
        var set = server.Requests.Last().Data!;
        Assert.Equal("Gaming", set["sceneName"]!.GetValue<string>(), "scene");
        Assert.Equal(7, set["sceneItemId"]!.GetValue<int>(), "item id");
        Assert.Equal(false, set["sceneItemEnabled"]!.GetValue<bool>(), "flipped");
    }

    public static async Task Obs_ErrorsAreReadable()
    {
        using var server = new FakeObsServer("p");
        server.Handler = (_, _) => (null, (600, "No source was found by the name of `X`."));

        using var wrong = new ObsClient();
        wrong.Configure(new ObsSettings { Host = "127.0.0.1", Port = server.Port, Password = "zle" });
        await ExpectObsError(() => wrong.TestAsync(), "Nesprávne heslo");

        using var noPass = new ObsClient();
        noPass.Configure(new ObsSettings { Host = "127.0.0.1", Port = server.Port });
        await ExpectObsError(() => noPass.TestAsync(), "vyžaduje heslo");

        using var obs = new ObsClient();
        obs.Configure(new ObsSettings { Host = "127.0.0.1", Port = server.Port, Password = "p" });
        await ExpectObsError(() => obs.ExecuteAsync(new ObsAction { Command = ObsCommand.SwitchScene, Scene = "X" }), "nenašlo");
        await ExpectObsError(() => obs.ExecuteAsync(new ObsAction { Command = ObsCommand.ToggleMute }), "nemá nastavenú");

        using var none = new ObsClient { ConnectTimeout = TimeSpan.FromSeconds(1) };
        none.Configure(new ObsSettings { Host = "127.0.0.1", Port = 1 });
        await ExpectObsError(() => none.TestAsync(), "Nedá sa pripojiť");
    }

    public static async Task Obs_ReconnectsAfterObsRestart()
    {
        using var server = new FakeObsServer("p");
        server.Handler = (_, _) => (new JsonObject { ["obsVersion"] = "31.0.0" }, null);
        using var obs = new ObsClient();
        obs.Configure(new ObsSettings { Host = "127.0.0.1", Port = server.Port, Password = "p" });
        Assert.Equal("31.0.0", await obs.TestAsync(), "version");
        Assert.True(obs.IsConnected, "connected");

        server.DropAll();
        for (int i = 0; i < 50 && obs.IsConnected; i++) await Task.Delay(20);
        Assert.True(!obs.IsConnected, "noticed the drop");

        await obs.TestAsync(); // reconnects transparently
        Assert.Equal(2, server.Connections, "second connection");
    }

    public static async Task Obs_ConcurrentRequestsAreMatchedById()
    {
        using var server = new FakeObsServer("p");
        server.Handler = (type, data) => (new JsonObject { ["echo"] = data?["n"]?.GetValue<int>() ?? -1 }, null);
        using var obs = new ObsClient();
        obs.Configure(new ObsSettings { Host = "127.0.0.1", Port = server.Port, Password = "p" });

        var tasks = Enumerable.Range(0, 40)
            .Select(n => obs.RequestAsync("Echo", new JsonObject { ["n"] = n }))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        for (int n = 0; n < 40; n++)
            Assert.Equal(n, results[n].GetProperty("echo").GetInt32(), $"response {n}");
        Assert.Equal(1, server.Connections, "single connection");
    }

    // ───────── Language / starter buttons ─────────

    public static void Language_PicksTextAndParsesCodes()
    {
        try
        {
            L.Set(AppLanguage.En);
            Assert.Equal("Main", DeckConfig.DefaultProfileName, "english default profile");
            Assert.Equal("The button does not exist.", L.T("Tlačidlo neexistuje.", "The button does not exist."));
            L.Set(AppLanguage.Sk);
            Assert.Equal("Hlavný", DeckConfig.DefaultProfileName, "slovak default profile");
            Assert.Equal(AppLanguage.En, L.Parse(" EN "), "parse en");
            Assert.Equal(AppLanguage.Sk, L.Parse("sk"), "parse sk");
            Assert.True(L.Parse(null) == null && L.Parse("de") == null, "unknown codes");
            Assert.Equal("en", L.Code(AppLanguage.En));
        }
        finally
        {
            L.Set(AppLanguage.Sk);
        }
    }

    public static async Task Language_ErrorsFollowTheLanguage()
    {
        using var dir = new TempDir();
        var deck = new Deck(new ConfigStore(dir.Path), new FakeExecutor());
        deck.Load();
        try
        {
            L.Set(AppLanguage.En);
            var result = await deck.PressAsync("missing");
            Assert.Equal("The button does not exist.", result.Error, "english error");
        }
        finally
        {
            L.Set(AppLanguage.Sk);
        }
    }

    public static void Language_DefaultNamesFollowTheLanguage()
    {
        var c = new DeckConfig();
        c.Normalize(); // "Hlavný"
        DeckConfig.AddMediaButtons(c.ActiveProfile);
        c.AddMediaProfile(); // "Zvuk"
        c.AddMediaProfile(); // "Zvuk 2"
        c.AddProfile("Hlavný (kópia)");
        c.AddProfile("Moje hry");
        c.ActiveProfile.Buttons.Add(new DeckButton { Row = 1, Label = "Stlmiť", Action = new KeysAction { Keys = "Ctrl+M" } });

        c.TranslateDefaults(AppLanguage.En);
        Assert.Equal("Main,Sound,Sound 2,Main (copy),Moje hry", string.Join(",", c.Profiles.Select(p => p.Name)), "profiles in English");
        Assert.Equal("Play / pause,Next,Volume down,Volume up,Mute", string.Join(",",
            c.Profiles[0].Buttons.Where(b => b.Action is MediaAction).Select(b => b.Label)), "starter labels in English");
        Assert.Equal("Stlmiť", c.Profiles[0].Buttons.Single(b => b.Action is KeysAction).Label, "own button with the same text untouched");

        c.TranslateDefaults(AppLanguage.Sk);
        Assert.Equal("Hlavný,Zvuk,Zvuk 2,Hlavný (kópia),Moje hry", string.Join(",", c.Profiles.Select(p => p.Name)), "back to Slovak");
        Assert.Equal("Tichšie", c.Profiles[1].Buttons[2].Label, "labels back to Slovak");
    }

    public static void Starter_MediaButtonsInFirstRow()
    {
        var c = new DeckConfig();
        c.Normalize();
        var p = c.ActiveProfile;
        p.Buttons.Add(new DeckButton { Row = 0, Col = 1, Label = "mine" });
        DeckConfig.AddMediaButtons(p);
        Assert.Equal(6, p.Buttons.Count, "5 added + existing kept");
        Assert.Equal(1, p.Buttons.Count(b => b.Row == 0 && b.Col == 1), "existing cell not overwritten");
        Assert.True(p.Buttons.Where(b => b.Action is MediaAction).Select(b => b.Col).SequenceEqual(new[] { 0, 2, 3, 4, 5 }), "skips taken cell");
        Assert.Equal(6, p.Columns, "grid widened to fit");
        var commands = p.Buttons.Select(b => b.Action).OfType<MediaAction>().Select(m => m.Command).ToArray();
        Assert.True(commands.SequenceEqual(new[] { MediaCommand.PlayPause, MediaCommand.Next, MediaCommand.VolumeDown, MediaCommand.VolumeUp, MediaCommand.Mute }), "commands");

        var sound = c.AddMediaProfile();
        Assert.Equal("Zvuk", sound.Name);
        Assert.Equal(5, sound.Buttons.Count, "sound profile buttons");
        Assert.Equal("Zvuk 2", c.AddMediaProfile().Name, "unique name");
    }

    // ───────── Updater ─────────

    public static void Update_ParsesManifest()
    {
        var hash = new string('a', 64);
        var info = Updater.ParseManifest($"0.2.57\r\n{hash.ToUpperInvariant()}\r\n");
        Assert.Equal("0.2.57", info?.Version, "version");
        Assert.Equal(hash, info?.Sha256, "hash is lower-cased");
        Assert.True(Updater.ParseManifest("") == null, "empty");
        Assert.True(Updater.ParseManifest("latest\n" + hash) == null, "bad version");
        Assert.True(Updater.ParseManifest("0.2.57\nabc") == null, "short hash");
        Assert.True(Updater.ParseManifest("0.2.57\n" + new string('z', 64)) == null, "non-hex hash");
    }

    public static void Update_ComparesVersionsNumerically()
    {
        Assert.True(Updater.IsNewer("0.2.10", "0.2.9"), "10 > 9");
        Assert.True(!Updater.IsNewer("0.2.9", "0.2.10"), "9 < 10");
        Assert.True(!Updater.IsNewer("0.2.5", "0.2.5"), "same");
        Assert.True(!Updater.IsNewer("0.2", "0.2.0"), "0.2 == 0.2.0");
        Assert.True(Updater.IsNewer("0.3.1", "0.2.99"), "minor");
        Assert.True(Updater.IsNewer("0.2.1", "dev"), "unparsable current");
        Assert.True(!Updater.IsNewer("bogus", "0.2.1"), "unparsable candidate");
    }

    public static async Task Update_CheckFindsNewerBuildOnly()
    {
        var exe = System.Text.Encoding.UTF8.GetBytes("new exe");
        using var http = Updater.CreateHttpClient(new FakeReleaseHandler("0.2.60", exe));
        Assert.Equal("0.2.60", (await Updater.CheckAsync(http, "0.2.57", FakeReleaseHandler.Base))?.Version, "newer");
        Assert.True(await Updater.CheckAsync(http, "0.2.60", FakeReleaseHandler.Base) == null, "same version");
        Assert.True(await Updater.CheckAsync(http, "0.2.61", FakeReleaseHandler.Base) == null, "running a newer one");
    }

    public static async Task Update_CheckErrorsAreReadable()
    {
        using var missing = Updater.CreateHttpClient(new FakeReleaseHandler(null, []));
        await ExpectUpdateError(() => Updater.CheckAsync(missing, "0.2.1", FakeReleaseHandler.Base), "nie je žiadna verzia");

        using var offline = Updater.CreateHttpClient(new FakeReleaseHandler("0.2.60", [], offline: true));
        await ExpectUpdateError(() => Updater.CheckAsync(offline, "0.2.1", FakeReleaseHandler.Base), "internet");
    }

    public static async Task Update_DownloadInstallAndCleanup()
    {
        using var dir = new TempDir();
        var target = System.IO.Path.Combine(dir.Path, "VirtualStreamDeck.exe");
        File.WriteAllText(target, "old exe");

        var exe = new byte[300_000];
        new Random(1).NextBytes(exe);
        var handler = new FakeReleaseHandler("0.2.60", exe);
        using var http = Updater.CreateHttpClient(handler);
        var info = await Updater.CheckAsync(http, "0.2.57", FakeReleaseHandler.Base);

        var percents = new List<int>();
        var downloaded = await Updater.DownloadAsync(http, info!, target, new SyncProgress(percents.Add), FakeReleaseHandler.Base);
        Assert.Equal(target + ".download", downloaded, "download path");
        Assert.True(percents.Count > 0 && percents[^1] == 100, "progress reaches 100 %");
        Assert.Equal("old exe", File.ReadAllText(target), "running exe untouched until install");

        Updater.Install(downloaded, target);
        Assert.True(File.ReadAllBytes(target).AsSpan().SequenceEqual(exe), "new exe in place");
        Assert.Equal("old exe", File.ReadAllText(target + ".old"), "old exe kept as .old");
        Assert.True(!File.Exists(downloaded), "no .download left");

        // A second update while the first .old still exists replaces it.
        var again = await Updater.DownloadAsync(http, info!, target, null, FakeReleaseHandler.Base);
        Updater.Install(again, target);
        Assert.True(File.ReadAllBytes(target + ".old").AsSpan().SequenceEqual(exe), ".old is the previous exe");

        File.WriteAllText(target + ".download", "half");
        Updater.Cleanup(target);
        Assert.True(!File.Exists(target + ".old") && !File.Exists(target + ".download"), "leftovers removed");
        Assert.True(File.Exists(target), "exe itself stays");
    }

    public static async Task Update_RejectsCorruptDownload()
    {
        using var dir = new TempDir();
        var target = System.IO.Path.Combine(dir.Path, "VirtualStreamDeck.exe");
        File.WriteAllText(target, "old exe");
        var handler = new FakeReleaseHandler("0.2.60", "real"u8.ToArray()) { ServeExe = "tampered"u8.ToArray() };
        using var http = Updater.CreateHttpClient(handler);
        var info = await Updater.CheckAsync(http, "0.2.57", FakeReleaseHandler.Base);
        await ExpectUpdateError(() => Updater.DownloadAsync(http, info!, target, null, FakeReleaseHandler.Base), "nesedí");
        Assert.True(!File.Exists(target + ".download"), "corrupt file deleted");
        Assert.Equal("old exe", File.ReadAllText(target), "exe untouched");
    }

    // ───────── Mac ─────────

    public static void Mac_KeysMapToMacKeyCodes()
    {
        Assert.Equal((ushort)0x00, MacKeys.ToMac(Hotkey.Parse("A").VirtualKey), "A");
        Assert.Equal((ushort)0x2E, MacKeys.ToMac(Hotkey.Parse("M").VirtualKey), "M");
        Assert.Equal((ushort)0x12, MacKeys.ToMac(Hotkey.Parse("1").VirtualKey), "1");
        Assert.Equal((ushort)0x7A, MacKeys.ToMac(Hotkey.Parse("F1").VirtualKey), "F1");
        Assert.Equal((ushort)0x69, MacKeys.ToMac(Hotkey.Parse("F13").VirtualKey), "F13");
        Assert.Equal((ushort)0x24, MacKeys.ToMac(Hotkey.Parse("Enter").VirtualKey), "Return");
        Assert.Equal((ushort)0x7E, MacKeys.ToMac(Hotkey.Parse("Up").VirtualKey), "Up");
        Assert.True(MacKeys.ToMac(Hotkey.Parse("F21").VirtualKey) == null, "no F21 on a Mac");

        // Every letter, digit, F1–F20, keypad key and punctuation key exists, and no two share a code.
        var names = Enumerable.Range('A', 26).Select(c => ((char)c).ToString())
            .Concat(Enumerable.Range(0, 10).Select(i => i.ToString()))
            .Concat(Enumerable.Range(1, 20).Select(i => "F" + i))
            .Concat(Enumerable.Range(0, 10).Select(i => "NumPad" + i))
            .Concat(new[] { "Multiply", "Add", "Subtract", "Decimal", "Divide", "Plus", "Minus", "Comma", "Period",
                "Slash", "Backslash", "Semicolon", "Quote", "Backtick", "OpenBracket", "CloseBracket", "Space", "Tab",
                "Escape", "Backspace", "Delete", "Home", "End", "PageUp", "PageDown", "Left", "Right" });
        var codes = new HashSet<ushort>();
        foreach (var n in names)
        {
            var code = MacKeys.ToMac(Hotkey.Parse(n).VirtualKey);
            Assert.True(code != null, $"{n} has a Mac key code");
            Assert.True(codes.Add(code!.Value), $"{n}: code 0x{code:X2} used twice");
            Assert.Equal(Hotkey.Parse(n).VirtualKey, MacKeys.FromMac(code.Value), $"{n} maps back");
        }
    }

    public static void Mac_ModifiersAndDisplay()
    {
        var hk = Hotkey.Parse("Cmd+Option+Shift+K");
        Assert.Equal("Alt+Shift+Win+K", hk.ToString(), "stored like on Windows");
        Assert.Equal(MacKeys.CarbonCmd | MacKeys.CarbonOption | MacKeys.CarbonShift, MacKeys.CarbonModifiers(hk.Modifiers), "carbon");
        Assert.Equal(MacKeys.CgCommand | MacKeys.CgOption | MacKeys.CgShift, MacKeys.CgFlags(hk.Modifiers), "cg flags");
        Assert.Equal("⌥⇧⌘K", MacKeys.Display(hk), "display");
        Assert.Equal("⌃⌘↩", MacKeys.Display(Hotkey.Parse("Ctrl+Win+Enter")), "display symbols");
    }

    public static void Mac_OpenArguments()
    {
        Assert.True(MacActionExecutor.SplitArgs("a \"b c\" d").SequenceEqual(new[] { "a", "b c", "d" }), "split");
        Assert.True(MacActionExecutor.SplitArgs("  ").Count == 0, "empty");
        Assert.True(MacActionExecutor.SplitArgs("\"\"").SequenceEqual(new[] { "" }), "explicit empty arg");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home + "/Music", MacActionExecutor.ExpandHome("~/Music"), "home");
        Assert.Equal("/Applications/Safari.app", MacActionExecutor.ExpandHome("/Applications/Safari.app"), "absolute");
    }

    public static void Update_ManifestListsEveryPlatform()
    {
        string h(char c) => new string(c, 64);
        var info = Updater.ParseManifest($"0.3.1\n{h('a')}\n{Updater.MacArm64Name} {h('B')}\n{Updater.MacX64Name}  {h('c')}\njunk line\n");
        Assert.Equal(h('a'), info!.Sha256, "line 2 stays the exe (old Windows builds read only that)");
        Assert.Equal(h('a'), info.HashOf(Updater.ExeName), "exe");
        Assert.Equal(h('b'), info.HashOf(Updater.MacArm64Name), "arm64, lower-cased");
        Assert.Equal(h('c'), info.HashOf(Updater.MacX64Name), "x64");
        Assert.True(info.HashOf("other.zip") == null, "unknown asset");
        Assert.True(Updater.ParseManifest($"0.2.8\n{h('a')}")!.HashOf(Updater.MacArm64Name) == null, "old manifest: exe only");
        Assert.Equal("/Applications/Virtual Stream Deck.app",
            Updater.BundleOf("/Applications/Virtual Stream Deck.app/Contents/MacOS/VirtualStreamDeck"), "bundle of the executable");
        Assert.True(Updater.BundleOf("/usr/local/bin/vsd") == null, "not in a bundle");
    }

    public static async Task Update_MacBundleIsSwapped()
    {
        using var dir = new TempDir();
        var bundle = System.IO.Path.Combine(dir.Path, "Virtual Stream Deck.app");
        Directory.CreateDirectory(System.IO.Path.Combine(bundle, "Contents", "MacOS"));
        File.WriteAllText(System.IO.Path.Combine(bundle, "Contents", "MacOS", "VirtualStreamDeck"), "old");

        // What CI uploads: the .app folder zipped (ditto -c -k --keepParent).
        using var src = new TempDir();
        var newApp = System.IO.Path.Combine(src.Path, "Virtual Stream Deck.app", "Contents", "MacOS");
        Directory.CreateDirectory(newApp);
        File.WriteAllText(System.IO.Path.Combine(newApp, "VirtualStreamDeck"), "new");
        var zipBytesPath = System.IO.Path.Combine(dir.Path, "src.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(src.Path, zipBytesPath);
        var zip = File.ReadAllBytes(zipBytesPath);
        File.Delete(zipBytesPath);

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(zip));
        var info = new UpdateInfo("0.3.1", new string('a', 64), new Dictionary<string, string> { [Updater.MacArm64Name] = hash });
        using var http = Updater.CreateHttpClient(new AssetHandler(new() { [Updater.MacArm64Name] = zip }));
        var download = System.IO.Path.Combine(dir.Path, "update.zip");
        await Updater.DownloadAssetAsync(http, info, Updater.MacArm64Name, download, null, FakeReleaseHandler.Base);
        await ExpectUpdateError(() => Updater.DownloadAssetAsync(http, info, Updater.MacX64Name, download + "2", null, FakeReleaseHandler.Base),
            "nie je pre tento počítač");

        Updater.InstallMacBundle(download, bundle);
        Assert.Equal("new", File.ReadAllText(System.IO.Path.Combine(bundle, "Contents", "MacOS", "VirtualStreamDeck")), "new app in place");
        Assert.Equal("old", File.ReadAllText(System.IO.Path.Combine(bundle + ".old", "Contents", "MacOS", "VirtualStreamDeck")), "old kept as .app.old");
        Assert.True(!File.Exists(download), "zip removed");
        Assert.True(Directory.GetDirectories(dir.Path, ".vsd-update-*").Length == 0, "staging removed");

        Updater.CleanupMacBundle(bundle);
        Assert.True(!Directory.Exists(bundle + ".old"), ".app.old removed on next start");
        Assert.True(Directory.Exists(bundle), "app stays");
    }

    private sealed class AssetHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var name = Uri.UnescapeDataString(request.RequestUri!.Segments[^1]);
            return Task.FromResult(files.TryGetValue(name, out var body)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private static async Task ExpectUpdateError(Func<Task> action, string expected)
    {
        try
        {
            await action();
        }
        catch (UpdateException ex)
        {
            Assert.True(ex.Message.Contains(expected), $"expected '{expected}' in '{ex.Message}'");
            return;
        }
        throw new Exception($"expected UpdateException containing '{expected}'");
    }

    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    /// <summary>Serves version.txt and the exe like the GitHub "latest" release.</summary>
    private sealed class FakeReleaseHandler(string? version, byte[] exe, bool offline = false) : HttpMessageHandler
    {
        public static readonly Uri Base = new("https://example.test/releases/download/latest/");
        public byte[]? ServeExe { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (offline) throw new HttpRequestException("No such host is known.");
            var name = request.RequestUri!.Segments[^1];
            if (version == null) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            var body = name switch
            {
                Updater.ManifestName => System.Text.Encoding.ASCII.GetBytes(
                    $"{version}\r\n{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(exe))}"),
                Updater.ExeName => ServeExe ?? exe,
                _ => null,
            };
            return Task.FromResult(body == null
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private static async Task ExpectObsError(Func<Task> action, string expected)
    {
        try
        {
            await action();
        }
        catch (ObsException ex)
        {
            Assert.True(ex.Message.Contains(expected), $"expected '{expected}' in '{ex.Message}'");
            return;
        }
        throw new Exception($"expected ObsException containing '{expected}'");
    }

    private sealed class FakeExecutor : IActionExecutor
    {
        public int Count;
        public Task ExecuteAsync(DeckAction action)
        {
            Count++;
            if (action is KeysAction k) throw new InvalidOperationException(k.Keys);
            return Task.CompletedTask;
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("vsd-test-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
