using System.Net;
using System.Security.Cryptography;

namespace VirtualStreamDeck.Core.Update;

/// <summary>A newer build published on GitHub. <paramref name="Assets"/>: file name → SHA-256.</summary>
public sealed record UpdateInfo(string Version, string Sha256, IReadOnlyDictionary<string, string> Assets)
{
    public UpdateInfo(string version, string sha256)
        : this(version, sha256, new Dictionary<string, string> { [Updater.ExeName] = sha256 })
    {
    }

    /// <summary>SHA-256 of an asset, or null if this release doesn't have it.</summary>
    public string? HashOf(string asset) => Assets.TryGetValue(asset, out var h) ? h : null;
}

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Self-update from the GitHub release "latest".
///
/// CI publishes <c>VirtualStreamDeck.exe</c>, the Mac apps zipped (<c>VirtualStreamDeck-mac-arm64.zip</c>,
/// <c>VirtualStreamDeck-mac-x64.zip</c>) and <c>version.txt</c>: line 1 version, line 2 SHA-256 of the exe
/// (what 0.2.8–0.2.15 read), then one "&lt;file&gt; &lt;sha256&gt;" line per file. The app downloads the
/// update itself, so it has no "downloaded from the internet" mark: no SmartScreen / Gatekeeper warning.
///
/// Install: the running exe is renamed to <c>*.old</c> (Windows allows renaming a running exe,
/// not deleting it), the new file takes its name, the new build starts and takes over
/// the running instance. The <c>*.old</c> file is deleted on the next start.
/// </summary>
public static class Updater
{
    public static readonly Uri DefaultBaseUri =
        new("https://github.com/bertsuperman/virtual-stream-desk/releases/download/latest/");

    public const string ManifestName = "version.txt";
    public const string ExeName = "VirtualStreamDeck.exe";
    public const string MacArm64Name = "VirtualStreamDeck-mac-arm64.zip";
    public const string MacX64Name = "VirtualStreamDeck-mac-x64.zip";

