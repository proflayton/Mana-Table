using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private static string UpdateSize(long bytes) => bytes < 1_048_576 ? $"{bytes / 1024d:N0} KB" : $"{bytes / 1_048_576d:N1} MB";
    private bool PreparingUpdate => updateDownloading && updateStage == Mana.Client.UpdatePreparationStage.Preparing;
    private string UpdateStatus => PreparingUpdate ? "Preparing update" : updateDownloading ? "Downloading update" : preparedUpdate != null ? "Update ready"
        : updateChecking ? "Checking updates..." : updateError.Length > 0 ? "Update needs attention"
        : availableUpdate != null ? "Update " + availableUpdate.Version + " available" : updateNotice.StartsWith("Updated to ", StringComparison.Ordinal) ? updateNotice
        : updateChecked ? "Up to date" : "";
    private void DrawUpdateFooter(bool home)
    {
        string version = appUpdates?.Build.IsRelease == true ? "Version " + CurrentAppVersion : "Development build";
        canvas.Text(version, 64, 960, Muted * .65f, .58f, 500, 1);
        string status = UpdateStatus.Length == 0 || UpdateStatus.StartsWith("Updated to ", StringComparison.Ordinal) ? "Updates" : UpdateStatus;
        MenuAction("updates-open", status, new(1310, 939, 235, 45), OpenAppUpdates, loaded && !ActivityOpen,
            preparedUpdate != null || availableUpdate != null || updateError.Length > 0, .7f);
    }
    private void DrawAppUpdates()
    {
        // A system drawer leaves the game visible. Closing it never cancels a
        // transfer or commits the game interaction underneath the pointer.
        var drawer = SystemDrawer("Updates", "updates-", UpdateScope, CloseAppUpdates, 660);
        int x = drawer.X, top = drawer.Y;
        canvas.Text("Installed  " + CurrentAppVersion, x + 30, top + 82, Muted, .74f, 360, 1);
        bool supported = appUpdates?.Build.CanCheck == true;
        if (supported && availableUpdate != null && !updateDownloading && !updateChecking && !updateValidating)
            MenuAction("updates-check-again", "Refresh", new(x + 404, top + 73, 130, 38), CheckAppUpdates, size: .68f);
        string title = !supported ? "Development build" : updateValidating ? "Verifying update"
            : PreparingUpdate ? "Preparing " + availableUpdate?.Version
            : updateDownloading ? "Downloading " + availableUpdate?.Version
            : preparedUpdate != null ? preparedUpdate.Release.Version + " is ready"
            : updateChecking ? "Checking for updates"
            : availableUpdate != null ? availableUpdate.Version + " is available"
            : updateChecked ? "You're up to date" : "Ready when you are";
        canvas.Text(title, x + 28, top + 150, Ink, 1.3f, 504, 3, true);
        string detail = !supported ? "Install a release to receive updates."
            : updateDownloading && updateFullFallback ? "Using the complete download because the smaller patch couldn't be applied."
            : updateDownloading ? "You can close this and keep playing."
            : preparedUpdate != null && installUpdate != null ? "Restart when you're ready. Your decks and settings stay with you."
            : preparedUpdate != null ? "Open the download folder to update this portable copy."
            : availableUpdate != null ? UpdateSize(availableUpdate.SizeBytes) + (availableUpdate.UsesDelta ? " incremental download" : " download")
            : "New releases download only when you choose.";
        canvas.Text(detail, x + 30, top + 264, Muted, .85f, 500, 4);
        if (updateDownloading) {
            long total = Math.Max(0, updateTotal);
            float fraction = total == 0 ? 0 : Math.Clamp(updateBytes / (float)total, 0, 1);
            canvas.Rounded(new(x + 30, top + 366, 500, 8), new(6, 14, 20), 4);
            if (fraction > 0) canvas.Rounded(new(x + 30, top + 366, Math.Max(2, (int)(500 * fraction)), 8), Blue, 4);
            canvas.Text(PreparingUpdate ? "Download complete" : UpdateSize(updateBytes) + " / " + (total > 0 ? UpdateSize(total) : "…"), x + 30, top + 386, Ink, .75f, 500, 1);
        }
        string blocker = preparedUpdate != null && installUpdate != null ? UpdateInstallBlocker() : "";
        string feedback = updateError.Length > 0 ? updateError : blocker.Length > 0 ? blocker : updateNotice;
        if (feedback.Length > 0) canvas.Text(feedback, x + 30, top + 432, updateError.Length > 0 ? Red : Gold, .78f, 500, 4);
        int buttons = top + 535;
        if (updateDownloading) Button("updates-cancel", updateDownloadCancellation?.IsCancellationRequested == true ? "Cancelling…" : "Cancel", x + 30, buttons, 500,
            CancelAppUpdateDownload, updateDownloadCancellation?.IsCancellationRequested == false, height: 44);
        else if (preparedUpdate != null && installUpdate != null) Button("updates-install", updateValidating ? "Verifying…" : "Restart to update", x + 30, buttons, 500,
            InstallAppUpdate, !updateChecking && !updateValidating && blocker.Length == 0, true, 44);
        else if (preparedUpdate != null) Button("updates-folder", "Open download folder", x + 30, buttons, 500, () => OpenUpdateLocation(true), height: 44);
        else if (availableUpdate != null) Button("updates-download", updateError.Length > 0 ? "Retry download" : "Download update", x + 30, buttons, 500,
            DownloadAppUpdate, !updateChecking && !updateValidating, true, 44);
        else if (supported) Button("updates-check", updateChecking ? "Checking…" : "Check for updates", x + 30, buttons, 500, CheckAppUpdates, !updateChecking, true, 44);
        MenuAction("updates-releases", "Release notes", new(x + 350, top + 595, 190, 38), () => OpenUpdateLocation(), size: .68f);
        if (supported) MenuAction("updates-startup", "Check on startup: " + (updatePreferences.CheckOnStartup ? "On" : "Off"), new(x + 18, top + 595, 320, 38), ToggleStartupUpdateCheck, size: .68f);
    }
}
