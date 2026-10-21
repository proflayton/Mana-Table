using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mana.Client;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

// A separate installed executable exercises the real SDK lifecycle. It never
// opens the game and contains the local feed hooks excluded from product Main.
internal static class Program
{
    private sealed record Settings(string AppId, string Source, string ExpectedVersion, string Phase);
    private static int Main(string[] args)
    {
        bool restarted = false;
        VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false).OnRestarted(_ => restarted = true).Run();
        return RunAsync(args, restarted).GetAwaiter().GetResult();
    }
    private static async Task<int> RunAsync(string[] args, bool restarted)
    {
        string? Arg(string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
        string profile = Path.GetFullPath(Arg("--profile") ?? throw new ArgumentException("The lifecycle probe requires an explicit isolated profile."));
        Directory.CreateDirectory(profile);
        string settingsPath = Path.Combine(profile, "probe-settings.json");
        try {
            var locator = VelopackLocator.Current;
            if (locator.AppId?.StartsWith("ManaTableNative-Smoke-", StringComparison.Ordinal) != true
                || locator.AppId.Length != "ManaTableNative-Smoke-".Length + 32 || locator.Channel != "win-x64"
                || locator.RootAppDir == null || profile.StartsWith(Path.GetFullPath(locator.RootAppDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The lifecycle probe only runs from its genuine isolated SDK installation and external test profile.");
            var build = AppBuild.Load(AppContext.BaseDirectory);
            if (!build.CanCheck || build.Version != locator.CurrentlyInstalledVersion?.ToString()) throw new InvalidOperationException("Installed app metadata disagrees with the real locator.");
            Settings settings;
            if (Arg("--source") is { } feed) {
                settings = new(locator.AppId, Path.GetFullPath(feed), Arg("--expect") ?? throw new ArgumentException("Expected version required."), "Idle");
                Save(settingsPath, settings);
            } else settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath)) ?? throw new InvalidDataException("Probe settings missing.");
            if (settings.AppId != locator.AppId) throw new InvalidOperationException("The test identity changed.");
            void Event(string kind, object? detail = null) => File.AppendAllText(Path.Combine(profile, "events.jsonl"), JsonSerializer.Serialize(new {
                kind, utc = DateTime.UtcNow, currentVersion = build.Version, appId = locator.AppId, locator.RootAppDir, locator.PackagesDir,
                locator.IsPortable, restarted, profile, detail
            }) + "\n");

            if (settings.Phase == "Restarting" && !args.Any(arg => arg is "--stage" or "--apply" or "--observe")) {
                if (build.Version != settings.ExpectedVersion || !restarted) throw new InvalidOperationException("The SDK did not restart the expected target version.");
                if (File.ReadAllText(Path.Combine(profile, "sentinel.txt")) != "Keep my decks, settings and chosen profile.") throw new InvalidDataException("Profile sentinel changed.");
                Event("restart-healthy");
                Save(Path.Combine(profile, "healthy-" + build.Version + ".json"), new { passed = true, version = build.Version, restarted, profile, locator.RootAppDir });
                Save(settingsPath, settings with { Phase = "Healthy" });
                return 0;
            }

            var source = new ObservedSource(new SimpleFileSource(new DirectoryInfo(settings.Source)));
            using var updates = IncrementalAppUpdates.ForTests(build, source, locator,
                (_, token) => File.ReadAllBytesAsync(Path.Combine(settings.Source, "update-manifest.json"), token), settings.AppId);
            var release = await updates.CheckAsync() ?? throw new InvalidOperationException("The installed SDK did not find the next update.");
            if (release.Version.ToString(3) != settings.ExpectedVersion || source.Downloads.Count != 0) throw new InvalidOperationException("Checking changed the target or downloaded a payload.");
            Event("checked", new { release.Version, release.UsesDelta, release.SizeBytes, release.FullSizeBytes, source.Downloads });

            if (args.Contains("--observe")) {
                var pending = await updates.GetPreparedAsync(release);
                Event("ordinary-start-kept-current", new { pending = pending?.Release.Version.ToString(3) });
                Save(Path.Combine(profile, "observed.json"), new { passed = pending != null && build.Version != settings.ExpectedVersion,
                    currentVersion = build.Version, target = settings.ExpectedVersion, automaticApplyDisabled = true, downloads = source.Downloads.Count });
                return 0;
            }
            if (args.Contains("--apply")) {
                var ready = await updates.GetPreparedAsync(release) ?? throw new InvalidOperationException("There is no verified prepared update.");
                await updates.ValidateAsync(ready);
                Save(settingsPath, settings with { Phase = "Restarting" }); Event("explicit-restart");
                updates.StartRestart(ready, profile);
                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            bool cancellationQueued = false;
            int? cancelledHelper = null;
            Task? stopping = null;
            bool disposeRequested = args.Contains("--dispose-preparation"), disposedHelper = false;
            double disposeMilliseconds = 0;
            var stages = new List<UpdateDownloadProgress>();
            var progress = new CallbackProgress(value => {
                stages.Add(value); Event("progress", value);
                if ((args.Contains("--cancel-preparation") || disposeRequested) && value.Stage == UpdatePreparationStage.Preparing && !cancellationQueued) {
                    cancellationQueued = true;
                    stopping = Task.Run(async () => {
                        for (int attempt = 0; attempt < 150 && cancelledHelper == null; attempt++) {
                            foreach (var process in Process.GetProcessesByName("Update")) using (process) {
                                try {
                                    if (string.Equals(process.MainModule?.FileName, locator.UpdateExePath, StringComparison.OrdinalIgnoreCase)) {
                                        cancelledHelper = process.Id; break;
                                    }
                                } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                            }
                            if (cancelledHelper == null) await Task.Delay(10);
                        }
                        if (disposeRequested) {
                            Event("disposing-with-native-helper", new { processId = cancelledHelper });
                            var timer = Stopwatch.StartNew(); updates.Dispose(); disposeMilliseconds = timer.Elapsed.TotalMilliseconds;
                            if (cancelledHelper is { } pid) {
                                try { using var owned = Process.GetProcessById(pid); disposedHelper = owned.HasExited; }
                                catch (ArgumentException) { disposedHelper = true; }
                            }
                            Event("disposed-native-helper", new { processId = cancelledHelper, disposedHelper, disposeMilliseconds });
                        } else {
                            Event("cancelling-native-helper", new { processId = cancelledHelper });
                            cancellation.Cancel();
                        }
                    });
                }
            });
            bool cancelled = false;
            try { await updates.DownloadAsync(release, Path.Combine(profile, "unused-sdk-cache"), progress, cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested || disposeRequested) { cancelled = true; }
            if (stopping != null) await stopping;
            if (args.Contains("--cancel-preparation") || disposeRequested) {
                if (!cancelled || cancelledHelper == null || !Directory.EnumerateFiles(locator.PackagesDir!, "*" + build.Version + "*full.nupkg").Any())
                    throw new InvalidOperationException("Cancelling native preparation did not retain its usable base.");
                if (disposeRequested && !disposedHelper) throw new InvalidOperationException("Dispose returned before its native preparation helper exited.");
                Event("cancelled-base-retained");
            } else {
                var ready = await updates.GetPreparedAsync(release) ?? throw new InvalidDataException("Prepared contents did not validate.");
                await updates.ValidateAsync(ready);
                if (args.Contains("--require-delta") && (!release.UsesDelta || source.Downloads.Any(file => file.Type != "Delta")))
                    throw new InvalidDataException("The real SDK did not prepare only delta artifacts.");
                if (args.Contains("--require-full-fallback") && (!stages.Any(stage => stage.FullFallback) || !source.Downloads.Any(file => file.Type == "Full")))
                    throw new InvalidDataException("A failed patch did not fall back to a full verified package.");
                Event("prepared-verified", new { ready.InstallerPath });
                if (args.Contains("--reject-contained-profile")) {
                    int rejectedProfiles = 0;
                    foreach (var contained in new[] { locator.RootAppDir, Path.Combine(locator.RootAppDir!, "current"), Path.Combine(locator.RootAppDir!, "current", ".."), Path.Combine(locator.RootAppDir!, "packages", "my-profile") }) {
                        try { updates.StartRestart(ready, contained!); }
                        catch (InvalidOperationException error) when (error.Message.Contains("outside the application installation", StringComparison.Ordinal)) { rejectedProfiles++; }
                    }
                    if (rejectedProfiles != 4) throw new InvalidOperationException("A contained profile was allowed to schedule apply.");
                    Event("contained-profiles-rejected", new { rejectedProfiles });
                }
            }
            Save(Path.Combine(profile, Arg("--result") ?? "stage-result.json"), new { passed = true, currentVersion = build.Version, target = settings.ExpectedVersion,
                release.UsesDelta, release.SizeBytes, release.FullSizeBytes, cancelled, cancelledHelper, disposedHelper, disposeMilliseconds, stages, source.Downloads, profile, locator.RootAppDir, locator.IsPortable });
            return 0;
        } catch (Exception error) {
            File.WriteAllText(Path.Combine(profile, "probe-error.log"), error.ToString());
            return 1;
        }
    }
    private static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value));
    private sealed class CallbackProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }
    private sealed class ObservedSource(IUpdateSource inner) : IUpdateSource
    {
        internal sealed record Download(string FileName, string Type, long Bytes, double Seconds);
        internal readonly List<Download> Downloads = [];
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
            => inner.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
        public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset asset, string path, Action<int> progress, CancellationToken cancelToken = default)
        {
            var clock = Stopwatch.StartNew();
            try { await inner.DownloadReleaseEntry(logger, asset, path, progress, cancelToken); }
            finally { Downloads.Add(new(asset.FileName, asset.Type.ToString(), File.Exists(path) ? new FileInfo(path).Length : 0, clock.Elapsed.TotalSeconds)); }
        }
    }
}
