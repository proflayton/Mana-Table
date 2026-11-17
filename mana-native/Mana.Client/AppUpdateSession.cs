using System.Text.Json;

namespace Mana.Client;

public enum AppUpdatePhase { Idle, Checking, Available, Downloading, Preparing, Cancelling, Ready, Verifying, Installing, Closed }
public enum AppUpdateFailure { None, Check, Download, Verification, Installation, Preference, Location, Blocked }

public sealed record AppUpdateView(AppUpdatePhase Phase = AppUpdatePhase.Idle, long Generation = 0,
    AppRelease? Release = null, PreparedUpdate? Prepared = null, bool Checked = false,
    long BytesReceived = 0, long TotalBytes = 0, bool FullFallback = false,
    AppUpdateFailure Failure = AppUpdateFailure.None, string Error = "", string Notice = "", bool RestartDeferred = false,
    bool InstallerLaunched = false)
{
    public bool Checking => Phase == AppUpdatePhase.Checking;
    public bool Downloading => Phase is AppUpdatePhase.Downloading or AppUpdatePhase.Preparing or AppUpdatePhase.Cancelling;
    public bool Verifying => Phase == AppUpdatePhase.Verifying;
    public bool Busy => Checking || Downloading || Verifying;
}

/// <summary>
/// Composition owns one workflow through view retirement and actual provider-task
/// settlement. Its mailbox is independent of the rules session. Only the current
/// view can submit commands; one composition pump applies returned results.
/// </summary>
public sealed class AppUpdateSession
{
    private readonly IAppUpdates? service;
    private readonly string profile;
    private readonly Action<PreparedUpdate>? install;
    private readonly Action installed;
    private readonly object stateGate = new(), mailboxGate = new();
    private readonly string identity = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? cancellation;
    private Task request = Task.CompletedTask;
    private Task? closeTask;
    private long requestGeneration, roleGeneration;
    private volatile bool closed;
    private bool automaticChecked, restartAcknowledged, pumping;
    private int pumpThread;
    private AppUpdateViewBinding? current;
    private OwnerCompletion? completion;
    private ProgressSample? progress;
    private TaskCompletionSource queuedWork = Signal();
    private sealed record OwnerCompletion(long Generation, Action<AppUpdateInstallContext> Apply);
    private sealed record ProgressSample(long Generation, UpdateDownloadProgress Value);
    private sealed record Preferences(bool CheckOnStartup = true);
    private Preferences preferences = new();

