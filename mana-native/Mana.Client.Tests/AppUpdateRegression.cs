using Mana.Client;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

internal static class AppUpdateRegression
{
    private const string Repository = "https://github.com/proflayton/Mana-Table";
    private const string Api = "https://api.github.com/repos/proflayton/Mana-Table/releases?per_page=30";
    private static readonly AppBuild Build = new("0.2.9", true, new string('a', 40));

    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool value, string description) { if (!value) throw new Exception(description); checks++; Console.WriteLine(description); }
        async Task Reject<T>(Func<Task> action, string description) where T : Exception {
            try { await action(); } catch (T) { Check(true, description); return; }
            throw new Exception(description + " (the operation was accepted)");
        }
        using var temp = new TemporaryFiles();
        Check(!AppBuild.Load(temp.Root).CanCheck, "Missing build metadata disables update checks");
        string buildFile = Path.Combine(temp.Root, "build.json");
        await File.WriteAllTextAsync(buildFile, "{bad");
        Check(!AppBuild.Load(temp.Root).CanCheck, "Corrupt build metadata fails closed");
        foreach (var invalid in new[] { Build with { IsRelease = false }, Build with { Version = "0.2.10-beta" },
            Build with { Version = "0.2.10.1" }, Build with { Version = "unknown" }, Build with { SourceRevision = "unknown" } }) {
            using var fixture = new Feed(invalid);
            Check(!invalid.CanCheck && await fixture.Updates.CheckAsync() == null && fixture.Requests.Count == 0,
                "Development or unknown builds make no update requests: " + invalid.Version + "/" + invalid.IsRelease);
        }
        await File.WriteAllTextAsync(buildFile, "{\"version\":\"0.2.9\",\"release\":true,\"sourceRevision\":\"" + Build.SourceRevision + "\"}", new UTF8Encoding(true));
        Check(AppBuild.Load(temp.Root) == Build && AppBuild.Load(temp.Root).CanCheck, "Release metadata with a UTF-8 BOM is read defensively");
        await File.WriteAllTextAsync(buildFile, new string(' ', 65_537));
        Check(!AppBuild.Load(temp.Root).CanCheck, "Oversized build metadata cannot enable the updater");

        using (var fixture = new Feed()) {
            fixture.Add("0.2.9");
            fixture.Add("0.2.8");
            var next = fixture.Add("0.2.10");
            fixture.Add("0.2.100").Release["prerelease"] = true;
            fixture.Add("0.3.0").Release["draft"] = true;
            fixture.Add("9.0.0").Release["tag_name"] = "mana-table-v9.0.0";
            var legacy = fixture.Add("0.2.12"); legacy.Assets.RemoveAt(0);
            var release = await fixture.Updates.CheckAsync();
            Check(release?.Version == new Version(0, 2, 10), "Numeric versions choose 0.2.10 over 0.2.9 and ignore prereleases, drafts, Electron and legacy installers");
            Check(release!.PageUrl.OriginalString == Repository + "/releases/tag/mana-native-v0.2.10"
                && release.InstallerUrl.OriginalString == Url("0.2.10", "ManaTable-Setup.exe"), "The offered page and installer are pinned to the exact native tag");
            Check(fixture.Requests.All(r => r.Method == HttpMethod.Get && r.UserAgent == "ManaTable/0.2.9"
                && r.ApiVersion == "2022-11-28" && !r.Authorization), "Public update requests identify the app and API version without carrying tokens");
            Check(fixture.Requests[0].Url == Api && fixture.Requests.Count == 2, "Discovery uses the bounded release list and only the selected manifest");
            string cache = Path.Combine(temp.Root, "download");
            string oldHashDirectory = Path.Combine(cache, "mana-native-v0.2.8", new string('b', 64));
            Directory.CreateDirectory(oldHashDirectory);
            string obsoleteInstaller = Path.Combine(oldHashDirectory, "ManaTable-Setup.exe");
            string stalePartial = Path.Combine(oldHashDirectory, "ManaTable-Setup.exe." + new string('a', 32) + ".partial");
            string recentPartial = Path.Combine(oldHashDirectory, "ManaTable-Setup.exe." + new string('b', 32) + ".partial");
            string unrelated = Path.Combine(oldHashDirectory, "my-notes.txt");
            string futureDirectory = Path.Combine(cache, "mana-native-v0.2.11", new string('c', 64));
            Directory.CreateDirectory(futureDirectory);
            string futureInstaller = Path.Combine(futureDirectory, "ManaTable-Setup.exe");
            foreach (string path in new[] { obsoleteInstaller, stalePartial, recentPartial, unrelated, futureInstaller }) await File.WriteAllTextAsync(path, "preserve unless owned and obsolete");
            File.SetLastWriteTimeUtc(stalePartial, DateTime.UtcNow.AddDays(-2));
            var progress = new ProgressLog();
            var prepared = await fixture.Updates.DownloadAsync(release, cache, progress);
            Check((await File.ReadAllBytesAsync(prepared.InstallerPath)).SequenceEqual(next.Payload), "The prepared installer contains the exact verified response bytes");
            Check(prepared.InstallerPath.StartsWith(Path.GetFullPath(cache) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && prepared.InstallerPath.Contains("mana-native-v0.2.10"), "Verified installers stay in their version-specific private cache");
            Check(progress.Values.Count > 2 && progress.Values[0].BytesReceived == 0 && progress.Values[^1].BytesReceived == release.SizeBytes
                && progress.Values.All(p => p.TotalBytes == release.SizeBytes)
                && progress.Values.Zip(progress.Values.Skip(1)).All(p => p.First.BytesReceived <= p.Second.BytesReceived), "Download progress reports bounded monotonic byte counts and completion");
            Check(!File.Exists(obsoleteInstaller) && !File.Exists(stalePartial), "Successful download prunes only recognized installers and stale partials for already-installed versions");
            Check(File.Exists(unrelated) && File.Exists(recentPartial) && File.Exists(futureInstaller), "Cache cleanup preserves unrelated files, active partials and future prepared versions");
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(prepared.InstallerPath)!, "*.partial").Any(), "Successful verification leaves no partial installer for the ready version");
            await fixture.Updates.ValidateAsync(prepared);
            int requests = fixture.Requests.Count;
            Check(await fixture.Updates.DownloadAsync(release, cache) == prepared && fixture.Requests.Count == requests, "A valid cached installer is revalidated and reused without another download");
            var tampered = next.Payload.ToArray(); tampered[0] ^= 0xff;
            await File.WriteAllBytesAsync(prepared.InstallerPath, tampered);
            await Reject<InvalidDataException>(() => fixture.Updates.ValidateAsync(prepared), "Revalidation detects same-length tampering immediately before installation");
            var restored = await fixture.Updates.DownloadAsync(release, cache);
            Check(fixture.Requests.Count == requests + 1 && (await File.ReadAllBytesAsync(restored.InstallerPath)).SequenceEqual(next.Payload), "Corrupt cached installers are discarded and downloaded again");
            await File.WriteAllBytesAsync(restored.InstallerPath, [1, 2]);
            await Reject<InvalidDataException>(() => fixture.Updates.ValidateAsync(restored), "Revalidation also rejects truncated ready files");
            int before = fixture.Requests.Count;
            await Reject<InvalidDataException>(() => fixture.Updates.DownloadAsync(release with { InstallerUrl = new("https://example.invalid/setup.exe") }, cache), "A caller cannot replace the pinned installer address");
            Check(fixture.Requests.Count == before, "An unsafe prepared release is rejected before any HTTP request");
        }

        using (var fixture = new Feed()) {
            fixture.Add("0.2.9"); fixture.Add("0.1.0");
            Check(await fixture.Updates.CheckAsync() == null, "Equal or older native versions never trigger a downgrade");
        }
        using (var fixture = new Feed()) {
            fixture.Add("4.0.0").Release["tag_name"] = "electron-v4.0.0";
            Check(await fixture.Updates.CheckAsync() == null, "An Electron-only release list has no native update");
        }
        using (var fixture = new Feed()) {
            fixture.Add("0.2.10");
            var unsupported = fixture.Add("0.2.11"); unsupported.Manifest["installerProtocol"] = 2; fixture.Publish(unsupported);
            await Reject<InvalidDataException>(() => fixture.Updates.CheckAsync(), "An unsupported highest manifest reports unavailable instead of falling back to another installer");
            Check(!fixture.Requests.Any(r => r.Url == Url("0.2.10", "installer-manifest.json")), "Unsupported protocol handling never silently chooses an older candidate");
        }
        var invalidManifestCases = new (string Description, Action<Package> Mutate)[] {
            ("manifest version", p => p.Manifest["version"] = "0.2.11"),
            ("architecture", p => p.Manifest["architecture"] = "win-arm64"),
            ("source revision", p => p.Manifest["sourceRevision"] = "unknown"),
            ("installer filename", p => p.Manifest["installer"] = "other.exe"),
            ("smoke-only installer", p => p.Manifest["smokeOnly"] = true),
            ("missing smoke proof", p => p.Manifest.Remove("smokeOnly")),
            ("manifest size", p => p.Manifest["installerBytes"] = p.Payload.Length + 1),
            ("malformed hash", p => p.Manifest["installerSha256"] = "bad"),
            ("asset hash", p => p.Installer["digest"] = "sha256:" + new string('0', 64)),
            ("asset size", p => p.Installer["size"] = p.Payload.Length - 1),
            ("oversized installer", p => { p.Manifest["installerBytes"] = 500L * 1024 * 1024 + 1; p.Installer["size"] = 500L * 1024 * 1024 + 1; })
        };
        foreach (var invalid in invalidManifestCases) {
            using var fixture = new Feed(); var package = fixture.Add("0.2.10"); invalid.Mutate(package); fixture.Publish(package);
            await Reject<InvalidDataException>(() => fixture.Updates.CheckAsync(), "Release verification rejects conflicting " + invalid.Description);
        }
        foreach (var bad in new[] { "https://github.com.evil.invalid/setup.exe", "http://github.com/proflayton/Mana-Table/setup.exe",
            Url("0.2.10", "ManaTable-Setup.exe") + "?redirect=elsewhere", Url("0.2.11", "ManaTable-Setup.exe") }) {
            using var fixture = new Feed(); var package = fixture.Add("0.2.10"); package.Installer["browser_download_url"] = bad;
            await Reject<InvalidDataException>(() => fixture.Updates.CheckAsync(), "Unsafe installer metadata is never followed: " + bad);
            Check(fixture.Requests.Count == 1, "An unsafe installer address is rejected before following any release assets");
        }
        using (var fixture = new Feed()) {
            var package = fixture.Add("0.2.10"); package.Assets[0]!["browser_download_url"] = "https://example.invalid/manifest.json";
            await Reject<InvalidDataException>(() => fixture.Updates.CheckAsync(), "The manifest itself must have the exact canonical GitHub asset URL");
        }
        using (var fixture = new Feed()) {
            var package = fixture.Add("0.2.10"); package.Release["html_url"] = "https://example.invalid/release";
            await Reject<InvalidDataException>(() => fixture.Updates.CheckAsync(), "Release-page links cannot redirect users to an unrelated site");
        }
        using (var fixture = new Feed()) {
            fixture.Handler.Override = (_, _) => Task.FromResult(Response(new byte[2 * 1024 * 1024 + 1]));
            await Reject<InvalidDataException>(() => fixture.Updates.CheckAsync(), "Update metadata has a hard response-size limit");
        }
        using (var fixture = new Feed()) {
            fixture.Handler.Override = (_, _) => throw new HttpRequestException("Offline fake");
            await Reject<HttpRequestException>(() => fixture.Updates.CheckAsync(), "Network failures remain errors rather than false no-update results");
        }
        using (var fixture = new Feed()) {
            using var cancel = new CancellationTokenSource();
            fixture.Handler.Override = (_, token) => { cancel.Cancel(); token.ThrowIfCancellationRequested(); throw new Exception("Cancellation was ignored"); };
            await Reject<OperationCanceledException>(() => fixture.Updates.CheckAsync(cancel.Token), "Checking can be canceled without starting another request");
        }

        foreach (var kind in new[] { "hash", "short", "overrun" }) {
            using var fixture = new Feed(); var package = fixture.Add("0.2.10"); var release = (await fixture.Updates.CheckAsync())!;
            byte[] bad = kind == "short" ? package.Payload[..^1] : kind == "overrun" ? [.. package.Payload, 1] : package.Payload.Select(b => (byte)(b ^ 1)).ToArray();
            fixture.Handler.Routes[release.InstallerUrl.OriginalString] = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekingStream(bad)) });
            string cache = Path.Combine(temp.Root, "bad-" + kind);
            await Reject<InvalidDataException>(() => fixture.Updates.DownloadAsync(release, cache), "Failed " + kind + " verification cannot create a ready installer");
            Check(!Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Any(), "Failed " + kind + " verification removes its partial file");
        }
        using (var fixture = new Feed()) {
            var package = fixture.Add("0.2.10"); var release = (await fixture.Updates.CheckAsync())!;
            using var cancel = new CancellationTokenSource();
            fixture.Handler.Routes[release.InstallerUrl.OriginalString] = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekingStream(package.Payload, cancel)) });
            string cache = Path.Combine(temp.Root, "canceled");
            await Reject<OperationCanceledException>(() => fixture.Updates.DownloadAsync(release, cache, cancellationToken: cancel.Token), "Canceling an in-progress installer stream propagates cancellation");
            Check(!Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Any(), "Canceled downloads leave neither a partial nor a ready installer");
        }
        return checks;
    }

    private static string Url(string version, string file) => $"{Repository}/releases/download/mana-native-v{version}/{file}";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private sealed record Package(string Version, JsonObject Manifest, JsonObject Release, byte[] Payload)
    {
        public JsonArray Assets => (JsonArray)Release["assets"]!;
        public JsonObject Installer => (JsonObject)Assets[1]!;
    }
    private sealed record RequestInfo(string Url, HttpMethod Method, string UserAgent, string ApiVersion, bool Authorization);
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<RequestInfo> Requests { get; } = [];
        public Dictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> Routes { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Override { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(new(request.RequestUri!.OriginalString, request.Method, request.Headers.UserAgent.ToString(),
                request.Headers.GetValues("X-GitHub-Api-Version").Single(), request.Headers.Authorization != null));
            if (Override != null) return Override(request, token);
            if (!Routes.TryGetValue(request.RequestUri.OriginalString, out var response)) throw new Exception("Unexpected fake HTTP request " + request.RequestUri);
            return response(request, token);
        }
    }
    private sealed class Feed : IDisposable
    {
        public FakeHandler Handler { get; } = new();
        public List<RequestInfo> Requests => Handler.Requests;
        public List<JsonObject> Releases { get; } = [];
        private readonly HttpClient http;
        public GitHubAppUpdates Updates { get; }
        public Feed(AppBuild? build = null)
        {
            http = new(Handler);
            Updates = new(build ?? Build, http);
            Handler.Routes[Api] = (_, _) => Task.FromResult(Response(Encoding.UTF8.GetBytes(new JsonArray(Releases.Select(r => r.DeepClone()).ToArray()).ToJsonString())));
        }
        public Package Add(string version)
        {
            var payload = Enumerable.Range(0, 180_000).Select(i => (byte)(i * 37 + 11)).ToArray();
            var manifest = new JsonObject { ["version"] = version, ["architecture"] = "win-x64", ["installer"] = "ManaTable-Setup.exe",
                ["installerProtocol"] = 1, ["installerBytes"] = payload.Length, ["installerSha256"] = Hash(payload),
                ["sourceRevision"] = Build.SourceRevision, ["smokeOnly"] = false };
            var release = new JsonObject { ["tag_name"] = "mana-native-v" + version, ["draft"] = false, ["prerelease"] = false,
                ["html_url"] = Repository + "/releases/tag/mana-native-v" + version,
                ["assets"] = new JsonArray(new JsonObject { ["name"] = "installer-manifest.json", ["browser_download_url"] = Url(version, "installer-manifest.json") },
                    new JsonObject { ["name"] = "ManaTable-Setup.exe", ["browser_download_url"] = Url(version, "ManaTable-Setup.exe"), ["size"] = payload.Length, ["digest"] = "sha256:" + Hash(payload) }) };
            var package = new Package(version, manifest, release, payload); Releases.Add(release); Publish(package); return package;
        }
        public void Publish(Package package)
        {
            // Include the BOM produced by Windows PowerShell's UTF8 manifest writer.
            byte[] manifest = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(package.Manifest.ToJsonString())];
            package.Assets[0]!["size"] = manifest.Length;
            package.Assets[0]!["digest"] = "sha256:" + Hash(manifest);
            Handler.Routes[Url(package.Version, "installer-manifest.json")] = (_, _) => Task.FromResult(Response(manifest));
            Handler.Routes[Url(package.Version, "ManaTable-Setup.exe")] = (_, _) => Task.FromResult(Response(package.Payload));
        }
        public void Dispose() { Updates.Dispose(); http.Dispose(); }
    }
    private sealed class ProgressLog : IProgress<UpdateDownloadProgress>
    {
        public List<UpdateDownloadProgress> Values { get; } = [];
        public void Report(UpdateDownloadProgress value) => Values.Add(value);
    }
    private sealed class NonSeekingStream(byte[] bytes, CancellationTokenSource? cancel = null) : Stream
    {
        private int position, reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (reads++ > 0 && cancel != null) { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
            int count = Math.Min(buffer.Length, bytes.Length - position);
            bytes.AsMemory(position, count).CopyTo(buffer); position += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) { int copied = Math.Min(count, bytes.Length - position); Array.Copy(bytes, position, buffer, offset, copied); position += copied; return copied; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class TemporaryFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ManaTable-Updater-" + Guid.NewGuid().ToString("N"));
        public TemporaryFiles() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            string prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "ManaTable-Updater-");
            if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test cleanup path");
            Directory.Delete(resolved, true);
        }
    }
}
