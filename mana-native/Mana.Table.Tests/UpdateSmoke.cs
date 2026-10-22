using Mana.Client;
using Mana.Contracts;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Security.Cryptography;
using System.Text.Json;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal static class UpdateSmoke
{
    internal static void Run(string profile, Point? size)
    {
        var checks = new List<string>();
        string Case(string name) { string path = Path.Combine(profile, name); Directory.CreateDirectory(path); return path; }
        void Check(bool valid, string text) {
            if (!valid) throw new InvalidOperationException(text);
            checks.Add(text); File.AppendAllText(Path.Combine(profile, "update-progress.log"), text + "\n");
        }
        TableWindow.Probe Scene(string path, FakeUpdates? service, Action<Script, TableWindow.Probe, StartupEngine> arrange, Action<PreparedUpdate>? install = null)
        {
            var engine = new StartupEngine();
            using var game = new TableWindow(TestTableServices.For(engine), path, null, null, size, artSource: new EmptyArt(), appUpdates: service, installUpdate: install);
            var view = game.TestPort; var script = new Script(view); game.Automation = script;
            arrange(script, view, engine); game.Run(); return view;
        }
        void QuietFrames(Script script, TableWindow.Probe view, FakeUpdates fake, bool expectCheck)
        {
            script.Then(() => {
                view.SetAutomation(null);
                _ = Task.Run(async () => {
                    try {
                        if (expectCheck) await fake.CheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        else await Task.Delay(180);
                    } finally { view.Post(() => view.SetAutomation(script)); }
                });
            });
        }
        string preferencesPath = Case("manual-check-and-preference");
        var manual = new FakeUpdates(preferencesPath);
        Scene(preferencesPath, manual, (s, v, engine) => {
            s.Then(() => Check(manual.CheckCalls == 0 && v.UpdateCheckOnStartup && !v.UpdatesOpen, "Automation does not initiate network update checks; startup checks default on"));
            s.Click("updates-open");
            s.Then(() => Check(v.UpdatesOpen && v.Hits.All(h => h.Id.StartsWith("updates-")), "Home opens a modal with only scoped update controls"));
            s.Click("updates-check"); s.Until(() => manual.CheckCalls == 1);
            s.Then(() => { Check(!v.Busy && !v.ActivityOpen && v.UpdateChecking, "An update check leaves the engine and activity overlay idle"); s.Capture("01-checking"); manual.CheckGate.SetException(new HttpRequestException("Controlled offline response.")); });
            s.Until(() => !v.UpdateChecking);
            s.Then(() => { Check(v.UpdateError.Contains("Couldn't check") && v.Error.Length == 0, "Check failure has a local retry message without a global error"); s.Capture("02-check-error"); manual.ResetCheck(); });
            s.Click("updates-check"); s.Until(() => manual.CheckCalls == 2);
            s.Then(() => manual.CheckGate.SetResult(manual.Release)); s.Until(() => v.AvailableUpdate != null);
            s.Then(() => { Check(v.AvailableUpdate!.Version == manual.Release.Version && !v.UpdateChecking, "Retry finds the newer installer release"); s.Capture("03-available"); });
            s.Click("updates-startup");
            s.Then(() => Check(!v.UpdateCheckOnStartup && File.Exists(Path.Combine(preferencesPath, "update-preferences.json")), "The startup-check choice is persisted"));
            s.Pulse(Keys.Escape);
            s.Then(() => Check(!v.UpdatesOpen && v.WelcomeOpen && engine.Writes == 0, "Escape closes Updates without dismissing Home or writing to the engine"));
        });
        var optOut = new FakeUpdates(preferencesPath);
        Scene(preferencesPath, optOut, (s, v, _) => {
            s.Then(() => Check(!v.UpdateCheckOnStartup, "The saved startup opt-out survives a new app session"));
            QuietFrames(s, v, optOut, false);
            s.Then(() => Check(optOut.CheckCalls == 0 && !v.UpdatesOpen && v.WelcomeOpen, "Actual startup eligibility respects opt-out while Automation is absent"));
            s.Click("updates-open"); s.Click("updates-check"); s.Until(() => optOut.CheckCalls == 1);
            s.Then(() => optOut.CheckGate.SetResult(null)); s.Until(() => !v.UpdateChecking);
            s.Then(() => { Check(v.AvailableUpdate == null && v.UpdateError.Length == 0 && !v.UpdateCheckOnStartup, "Manual checks remain available with startup checks off"); s.Capture("04-current"); });
        });
        string quietPath = Case("quiet-startup"); var quiet = new FakeUpdates(quietPath);
        Scene(quietPath, quiet, (s, v, _) => {
            QuietFrames(s, v, quiet, true); s.Until(() => quiet.CheckCalls == 1);
            s.Then(() => { Check(!v.UpdatesOpen && v.WelcomeOpen && !v.Busy && !v.ActivityOpen, "The actual automatic check stays quiet and leaves Home usable"); quiet.CheckGate.SetResult(quiet.Release); });
            s.Until(() => v.AvailableUpdate != null); s.Wait(.25);
            s.Then(() => { Check(quiet.CheckCalls == 1 && !v.UpdatesOpen && v.Hits.Any(h => h.Id == "experience-ai"), "The startup check runs once and availability adds no forced modal"); s.Capture("05-home-badge"); });
        });
        string quietFailurePath = Case("quiet-check-failure"); var quietFailure = new FakeUpdates(quietFailurePath);
        Scene(quietFailurePath, quietFailure, (s, v, _) => {
            QuietFrames(s, v, quietFailure, true); s.Until(() => quietFailure.CheckCalls == 1);
            s.Then(() => quietFailure.CheckGate.SetException(new HttpRequestException("Controlled release check failure.")));
            s.Until(() => !v.UpdateChecking);
            s.Then(() => { Check(!v.UpdatesOpen && v.WelcomeOpen && !v.ActivityOpen && v.Error.Length == 0 && v.UpdateError.Contains("Couldn't check"), "A failed quiet startup check changes only update status and leaves Home uninterrupted"); s.Capture("05-home-check-failure"); });
        });
        string path = Case("download-and-install"); var service = new FakeUpdates(path);
        int launches = 0; bool failLaunch = true;
        var last = Scene(path, service, (s, v, engine) => {
            s.Click("updates-open"); s.Click("updates-check"); s.Until(() => service.CheckCalls == 1);
            s.Then(() => service.CheckGate.SetResult(service.Release)); s.Until(() => v.AvailableUpdate != null);
            s.Click("updates-download"); s.Until(() => service.DownloadCalls == 1);
            s.Then(() => service.Report(64 * 1024 * 1024)); s.Until(() => v.UpdateBytes == 64 * 1024 * 1024);
            s.Then(() => { Check(v.UpdateDownloading && !v.Busy && !v.ActivityOpen, "Download exposes byte progress without blocking the engine"); s.Capture("06-progress"); });
            s.Click("updates-close");
            s.Then(() => { v.Accept(PresentationSmoke.Example()); Check(v.UpdateDownloading && !v.UpdatesOpen, "Download continues after the panel closes and a game opens"); });
            s.Click("settings"); s.Click("updates-settings");
            s.Then(() => Check(v.UpdatesOpen && v.Match != null, "Settings opens Updates during gameplay without replacing the game"));
            s.Pulse(Keys.Space, Keys.F, Keys.C, Keys.LeftControl, Keys.Enter);
            s.Then(() => Check(engine.Writes == 0 && !v.Automatic && !v.Held && v.Match!.Decision != null, "Gameplay shortcuts cannot leak through the update panel"));
            s.Click("updates-cancel"); s.Until(() => !v.UpdateDownloading);
            s.Then(() => { Check(service.Cancellations == 1 && v.PreparedUpdate == null && v.UpdateNotice.Contains("cancelled"), "Cancel stops the download and leaves a clear retry path"); v.Accept(null); service.ResetDownload(); });
            s.Click("updates-download"); s.Until(() => service.DownloadCalls == 2);
            s.Then(() => service.DownloadGate.SetResult(service.Prepared)); s.Until(() => v.PreparedUpdate != null);
            s.Then(() => { Check(!v.UpdateDownloading && v.Hits.Any(h => h.Id == "updates-install"), "A verified download exposes an explicit Install and restart action"); s.Capture("07-ready"); v.SetUpdateGuardState(connected: true); });
            s.Then(() => { Check(v.UpdateInstallBlocker.Contains("friends table") && !v.Hits.Any(h => h.Id == "updates-install"), "An active friends table prevents installation without disconnecting it"); v.SetUpdateGuardState(saveError: "Controlled unsaved deck"); });
            s.Then(() => { Check(v.UpdateInstallBlocker.Contains("Save your deck") && !v.Hits.Any(h => h.Id == "updates-install"), "A failed deck save prevents installation"); v.SetUpdateGuardState(text: true); });
            s.Then(() => { Check(v.UpdateInstallBlocker.Contains("current input") && !v.Hits.Any(h => h.Id == "updates-install"), "Active text input prevents installation"); v.SetUpdateGuardState(); });
            s.Click("updates-install"); s.Until(() => service.ValidationCalls == 1);
            s.Then(() => { Check(v.UpdateValidating && launches == 0, "Install revalidates the cached installer before launching"); v.Accept(PresentationSmoke.Example() with { Id = "update-validation-race" }); service.ValidationGate.SetResult(); });
            s.Until(() => !v.UpdateValidating);
            s.Then(() => { Check(launches == 0 && v.Match != null && v.UpdateInstallBlocker.Contains("game"), "A game opened during validation prevents the installer callback"); s.Capture("08-deferred"); v.Accept(null); service.ResetValidation(); });
            s.Click("updates-install"); s.Until(() => service.ValidationCalls == 2);
            s.Then(() => service.ValidationGate.SetException(new CryptographicException("Controlled changed installer hash.")));
            s.Until(() => !v.UpdateValidating);
            s.Then(() => { Check(v.PreparedUpdate == null && launches == 0 && v.UpdateError.Contains("could not be verified"), "A changed installer fails verification and requires a fresh download"); service.ResetDownload(); });
            s.Click("updates-download"); s.Until(() => service.DownloadCalls == 3);
            s.Then(() => service.DownloadGate.SetResult(service.Prepared)); s.Until(() => v.PreparedUpdate != null);
            s.Then(service.ResetValidation); s.Click("updates-install"); s.Until(() => service.ValidationCalls == 3);
            s.Then(() => service.ValidationGate.SetResult()); s.Until(() => !v.UpdateValidating);
            s.Then(() => { Check(launches == 1 && !v.UpdateInstallerLaunched && v.PreparedUpdate != null && v.UpdateError.Contains("Couldn't start"), "A failed installer launch keeps the app open with a retry action"); s.Capture("09-launch-error"); failLaunch = false; service.ResetValidation(); });
            s.Click("updates-install"); s.Until(() => service.ValidationCalls == 4);
            s.Then(() => service.ValidationGate.SetResult()); s.Until(() => v.UpdateInstallerLaunched);
        }, ready => { launches++; if (failLaunch) throw new IOException("Controlled process launch failure."); });
        Check(launches == 2 && last.UpdateInstallerLaunched, "The app exits only after the explicit installer callback succeeds");
        string portablePath = Case("portable"); var portable = new FakeUpdates(portablePath);
        Scene(portablePath, portable, (s, v, _) => {
            s.Click("updates-open"); s.Click("updates-check"); s.Until(() => portable.CheckCalls == 1);
            s.Then(() => portable.CheckGate.SetResult(portable.Release)); s.Until(() => v.AvailableUpdate != null);
            s.Click("updates-download"); s.Until(() => portable.DownloadCalls == 1);
            s.Then(() => portable.DownloadGate.SetResult(portable.Prepared)); s.Until(() => v.PreparedUpdate != null);
            s.Then(() => { Check(!v.Hits.Any(h => h.Id == "updates-install") && v.Hits.Any(h => h.Id == "updates-folder"), "Portable copies offer their download folder and manual releases instead of in-place installation"); s.Capture("10-portable"); portable.ResetCheck(); });
            s.Click("updates-check-again"); s.Until(() => portable.CheckCalls == 2);
            s.Then(() => portable.CheckGate.SetResult(portable.Release with { Sha256 = new('b', 64) })); s.Until(() => !v.UpdateChecking);
            s.Then(() => Check(v.PreparedUpdate == null && v.Hits.Any(h => h.Id == "updates-download"), "Changed release identity invalidates a cached installer even at the same version"));
        });
        string closingPath = Case("closing-download"); var closing = new FakeUpdates(closingPath);
        Scene(closingPath, closing, (s, v, _) => {
            s.Click("updates-open"); s.Click("updates-check"); s.Until(() => closing.CheckCalls == 1);
            s.Then(() => closing.CheckGate.SetResult(closing.Release)); s.Until(() => v.AvailableUpdate != null);
            s.Click("updates-download"); s.Until(() => closing.DownloadCalls == 1);
        });
        Check(closing.Cancellations == 1, "Closing the app cancels a background download before teardown");
        Scene(Case("development"), null, (s, v, _) => {
            s.Click("updates-open");
            s.Then(() => { Check(!v.Hits.Any(h => h.Id is "updates-check" or "updates-download" or "updates-install") && v.Hits.Any(h => h.Id == "updates-releases"), "Development builds expose manual releases without network checks"); s.Capture("11-development"); });
        });
        File.WriteAllText(Path.Combine(profile, "update-ui-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class FakeUpdates(string profile) : IAppUpdates
    {
        public AppBuild Build { get; } = new("0.2.7", true, new('a', 40));
        internal AppRelease Release { get; } = new(new Version(0, 2, 8), new("https://github.com/proflayton/Mana-Table/releases/tag/mana-native-v0.2.8"),
            new("https://github.com/proflayton/Mana-Table/releases/download/mana-native-v0.2.8/ManaTable-Setup.exe"), 128 * 1024 * 1024, new('a', 64));
        internal PreparedUpdate Prepared => new(Release, Path.Combine(profile, "updates", "ManaTable-Setup.exe"));
        internal TaskCompletionSource<AppRelease?> CheckGate = Gate<AppRelease?>();
        internal TaskCompletionSource<PreparedUpdate> DownloadGate = Gate<PreparedUpdate>();
        internal TaskCompletionSource ValidationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CheckStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IProgress<UpdateDownloadProgress>? progress;
        internal int CheckCalls, DownloadCalls, ValidationCalls;
        private readonly List<CancellationToken> downloadTokens = [];
        internal int Cancellations { get { lock (downloadTokens) return downloadTokens.Count(t => t.IsCancellationRequested); } }
        private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void ResetCheck() { CheckGate = Gate<AppRelease?>(); CheckStarted = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        internal void ResetDownload() => DownloadGate = Gate<PreparedUpdate>();
        internal void ResetValidation() => ValidationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Report(long bytes) => progress?.Report(new(bytes, Release.SizeBytes));
        public async Task<AppRelease?> CheckAsync(CancellationToken cancellationToken = default) {
            Interlocked.Increment(ref CheckCalls); CheckStarted.TrySetResult(); return await CheckGate.Task.WaitAsync(cancellationToken);
        }
        public async Task<PreparedUpdate> DownloadAsync(AppRelease release, string cacheDirectory, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default) {
            this.progress = progress; lock (downloadTokens) downloadTokens.Add(cancellationToken); Interlocked.Increment(ref DownloadCalls);
            return await DownloadGate.Task.WaitAsync(cancellationToken);
        }
        public async Task ValidateAsync(PreparedUpdate update, CancellationToken cancellationToken = default) {
            Interlocked.Increment(ref ValidationCalls); await ValidationGate.Task.WaitAsync(cancellationToken);
        }
    }
    private sealed class EmptyArt : ICardArtSource
    {
        public Task<byte[]?> LoadAsync(ArtRequest art, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
        public void Dispose() { }
    }
    private sealed class Script(TableWindow.Probe view) : ITableAutomation
    {
        private readonly Queue<Action> steps = [];
        private KeyboardState keyboard = new(); private MouseState mouse;
        private int mousePhase; private Point target; private double until;
        private Func<bool>? waiting;
        public string? ExpectedClick { get; private set; }
        public bool ClickAccepted { get; set; }
        public KeyboardState? ReadKeyboard() => keyboard;
        public MouseState ReadMouse() {
            if (mousePhase is not (1 or 2)) return mouse;
            bool down = mousePhase++ == 1;
            var viewport = view.GraphicsDevice.Viewport; float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
            mouse = new((int)((viewport.Width - 1600 * scale) / 2 + target.X * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + target.Y * scale), 0,
                down ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
            return mouse;
        }
        internal void Then(Action action) => steps.Enqueue(action);
        internal void Until(Func<bool> condition) => Then(() => waiting = condition);
        internal void Wait(double seconds) => Then(() => until = view.Now + seconds);
        internal void Pulse(params Keys[] keys) { Then(() => keyboard = new(keys)); Then(() => keyboard = new()); }
        internal void Click(string id) => Then(() => {
            var hit = view.Hits.LastOrDefault(h => h.Id == id) ?? throw new InvalidOperationException("Missing update control " + id);
            target = hit.Bounds.Center; ExpectedClick = id; ClickAccepted = false; mousePhase = 1;
        });
        internal void Capture(string name) { using var output = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height); }
        public void Frame(Texture2D surface) {
            if (view.Error.Length > 0) { Capture("failure"); throw new InvalidOperationException(view.Error); }
            if (view.Now > 35) { Capture("timeout"); throw new TimeoutException("Update UI script timed out. " + view.UpdateError); }
            if (mousePhase is 1 or 2) return;
            if (mousePhase == 3) {
                if (!ClickAccepted) { Capture("failure"); throw new InvalidOperationException("Rejected update click " + ExpectedClick); }
                mousePhase = 0; ExpectedClick = null;
            }
            if (!view.Loaded || view.Busy || view.ActivityOpen || view.Now < until) return;
            if (waiting != null) { if (!waiting()) return; waiting = null; }
            if (steps.TryDequeue(out var step)) step(); else view.Exit();
        }
    }
    private sealed class StartupEngine : ICardEngine
    {
        internal int Writes;
        public string Name => "Controlled update scenario";
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<DeckSummary>> DecksAsync() => Task.FromResult<IReadOnlyList<DeckSummary>>([]);
        public Task<IReadOnlyList<DeckPreset>> PresetsAsync() => Task.FromResult<IReadOnlyList<DeckPreset>>([]);
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private Task Reject() { Writes++; throw new InvalidOperationException("Updater unexpectedly changed game state"); }
        private Task<T> Reject<T>() { Writes++; throw new InvalidOperationException("Updater unexpectedly changed game state"); }
        public Task<CatalogPage> SearchCardsAsync(CardQuery query) => Task.FromResult(new CatalogPage(0, 0, []));
        public Task<DeckDetails> OpenDeckAsync(string id) => Reject<DeckDetails>();
        public Task<DeckSummary> ImportAsync(string name, string text) => Reject<DeckSummary>();
        public Task<DeckSummary> ImportPresetAsync(string id) => Reject<DeckSummary>();
        public Task<DeckDetails> CreateDeckAsync(string name) => Reject<DeckDetails>();
        public Task<DeckDetails> EditDeckAsync(string id, long revision, IReadOnlyList<DeckEdit> edits) => Reject<DeckDetails>();
        public Task<DeckDetails> RenameDeckAsync(string id, long revision, string name) => Reject<DeckDetails>();
        public Task<DeckDetails> DuplicateDeckAsync(string id, long revision, string name) => Reject<DeckDetails>();
        public Task<DeckDetails> UndoDeckAsync(string id, long revision) => Reject<DeckDetails>();
        public Task<DeckDetails> RedoDeckAsync(string id, long revision) => Reject<DeckDetails>();
        public Task<DeckDetails> SaveDeckAsync(string id, long revision) => Reject<DeckDetails>();
        public Task<string> ExportDeckAsync(string id, long revision) => Reject<string>();
        public Task<DeckDetails> SetCommandersAsync(string id, long revision, IReadOnlyList<string> ids) => Reject<DeckDetails>();
        public Task<SoloSetup> PrepareSoloAsync(string id) => Reject<SoloSetup>();
        public Task StartSoloAsync(string id, IReadOnlyList<string> opponents) => Reject();
        public Task<Lobby> HostAsync(int players, bool forwarding) => Reject<Lobby>();
        public Task<Lobby> JoinAsync(string address) => Reject<Lobby>();
        public Task<Lobby> LobbyAsync() => Reject<Lobby>();
        public Task SelectDeckAsync(string id) => Reject();
        public Task SetReadyAsync(bool ready) => Reject();
        public Task StartAsync() => Reject();
        public Task ReturnToLobbyAsync() => Reject();
        public Task LeaveAsync() => Reject();
        public Task ExecuteAsync(GameCommand reply) => Reject();
        public Task ConcedeAsync(string id) => Reject();
    }
}
