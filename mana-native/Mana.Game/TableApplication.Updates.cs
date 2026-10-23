using Mana.Client;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private AppUpdateSession updateSession = null!;
    private void HandleUpdateIntent(ShellIntent.Update intent)
    {
        switch (intent) {
            case ShellIntent.OpenUpdates: OpenAppUpdates(); break;
            case ShellIntent.CloseUpdates: CloseAppUpdates(); break;
            case ShellIntent.CheckUpdates: CheckAppUpdates(); break;
            case ShellIntent.DownloadUpdate: DownloadAppUpdate(); break;
            case ShellIntent.CancelUpdateDownload: CancelAppUpdateDownload(); break;
            case ShellIntent.InstallUpdate: InstallAppUpdate(); break;
            case ShellIntent.OpenUpdateLocation location: OpenUpdateLocation(location.Folder); break;
            case ShellIntent.ToggleStartupUpdateCheck: ToggleStartupUpdateCheck(); break;
        }
    }
    private static readonly Uri AppReleasesPage = new("https://github.com/proflayton/Mana-Table/releases");
    private bool updatesOpen;
    private int updateModalGeneration;
    private AppUpdateView UpdateView => updateSession.View;
    private string UpdateScope => $"updates:{updatesOpen}:{updateModalGeneration}:{UpdateView.Generation}";
    private string CurrentAppVersion => updateSession.Build?.Version is { Length: > 0 } version ? version : "Development build";
    private bool AutomaticUpdateCheckAllowed => updateSession.CheckOnStartup && updateSession.Build?.CanCheck == true && fixture == null && initialState == null
        && allowAutomaticChecks && !Automated && loaded && !StartupVisible && !closing;
    private void InitializeAppUpdates(IAppUpdates? service, Action<PreparedUpdate>? install)
        => updateSession = new(service, profile, session.Post, UpdateInstallBlocker, install, RequestExit);
    private void UpdateAppUpdates() => updateSession.Tick(
        fixture == null && initialState == null && allowAutomaticChecks && !Automated && loaded && !StartupVisible && !closing,
        restartedAfterUpdate && loaded && !StartupVisible && !closing);
    private void ToggleStartupUpdateCheck() => updateSession.ToggleStartupCheck();
    private void OpenAppUpdates()
    {
        if (!loaded || ActivityOpen) return;
        updatesOpen = true; updateModalGeneration++; ClearExperienceInput();
    }
    private void CloseAppUpdates() { updatesOpen = false; updateModalGeneration++; pressed = null; }
    private void CheckAppUpdates() => updateSession.Check();
    private void DownloadAppUpdate() => updateSession.Download();
    private void CancelAppUpdateDownload() => updateSession.CancelDownload();
    private string UpdateInstallBlocker()
    {
        if (match != null) return "Return to setup from your game before updating. Your update is ready.";
        if (Connected) return "Leave your friends table before updating. Your update is ready.";
        if (!loaded || busy || ActivityOpen) return "Wait for the current action to finish before updating.";
        if (selectedDeckDetails?.SaveError.Length > 0) return "Save your deck successfully in Build before updating.";
        if (dragging || textFocus || chatFocus || chatOpen && chatDraft.Length > 0 || setupModal.Length > 0)
            return "Finish or close your current input before updating.";
        if (CaptureMode) return "Finish the current capture before updating.";
        return "";
    }
    private void InstallAppUpdate() => updateSession.Install();
    private void OpenUpdateLocation(bool folder = false)
    {
        try {
            string target = folder && UpdateView.Prepared != null ? Path.GetDirectoryName(UpdateView.Prepared.InstallerPath)!
                : (UpdateView.Release?.PageUrl ?? AppReleasesPage).AbsoluteUri;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        } catch (Exception error) { updateSession.ReportLocationFailure(error); }
    }
    private void CloseUpdateRequests() => updateSession.Close();
}
