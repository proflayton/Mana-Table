using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Mana.Client;

// A bounded public-repository source. Index entries must match actual assets
// on their own native release; arbitrary URLs and cross-product feeds fail closed.
internal sealed class ManagedGitHubSource(AppBuild build, HttpClient http) : IUpdateSource
{
    internal const string Repository = "https://github.com/proflayton/Mana-Table";
    internal const string PackageId = "ManaTable", Channel = "win-x64";
    private const string Releases = "https://api.github.com/repos/proflayton/Mana-Table/releases?per_page=30";
    private readonly Dictionary<string, (Uri Url, Uri Manifest, long ManifestBytes, string? ManifestDigest)> locations = new(StringComparer.Ordinal);
    internal CancellationToken OperationToken { get; set; }

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (appId != PackageId || channel != Channel) throw Invalid();
        using var document = AppBuild.Parse(await ReadMetadata(new(Releases), 2 * 1024 * 1024, OperationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw Invalid();
        locations.Clear();
        var entries = new List<VelopackAsset>();
        var candidates = new List<(Version Version, string Tag, JsonElement Assets)>();
        foreach (var release in document.RootElement.EnumerateArray()) {
            if (!False(release, "draft") || !False(release, "prerelease")) continue;
            string tag = AppBuild.String(release, "tag_name");
            if (!tag.StartsWith("mana-native-v", StringComparison.Ordinal) || !AppBuild.TryVersion(tag[13..], out var version)) continue;
            if (version <= System.Version.Parse(build.Version)) continue;
            Canonical(AppBuild.String(release, "html_url"), $"{Repository}/releases/tag/{tag}");
            if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                candidates.Add((version, tag, assets));
        }
        foreach (var release in candidates.OrderByDescending(item => item.Version).Take(10)) {
            var indexAsset = Named(release.Assets, "releases." + Channel + ".json", optional: true);
            if (indexAsset == null) continue;
            var manifestAsset = Named(release.Assets, "update-manifest.json")!.Value;
            var indexUrl = AssetUrl(release.Tag, AppBuild.String(indexAsset.Value, "name"));
            Canonical(AppBuild.String(indexAsset.Value, "browser_download_url"), indexUrl.AbsoluteUri);
            byte[] bytes = await ReadMetadata(indexUrl, 512 * 1024, OperationToken);
            VerifyAsset(indexAsset.Value, bytes);
            var feed = VelopackAssetFeed.FromJson(System.Text.Encoding.UTF8.GetString(bytes));
            foreach (var item in feed.Assets) {
                if (item.PackageId != PackageId || item.Version.ToString() != release.Version.ToString(3)
                    || item.Type is not (VelopackAssetType.Full or VelopackAssetType.Delta)
                    || !SafeFile(item.FileName) || !item.FileName.StartsWith(PackageId + "-" + item.Version + "-", StringComparison.Ordinal)
                    || !AppBuild.Hex(item.SHA256, 64) || item.Size <= 0 || item.Size > UpdatePackageContract.MaximumPackageBytes) throw Invalid();
                var actual = Named(release.Assets, item.FileName)!.Value;
                var url = AssetUrl(release.Tag, item.FileName);
                Canonical(AppBuild.String(actual, "browser_download_url"), url.AbsoluteUri);
                if (actual.GetProperty("size").GetInt64() != item.Size) throw Invalid();
                VerifyDigest(actual, item.SHA256);
                var manifest = AssetUrl(release.Tag, "update-manifest.json");
                Canonical(AppBuild.String(manifestAsset, "browser_download_url"), manifest.AbsoluteUri);
                long manifestBytes = manifestAsset.GetProperty("size").GetInt64();
                if (manifestBytes <= 0 || manifestBytes > UpdatePackageContract.MaximumMetadataBytes) throw Invalid();
                string? manifestDigest = manifestAsset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String ? digest.GetString() : null;
                if (!locations.TryAdd(Key(item), (url, manifest, manifestBytes, manifestDigest))) throw Invalid();
                entries.Add(item);
            }
        }
        return new() { Assets = entries.ToArray() };
    }

    internal Uri Location(VelopackAsset asset) => locations.TryGetValue(Key(asset), out var location) ? location.Url : throw Invalid();
    internal async Task<byte[]> Contract(VelopackAsset asset, CancellationToken token)
    {
        if (!locations.TryGetValue(Key(asset), out var location)) throw Invalid();
        var bytes = await ReadMetadata(location.Manifest, UpdatePackageContract.MaximumMetadataBytes, token);
        if (bytes.Length != location.ManifestBytes || location.ManifestDigest != null
            && location.ManifestDigest != "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()) throw Invalid();
        return bytes;
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var request = Request(Location(releaseEntry));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is { } length && length != releaseEntry.Size) throw Invalid();
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var output = new FileStream(localFile, FileMode.Create, FileAccess.Write, FileShare.None, 65_536, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65_536]; long received = 0; int count;
        progress(0);
        while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0) {
            received += count;
            if (received > releaseEntry.Size) throw Invalid();
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            progress((int)Math.Clamp(received * 100 / releaseEntry.Size, 0, 100));
        }
        if (received != releaseEntry.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(releaseEntry.SHA256, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        await output.FlushAsync(timeout.Token); output.Flush(true);
    }

    private async Task<byte[]> ReadMetadata(Uri uri, int maximum, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = Request(uri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > maximum) throw Invalid();
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream(); var buffer = new byte[16_384]; int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0) {
            if (output.Length + count > maximum) throw Invalid();
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ManaTable", build.Version));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(uri.Host == "api.github.com" ? "application/vnd.github+json" : "application/octet-stream"));
        return request;
    }
    private static bool False(JsonElement item, string property) => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.False;
    private static bool SafeFile(string name) => name.Length < 160 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');
    private static string Key(VelopackAsset asset) => asset.FileName + ":" + asset.SHA256 + ":" + asset.Size;
    private static Uri AssetUrl(string tag, string name) => new($"{Repository}/releases/download/{tag}/{name}");
    private static void Canonical(string actual, string expected) { if (actual != expected) throw Invalid(); }
    private static JsonElement? Named(JsonElement assets, string name, bool optional = false)
    {
        var found = assets.EnumerateArray().Where(asset => AppBuild.String(asset, "name") == name).ToArray();
        if (found.Length == 0 && optional) return null;
        return found.Length == 1 ? found[0] : throw Invalid();
    }
    private static void VerifyAsset(JsonElement asset, byte[] bytes)
    {
        if (asset.GetProperty("size").GetInt64() != bytes.Length) throw Invalid();
        VerifyDigest(asset, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    private static void VerifyDigest(JsonElement asset, string hash)
    {
        if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind != JsonValueKind.Null
            && digest.GetString() != "sha256:" + hash.ToLowerInvariant()) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("The published incremental update information could not be verified.");
}
