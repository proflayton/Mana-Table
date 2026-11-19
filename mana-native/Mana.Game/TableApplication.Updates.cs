using Mana.Client;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly AppUpdateViewBinding updateView;
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
    private AppUpdateView UpdateView => updateView.View;
    private string UpdateScope => $"updates:{updateView.Scope}:{updateView.Current}:{closing}:{updatesOpen}:{updateModalGeneration}:{UpdateView.Generation}";
    private string CurrentAppVersion => updateView.Build?.Version is { Length: > 0 } version ? version : "Development build";
    private bool UpdateStartupAllowed => fixture == null && initialState == null
        && allowAutomaticChecks && !Automated && loaded && !StartupVisible && !closing;
    private bool AutomaticUpdateCheckAllowed => updateView.CheckOnStartup && updateView.Build?.CanCheck == true && UpdateStartupAllowed;
    internal AppUpdateInstallContext ReadUpdateInstallContext() => new(UpdateInstallBlocker(), UpdateStartupAllowed,
        restartedAfterUpdate && loaded && !StartupVisible && !closing);
    private void ToggleStartupUpdateCheck() => updateView.ToggleStartupCheck();
    private void OpenAppUpdates()
    {
        if (!loaded || ActivityOpen) return;
        updatesOpen = true; updateModalGeneration++; ClearExperienceInput();
    }
    private void CloseAppUpdates() { updatesOpen = false; updateModalGeneration++; pressed = null; }
    private void CheckAppUpdates() => updateView.Check();
    private void DownloadAppUpdate() => updateView.Download();
    private void CancelAppUpdateDownload() => updateView.CancelDownload();
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
    private void InstallAppUpdate() => updateView.Install(ReadUpdateInstallContext());
    private void OpenUpdateLocation(bool folder = false)
    {
        try {
            string target = folder && UpdateView.Prepared != null ? Path.GetDirectoryName(UpdateView.Prepared.InstallerPath)!
                : (UpdateView.Release?.PageUrl ?? AppReleasesPage).AbsoluteUri;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        } catch (Exception error) { updateView.ReportLocationFailure(error); }
    }
}
