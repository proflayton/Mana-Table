using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private static string UpdateSize(long bytes) => bytes < 1_048_576 ? $"{bytes / 1024d:N0} KB" : $"{bytes / 1_048_576d:N1} MB";
    private bool PreparingUpdate => UpdateView.Downloading && UpdateView.Phase == Mana.Client.AppUpdatePhase.Preparing;
    private string UpdateStatus => PreparingUpdate ? "Preparing update" : UpdateView.Downloading ? "Downloading update" : UpdateView.Prepared != null ? "Update ready"
        : UpdateView.Checking ? "Checking updates..." : UpdateView.Error.Length > 0 ? "Update needs attention"
        : UpdateView.Release != null ? "Update " + UpdateView.Release.Version + " available" : UpdateView.Notice.StartsWith("Updated to ", StringComparison.Ordinal) ? UpdateView.Notice
        : UpdateView.Checked ? "Up to date" : "";
    private void DrawUpdateFooter(bool home)
    {
        string version = updateSession.Build?.IsRelease == true ? "Version " + CurrentAppVersion : "Development build";
        canvas.Text(version, 64, 960, Muted * .65f, .58f, 500, 1);
        string status = UpdateStatus.Length == 0 || UpdateStatus.StartsWith("Updated to ", StringComparison.Ordinal) ? "Updates" : UpdateStatus;
        MenuAction("updates-open", status, new(1310, 939, 235, 45), new ShellIntent.OpenUpdates(), loaded && !ActivityOpen,
            UpdateView.Prepared != null || UpdateView.Release != null || UpdateView.Error.Length > 0, .7f);
    }
    private void DrawAppUpdates()
    {
        // A system drawer leaves the game visible. Closing it never cancels a
        // transfer or commits the game interaction underneath the pointer.
        var drawer = SystemDrawer("Updates", "updates-", new ShellIntent.CloseUpdates(), 660);
        int x = drawer.X, top = drawer.Y;
        canvas.Text("Installed  " + CurrentAppVersion, x + 30, top + 82, Muted, .74f, 360, 1);
        bool supported = updateSession.Build?.CanCheck == true;
        if (supported && UpdateView.Release != null && updateSession.CanCheck)
            MenuAction("updates-check-again", "Refresh", new(x + 404, top + 73, 130, 38), new ShellIntent.CheckUpdates(), size: .68f);
        string title = !supported ? "Development build" : UpdateView.Verifying ? "Verifying update"
            : PreparingUpdate ? "Preparing " + UpdateView.Release?.Version
            : UpdateView.Downloading ? "Downloading " + UpdateView.Release?.Version
            : UpdateView.Prepared != null ? UpdateView.Prepared.Release.Version + " is ready"
            : UpdateView.Checking ? "Checking for updates"
            : UpdateView.Release != null ? UpdateView.Release.Version + " is available"
            : UpdateView.Checked ? "You're up to date" : "Ready when you are";
        canvas.Text(title, x + 28, top + 150, Ink, 1.3f, 504, 3, true);
        string detail = !supported ? "Install a release to receive updates."
            : UpdateView.Downloading && UpdateView.FullFallback ? "Using the complete download because the smaller patch couldn't be applied."
            : UpdateView.Downloading ? "You can close this and keep playing."
            : UpdateView.Prepared != null && updateSession.CanInstall ? "Restart when you're ready. Your decks and settings stay with you."
            : UpdateView.Prepared != null ? "Open the download folder to update this portable copy."
            : UpdateView.Release != null ? UpdateSize(UpdateView.Release.SizeBytes) + (UpdateView.Release.UsesDelta ? " incremental download" : " download")
            : "New releases download only when you choose.";
        canvas.Text(detail, x + 30, top + 264, Muted, .85f, 500, 4);
        if (UpdateView.Downloading) {
            long total = Math.Max(0, UpdateView.TotalBytes);
            float fraction = total == 0 ? 0 : Math.Clamp(UpdateView.BytesReceived / (float)total, 0, 1);
            canvas.Rounded(new(x + 30, top + 366, 500, 8), new(6, 14, 20), 4);
            if (fraction > 0) canvas.Rounded(new(x + 30, top + 366, Math.Max(2, (int)(500 * fraction)), 8), Blue, 4);
            canvas.Text(PreparingUpdate ? "Download complete" : UpdateSize(UpdateView.BytesReceived) + " / " + (total > 0 ? UpdateSize(total) : "…"), x + 30, top + 386, Ink, .75f, 500, 1);
        }
        string blocker = UpdateView.Prepared != null && updateSession.CanInstall ? UpdateInstallBlocker() : "";
        string feedback = UpdateView.Error.Length > 0 ? UpdateView.Error : blocker.Length > 0 ? blocker : UpdateView.Notice;
        if (feedback.Length > 0) canvas.Text(feedback, x + 30, top + 432, UpdateView.Error.Length > 0 ? Red : Gold, .78f, 500, 4);
        int buttons = top + 535;
        if (UpdateView.Downloading) Button("updates-cancel", UpdateView.Phase == Mana.Client.AppUpdatePhase.Cancelling ? "Cancelling…" : "Cancel", x + 30, buttons, 500,
            new ShellIntent.CancelUpdateDownload(), UpdateView.Phase != Mana.Client.AppUpdatePhase.Cancelling, height: 44);
        else if (UpdateView.Prepared != null && updateSession.CanInstall) Button("updates-install", UpdateView.Verifying ? "Verifying…" : "Restart to update", x + 30, buttons, 500,
            new ShellIntent.InstallUpdate(), UpdateView.Phase == Mana.Client.AppUpdatePhase.Ready && blocker.Length == 0, true, 44);
        else if (UpdateView.Prepared != null) Button("updates-folder", "Open download folder", x + 30, buttons, 500, new ShellIntent.OpenUpdateLocation(true), height: 44);
        else if (UpdateView.Release != null) Button("updates-download", UpdateView.Error.Length > 0 ? "Retry download" : "Download update", x + 30, buttons, 500,
            new ShellIntent.DownloadUpdate(), UpdateView.Phase == Mana.Client.AppUpdatePhase.Available, true, 44);
        else if (supported) Button("updates-check", UpdateView.Checking ? "Checking…" : "Check for updates", x + 30, buttons, 500, new ShellIntent.CheckUpdates(), updateSession.CanCheck, true, 44);
        MenuAction("updates-releases", "Release notes", new(x + 350, top + 595, 190, 38), new ShellIntent.OpenUpdateLocation(), size: .68f);
        if (supported) MenuAction("updates-startup", "Check on startup: " + (updateSession.CheckOnStartup ? "On" : "Off"), new(x + 18, top + 595, 320, 38), new ShellIntent.ToggleStartupUpdateCheck(), size: .68f);
    }
}
