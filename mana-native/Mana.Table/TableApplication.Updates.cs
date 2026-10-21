using Mana.Client;
using System.Text.Json;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly IAppUpdates? appUpdates;
    private readonly Action<PreparedUpdate>? installUpdate;
    private static readonly Uri AppReleasesPage = new("https://github.com/proflayton/Mana-Table/releases");
    private bool updatesOpen, updateAutoChecked, updateChecking, updateDownloading, updateValidating, updateInstallerLaunched;
    private bool updateChecked, updateInstallDeferred;
    private string updateError = "", updateNotice = "";
    private AppRelease? availableUpdate;
    private PreparedUpdate? preparedUpdate;
    private long updateBytes, updateTotal;
    private int updateCheckGeneration, updateDownloadGeneration, updateModalGeneration;
    private CancellationTokenSource? updateCheckCancellation, updateDownloadCancellation;
    private readonly object updateCompletionGate = new();
    private bool updateRequestsClosed;
    private sealed record UpdatePreferences(bool CheckOnStartup = true);
    private UpdatePreferences updatePreferences = new();
    private string UpdateScope => $"updates:{updatesOpen}:{updateModalGeneration}";
    private void PostUpdateCompletion(Action action)
    {
        lock (updateCompletionGate) {
            if (!updateRequestsClosed) updates.Enqueue(() => { if (!closing) action(); });
        }
    }
    private string CurrentAppVersion => appUpdates?.Build.Version is { Length: > 0 } version ? version : "Development build";
    private bool AutomaticUpdateCheckAllowed => updatePreferences.CheckOnStartup && appUpdates?.Build.CanCheck == true && fixture == null && initialState == null
        && allowAutomaticChecks && !Automated && loaded && !StartupVisible && !closing;
    private void UpdateAppUpdates()
    {
        if (updateInstallDeferred && UpdateInstallBlocker().Length == 0) { updateNotice = ""; updateInstallDeferred = false; }
        if (!updateAutoChecked && AutomaticUpdateCheckAllowed) { updateAutoChecked = true; CheckAppUpdates(); }
    }
    private void LoadUpdatePreferences()
    {
        try {
            string path = Path.Combine(profile, "update-preferences.json");
            if (File.Exists(path)) updatePreferences = JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(path)) ?? new();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { updatePreferences = new(); }
    }
    private void ToggleStartupUpdateCheck()
    {
        updatePreferences = updatePreferences with { CheckOnStartup = !updatePreferences.CheckOnStartup };
        try { File.WriteAllText(Path.Combine(profile, "update-preferences.json"), JsonSerializer.Serialize(updatePreferences)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { updateError = "Couldn't save the update preference. " + ex.Message; }
    }
    private void OpenAppUpdates()
    {
        if (!loaded || ActivityOpen) return;
        updatesOpen = true; updateModalGeneration++; ClearExperienceInput();
    }
    private void CloseAppUpdates() { updatesOpen = false; updateModalGeneration++; pressed = null; }
    private void CheckAppUpdates()
    {
        if (appUpdates?.Build.CanCheck != true || updateChecking || updateDownloading || updateValidating || closing) return;
        var service = appUpdates;
        int generation = ++updateCheckGeneration;
        updateCheckCancellation?.Dispose(); updateCheckCancellation = new();
        var token = updateCheckCancellation.Token;
        updateChecking = true; updateError = updateNotice = ""; updateModalGeneration++;
        _ = Task.Run(async () => {
            try {
                var release = await service.CheckAsync(token);
                PostUpdateCompletion(() => {
                    if (closing || generation != updateCheckGeneration) return;
                    availableUpdate = release; updateChecked = true; updateChecking = false; updateModalGeneration++;
                    if (preparedUpdate?.Release != release) preparedUpdate = null;
                });
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                PostUpdateCompletion(() => { if (generation == updateCheckGeneration) updateChecking = false; });
            } catch (Exception ex) {
                PostUpdateCompletion(() => {
                    if (closing || generation != updateCheckGeneration) return;
                    updateChecking = false; updateError = "Couldn't check for updates. " + ex.Message; updateModalGeneration++;
                });
            }
        });
    }
    private void DownloadAppUpdate()
    {
        if (appUpdates is not { } service || availableUpdate is not { } release || updateDownloading || updateChecking || updateValidating || closing) return;
        int generation = ++updateDownloadGeneration;
        updateDownloadCancellation?.Dispose(); updateDownloadCancellation = new();
        var token = updateDownloadCancellation.Token;
        updateDownloading = true; preparedUpdate = null; updateError = updateNotice = "";
        updateBytes = 0; updateTotal = release.SizeBytes; updateModalGeneration++;
        var progress = new Progress<UpdateDownloadProgress>(value => PostUpdateCompletion(() => {
            if (closing || generation != updateDownloadGeneration || !updateDownloading) return;
            updateBytes = value.BytesReceived; updateTotal = value.TotalBytes;
        }));
        _ = Task.Run(async () => {
            try {
                var ready = await service.DownloadAsync(release, Path.Combine(profile, "updates"), progress, token);
                token.ThrowIfCancellationRequested();
                PostUpdateCompletion(() => {
                    if (closing || generation != updateDownloadGeneration) return;
                    preparedUpdate = ready; updateDownloading = false; updateModalGeneration++;
                    updateBytes = updateTotal = release.SizeBytes;
                });
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                PostUpdateCompletion(() => {
                    if (closing || generation != updateDownloadGeneration) return;
                    updateDownloading = false; updateNotice = "Download cancelled. Download again whenever you're ready."; updateModalGeneration++;
                });
            } catch (Exception ex) {
                PostUpdateCompletion(() => {
                    if (closing || generation != updateDownloadGeneration) return;
                    updateDownloading = false; updateError = "Couldn't download the update. " + ex.Message; updateModalGeneration++;
                });
            }
        });
    }
    private void CancelAppUpdateDownload()
    {
        if (!updateDownloading || updateDownloadCancellation?.IsCancellationRequested != false) return;
        updateNotice = "Cancelling download..."; updateDownloadCancellation.Cancel();
    }
    private string UpdateInstallBlocker()
    {
        if (match != null) return "Return to setup from your game before installing. Your download is ready.";
        if (Connected) return "Leave your friends table before installing. Your download is ready.";
        if (!loaded || busy || ActivityOpen) return "Wait for the current action to finish before installing.";
        if (selectedDeckDetails?.SaveError.Length > 0) return "Save your deck successfully in Build before installing.";
        if (dragging || textFocus || chatFocus || chatOpen && chatDraft.Length > 0 || setupModal.Length > 0)
            return "Finish or close your current input before installing.";
        if (CaptureMode) return "Finish the current capture before installing.";
        return "";
    }
    private void InstallAppUpdate()
    {
        if (appUpdates is not { } service || preparedUpdate is not { } ready || installUpdate == null || updateValidating || closing) return;
        string blocker = UpdateInstallBlocker();
        if (blocker.Length > 0) { updateError = blocker; return; }
        updateValidating = true; updateError = ""; updateModalGeneration++;
        _ = Task.Run(async () => {
            try {
                await service.ValidateAsync(ready);
                PostUpdateCompletion(() => {
                    updateValidating = false; updateModalGeneration++;
                    if (closing || preparedUpdate != ready) return;
                    string changed = UpdateInstallBlocker();
                    if (changed.Length > 0) { updateNotice = changed; updateInstallDeferred = true; return; }
                    try {
                        installUpdate(ready); updateInstallerLaunched = true; RequestExit();
                    } catch (Exception ex) { updateError = "Couldn't start the installer. " + ex.Message; }
                });
            } catch (Exception ex) {
                PostUpdateCompletion(() => {
                    if (closing) return;
                    updateValidating = false; preparedUpdate = null; updateModalGeneration++;
                    updateError = "The downloaded installer could not be verified. Download it again. " + ex.Message;
                });
            }
        });
    }
    private void OpenUpdateLocation(bool folder = false)
    {
        try {
            string target = folder && preparedUpdate != null ? Path.GetDirectoryName(preparedUpdate.InstallerPath)! : (availableUpdate?.PageUrl ?? AppReleasesPage).AbsoluteUri;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        } catch (Exception ex) { updateError = "Couldn't open that location. " + ex.Message; }
    }
    private void CloseUpdateRequests()
    {
        lock (updateCompletionGate) updateRequestsClosed = true;
        updateCheckCancellation?.Cancel(); updateDownloadCancellation?.Cancel();
        updateCheckCancellation?.Dispose(); updateDownloadCancellation?.Dispose();
    }
}