    /// <summary>The file this computer needs: the exe on Windows, the zip for this Mac's processor.</summary>
    public static string? AssetForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return ExeName;
        if (OperatingSystem.IsMacOS())
            return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                   System.Runtime.InteropServices.Architecture.Arm64 ? MacArm64Name : MacX64Name;
        return null;
    }

    private const string DownloadSuffix = ".download";
    private const string OldSuffix = ".old";

    public static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)
    {
        var http = handler != null ? new HttpClient(handler) : new HttpClient();
        http.Timeout = Timeout.InfiniteTimeSpan; // callers pass a CancellationToken with their own limit
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VirtualStreamDeck-Updater");
        return http;
    }

    /// <summary>"0.2.57\n&lt;exe sha256&gt;\n&lt;file&gt; &lt;sha256&gt;…" → info, or null if the text is not a valid manifest.</summary>
    public static UpdateInfo? ParseManifest(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || !System.Version.TryParse(lines[0], out _)) return null;
        var hash = lines[1].ToLowerInvariant();
        if (!IsHash(hash)) return null;

        var assets = new Dictionary<string, string>(StringComparer.Ordinal) { [ExeName] = hash };
        foreach (var line in lines.Skip(2))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && IsHash(parts[1].ToLowerInvariant())) assets[parts[0]] = parts[1].ToLowerInvariant();
        }
        return new UpdateInfo(lines[0], hash, assets);
    }

    private static bool IsHash(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

    public static bool IsNewer(string candidate, string current) =>
        System.Version.TryParse(candidate, out var c) &&
        (!System.Version.TryParse(current, out var cur) || Normalize(c) > Normalize(cur));

    // 0.2 == 0.2.0 == 0.2.0.0
    private static System.Version Normalize(System.Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>Returns the published build if it is newer than <paramref name="currentVersion"/>, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, string currentVersion,
        Uri? baseUri = null, CancellationToken ct = default)
    {
        var url = new Uri(baseUri ?? DefaultBaseUri, ManifestName);
        string text;
        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            EnsureFound(response);
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not UpdateException && !ct.IsCancellationRequested)
        {
            throw Offline(ex);
        }

        var info = ParseManifest(text)
                   ?? throw new UpdateException(L.T("Informácia o novej verzii na GitHube je neplatná. Skús to neskôr.", "The version info on GitHub is invalid. Try again later."));
        return IsNewer(info.Version, currentVersion) ? info : null;
    }

    /// <summary>
    /// Downloads the exe next to <paramref name="targetExe"/> as <c>*.download</c> and checks its SHA-256.
    /// Returns the path of the verified file.
    /// </summary>
    public static Task<string> DownloadAsync(HttpClient http, UpdateInfo info, string targetExe,
        IProgress<int>? progress = null, Uri? baseUri = null, CancellationToken ct = default) =>
        DownloadAssetAsync(http, info, ExeName, targetExe + DownloadSuffix, progress, baseUri, ct);

    /// <summary>Downloads one file of the release to <paramref name="path"/> and checks its SHA-256.</summary>
    public static async Task<string> DownloadAssetAsync(HttpClient http, UpdateInfo info, string asset, string path,
        IProgress<int>? progress = null, Uri? baseUri = null, CancellationToken ct = default)
    {
        var expected = info.HashOf(asset)
                       ?? throw new UpdateException(L.T("Táto verzia ešte nie je pre tento počítač. Skús to neskôr.",
                           "This version is not available for this computer yet. Try again later."));
        var url = new Uri(baseUri ?? DefaultBaseUri, asset);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            EnsureFound(response);
            long? total = response.Content.Headers.ContentLength;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = OpenForWrite(path))
            {
                var buffer = new byte[81920];
                long done = 0;
                int lastPercent = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total is > 0)
                    {
                        int percent = (int)(done * 100 / total.Value);
                        if (percent != lastPercent) progress?.Report(lastPercent = percent);
                    }
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != expected)
                throw new UpdateException(
                    L.T("Stiahnutý súbor nesedí s verziou na GitHube (možno sa práve zostavuje novšia). Skús to o pár minút.",
                        "The downloaded file does not match the version on GitHub (a newer one may be building). Try again in a few minutes."));
            return path;
        }
        catch (Exception ex)
        {
            TryDelete(path);
            if (ex is UpdateException || ct.IsCancellationRequested) throw;
            throw Offline(ex);
        }
    }

    /// <summary>Puts the downloaded file in place of <paramref name="targetExe"/> (which may be running).</summary>
    public static void Install(string downloaded, string targetExe)
    {
        var old = targetExe + OldSuffix;
        if (!TryDelete(old)) old = $"{targetExe}.{Guid.NewGuid():N}{OldSuffix}"; // an older .old is still in use

        try
        {
            File.Move(targetExe, old);
        }
        catch (Exception ex)
        {
            TryDelete(downloaded);
            throw NotWritable(targetExe, ex);
        }

        try
        {
            File.Move(downloaded, targetExe);
        }
        catch (Exception ex)
        {
            try { File.Move(old, targetExe); } catch { /* best effort: the .old file is still there */ }
            TryDelete(downloaded);
            throw NotWritable(targetExe, ex);
        }
    }

    // ───────────── macOS: the app is a folder (Stream Deck.app) ─────────────

    /// <summary>".../Virtual Stream Deck.app/Contents/MacOS/VirtualStreamDeck" → ".../Virtual Stream Deck.app", or null.</summary>
    public static string? BundleOf(string? executablePath)
    {
        for (var dir = executablePath == null ? null : Path.GetDirectoryName(executablePath); dir != null; dir = Path.GetDirectoryName(dir))
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return dir;
        return null;
    }

    /// <summary>
    /// Unpacks the downloaded zip and swaps the .app folder: the running one becomes "*.app.old"
    /// (a running app can be moved on macOS), the new one takes its place.
    /// </summary>
    public static void InstallMacBundle(string downloadedZip, string bundlePath)
    {
        bundlePath = Path.TrimEndingDirectorySeparator(bundlePath);
        var parent = Path.GetDirectoryName(bundlePath)!;
        var staging = Path.Combine(parent, $".vsd-update-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            Unzip(downloadedZip, staging);
            var newApp = Directory.GetDirectories(staging, "*.app").FirstOrDefault()
                         ?? throw new UpdateException(L.T("V stiahnutom balíku nie je appka. Skús to znova.",
                             "The downloaded package has no app in it. Try again."));

            var old = bundlePath + OldSuffix;
            if (!TryDeleteDirectory(old)) old = $"{bundlePath}.{Guid.NewGuid():N}{OldSuffix}";
            try
            {
                Directory.Move(bundlePath, old);
            }
            catch (Exception ex) when (ex is not UpdateException)
            {
                throw NotWritable(bundlePath, ex);
            }
            try
            {
                Directory.Move(newApp, bundlePath);
            }
            catch (Exception ex)
            {
                try { Directory.Move(old, bundlePath); } catch { /* best effort */ }
                throw NotWritable(bundlePath, ex);
            }
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDelete(downloadedZip);
        }
    }

    /// <summary>ditto on a Mac (keeps permissions, symlinks and signatures), ZipFile elsewhere (tests).</summary>
    private static void Unzip(string zip, string destination)
    {
        if (OperatingSystem.IsMacOS() && File.Exists("/usr/bin/ditto"))
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/ditto") { RedirectStandardError = true };
            foreach (var a in new[] { "-x", "-k", zip, destination }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            if (p.ExitCode != 0) throw new UpdateException(L.T("Balík sa nepodarilo rozbaliť: ", "Could not unpack the package: ") + p.StandardError.ReadToEnd().Trim());
            return;
        }
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, destination);
    }

    /// <summary>Deletes "*.app.old" leftovers next to the bundle.</summary>
    public static void CleanupMacBundle(string bundlePath)
    {
        bundlePath = Path.TrimEndingDirectorySeparator(bundlePath);
        var parent = Path.GetDirectoryName(bundlePath);
        if (parent == null) return;
        try
        {
            foreach (var d in Directory.EnumerateDirectories(parent, Path.GetFileName(bundlePath) + "*" + OldSuffix)) TryDeleteDirectory(d);
            foreach (var d in Directory.EnumerateDirectories(parent, ".vsd-update-*")) TryDeleteDirectory(d);
        }
        catch
        {
            // not readable: nothing to clean
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Deletes leftovers of a previous update (call on startup; files still in use are skipped).</summary>
    public static void Cleanup(string targetExe)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(targetExe));
        if (dir == null) return;
        var name = Path.GetFileName(targetExe);
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, name + "*" + OldSuffix)) TryDelete(f);
        }
        catch
        {
            // folder not readable: nothing to clean
        }
        TryDelete(targetExe + DownloadSuffix);
    }

    private static FileStream OpenForWrite(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        }
        catch (Exception ex)
        {
            throw NotWritable(path, ex);
        }
    }

    private static void EnsureFound(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new UpdateException(L.T("Na GitHube teraz nie je žiadna verzia (možno sa práve zostavuje). Skús to o pár minút.",
                "There is no version on GitHub right now (it may be building). Try again in a few minutes."));
        if (!response.IsSuccessStatusCode)
            throw new UpdateException(L.T($"GitHub odpovedal chybou {(int)response.StatusCode}. Skús to neskôr.", $"GitHub returned error {(int)response.StatusCode}. Try again later."));
    }

    private static UpdateException Offline(Exception ex) =>
        new(L.T("Nepodarilo sa spojiť s GitHubom. Skontroluj pripojenie na internet.", "Could not connect to GitHub. Check your internet connection."), ex);

    private static UpdateException NotWritable(string path, Exception ex) =>
        new(L.T($"Do priečinka s programom sa nedá zapisovať ({Path.GetDirectoryName(Path.GetFullPath(path))}). " +
                "Presuň program napr. na Plochu alebo stiahni novú verziu ručne.",
                $"The program folder is not writable ({Path.GetDirectoryName(Path.GetFullPath(path))}). " +
                "Move the program, e.g. to the Desktop, or download the new version manually."), ex);

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
