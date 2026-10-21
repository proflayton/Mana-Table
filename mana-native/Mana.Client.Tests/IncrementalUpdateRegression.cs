using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mana.Client;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

internal static class IncrementalUpdateRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var fixture = new Fixture();
        using var updates = fixture.Updates();
        var release = (await updates.CheckAsync())!;
        check(release is { PayloadKind: UpdatePayloadKind.Incremental, UsesDelta: false }
            && release.SizeBytes == fixture.Asset.Size && fixture.Source.Downloads == 0,
            "Checking incremental updates issues a sized full plan when no base exists and never downloads application content");
        var progress = new ProgressLog();
        var ready = await updates.DownloadAsync(release, fixture.Root, progress);
        await updates.ValidateAsync(ready);
        check(fixture.Source.Downloads == 1 && progress.Values.Any(item => item.Stage == UpdatePreparationStage.Downloading)
            && progress.Values.Any(item => item.Stage == UpdatePreparationStage.Preparing)
            && progress.Values[^1].Stage == UpdatePreparationStage.Ready && await updates.GetPreparedAsync(release) == ready,
            "An explicit download verifies the prepared application contents and reports downloading, preparing and ready without applying");
        await updates.DownloadAsync(release, fixture.Root);
        check(fixture.Source.Downloads == 1, "A previously prepared incremental update is revalidated and reused without network transfer");

        await File.WriteAllBytesAsync(ready.InstallerPath, fixture.Zip(CompressionLevel.NoCompression, altered: true));
        bool rejected = await Reject(() => updates.ValidateAsync(ready));
        var recovery = new ProgressLog();
        await updates.DownloadAsync(release, fixture.Root, recovery);
        check(rejected && fixture.Source.Downloads == 2 && recovery.Values.Any(item => item.FullFallback)
            && await updates.GetPreparedAsync(release) != null,
            "A damaged cached package cannot become ready or restart and is recovered through a verified full download");

        byte[] rewritten = fixture.Zip(CompressionLevel.NoCompression);
        var definition = UpdatePackageContract.Parse(fixture.Contract, fixture.Asset, "win-x64");
        string rewrittenPath = Path.Combine(fixture.Root, "rewritten.nupkg");
        await File.WriteAllBytesAsync(rewrittenPath, rewritten);
        await definition.ValidateAsync(rewrittenPath, default);
        check(!SHA256.HashData(rewritten).SequenceEqual(SHA256.HashData(fixture.Source.Bytes)),
            "A reconstructed package can use a different ZIP compression layout while every published application file remains verified");
        bool extraRejected = false, absentRejected = false;
        await File.WriteAllBytesAsync(rewrittenPath, fixture.Zip(CompressionLevel.NoCompression, extra: true));
        extraRejected = await Reject(() => definition.ValidateAsync(rewrittenPath, default));
        await File.WriteAllBytesAsync(rewrittenPath, fixture.Zip(CompressionLevel.NoCompression, omit: true));
        absentRejected = await Reject(() => definition.ValidateAsync(rewrittenPath, default));
        check(extraRejected && absentRejected && !UpdatePackageContract.SafePath("lib/app/../outside.exe"),
            "Package content proof rejects added, missing or unsafe paths rather than only checking the executable");
        check(await Reject(() => updates.DownloadAsync(release with { SizeBytes = release.SizeBytes + 1 }, fixture.Root)),
            "A caller cannot alter an issued update plan before downloading or restarting it");

        using var cancelled = new Fixture(withBase: true);
        using var cancelledUpdates = cancelled.Updates();
        var cancelledRelease = (await cancelledUpdates.CheckAsync())!;
        cancelled.Source.Block = true;
        using var cancellation = new CancellationTokenSource();
        var downloading = cancelledUpdates.DownloadAsync(cancelledRelease, cancelled.Root, cancellationToken: cancellation.Token);
        await cancelled.Source.Started.Task; cancellation.Cancel();
        bool didCancel = false;
        try { await downloading; } catch (OperationCanceledException) { didCancel = true; }
        check(didCancel && File.Exists(cancelled.BasePath) && !File.Exists(Path.Combine(cancelled.Packages, cancelled.Asset.FileName)),
            "Cancelling preparation preserves the usable base package and cannot leave an unverified target ready");

        using var delta = new Fixture(withBase: true, withDelta: true);
        using var deltaUpdates = delta.Updates();
        var deltaRelease = (await deltaUpdates.CheckAsync())!;
        var fallbackProgress = new ProgressLog();
        await deltaUpdates.DownloadAsync(deltaRelease, delta.Root, fallbackProgress);
        check(deltaRelease.UsesDelta && deltaRelease.SizeBytes == 1 && deltaRelease.FullSizeBytes == delta.Asset.Size
            && fallbackProgress.Values.Any(item => item.FullFallback && item.TotalBytes == delta.Asset.Size),
            "The offered delta size comes from the SDK strategy and an unavailable native patch helper visibly switches to the full package");
        await CheckPublishedSource(fixture, check);
    }

    private static async Task CheckPublishedSource(Fixture fixture, Action<bool, string> check)
    {
        const string repository = "https://github.com/proflayton/Mana-Table";
        const string tag = "mana-native-v0.2.57";
        string Base(string file) => repository + "/releases/download/" + tag + "/" + file;
        var published = fixture.Asset with { PackageId = "ManaTable", FileName = "ManaTable-0.2.57-full.nupkg" };
        byte[] index = JsonSerializer.SerializeToUtf8Bytes(new { Assets = new[] { new {
            published.PackageId, Version = published.Version.ToString(), Type = "Full", published.FileName,
            published.SHA1, published.SHA256, published.Size, NotesMarkdown = "", NotesHTML = ""
        } } });
        var files = new Dictionary<string, byte[]> {
            [Base("releases.win-x64.json")] = index,
            [Base("update-manifest.json")] = fixture.Contract
        };
        var assets = files.Select(file => new Dictionary<string, object> {
            ["name"] = file.Key.Split('/').Last(), ["browser_download_url"] = file.Key, ["size"] = file.Value.Length,
            ["digest"] = "sha256:" + Convert.ToHexString(SHA256.HashData(file.Value)).ToLowerInvariant()
        }).ToList();
        assets.Add(new() { ["name"] = published.FileName, ["browser_download_url"] = Base(published.FileName), ["size"] = published.Size,
            ["digest"] = "sha256:" + published.SHA256.ToLowerInvariant() });
        byte[] Releases() => JsonSerializer.SerializeToUtf8Bytes(new[] { new { draft = false, prerelease = false, tag_name = tag,
            html_url = repository + "/releases/tag/" + tag, assets } });
        var handler = new FeedHandler(uri => uri.Host == "api.github.com" ? Releases() : files[uri.AbsoluteUri]);
        using var http = new HttpClient(handler);
        var source = new ManagedGitHubSource(new("0.2.56", true, new string('b', 40)), http);
        var feed = await source.GetReleaseFeed(new NullVelopackLogger(), "ManaTable", "win-x64");
        await source.Contract(feed.Assets.Single(), default);
        check(feed.Assets.Length == 1 && handler.Requests.Count == 3 && handler.Requests.All(uri => !uri.AbsoluteUri.EndsWith(".nupkg")),
            "The production incremental source checks bounded public release, index and content metadata without fetching a payload");
        assets[^1]["browser_download_url"] = "https://example.invalid/unrelated.nupkg";
        bool badAddress = await Reject(() => source.GetReleaseFeed(new NullVelopackLogger(), "ManaTable", "win-x64"));
        assets[^1]["browser_download_url"] = Base(published.FileName);
        assets[^1]["size"] = published.Size + 1;
        bool badSize = await Reject(() => source.GetReleaseFeed(new NullVelopackLogger(), "ManaTable", "win-x64"));
        check(badAddress && badSize, "A published index cannot redirect downloads or disagree with its actual GitHub asset size");
    }

    private sealed class FeedHandler(Func<Uri, byte[]> read) : HttpMessageHandler
    {
        internal readonly List<Uri> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(read(request.RequestUri!)) });
        }
    }

    private static async Task<bool> Reject(Func<Task> action)
    {
        try { await action(); return false; }
        catch (InvalidDataException) { return true; }
    }
    private sealed class ProgressLog : IProgress<UpdateDownloadProgress>
    {
        internal readonly List<UpdateDownloadProgress> Values = [];
        public void Report(UpdateDownloadProgress value) => Values.Add(value);
    }

    private sealed class Fixture : IDisposable
    {
        private const string Id = "ManaTable-Updater-Tests";
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "ManaTable-Incremental-Tests", Guid.NewGuid().ToString("N"));
        internal string Packages => Path.Combine(Root, "packages");
        internal string BasePath => Path.Combine(Packages, Id + "-0.2.56-full.nupkg");
        internal readonly ControlledSource Source;
        internal readonly VelopackAsset Asset;
        internal readonly byte[] Contract;
        private readonly VelopackAsset? baseAsset;
        private readonly Dictionary<string, byte[]> files;
        internal Fixture(bool withBase = false, bool withDelta = false)
        {
            Directory.CreateDirectory(Packages);
            files = new() {
                [Id + ".nuspec"] = Encoding.UTF8.GetBytes("<package><metadata><id>" + Id + "</id><version>0.2.57</version><authors>Mana Table tests</authors><description>Isolated package contract fixture</description></metadata></package>"),
                ["lib/app/build.json"] = JsonSerializer.SerializeToUtf8Bytes(new { version = "0.2.57", release = true, sourceRevision = new string('a', 40) }),
                ["lib/app/ManaTable.exe"] = Encoding.UTF8.GetBytes(new string('x', 4096))
            };
            byte[] bytes = Zip(CompressionLevel.SmallestSize);
            Asset = new() { PackageId = Id, Version = SemanticVersion.Parse("0.2.57"), FileName = Id + "-0.2.57-full.nupkg",
                SHA256 = Convert.ToHexString(SHA256.HashData(bytes)), SHA1 = Convert.ToHexString(SHA1.HashData(bytes)), Size = bytes.Length, Type = VelopackAssetType.Full };
            Source = new(bytes, Asset, withDelta);
            Contract = JsonSerializer.SerializeToUtf8Bytes(new { updateProtocol = 2, framework = "velopack", packageId = Id, channel = "win-x64",
                version = "0.2.57", sourceRevision = new string('a', 40), fullPackage = new { fileName = Asset.FileName, sizeBytes = Asset.Size, sha256 = Asset.SHA256 },
                entries = files.Select(file => new { path = file.Key, sizeBytes = file.Value.Length, sha256 = Convert.ToHexString(SHA256.HashData(file.Value)) }) });
            if (withBase) {
                File.WriteAllBytes(BasePath, bytes);
                baseAsset = Asset with { Version = SemanticVersion.Parse("0.2.56"), FileName = Path.GetFileName(BasePath) };
            }
        }
        internal IncrementalAppUpdates Updates()
        {
            var locator = new TestVelopackLocator(Id, "0.2.56", Packages, Root, Root, Path.Combine(Root, "Update.exe"),
                channel: "win-x64", localPackage: baseAsset);
            return IncrementalAppUpdates.ForTests(new("0.2.56", true, new string('b', 40)), Source, locator,
                (_, _) => Task.FromResult(Contract), Id);
        }
        internal byte[] Zip(CompressionLevel compression, bool altered = false, bool extra = false, bool omit = false)
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true)) {
                foreach (var file in files) {
                    if (omit && file.Key.EndsWith("ManaTable.exe")) continue;
                    using var entry = archive.CreateEntry(file.Key, compression).Open();
                    entry.Write(altered && file.Key.EndsWith("ManaTable.exe") ? Encoding.UTF8.GetBytes(new string('y', 4096)) : file.Value);
                }
                if (extra) { using var entry = archive.CreateEntry("lib/app/unexpected.exe").Open(); entry.WriteByte(42); }
            }
            return output.ToArray();
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    private sealed class ControlledSource(byte[] bytes, VelopackAsset full, bool withDelta) : IUpdateSource
    {
        internal byte[] Bytes => bytes;
        internal int Downloads;
        internal bool Block;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
            => Task.FromResult(new VelopackAssetFeed { Assets = withDelta ? [full, full with { Type = VelopackAssetType.Delta, Size = 1, FileName = full.FileName.Replace("-full", "-delta") }] : [full] });
        public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset asset, string path, Action<int> progress, CancellationToken cancelToken = default)
        {
            Downloads++; Started.TrySetResult(); progress(0);
            if (Block) await Task.Delay(Timeout.Infinite, cancelToken);
            cancelToken.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync(path, bytes, cancelToken); progress(100);
        }
    }
}
