using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mana.Client;

public record AppBuild(string Version, bool IsRelease, string SourceRevision)
{
    public bool CanCheck => IsRelease && TryVersion(Version, out _) && Hex(SourceRevision, 40);

    public static AppBuild Load(string appDirectory)
    {
        try {
            var path = Path.Combine(appDirectory, "build.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 65_536) return Unknown;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 65_536) return Unknown;
            using var json = Parse(bytes);
            var root = json.RootElement;
            return new(String(root, "version"), root.GetProperty("release").ValueKind == JsonValueKind.True,
                String(root, "sourceRevision"));
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or JsonException or InvalidOperationException or KeyNotFoundException) {
            return Unknown;
        }
    }

    private static AppBuild Unknown => new("", false, "");
    internal static bool TryVersion(string? text, out Version version)
    {
        version = null!;
        return text is { Length: <= 32 } && Regex.IsMatch(text, @"\A[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)
            && System.Version.TryParse(text, out version!) && version.ToString(3) == text;
    }
    internal static bool Hex(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);
    internal static string String(JsonElement json, string name) => json.GetProperty(name).GetString()
        ?? throw new InvalidDataException("The update information is incomplete.");
    internal static JsonDocument Parse(byte[] bytes) => JsonDocument.Parse(bytes.AsMemory(
        bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0),
        new JsonDocumentOptions { MaxDepth = 32 });
}

public record AppRelease(Version Version, Uri PageUrl, Uri InstallerUrl, long SizeBytes, string Sha256);
public record UpdateDownloadProgress(long BytesReceived, long TotalBytes);
public record PreparedUpdate(AppRelease Release, string InstallerPath);

public interface IAppUpdates
{
    AppBuild Build { get; }
    Task<AppRelease?> CheckAsync(CancellationToken cancellationToken = default);
    Task<PreparedUpdate> DownloadAsync(AppRelease release, string cacheDirectory,
        IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
    Task ValidateAsync(PreparedUpdate update, CancellationToken cancellationToken = default);
}

/// <summary>Only published, updater-aware native installers from the project's public repository are eligible.</summary>
public sealed class GitHubAppUpdates : IAppUpdates, IDisposable
{
    private const string Repository = "https://github.com/proflayton/Mana-Table";
    private const string Releases = "https://api.github.com/repos/proflayton/Mana-Table/releases?per_page=30";
    private const string TagPrefix = "mana-native-v", Installer = "ManaTable-Setup.exe", Manifest = "installer-manifest.json";
    private const long MaximumInstallerBytes = 500L * 1024 * 1024;
    private const int MaximumMetadataBytes = 2 * 1024 * 1024, MaximumManifestBytes = 65_536;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly SemaphoreSlim downloads = new(1, 1);
    public AppBuild Build { get; }

    public GitHubAppUpdates(AppBuild build, HttpClient? httpClient = null)
    {
        Build = build ?? throw new ArgumentNullException(nameof(build));
        ownsHttp = httpClient == null;
        http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<AppRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!Build.CanCheck) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try {
            using var document = AppBuild.Parse(await ReadMetadata(new Uri(Releases), MaximumMetadataBytes, true, timeout.Token));
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw InvalidInformation();
            AppBuild.TryVersion(Build.Version, out var installed);
            var candidates = new List<(Version Version, string Tag, JsonElement Release, JsonElement Manifest)>();
            foreach (var release in document.RootElement.EnumerateArray()) {
                if (!False(release, "draft") || !False(release, "prerelease")
                    || !release.TryGetProperty("tag_name", out var tagValue) || tagValue.ValueKind != JsonValueKind.String) continue;
                var tag = tagValue.GetString()!;
                if (!tag.StartsWith(TagPrefix, StringComparison.Ordinal) || !AppBuild.TryVersion(tag[TagPrefix.Length..], out var version)
                    || version <= installed) continue;
                if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
                var manifests = NamedAssets(assets, Manifest);
                if (manifests.Length == 0) continue; // Older installers cannot perform a verified updater handoff.
                if (manifests.Length != 1) throw InvalidInformation();
                candidates.Add((version, tag, release, manifests[0]));
            }
            if (candidates.Count == 0) return null;
            var next = candidates.OrderByDescending(c => c.Version).First();
            var page = Canonical(AppBuild.String(next.Release, "html_url"), $"{Repository}/releases/tag/{next.Tag}");
            var manifestUrl = Canonical(AppBuild.String(next.Manifest, "browser_download_url"), AssetUrl(next.Tag, Manifest));
            var setup = NamedAssets(next.Release.GetProperty("assets"), Installer);
            if (setup.Length != 1) throw InvalidInformation();
            var installerUrl = Canonical(AppBuild.String(setup[0], "browser_download_url"), AssetUrl(next.Tag, Installer));
            long assetBytes = Bytes(setup[0], "size", MaximumInstallerBytes);
            byte[] manifestBytes = await ReadMetadata(manifestUrl, MaximumManifestBytes, false, timeout.Token);
            if (next.Manifest.TryGetProperty("size", out var manifestSize)
                && (!manifestSize.TryGetInt64(out var expectedManifestBytes) || expectedManifestBytes != manifestBytes.Length)) throw InvalidInformation();
            CheckAssetDigest(next.Manifest, Convert.ToHexString(SHA256.HashData(manifestBytes)));
            using var manifest = AppBuild.Parse(manifestBytes);
            var info = manifest.RootElement;
            if (!info.TryGetProperty("installerProtocol", out var protocol) || !protocol.TryGetInt32(out int protocolNumber) || protocolNumber != 1)
                throw new InvalidDataException("This update needs a newer updater. Download its installer from the releases page.");
            if (AppBuild.String(info, "version") != next.Version.ToString(3) || AppBuild.String(info, "architecture") != "win-x64"
                || AppBuild.String(info, "installer") != Installer || !False(info, "smokeOnly")
                || !AppBuild.Hex(AppBuild.String(info, "sourceRevision"), 40)) throw InvalidInformation();
            long size = Bytes(info, "installerBytes", MaximumInstallerBytes);
            var hash = AppBuild.String(info, "installerSha256");
            if (size != assetBytes || !AppBuild.Hex(hash, 64)) throw InvalidInformation();
            CheckAssetDigest(setup[0], hash);
            return new(next.Version, page, installerUrl, size, hash.ToUpperInvariant());
        } catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) {
            throw new TimeoutException("Checking for updates took too long. Try again later.", error);
        } catch (HttpRequestException error) {
            throw new HttpRequestException("Could not check for updates. Check your connection and try again later.", error, error.StatusCode);
        } catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) {
            throw InvalidInformation(error);
        }
    }

