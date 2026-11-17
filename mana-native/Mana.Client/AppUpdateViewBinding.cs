namespace Mana.Client;

/// <summary>Fresh current-view facts supplied by composition, never a callback
/// into a replaceable Game. A default/no-view context cannot permit restart.</summary>
public readonly record struct AppUpdateInstallContext
{
    private readonly string? blocker;
    public string Blocker => blocker ?? "There is no active update view.";
    public bool StartupAllowed { get; }
    public bool RestartedAfterUpdate { get; }
    public AppUpdateInstallContext(string blocker, bool startupAllowed = false, bool restartedAfterUpdate = false)
        => (this.blocker, StartupAllowed, RestartedAfterUpdate) = (blocker ?? throw new ArgumentNullException(nameof(blocker)), startupAllowed, restartedAfterUpdate);
    public static AppUpdateInstallContext NoView => default;
}

/// <summary>One revocable view over a composition-owned update workflow.
/// Retiring it never cancels accepted discovery, downloads or preparation.</summary>
public sealed class AppUpdateViewBinding : IDisposable
{
    private readonly AppUpdateSession? owner;
    private static readonly AppUpdateView disabledView = new();
    internal AppUpdateViewBinding(AppUpdateSession owner, string scope) => (this.owner, Scope) = (owner, scope);
    private AppUpdateViewBinding() { Scope = "updates-disabled"; }
    public static AppUpdateViewBinding Disabled { get; } = new();
    public string Scope { get; }
    public bool Current => owner?.IsCurrent(this) == true;
    public AppUpdateView View => owner?.View ?? disabledView;
    public AppBuild? Build => owner?.Build;
    public bool CheckOnStartup => owner?.CheckOnStartup ?? false;
    public bool CanCheck => owner?.MayCheck(this) == true;
    public bool CanInstall => Current && owner!.CanInstall;
    public bool Check() => owner?.Check(this) == true;
    public bool Download() => owner?.Download(this) == true;
    public bool CancelDownload() => owner?.CancelDownload(this) == true;
    public bool Install(AppUpdateInstallContext context) => owner?.Install(this, context) == true;
    public void ToggleStartupCheck() => owner?.ToggleStartupCheck(this);
    public void ReportLocationFailure(Exception error) => owner?.ReportLocationFailure(this, error);
    public void Dispose() => owner?.Detach(this);
}
