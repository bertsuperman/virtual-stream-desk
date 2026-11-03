using System.Net;
using System.Security.Cryptography;

namespace VirtualStreamDeck.Core.Update;

/// <summary>A newer build published on GitHub.</summary>
public sealed record UpdateInfo(string Version, string Sha256);

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Self-update from the GitHub release "latest".
///
/// CI publishes two assets: <c>VirtualStreamDeck.exe</c> and <c>version.txt</c>
/// (line 1: version, line 2: SHA-256 of the exe). The app downloads the exe itself, so the file
/// has no "downloaded from the internet" mark and SmartScreen does not warn about it.
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

    private const string DownloadSuffix = ".download";
    private const string OldSuffix = ".old";

    public static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)
    {
        var http = handler != null ? new HttpClient(handler) : new HttpClient();
        http.Timeout = Timeout.InfiniteTimeSpan; // callers pass a CancellationToken with their own limit
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VirtualStreamDeck-Updater");
        return http;
    }

    /// <summary>"0.2.57\n&lt;sha256&gt;" → info, or null if the text is not a valid manifest.</summary>
    public static UpdateInfo? ParseManifest(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || !System.Version.TryParse(lines[0], out _)) return null;
        var hash = lines[1].ToLowerInvariant();
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return null;
        return new UpdateInfo(lines[0], hash);
    }

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
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo info, string targetExe,
        IProgress<int>? progress = null, Uri? baseUri = null, CancellationToken ct = default)
    {
        var url = new Uri(baseUri ?? DefaultBaseUri, ExeName);
        var path = targetExe + DownloadSuffix;
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
            if (actual != info.Sha256)
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