    public async Task<PreparedUpdate> DownloadAsync(AppRelease release, string cacheDirectory,
        IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        RequireRelease(release);
        await downloads.WaitAsync(cancellationToken);
        string? partial = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try {
            var directory = Path.Combine(Path.GetFullPath(cacheDirectory), TagPrefix + release.Version.ToString(3), release.Sha256.ToUpperInvariant());
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var prepared = new PreparedUpdate(release, Path.Combine(directory, Installer));
            if (File.Exists(prepared.InstallerPath)) {
                try {
                    await ValidateAsync(prepared, timeout.Token);
                    timeout.Token.ThrowIfCancellationRequested();
                    progress?.Report(new(release.SizeBytes, release.SizeBytes));
                    PruneOldCache(cacheDirectory, prepared.InstallerPath);
                    return prepared;
                } catch (InvalidDataException) { File.Delete(prepared.InstallerPath); }
            }
            partial = Path.Combine(directory, Installer + "." + Guid.NewGuid().ToString("N") + ".partial");
            using var request = Request(release.InstallerUrl, false);
            HttpResponseMessage downloadResponse;
            using (var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token)) {
                headersTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                downloadResponse = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
            }
            using var response = downloadResponse;
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is long contentLength && contentLength != release.SizeBytes)
                throw new InvalidDataException("The update download size does not match the published installer.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            progress?.Report(new(0, release.SizeBytes));
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, true)) {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var buffer = new byte[65_536];
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0) {
                    received += count;
                    if (received > release.SizeBytes || received > MaximumInstallerBytes)
                        throw new InvalidDataException("The update download is larger than its published size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    progress?.Report(new(received, release.SizeBytes));
                }
                await output.FlushAsync(timeout.Token);
                output.Flush(true);
            }
            if (received != release.SizeBytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update download could not be verified. Download it again.");
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partial, prepared.InstallerPath, true);
            partial = null;
            PruneOldCache(cacheDirectory, prepared.InstallerPath);
            return prepared;
        } catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) {
            throw new TimeoutException("Downloading the update took too long. Try again later.", error);
        } catch (HttpRequestException error) {
            throw new HttpRequestException("Could not download the update. Check your connection and try again.", error, error.StatusCode);
        } finally {
            if (partial != null) { try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            downloads.Release();
        }
    }

    public async Task ValidateAsync(PreparedUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        RequireRelease(update.Release);
        if (Path.GetFileName(update.InstallerPath) != Installer || !File.Exists(update.InstallerPath)
            || (File.GetAttributes(update.InstallerPath) & FileAttributes.ReparsePoint) != 0) throw InvalidDownload();
        await using var file = new FileStream(update.InstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, true);
        if (file.Length != update.Release.SizeBytes) throw InvalidDownload();
        var hash = await SHA256.HashDataAsync(file, cancellationToken);
        if (!Convert.ToHexString(hash).Equals(update.Release.Sha256, StringComparison.OrdinalIgnoreCase)) throw InvalidDownload();
    }

    private void RequireRelease(AppRelease release)
    {
        if (!Build.CanCheck) throw new InvalidOperationException("Updates are available in installed release builds.");
        ArgumentNullException.ThrowIfNull(release);
        if (release.Version is not { Build: >= 0, Revision: -1 }
            || !AppBuild.TryVersion(release.Version.ToString(3), out var version) || version <= System.Version.Parse(Build.Version)
            || release.SizeBytes <= 0 || release.SizeBytes > MaximumInstallerBytes || !AppBuild.Hex(release.Sha256, 64)) throw InvalidInformation();
        var tag = TagPrefix + version.ToString(3);
        Canonical(release.PageUrl?.OriginalString, $"{Repository}/releases/tag/{tag}");
        Canonical(release.InstallerUrl?.OriginalString, AssetUrl(tag, Installer));
    }

    private async Task<byte[]> ReadMetadata(Uri url, int maximumBytes, bool api, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = Request(url, api);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > maximumBytes) throw InvalidInformation();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var result = new MemoryStream();
        var buffer = new byte[16_384];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0) {
            if (result.Length + count > maximumBytes) throw InvalidInformation();
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    private HttpRequestMessage Request(Uri url, bool api)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ManaTable", Build.Version));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(api ? "application/vnd.github+json" : "application/octet-stream"));
        return request;
    }
    private static string AssetUrl(string tag, string file) => $"{Repository}/releases/download/{tag}/{file}";
    private static Uri Canonical(string? url, string expected)
    {
        if (url != expected) throw new InvalidDataException("The update download address could not be verified.");
        return new Uri(expected, UriKind.Absolute);
    }
    private static bool False(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.False;
    private static JsonElement[] NamedAssets(JsonElement assets, string name) => assets.EnumerateArray()
        .Where(asset => asset.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == name).ToArray();
    private static long Bytes(JsonElement value, string name, long maximum)
    {
        if (!value.TryGetProperty(name, out var field) || !field.TryGetInt64(out var bytes) || bytes <= 0 || bytes > maximum) throw InvalidInformation();
        return bytes;
    }
    private static void CheckAssetDigest(JsonElement asset, string sha256)
    {
        if (!asset.TryGetProperty("digest", out var value) || value.ValueKind == JsonValueKind.Null) return;
        var digest = value.GetString();
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || !AppBuild.Hex(digest[7..], 64)
            || !digest[7..].Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw InvalidInformation();
    }
    private void PruneOldCache(string cacheDirectory, string readyPath)
    {
        // Only our exact files in recognized, already-installed version directories
        // belong to this cleanup. Unknown files and reparse points are preserved.
        try {
            string root = Path.GetFullPath(cacheDirectory);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
            var installed = System.Version.Parse(Build.Version);
            foreach (var versionDirectory in Directory.EnumerateDirectories(root).Take(64)) {
                string name = Path.GetFileName(versionDirectory);
                if (!name.StartsWith(TagPrefix, StringComparison.Ordinal) || !AppBuild.TryVersion(name[TagPrefix.Length..], out var version)
                    || version > installed || !PlainDirectory(versionDirectory)) continue;
                foreach (var hashDirectory in Directory.EnumerateDirectories(versionDirectory).Take(64)) {
                    if (!AppBuild.Hex(Path.GetFileName(hashDirectory), 64) || !PlainDirectory(hashDirectory)) continue;
                    Remove(Path.Combine(hashDirectory, Installer));
                    foreach (string path in Directory.EnumerateFiles(hashDirectory, Installer + ".*.partial").Take(64)) {
                        string file = Path.GetFileName(path);
                        if (Regex.IsMatch(file, @"\AManaTable-Setup\.exe\.[0-9a-fA-F]{32}\.partial\z", RegexOptions.CultureInvariant)
                            && File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1)) Remove(path);
                    }
                    RemoveEmptyDirectory(hashDirectory);
                }
                RemoveEmptyDirectory(versionDirectory);
            }
            void Remove(string path) {
                try {
                    if (!string.Equals(path, readyPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                        && (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0) File.Delete(path);
                } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        static bool PlainDirectory(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        static void RemoveEmptyDirectory(string path) {
            try { Directory.Delete(path, false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    private static InvalidDataException InvalidInformation(Exception? inner = null) => new("The published update information could not be verified. Try again later.", inner);
    private static InvalidDataException InvalidDownload() => new("The downloaded installer has changed or is missing. Download the update again.");
    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
