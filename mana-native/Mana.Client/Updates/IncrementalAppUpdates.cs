using System.Diagnostics;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace Mana.Client;

// Velopack performs package caching, native patching, full fallback and apply.
// This adapter owns our issued plan, explicit user stages and content proof.
public sealed class IncrementalAppUpdates : IAppUpdates, IDisposable
{
    private sealed record Plan(AppRelease Release, UpdateInfo Info, UpdatePackageContract Contract);
    private readonly UpdateManager manager;
    private readonly IVelopackLocator locator;
    private readonly TrackingSource source;
    private readonly Func<VelopackAsset, CancellationToken, Task<byte[]>> contract;
    private readonly Func<VelopackAsset, Uri> location;
    private readonly HttpClient? ownedHttp;
    private readonly ManagedGitHubSource? github;
    private readonly Dictionary<string, Plan> plans = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim operations = new(1, 1);
    public AppBuild Build { get; }
    public bool IsPortable => manager.IsPortable;

    private IncrementalAppUpdates(AppBuild build, IUpdateSource source, IVelopackLocator locator,
        Func<VelopackAsset, CancellationToken, Task<byte[]>> contract, Func<VelopackAsset, Uri> location,
        string expectedAppId, HttpClient? ownedHttp = null, ManagedGitHubSource? github = null)
    {
        Build = build; this.locator = locator; this.contract = contract; this.location = location;
        this.ownedHttp = ownedHttp; this.github = github;
        this.source = new(source);
        manager = new PreservingUpdateManager(this.source, locator);
        if (!Build.CanCheck || !manager.IsInstalled || manager.AppId != expectedAppId
            || manager.CurrentVersion?.ToString() != Build.Version || locator.Channel != ManagedGitHubSource.Channel)
            throw new InvalidOperationException("This copy does not have an incremental-update installation.");
    }

    public static IncrementalAppUpdates? TryCreate(AppBuild build)
    {
        if (!build.CanCheck || !OperatingSystem.IsWindows()) return null;
        var locator = VelopackLocator.Current;
        if (locator.AppId != ManagedGitHubSource.PackageId || locator.CurrentlyInstalledVersion?.ToString() != build.Version
            || locator.Channel != ManagedGitHubSource.Channel) return null;
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var github = new ManagedGitHubSource(build, http);
        return new(build, github, locator, github.Contract, github.Location, ManagedGitHubSource.PackageId, http, github);
    }

    internal static IncrementalAppUpdates ForTests(AppBuild build, IUpdateSource source, IVelopackLocator locator,
        Func<VelopackAsset, CancellationToken, Task<byte[]>> contract, string testAppId) =>
        new(build, source, locator, contract, asset => new Uri("https://github.com/proflayton/Mana-Table/releases/download/mana-native-v" + asset.Version + "/" + asset.FileName), testAppId);

