using System.Diagnostics;
using System.Security.Cryptography;
using Mana.Client;
using Microsoft.Win32;

namespace Mana.Table;

// Setup waits for this process to exit before replacing files. Keeping the
// mutex handle alive also protects other Mana Table windows during an update.
internal static class WindowsUpdateInstaller
{
    internal const string MutexName = @"Local\ManaTableNative-30195D50-8D96-4E5E-9378-74C76B5D917C";
    internal const string UpdateGateName = MutexName + "-UpdateGate";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{30195D50-8D96-4E5E-9378-74C76B5D917C}_is1";

    internal static Mutex EnterInstance()
    {
        string name = ManagedInstallation.PackageId is { } id ? @"Local\ManaTableManaged-" + id : MutexName;
        using var gate = new Mutex(false, name + "-UpdateGate");
        bool acquired;
        try { acquired = gate.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Mana Table is updating. Setup will reopen the game when it finishes.");
        try { return new Mutex(false, name); }
        finally { gate.ReleaseMutex(); }
    }

    internal static Action<PreparedUpdate>? TryCreate(string applicationDirectory, string profile)
    {
        string directory = Path.GetFullPath(applicationDirectory);
        string playerProfile = Path.GetFullPath(profile);
        return RegisteredHere(directory) ? update => Launch(update, directory, playerProfile) : null;
    }

    private static bool RegisteredHere(string directory)
    {
        try {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var installation = user.OpenSubKey(UninstallKey);
            return installation?.GetValue("InstallLocation") is string registered
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(registered)),
                    Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) {
            return false;
        }
    }

    private static void Launch(PreparedUpdate update, string applicationDirectory, string profile)
    {
        ManagedInstallation.RequireExternalProfile(profile, applicationDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManaTable"));
        if (!RegisteredHere(applicationDirectory))
            throw new IOException("This installation has changed. Open the release page to install the update.");
        var build = AppBuild.Load(applicationDirectory);
        if (!build.CanCheck || !Version.TryParse(build.Version, out var installed) || update.Release.Version <= installed)
            throw new IOException("The update must be newer than the installed version.");
        string installer = Path.GetFullPath(update.InstallerPath);
        string cache = Path.GetFullPath(Path.Combine(profile, "updates")) + Path.DirectorySeparatorChar;
        if (!installer.StartsWith(cache, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(installer), "ManaTable-Setup.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Download this update again before installing it.");

        // Keep the verified executable closed to writes while Windows starts it.
        using var file = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != update.Release.SizeBytes
            || !Convert.ToHexString(SHA256.HashData(file)).Equals(update.Release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The downloaded update has changed. Download it again before installing.");
        var start = new ProcessStartInfo(installer) {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installer)!,
            WindowStyle = ProcessWindowStyle.Normal
        };
        foreach (string argument in new[] {
            "/SILENT", "/NORESTART", "/SP-",
            "/NOCLOSEAPPLICATIONS", "/NOFORCECLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS",
            "/MANATABLEUPDATE=1", "/MANATABLEPID=" + Environment.ProcessId,
            "/MANATABLEPROFILE=" + profile, "/DIR=" + applicationDirectory,
            "/LOG=" + Path.Combine(Path.GetDirectoryName(installer)!, "setup.log")
        }) start.ArgumentList.Add(argument);
        try {
            using var process = Process.Start(start);
            if (process == null) throw new IOException("Windows could not start Setup. Try again or open the release page.");
        } catch (System.ComponentModel.Win32Exception ex) {
            throw new IOException("Windows could not start Setup. Try again or open the release page.", ex);
        }
    }
}
