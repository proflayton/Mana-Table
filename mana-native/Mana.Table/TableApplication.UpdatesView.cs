using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private static string UpdateSize(long bytes) => bytes < 1_048_576 ? $"{bytes / 1024d:N0} KB" : $"{bytes / 1_048_576d:N1} MB";
    private bool PreparingUpdate => updateDownloading && updateStage == Mana.Client.UpdatePreparationStage.Preparing;
    private string UpdateStatus => PreparingUpdate ? "Preparing update" : updateDownloading ? "Downloading update" : preparedUpdate != null ? "Update ready"
        : availableUpdate != null ? "Update " + availableUpdate.Version + " available" : updateChecking ? "Checking updates..."
        : updateError.Length > 0 ? "Update check needs attention" : updateNotice.StartsWith("Updated to ", StringComparison.Ordinal) ? updateNotice
        : updateChecked ? "Up to date" : "";
    private void DrawUpdateFooter(bool home)
    {
        int x = home ? 90 : 48;
        string version = appUpdates?.Build.IsRelease == true ? "Version " + CurrentAppVersion : "Development build";
        canvas.Text(version + (UpdateStatus.Length == 0 ? "" : "  |  " + UpdateStatus), x, 932, availableUpdate != null ? Gold : Muted, .68f, home ? 680 : 730, 1);
        Button("updates-open", availableUpdate != null ? "Updates available" : "Updates", home ? 815 : 808, 920, home ? 270 : 274,
            OpenAppUpdates, loaded && !ActivityOpen, availableUpdate != null, 38);
    }
    private void DrawAppUpdates()
    {
        hits.Clear(); hovered = null; ClearCardComposition();
        int top = (DesignHeight - 720) / 2;
        canvas.Fill(new(0, 0, 1600, DesignHeight), Color.Black * .8f);
        canvas.Panel(new(190, top, 1220, 720), new(23, 32, 43), LobbyTrim, 16);
        canvas.Text("Mana Table updates", 226, top + 27, Ink, 1.4f, 950, 1, true);
        Button("updates-close", "Close", 1254, top + 22, 120, CloseAppUpdates, height: 38);
        canvas.Line(new(226, top + 83), new(1374, top + 83), LobbyTrim);
        canvas.Text("CURRENT VERSION", 228, top + 108, Gold, .68f, 700, 1, true);
        canvas.Text(CurrentAppVersion, 228, top + 140, Ink, 1.08f, 1040, 1, true);
        bool supported = appUpdates?.Build.CanCheck == true;
        string title = !supported ? "Get a packaged release" : updateValidating ? "Checking the prepared update..."
            : PreparingUpdate ? "Preparing version " + availableUpdate?.Version
            : updateDownloading ? (updateFullFallback ? "Downloading the complete update" : "Downloading version " + availableUpdate?.Version)
            : preparedUpdate != null ? "Version " + preparedUpdate.Release.Version + " is verified and ready"
            : updateChecking ? "Checking for a newer release..."
            : availableUpdate != null ? "Version " + availableUpdate.Version + " is available"
            : updateChecked ? "You're up to date" : "Choose when to update";
        canvas.Text(title, 228, top + 203, Ink, 1.2f, 1115, 2, true);
        string detail = !supported ? "Development builds use manual updates. Get the installer or portable download from the releases page."
            : PreparingUpdate ? "The download is complete. Preparing and checking the updated files. You can close this panel and keep building or playing."
            : updateDownloading ? updateFullFallback ? "The smaller patch could not be used. Downloading the complete update instead; you can keep building or playing."
                : "You can close this panel and keep building or playing. The download continues in the background."
            : preparedUpdate != null && installUpdate != null ? "Your update is prepared. Restart when you're ready. Mana Table will reopen with your decks and settings."
            : preparedUpdate != null ? "This is a portable copy. Use the releases page to replace your portable folder, or install a separate copy."
            : updateChecking ? "Your table and deck work stay available while the release check runs."
            : "Download when it suits you. Installation starts only after you choose it and leave any active game or friends table.";
        canvas.Text(detail, 229, top + 276, Muted, .85f, 1108, 3);
        if (availableUpdate != null && !updateDownloading) canvas.Text((availableUpdate.UsesDelta ? "Smaller update: " : "Update download: ")
            + UpdateSize(availableUpdate.SizeBytes), 229, top + 366, Muted, .79f, 1100, 1);
        if (updateDownloading) {
            long total = Math.Max(0, updateTotal);
            float fraction = total == 0 ? 0 : Math.Clamp(updateBytes / (float)total, 0, 1);
            canvas.Rounded(new(228, top + 369, 1145, 18), new(12, 22, 32), 8);
            if (fraction > 0) canvas.Rounded(new(228, top + 369, Math.Max(2, (int)(1145 * fraction)), 18), Blue, 8);
            canvas.Text(PreparingUpdate ? "Download complete · Preparing your update..." : UpdateSize(updateBytes) + " / "
                + (total > 0 ? UpdateSize(total) + $"  ({fraction:P0})" : "waiting for total size"), 229, top + 409, Ink, .85f, 1100, 1);
        }
        string blocker = preparedUpdate != null && installUpdate != null ? UpdateInstallBlocker() : "";
        string feedback = updateError.Length > 0 ? updateError : blocker.Length > 0 ? blocker : updateNotice;
        if (feedback.Length > 0) canvas.Text(feedback, 229, top + (preparedUpdate != null && installUpdate == null ? 403 : 461), updateError.Length > 0 ? Red : Gold, .8f, 1110, preparedUpdate != null && installUpdate == null ? 2 : 3);
        if (preparedUpdate != null && installUpdate == null) {
            canvas.Text("Your profile: " + profile, 229, top + 463, Muted, .7f, 1110, 2);
            canvas.Text("Downloaded installer: " + preparedUpdate.InstallerPath, 229, top + 514, Muted, .67f, 1110, 2);
        }
        int buttons = top + 564;
        if (updateDownloading) Button("updates-cancel", updateDownloadCancellation?.IsCancellationRequested == true ? "Cancelling..." : PreparingUpdate ? "Cancel preparation" : "Cancel download", 228, buttons, 320,
            CancelAppUpdateDownload, updateDownloadCancellation?.IsCancellationRequested == false, height: 46);
        else if (preparedUpdate != null && installUpdate != null) Button("updates-install", updateValidating ? "Verifying update..." : "Restart to update", 228, buttons, 410,
            InstallAppUpdate, !updateChecking && !updateValidating && blocker.Length == 0, true, 46);
        else if (preparedUpdate != null) Button("updates-folder", "Open download folder", 228, buttons, 360, () => OpenUpdateLocation(true), height: 46);
        else if (availableUpdate != null) Button("updates-download", updateError.Length > 0 ? "Retry download" : "Download update", 228, buttons, 360,
            DownloadAppUpdate, !updateChecking && !updateValidating, true, 46);
        else if (supported) Button("updates-check", updateChecking ? "Checking..." : updateError.Length > 0 ? "Retry check" : "Check for updates", 228, buttons, 360,
            CheckAppUpdates, !updateChecking, true, 46);
        Button("updates-releases", availableUpdate != null ? "Release notes" : "Open releases", 997, buttons, 376, () => OpenUpdateLocation(), height: 46);
        if (supported) {
            Button("updates-startup", "Check on startup: " + (updatePreferences.CheckOnStartup ? "On" : "Off"), 228, top + 641, 372, ToggleStartupUpdateCheck, height: 36);
            if (availableUpdate != null && !updateDownloading && !updateChecking && !updateValidating)
                Button("updates-check-again", "Check again", 1187, top + 641, 186, CheckAppUpdates, height: 36);
        }
    }
}
