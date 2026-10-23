using System.Collections.Concurrent;
using Mana.Client;

internal static class AppUpdateSessionRegression
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string profile = Path.Combine(Path.GetTempPath(), "ManaTable-update-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try {
            await WorkflowAsync(profile, check);
            await FreshnessAsync(profile, check);
            await ClosedAsync(profile, check);
            await CloseJoinsPreparationAsync(profile, check);
            await PreferencesAsync(profile, check);
        } finally { Directory.Delete(profile, true); }
    }

    private static async Task WorkflowAsync(string profile, Action<bool, string> check)
    {
        var queue = new Completions(); var source = new Updates();
        int launches = 0, exits = 0; string blocker = ""; bool failLaunch = false;
        var session = new AppUpdateSession(source, profile, queue.Post, () => blocker, _ => {
            launches++; if (failLaunch) throw new IOException("Controlled launch failure");
        }, () => exits++);
        try {
            var idle = session.View;
            session.Tick(false, false);
            check(source.Checks == 0 && !session.Download() && !session.Install(), "An unopened updater cannot download or install without an offered release");
            session.Tick(true, true); session.Tick(true, true);
            await source.CheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(source.Checks == 1 && session.View.Checking && session.View.Notice.Contains("Updated to "), "Eligible startup discovery runs once and acknowledges the installed version");
            check(!session.Check() && !session.Download(), "A discovery request cannot overlap another update operation");
            source.CheckGate.SetResult(source.Release);
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Available);
            check(source.Downloads == 0 && idle.Phase == AppUpdatePhase.Idle && session.View.Checked,
                "Discovery publishes a new immutable view without downloading the available update");

            check(session.Download(), "Only explicit download intent starts preparation");
            await source.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            long generation = session.View.Generation;
            source.Progress!.Report(new(512, 1024) { Stage = UpdatePreparationStage.Preparing });
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Preparing);
            check(session.View.Generation == generation && session.View.BytesReceived == 512 && !session.Check() && !session.Install(),
                "Progress preserves control authority while preparation excludes checks and installation");
            source.DownloadGate.SetResult(source.Prepared);
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Ready);
            blocker = "Game in progress";
            check(!session.Install() && source.Validations == 0 && session.View.Failure == AppUpdateFailure.Blocked,
                "A live-game blocker rejects restart before verification or installer launch");
            blocker = "";
            check(session.Install(), "Explicit safe restart first verifies the prepared payload");
            await source.ValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            blocker = "A table opened during verification"; source.ValidationGate.SetResult();
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Ready);
            check(launches == 0 && session.View.RestartDeferred && session.View.Prepared == source.Prepared,
                "A blocker appearing during verification keeps the payload ready without launching");
            blocker = ""; session.Tick(false, false);
            check(launches == 0 && !session.View.RestartDeferred && session.View.Notice.Length == 0,
                "Clearing the blocker clears feedback but never automatically restarts");

            source.ResetValidation(); session.Install();
            await source.ValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.ValidationGate.SetException(new InvalidDataException("Controlled tampering"));
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Available);
            check(session.View.Prepared == null && session.View.Failure == AppUpdateFailure.Verification && launches == 0,
                "Failed final verification revokes the prepared payload and requires another download");
            source.ResetDownload(); session.Download();
            await source.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.DownloadGate.SetResult(source.Prepared);
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Ready);
            failLaunch = true; source.ResetValidation(); session.Install();
            await source.ValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); source.ValidationGate.SetResult();
            await queue.Until(() => session.View.Failure == AppUpdateFailure.Installation);
            check(session.View.Phase == AppUpdatePhase.Ready && session.View.Prepared != null && !session.View.InstallerLaunched && launches == 1 && exits == 0,
                "A failed installer launch leaves the application and prepared retry available");
            failLaunch = false; source.ResetValidation(); session.Install();
            await source.ValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); source.ValidationGate.SetResult();
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Installing);
            check(launches == 2 && exits == 1 && !session.Install() && !session.Check() && !session.Download(),
                "Successful explicit installation requests exit once and rejects further update operations");
            session.Close();
            check(session.View.Phase == AppUpdatePhase.Closed && session.View.InstallerLaunched,
                "A successfully launched installer remains observable after application shutdown retires update authority");
        } finally { source.CompleteOutstanding(); session.Close(); }
    }

    private static async Task FreshnessAsync(string profile, Action<bool, string> check)
    {
        var queue = new Completions(); var source = new Updates();
        var session = new AppUpdateSession(source, profile, queue.Post, () => "");
        try {
            session.Check(); source.CheckGate.SetResult(source.Release);
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Available);
            session.Download(); await source.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oldProgress = source.Progress!;
            source.DownloadGate.SetResult(source.Prepared);
            await queue.Pending();
            check(session.CancelDownload() && session.View.Phase == AppUpdatePhase.Cancelling,
                "Cancellation remains available while a completed download awaits its presentation turn");
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Available);
            check(session.View.Prepared == null && session.View.Notice.Contains("cancelled"),
                "Cancellation wins over a queued successful result even when the service ignores cancellation");
            source.ResetDownload(); session.Download();
            await source.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            oldProgress.Report(new(999, 1000));
            source.Progress!.Report(new(42, 1000));
            await queue.Until(() => session.View.BytesReceived == 42);
            check(session.View.BytesReceived == 42, "Late progress from a cancelled operation cannot overwrite a retry's progress");
            source.DownloadGate.SetResult(source.Prepared with { Release = source.Release with { Sha256 = new('b', 64) } });
            await queue.Until(() => session.View.Failure == AppUpdateFailure.Download);
            check(session.View.Prepared == null && session.View.Phase == AppUpdatePhase.Available,
                "A provider cannot prepare a different payload identity than the release the player selected");

            source.ResetCheck(); source.Recovered = source.Prepared; session.Check(); source.CheckGate.SetResult(source.Release);
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Ready);
            check(source.Downloads == 2 && session.View.Prepared == source.Prepared, "A check can recover a verified payload without another download");
            source.ResetCheck(); session.Check(); source.CheckGate.SetException(new HttpRequestException("Controlled offline response"));
            await queue.Until(() => session.View.Failure == AppUpdateFailure.Check);
            check(session.View.Phase == AppUpdatePhase.Ready && session.View.Prepared == source.Prepared,
                "An offline refresh retains an already prepared payload and its explicit restart path");
            source.ResetCheck(); source.Recovered = null; session.Check(); source.CheckGate.SetResult(source.Release with { Sha256 = new('c', 64) });
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Available);
            check(session.View.Prepared == null, "A release identity change at the same version revokes the recovered payload");
        } finally { source.CompleteOutstanding(); session.Close(); }
    }

    private static async Task ClosedAsync(string profile, Action<bool, string> check)
    {
        var queue = new Completions(); var source = new Updates(); int launches = 0;
        var session = new AppUpdateSession(source, profile, queue.Post, () => "", _ => launches++);
        session.Check(); source.CheckGate.SetResult(source.Release); await queue.Until(() => session.View.Phase == AppUpdatePhase.Available);
        session.Download(); await source.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.DownloadGate.SetResult(source.Prepared); await queue.Until(() => session.View.Phase == AppUpdatePhase.Ready);
        session.Install(); await source.ValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.ValidationGate.SetResult(); await queue.Pending();
        session.Close(); queue.Drain();
        session.Tick(true, true); session.Close();
        check(launches == 0 && !session.View.InstallerLaunched && session.View.Phase == AppUpdatePhase.Closed && session.View.Prepared == null
            && !session.Install() && !session.Check() && !session.Download(),
            "Closing retires queued validation completions and permanently revokes update authority");
    }

    private static async Task PreferencesAsync(string profile, Action<bool, string> check)
    {
        var queue = new Completions(); var source = new Updates();
        var session = new AppUpdateSession(source, profile, queue.Post, () => "");
        session.ToggleStartupCheck(); session.Close();
        var reopened = new AppUpdateSession(source, profile, queue.Post, () => "");
        try {
            reopened.Tick(true, false);
            check(!reopened.CheckOnStartup && source.Checks == 0, "The existing startup preference survives session replacement and prevents automatic discovery");
            check(reopened.Check(), "Opting out of automatic discovery preserves explicit update checks");
            source.CheckGate.SetResult(null); await queue.Until(() => !reopened.View.Checking);
            check(reopened.View.Checked && reopened.View.Phase == AppUpdatePhase.Idle, "A successful empty check settles to current without offering a download");
        } finally { source.CompleteOutstanding(); reopened.Close(); }
        var development = new Updates { Build = new("", false, "") };
        var disabled = new AppUpdateSession(development, profile, queue.Post, () => "");
        disabled.Tick(true, false);
        check(!disabled.Check() && development.Checks == 0, "Unknown or development builds never invoke the update provider");
        disabled.Close();
    }

    private static async Task CloseJoinsPreparationAsync(string profile, Action<bool, string> check)
    {
        var queue = new Completions(); var source = new Updates { CancelAfterDownloadCleanup = true };
        var session = new AppUpdateSession(source, profile, queue.Post, () => "");
        session.Check(); source.CheckGate.SetResult(source.Release); await queue.Until(() => session.View.Phase == AppUpdatePhase.Available);
        session.Download(); await source.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = Task.Run(session.Close);
        try {
            await source.DownloadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(!closing.IsCompleted, "Shutdown requests cancellation but continues to own unfinished package preparation");
        } finally { source.DownloadGate.TrySetResult(source.Prepared); await closing.WaitAsync(TimeSpan.FromSeconds(5)); }
        queue.Drain();
        check(source.DownloadFinished && session.View.Phase == AppUpdatePhase.Closed,
            "Shutdown joins actual provider cleanup instead of abandoning writes after a cancelled wait");
    }

    private sealed class Completions
    {
        private readonly ConcurrentQueue<Action> queue = new();
        private readonly SemaphoreSlim posted = new(0);
        public void Post(Action action) { queue.Enqueue(action); posted.Release(); }
        public void Drain() { while (queue.TryDequeue(out var action)) action(); }
        public async Task Pending()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (queue.IsEmpty) await posted.WaitAsync(timeout.Token);
        }
        public async Task Until(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true) { Drain(); if (predicate()) return; await posted.WaitAsync(timeout.Token); }
        }
    }

    private sealed class Updates : IAppUpdates
    {
        public AppBuild Build { get; set; } = new("0.2.9", true, new('a', 40));
        public AppRelease Release { get; } = new(new(0, 2, 10), new("https://example.invalid/release"), new("https://example.invalid/update"), 1024, new('a', 64));
        public PreparedUpdate Prepared => new(Release, "controlled-update.exe");
        public PreparedUpdate? Recovered;
        public int Checks, Downloads, Validations;
        public TaskCompletionSource<AppRelease?> CheckGate = Gate<AppRelease?>();
        public TaskCompletionSource<PreparedUpdate> DownloadGate = Gate<PreparedUpdate>();
        public TaskCompletionSource ValidationGate = Gate();
        public TaskCompletionSource CheckStarted = Gate(), DownloadStarted = Gate(), ValidationStarted = Gate();
        public TaskCompletionSource DownloadCancelled = Gate();
        public bool DownloadFinished;
        public bool CancelAfterDownloadCleanup;
        public IProgress<UpdateDownloadProgress>? Progress;
        public async Task<AppRelease?> CheckAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Checks); var task = CheckGate.Task; CheckStarted.TrySetResult(); return await task; }
        public Task<PreparedUpdate?> GetPreparedAsync(AppRelease release, CancellationToken cancellationToken = default) => Task.FromResult(Recovered);
        public async Task<PreparedUpdate> DownloadAsync(AppRelease release, string cacheDirectory, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Downloads); Progress = progress; var task = DownloadGate.Task;
            using var registration = cancellationToken.Register(() => DownloadCancelled.TrySetResult());
            DownloadStarted.TrySetResult();
            try {
                var prepared = await task;
                if (CancelAfterDownloadCleanup) cancellationToken.ThrowIfCancellationRequested();
                return prepared;
            } finally { DownloadFinished = true; }
        }
        public async Task ValidateAsync(PreparedUpdate update, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Validations); var task = ValidationGate.Task; ValidationStarted.TrySetResult(); await task; }
        public void ResetCheck() { CheckGate = Gate<AppRelease?>(); CheckStarted = Gate(); }
        public void ResetDownload() { DownloadGate = Gate<PreparedUpdate>(); DownloadStarted = Gate(); }
        public void ResetValidation() { ValidationGate = Gate(); ValidationStarted = Gate(); }
        public void CompleteOutstanding() { CheckGate.TrySetCanceled(); DownloadGate.TrySetCanceled(); ValidationGate.TrySetCanceled(); }
    }
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
