using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualStreamDeck.Core;

/// <summary>Loads and saves config.json and owns the icons folder.</summary>
public sealed class ConfigStore
{
    public const string ConfigFileName = "config.json";
    public const string IconsFolderName = "icons";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowOutOfOrderMetadataProperties = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public ConfigStore(string directory)
    {
        Directory = Path.GetFullPath(directory);
        ConfigPath = Path.Combine(Directory, ConfigFileName);
        IconsDirectory = Path.Combine(Directory, IconsFolderName);
    }

    public string Directory { get; }
    public string ConfigPath { get; }
    public string IconsDirectory { get; }

    /// <summary>Set by <see cref="Load"/> when the config could not be read and was backed up.</summary>
    public string? LastLoadWarning { get; private set; }

    /// <summary>
    /// Portable mode: if config.json sits next to the exe, use that folder.
    /// Otherwise %AppData%\VirtualStreamDeck.
    /// </summary>
    public static string ResolveDirectory(string exeDirectory, string? appDataDirectory = null)
    {
        if (File.Exists(Path.Combine(exeDirectory, ConfigFileName))) return exeDirectory;
        appDataDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataDirectory, "VirtualStreamDeck");
    }

    /// <summary>True when the last <see cref="Load"/> found no config file (first start).</summary>
    public bool LoadedNew { get; private set; }

    public DeckConfig Load()
    {
        LastLoadWarning = null;
        LoadedNew = !File.Exists(ConfigPath);
        if (LoadedNew)
        {
            var fresh = new DeckConfig();
            fresh.Normalize();
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<DeckConfig>(json, JsonOptions) ?? new DeckConfig();
            config.Normalize(); // also migrates v1/v2 (buttons at the top level) into the first profile
            return config;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException)
        {
            var backup = Path.Combine(Directory, $"config.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try { File.Copy(ConfigPath, backup, overwrite: true); } catch { /* best effort */ }
            LastLoadWarning =
                L.T($"Konfiguráciu sa nepodarilo načítať ({ex.Message}).\nPôvodný súbor je zálohovaný ako {Path.GetFileName(backup)}.",
                    $"The configuration could not be loaded ({ex.Message}).\nThe original file was backed up as {Path.GetFileName(backup)}.");
            var fresh = new DeckConfig();
            fresh.Normalize();
            return fresh;
        }
    }

    /// <summary>Writes to a temp file first and then swaps it in, so a crash never leaves a half-written config.</summary>
    public void Save(DeckConfig config)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    public static string Serialize(DeckConfig config) => JsonSerializer.Serialize(config, JsonOptions);

    public static DeckConfig Deserialize(string json)
    {
        var config = JsonSerializer.Deserialize<DeckConfig>(json, JsonOptions) ?? new DeckConfig();
        config.Normalize();
        return config;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions)!;

    public string? ResolveIconPath(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var full = Path.GetFullPath(Path.Combine(Directory, relative));
        return File.Exists(full) ? full : null;
    }

    /// <summary>Path stored in config for a file inside the icons folder.</summary>
    public static string IconRelativePath(string fileName) => $"{IconsFolderName}/{fileName}";

    /// <summary>Deletes files in the icons folder that no button references. Returns how many were deleted.</summary>
    public int CleanupIcons(DeckConfig config)
    {
        if (!System.IO.Directory.Exists(IconsDirectory)) return 0;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in config.AllButtons)
        {
            if (string.IsNullOrWhiteSpace(b.Icon)) continue;
            used.Add(Path.GetFullPath(Path.Combine(Directory, b.Icon)));
        }

        int deleted = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(IconsDirectory))
        {
            if (used.Contains(Path.GetFullPath(file))) continue;
            try { File.Delete(file); deleted++; } catch { /* in use, try next time */ }
        }
        return deleted;
    }
}
