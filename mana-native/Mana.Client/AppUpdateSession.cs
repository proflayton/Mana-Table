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
/// Owns one update workflow and its asynchronous lifetime. Commands and posted
/// completions run on the caller's thread. Discovery never downloads; a blocked
/// restart always needs another explicit Install request after it becomes safe.
/// </summary>
public sealed class AppUpdateSession
{
    private readonly IAppUpdates? service;
    private readonly string profile;
    private readonly Action<Action> post;
    private readonly Action<PreparedUpdate>? install;
    private readonly Func<string> installBlocker;
    private readonly Action installed;
    private readonly object completionGate = new();
    private CancellationTokenSource? cancellation;
    private Task request = Task.CompletedTask;
    private long requestGeneration;
    private bool closed, automaticChecked, restartAcknowledged;
    private sealed record Preferences(bool CheckOnStartup = true);
    private Preferences preferences = new();

    public AppUpdateSession(IAppUpdates? service, string profile, Action<Action> post,
        Func<string> installBlocker, Action<PreparedUpdate>? install = null, Action? installed = null)
    {
        this.service = service; this.profile = profile; this.post = post;
        this.installBlocker = installBlocker; this.install = install; this.installed = installed ?? (() => { });
        try {
            string path = Path.Combine(profile, "update-preferences.json");
            if (File.Exists(path)) preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new();
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public AppBuild? Build => service?.Build;
    public AppUpdateView View { get; private set; } = new();
    public bool CheckOnStartup => preferences.CheckOnStartup;
    public bool CanInstall => install != null;
    public bool CanCheck => !closed && Build?.CanCheck == true && !View.Busy && View.Phase != AppUpdatePhase.Installing;

    public void Tick(bool startupAllowed, bool restartedAfterUpdate)
    {
        if (closed) return;
        if (View.RestartDeferred && installBlocker().Length == 0) Change(View with { Notice = "", RestartDeferred = false });
        if (!automaticChecked && startupAllowed && CheckOnStartup && Build?.CanCheck == true) {
            automaticChecked = true; Check();
        }
        if (!restartAcknowledged && restartedAfterUpdate && Build?.CanCheck == true) {
            restartAcknowledged = true;
            Change(View with { Notice = "Updated to " + Build.Version + "." });
        }
    }

    public void ToggleStartupCheck()
    {
        if (closed) return;
        preferences = preferences with { CheckOnStartup = !preferences.CheckOnStartup };
        try { File.WriteAllText(Path.Combine(profile, "update-preferences.json"), JsonSerializer.Serialize(preferences)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            Fail(AppUpdateFailure.Preference, "Couldn't save the update preference. " + error.Message);
        }
    }

    public bool Check()
    {
        if (!CanCheck || service == null) return false;
        Change(View with { Phase = AppUpdatePhase.Checking, Failure = AppUpdateFailure.None, Error = "", Notice = "", RestartDeferred = false });
        Start(async token => {
            var release = await service.CheckAsync(token).ConfigureAwait(false);
            var recovered = release == null ? null : await service.GetPreparedAsync(release, token).ConfigureAwait(false);
            if (recovered != null) RequirePrepared(release!, recovered);
            return (release, recovered);
        }, result => {
            var prepared = result.recovered ?? (View.Prepared?.Release == result.release ? View.Prepared : null);
            Change(View with { Phase = Settled(result.release, prepared), Release = result.release, Prepared = prepared, Checked = true });
        }, error => Fail(AppUpdateFailure.Check, "Couldn't check for updates. " + error.Message, Settled(View.Release, View.Prepared)));
        return true;
    }

    public bool Download()
    {
        if (closed || service == null || View.Phase != AppUpdatePhase.Available || View.Release is not { } release) return false;
        Change(View with { Phase = AppUpdatePhase.Downloading, Prepared = null, Failure = AppUpdateFailure.None,
            Error = "", Notice = "", RestartDeferred = false, BytesReceived = 0, TotalBytes = release.SizeBytes, FullFallback = false });
        long generation = requestGeneration + 1;
        var progress = new InlineProgress(value => Post(generation, () => {
            if (!View.Downloading || View.Phase == AppUpdatePhase.Cancelling) return;
            View = View with { Phase = value.Stage == UpdatePreparationStage.Preparing ? AppUpdatePhase.Preparing : AppUpdatePhase.Downloading,
                BytesReceived = value.BytesReceived, TotalBytes = value.TotalBytes, FullFallback = value.FullFallback };
        }));
        Start(async token => {
            var ready = await service.DownloadAsync(release, Path.Combine(profile, "updates"), progress, token).ConfigureAwait(false);
            RequirePrepared(release, ready);
            return ready;
        }, ready => Change(View with { Phase = AppUpdatePhase.Ready, Prepared = ready }),
            error => Fail(AppUpdateFailure.Download, "Couldn't download the update. " + error.Message, AppUpdatePhase.Available),
            () => Change(View with { Phase = AppUpdatePhase.Available, Notice = "Update cancelled. Download again whenever you're ready." }));
        return true;
    }

    public bool CancelDownload()
    {
        if (closed || !View.Downloading || View.Phase == AppUpdatePhase.Cancelling) return false;
        Change(View with { Phase = AppUpdatePhase.Cancelling, Notice = "Cancelling update..." });
        cancellation!.Cancel();
        return true;
    }

    public bool Install()
    {
        if (closed || service == null || install == null || View.Phase != AppUpdatePhase.Ready || View.Prepared is not { } ready) return false;
        string blocker = installBlocker();
        if (blocker.Length > 0) { Fail(AppUpdateFailure.Blocked, blocker); return false; }
        Change(View with { Phase = AppUpdatePhase.Verifying, Failure = AppUpdateFailure.None, Error = "", Notice = "", RestartDeferred = false });
        Start(async token => { await service.ValidateAsync(ready, token).ConfigureAwait(false); return ready; }, verified => {
            string changed = installBlocker();
            if (changed.Length > 0) {
                Change(View with { Phase = AppUpdatePhase.Ready, Notice = changed, RestartDeferred = true });
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

    public void ReportLocationFailure(Exception error)
    {
        if (!closed) Fail(AppUpdateFailure.Location, "Couldn't open that location. " + error.Message);
    }

    public void Close()
    {
        lock (completionGate) {
            if (closed) return;
            closed = true;
        }
        Change(new(AppUpdatePhase.Closed, InstallerLaunched: View.InstallerLaunched));
        cancellation?.Cancel();
        // Join the provider's real operation, rather than cancelling only our
        // wait: package writes and the owned native patcher must finish before
        // application shutdown can dispose their resources or exit the CLR.
        request.GetAwaiter().GetResult();
        cancellation?.Dispose();
    }

    private void Start<T>(Func<CancellationToken, Task<T>> work, Action<T> completed, Action<Exception> failed, Action? cancelled = null)
    {
        cancellation?.Dispose();
        cancellation = new();
        var token = cancellation.Token;
        long generation = ++requestGeneration;
        request = Task.Run(async () => {
            try {
                var result = await work(token).ConfigureAwait(false);
                Post(generation, () => { if (token.IsCancellationRequested) cancelled?.Invoke(); else completed(result); });
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                Post(generation, () => cancelled?.Invoke());
            } catch (Exception error) {
                Post(generation, () => { if (token.IsCancellationRequested) cancelled?.Invoke(); else failed(error); });
            }
        });
    }

    private void Post(long generation, Action completion)
    {
        lock (completionGate) {
            if (!closed) post(() => { if (!closed && generation == requestGeneration) completion(); });
        }
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
    private sealed class InlineProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }
}