    public async Task<AppRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken);
        try {
            if (github != null) github.OperationToken = cancellationToken;
            var info = await manager.CheckForUpdatesAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (info == null) return null;
            var target = info.TargetFullRelease;
            if (info.IsDowngrade || !AppBuild.TryVersion(target.Version.ToString(), out var version) || version <= System.Version.Parse(Build.Version)
                || target.PackageId != manager.AppId || target.Type != VelopackAssetType.Full || !AppBuild.Hex(target.SHA256, 64)
                || target.Size <= 0 || target.Size > UpdatePackageContract.MaximumPackageBytes) throw InvalidPlan();
            var definition = UpdatePackageContract.Parse(await contract(target, cancellationToken), target, ManagedGitHubSource.Channel);
            long deltaBytes = info.DeltasToTarget.Sum(asset => asset.Size);
            bool useDelta = info.BaseRelease != null && info.DeltasToTarget.Length is > 0 and <= 10 && deltaBytes > 0 && deltaBytes < target.Size;
            // Record the exact strategy offered to the user, including a full
            // fallback if a missing base or oversized chain rules out patches.
            if (!useDelta) info = new(target, false);
            var release = new AppRelease(version, new(ManagedGitHubSource.Repository + "/releases/tag/mana-native-v" + version.ToString(3)),
                location(target), useDelta ? deltaBytes : target.Size, target.SHA256) {
                PayloadKind = UpdatePayloadKind.Incremental, UsesDelta = useDelta, FullSizeBytes = target.Size, PlanId = Guid.NewGuid().ToString("N")
            };
            plans.Clear(); plans.Add(release.PlanId, new(release, info, definition));
            return release;
        } finally { operations.Release(); }
    }

    public async Task<PreparedUpdate> DownloadAsync(AppRelease release, string cacheDirectory,
        IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken);
        try {
            var plan = RequirePlan(release);
            var target = plan.Info.TargetFullRelease;
            string path = PackagePath(target);
            source.Begin(release, progress, cancellationToken);
            await manager.DownloadUpdatesAsync(plan.Info, _ => { }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            source.Preparing();
            try { await plan.Contract.ValidateAsync(path, cancellationToken); }
            catch (Exception error) when (error is InvalidDataException or IOException) {
                // The SDK may reuse a damaged cache or produce an invalid
                // reconstruction. A verified full package is the recovery path.
                File.Delete(path);
                source.FullFallback();
                await manager.DownloadUpdatesAsync(new(target, false), _ => { }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                source.Preparing();
                await plan.Contract.ValidateAsync(path, cancellationToken);
            }
            progress?.Report(new(source.Received, source.Total) { Stage = UpdatePreparationStage.Ready, FullFallback = source.FellBack, PreparationPercent = 100 });
            return new(release, path);
        } finally { source.End(); operations.Release(); }
    }

    public Task ValidateAsync(PreparedUpdate update, CancellationToken cancellationToken = default)
    {
        var plan = RequirePlan(update.Release);
        if (!Path.GetFullPath(update.InstallerPath).Equals(PackagePath(plan.Info.TargetFullRelease), StringComparison.OrdinalIgnoreCase)) throw InvalidPlan();
        return plan.Contract.ValidateAsync(update.InstallerPath, cancellationToken);
    }

    public async Task<PreparedUpdate?> GetPreparedAsync(AppRelease release, CancellationToken cancellationToken = default)
    {
        var plan = RequirePlan(release);
        string path = PackagePath(plan.Info.TargetFullRelease);
        if (!File.Exists(path)) return null;
        try { await plan.Contract.ValidateAsync(path, cancellationToken); return new(release, path); }
        catch (IOException) { return null; }
    }

    public void StartRestart(PreparedUpdate update, string profile)
    {
        var plan = RequirePlan(update.Release);
        ValidateAsync(update).GetAwaiter().GetResult();
        string playerProfile = Path.GetFullPath(profile);
        manager.WaitExitThenApplyUpdates(plan.Info.TargetFullRelease, silent: true, restart: true, restartArgs: ["--profile", playerProfile]);
        // The table host now closes its client and Java engine normally. Never
        // call ApplyUpdatesAndRestart, which exits before that cleanup can run.
    }

    private string PackagePath(VelopackAsset target)
    {
        if (!UpdatePackageContract.SafePath(target.FileName) || target.FileName.Contains('/')) throw InvalidPlan();
        return Path.GetFullPath(Path.Combine(locator.PackagesDir ?? throw InvalidPlan(), target.FileName));
    }
    private Plan RequirePlan(AppRelease release) => release.PayloadKind == UpdatePayloadKind.Incremental
        && plans.TryGetValue(release.PlanId, out var plan) && plan.Release == release ? plan : throw InvalidPlan();
    private static InvalidDataException InvalidPlan() => new("Check for updates again before preparing or restarting this update.");
    public void Dispose() { ownedHttp?.Dispose(); operations.Dispose(); }

    private sealed class PreservingUpdateManager(IUpdateSource source, IVelopackLocator locator)
        : UpdateManager(source, new() { AllowVersionDowngrade = false, MaximumDeltasBeforeFallback = 10 }, locator)
    {
        protected override async Task DownloadAndApplyDeltaUpdates(UpdateInfo updates, string targetFile, Action<int> progress, CancellationToken cancelToken)
        {
            // Use the framework's native patcher and checksums, with an owned
            // asynchronous lifetime so cancelling cannot orphan preparation.
            string updater = Locator.UpdateExePath ?? throw new FileNotFoundException("The update helper is missing.");
            if (!File.Exists(updater)) throw new FileNotFoundException("The update helper is missing.", updater);
            var deltas = updates.DeltasToTarget.OrderBy(asset => asset.Version).ToArray();
            for (int index = 0; index < deltas.Length; index++) {
                int at = index;
                string path = LocalPackage(deltas[index]);
                await Source.DownloadReleaseEntry(Log, deltas[index], path,
                    percent => progress((at * 70 + Math.Clamp(percent, 0, 100) * 70 / 100) / deltas.Length), cancelToken).ConfigureAwait(false);
                await VerifyPackageChecksumAsync(deltas[index], path).ConfigureAwait(false);
                cancelToken.ThrowIfCancellationRequested();
            }
            var start = new ProcessStartInfo(updater) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() };
            foreach (string argument in new[] { "patch", "--old", LocalPackage(updates.BaseRelease!), "--output", targetFile }) start.ArgumentList.Add(argument);
            if (Locator.RootAppDir is { Length: > 0 } root) { start.ArgumentList.Add("--root"); start.ArgumentList.Add(root); }
            foreach (var delta in deltas) { start.ArgumentList.Add("--delta"); start.ArgumentList.Add(LocalPackage(delta)); }
            cancelToken.ThrowIfCancellationRequested();
            using var process = Process.Start(start) ?? throw new IOException("The update helper could not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancelToken); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                if (!cancelToken.IsCancellationRequested) throw new TimeoutException("Preparing the smaller update took too long.");
                throw;
            }
            if (process.ExitCode != 0) throw new InvalidDataException("The smaller update could not be prepared.");
            progress(100);
        }

        private string LocalPackage(VelopackAsset asset)
        {
            if (!UpdatePackageContract.SafePath(asset.FileName) || asset.FileName.Contains('/')) throw InvalidPlan();
            return Path.Combine(Locator.PackagesDir ?? throw InvalidPlan(), asset.FileName);
        }

        protected override void CleanPackagesExcept(string? assetToKeep)
        {
            // An interrupted preparation must retain the previous usable base.
            if (assetToKeep != null && File.Exists(assetToKeep)) base.CleanPackagesExcept(assetToKeep);
            else if (assetToKeep != null) {
                try { File.Delete(assetToKeep + ".partial"); }
                catch (IOException error) { Log.Warn(error, "Could not remove an interrupted update fragment."); }
            }
        }
    }

    private sealed class TrackingSource(IUpdateSource inner) : IUpdateSource
    {
        private readonly object gate = new();
        private readonly Dictionary<string, long> received = new(StringComparer.Ordinal);
        private IProgress<UpdateDownloadProgress>? progress;
        private AppRelease? release;
        private CancellationToken token;
        internal long Received { get { lock (gate) return received.Values.Sum(); } }
        internal long Total { get; private set; }
        internal bool FellBack { get; private set; }
        internal void Begin(AppRelease release, IProgress<UpdateDownloadProgress>? progress, CancellationToken token)
        {
            lock (gate) { this.release = release; this.progress = progress; this.token = token; received.Clear(); Total = release.SizeBytes; FellBack = false; }
            Report(UpdatePreparationStage.Downloading);
        }
        internal void End() { lock (gate) { progress = null; release = null; } }
        internal void Preparing() => Report(UpdatePreparationStage.Preparing);
        internal void FullFallback()
        {
            lock (gate) { FellBack = true; received.Clear(); Total = release?.FullSizeBytes ?? Total; }
            Report(UpdatePreparationStage.Downloading);
        }
        private void Report(UpdatePreparationStage stage)
        {
            lock (gate) progress?.Report(new(received.Values.Sum(), Total) { Stage = stage, FullFallback = FellBack });
        }
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
            => inner.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
        public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset asset, string path, Action<int> sdkProgress, CancellationToken cancelToken = default)
        {
            token.ThrowIfCancellationRequested();
            if (release?.UsesDelta == true && asset.Type == VelopackAssetType.Full && !FellBack) FullFallback();
            await inner.DownloadReleaseEntry(logger, asset, path, percent => {
                lock (gate) received[asset.FileName] = asset.Size * Math.Clamp(percent, 0, 100) / 100;
                Report(UpdatePreparationStage.Downloading); sdkProgress(percent);
            }, cancelToken);
            lock (gate) received[asset.FileName] = asset.Size;
            if (Received >= Total) Preparing();
        }
    }
}