    // Program disposes the borrowed service after the window joins this owner.
    public AppUpdateSession(IAppUpdates? service, string profile,
        Action<PreparedUpdate>? install = null, Action? installed = null)
    {
        this.service = service; this.profile = profile;
        this.install = install; this.installed = installed ?? (() => { });
        try {
            string path = Path.Combine(profile, "update-preferences.json");
            if (File.Exists(path)) preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new();
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public AppUpdateView View { get; private set; } = new();
    internal AppBuild? Build => service?.Build;
    internal bool CheckOnStartup => preferences.CheckOnStartup;
    internal bool CanInstall => install != null && !closed;
    private bool CanCheck => !closed && Build?.CanCheck == true && !View.Busy && View.Phase != AppUpdatePhase.Installing;

    public AppUpdateViewBinding AttachView()
    {
        lock (stateGate) {
            ObjectDisposedException.ThrowIf(closed, this);
            if (current != null) throw new InvalidOperationException("Retire the current update view before attaching another.");
            return current = new(this, identity + ":" + ++roleGeneration);
        }
    }
    internal bool IsCurrent(AppUpdateViewBinding? view) => !closed && view != null && ReferenceEquals(current, view);
    internal bool MayCheck(AppUpdateViewBinding view) { lock (stateGate) return IsCurrent(view) && CanCheck; }
    internal void Detach(AppUpdateViewBinding view)
    {
        lock (stateGate) if (ReferenceEquals(current, view)) current = null;
    }

    /// <summary>Passive notification only. Each waiter cancels independently;
    /// waking never pumps work, changes the view, or grants a command.</summary>
    public Task WaitForQueuedWorkAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (mailboxGate) return closed || completion != null || progress != null
            ? Task.CompletedTask : queuedWork.Task.WaitAsync(cancellationToken);
    }

    // Composition calls once after the current application's rules/lobby/input
    // update, with that application's fresh blockers. No view means no install.
    public void Pump(AppUpdateViewBinding? view, AppUpdateInstallContext context)
    {
        lock (stateGate) {
            if (closed) return;
            if (pumping) throw new InvalidOperationException("The update owner cannot be pumped recursively.");
            pumping = true; pumpThread = Environment.CurrentManagedThreadId;
            try {
                context = IsCurrent(view) ? context : AppUpdateInstallContext.NoView;
                OwnerCompletion? returned; ProgressSample? latest;
                lock (mailboxGate) {
                    returned = completion; completion = null;
                    latest = progress; progress = null;
                    if (returned != null || latest != null) queuedWork = Signal();
                }
                if (latest?.Generation == requestGeneration) ApplyProgress(latest.Value);
                // No callback runs under mailboxGate. Returned work contains
                // owner code/composition handoff, never a borrowed Game callback.
                if (returned?.Generation == requestGeneration) {
                    try { returned.Apply(context); }
                    catch (Exception error) {
                        Fail(AppUpdateFailure.Installation, "The update handoff could not finish. " + error.Message,
                            closed ? AppUpdatePhase.Closed : View.InstallerLaunched ? AppUpdatePhase.Installing : Settled(View.Release, View.Prepared));
                    }
                }
                if (View.RestartDeferred && context.Blocker.Length == 0)
                    Change(View with { Notice = "", RestartDeferred = false });
                if (!automaticChecked && context.StartupAllowed && CheckOnStartup && Build?.CanCheck == true && IsCurrent(view)) {
                    automaticChecked = true; Check(view!);
                }
                if (!restartAcknowledged && context.RestartedAfterUpdate && Build?.CanCheck == true && IsCurrent(view)) {
                    restartAcknowledged = true;
                    Change(View with { Notice = "Updated to " + Build.Version + "." });
                }
            } finally { pumping = false; pumpThread = 0; }
        }
    }

    internal void ToggleStartupCheck(AppUpdateViewBinding view)
    {
        lock (stateGate) {
            if (!IsCurrent(view)) return;
            preferences = preferences with { CheckOnStartup = !preferences.CheckOnStartup };
            try { File.WriteAllText(Path.Combine(profile, "update-preferences.json"), JsonSerializer.Serialize(preferences)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                Fail(AppUpdateFailure.Preference, "Couldn't save the update preference. " + error.Message);
            }
        }
    }

    internal bool Check(AppUpdateViewBinding view)
    {
        lock (stateGate) {
            if (!IsCurrent(view) || !CanCheck || service == null) return false;
            Change(View with { Phase = AppUpdatePhase.Checking, Failure = AppUpdateFailure.None, Error = "", Notice = "", RestartDeferred = false });
            Start(async token => {
                var release = await service.CheckAsync(token).ConfigureAwait(false);
                var recovered = release == null ? null : await service.GetPreparedAsync(release, token).ConfigureAwait(false);
                if (recovered != null) RequirePrepared(release!, recovered);
                return (release, recovered);
            }, (result, _) => {
                var prepared = result.recovered ?? (View.Prepared?.Release == result.release ? View.Prepared : null);
                Change(View with { Phase = Settled(result.release, prepared), Release = result.release, Prepared = prepared, Checked = true });
            }, error => Fail(AppUpdateFailure.Check, "Couldn't check for updates. " + error.Message, Settled(View.Release, View.Prepared)));
            return true;
        }
    }

    internal bool Download(AppUpdateViewBinding view)
    {
        lock (stateGate) {
            if (!IsCurrent(view) || service == null || View.Phase != AppUpdatePhase.Available || View.Release is not { } release) return false;
            Change(View with { Phase = AppUpdatePhase.Downloading, Prepared = null, Failure = AppUpdateFailure.None,
                Error = "", Notice = "", RestartDeferred = false, BytesReceived = 0, TotalBytes = release.SizeBytes, FullFallback = false });
            long generation = requestGeneration + 1;
            var reporting = new InlineProgress(value => PostProgress(generation, value));
            Start(async token => {
                var ready = await service.DownloadAsync(release, Path.Combine(profile, "updates"), reporting, token).ConfigureAwait(false);
                RequirePrepared(release, ready);
                return ready;
            }, (ready, _) => Change(View with { Phase = AppUpdatePhase.Ready, Prepared = ready }),
                error => Fail(AppUpdateFailure.Download, "Couldn't download the update. " + error.Message, AppUpdatePhase.Available),
                () => Change(View with { Phase = AppUpdatePhase.Available, Notice = "Update cancelled. Download again whenever you're ready." }));
            return true;
        }
    }

    internal bool CancelDownload(AppUpdateViewBinding view)
    {
        lock (stateGate) {
            if (!IsCurrent(view) || !View.Downloading || View.Phase == AppUpdatePhase.Cancelling) return false;
            Change(View with { Phase = AppUpdatePhase.Cancelling, Notice = "Cancelling update..." });
            try { cancellation!.Cancel(); }
            catch (Exception error) { Fail(AppUpdateFailure.Download, "Update cancellation notification failed. " + error.Message); }
            return true;
        }
    }

    internal bool Install(AppUpdateViewBinding view, AppUpdateInstallContext context)
    {
        lock (stateGate) {
            if (!IsCurrent(view) || service == null || install == null || View.Phase != AppUpdatePhase.Ready || View.Prepared is not { } ready) return false;
            if (context.Blocker.Length > 0) { Fail(AppUpdateFailure.Blocked, context.Blocker); return false; }
            Change(View with { Phase = AppUpdatePhase.Verifying, Failure = AppUpdateFailure.None, Error = "", Notice = "", RestartDeferred = false });
            Start(async token => { await service.ValidateAsync(ready, token).ConfigureAwait(false); return ready; }, (verified, fresh) => {
                if (!IsCurrent(view)) {
                    Change(View with { Phase = AppUpdatePhase.Ready, Notice = "Your update is ready. Restart when you're ready.", RestartDeferred = true });
                    return;
                }
                if (fresh.Blocker.Length > 0) {
                    Change(View with { Phase = AppUpdatePhase.Ready, Notice = fresh.Blocker, RestartDeferred = true });
                    return;
                }
                try {
                    Change(View with { Phase = AppUpdatePhase.Installing });
                    install(verified);
                } catch (Exception error) {
                    Fail(AppUpdateFailure.Installation, "Couldn't start the update. " + error.Message, AppUpdatePhase.Ready);
                    return;
                }
                Change(View with { InstallerLaunched = true });
                installed();
            }, error => {
                Change(View with { Prepared = null });
                Fail(AppUpdateFailure.Verification, "The prepared update could not be verified. Download it again. " + error.Message, AppUpdatePhase.Available);
            });
            return true;
        }
    }

    internal void ReportLocationFailure(AppUpdateViewBinding view, Exception error)
    {
        lock (stateGate) if (IsCurrent(view)) Fail(AppUpdateFailure.Location, "Couldn't open that location. " + error.Message);
    }

    public Task CloseAsync()
    {
        lock (stateGate) {
            if (closeTask != null) return closeTask;
            closed = true; current = null;
            Change(new(AppUpdatePhase.Closed, InstallerLaunched: View.InstallerLaunched));
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            closeTask = done.Task;
            var actual = request; var stopping = cancellation;
            TaskCompletionSource notification;
            lock (mailboxGate) { notification = queuedWork; completion = null; progress = null; }
            notification.TrySetResult();
            _ = Task.Run(async () => {
                Exception? failure = null;
                try { stopping?.Cancel(); } catch (Exception error) { failure = error; }
                try { await actual.ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
                finally {
                    lock (stateGate) {
                        lock (mailboxGate) { completion = null; progress = null; }
                        cancellation = null;
                        try { stopping?.Dispose(); } catch (Exception error) { failure ??= error; }
                        if (failure != null) Fail(AppUpdateFailure.Download, "Update shutdown cleanup reported a failure. " + failure.Message, AppUpdatePhase.Closed);
                    }
                    done.TrySetResult();
                }
            });
            return closeTask;
        }
    }
    public void Close()
    {
        var closing = CloseAsync();
        if (!closing.IsCompleted && pumping && pumpThread == Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Use CloseAsync from an update handoff callback; the owner cannot synchronously join itself.");
        closing.GetAwaiter().GetResult();
    }

    private void Start<T>(Func<CancellationToken, Task<T>> work, Action<T, AppUpdateInstallContext> completed,
        Action<Exception> failed, Action? cancelled = null)
    {
        cancellation?.Dispose(); cancellation = new();
        var token = cancellation.Token;
        long generation = ++requestGeneration;
        request = Task.Run(async () => {
            Action<AppUpdateInstallContext> apply;
            try {
                var result = await work(token).ConfigureAwait(false);
                apply = context => { if (token.IsCancellationRequested) cancelled?.Invoke(); else completed(result, context); };
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                apply = _ => cancelled?.Invoke();
            } catch (Exception error) {
                apply = _ => { if (token.IsCancellationRequested) cancelled?.Invoke(); else failed(error); };
            }
            PostCompletion(generation, apply);
        });
    }
    private void PostCompletion(long generation, Action<AppUpdateInstallContext> apply)
    {
        TaskCompletionSource notification;
        lock (mailboxGate) {
            if (closed || generation != Interlocked.Read(ref requestGeneration)) return;
            completion = new(generation, apply); notification = queuedWork;
        }
        notification.TrySetResult();
    }
    private void PostProgress(long generation, UpdateDownloadProgress value)
    {
        TaskCompletionSource notification;
        lock (mailboxGate) {
            if (closed || generation != Interlocked.Read(ref requestGeneration)) return;
            progress = new(generation, value); notification = queuedWork;
        }
        notification.TrySetResult();
    }
    private void ApplyProgress(UpdateDownloadProgress value)
    {
        if (!View.Downloading || View.Phase == AppUpdatePhase.Cancelling) return;
        var phase = value.Stage == UpdatePreparationStage.Preparing ? AppUpdatePhase.Preparing : AppUpdatePhase.Downloading;
        var next = View with { Phase = phase, BytesReceived = value.BytesReceived, TotalBytes = value.TotalBytes, FullFallback = value.FullFallback };
        if (phase != View.Phase) Change(next);
        else View = next;
    }
    private void Change(AppUpdateView next) => View = next with { Generation = View.Generation + 1 };
    private void Fail(AppUpdateFailure failure, string error, AppUpdatePhase? phase = null)
        => Change(View with { Phase = phase ?? View.Phase, Failure = failure, Error = error });
    private static AppUpdatePhase Settled(AppRelease? release, PreparedUpdate? prepared)
        => prepared != null ? AppUpdatePhase.Ready : release != null ? AppUpdatePhase.Available : AppUpdatePhase.Idle;
    private static void RequirePrepared(AppRelease release, PreparedUpdate prepared)
    {
        if (prepared.Release != release || string.IsNullOrWhiteSpace(prepared.InstallerPath))
            throw new InvalidDataException("The prepared update does not match the selected release.");
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class InlineProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }
}
