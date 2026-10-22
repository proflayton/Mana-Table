using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;
using Velopack;
using Velopack.Locators;

namespace Mana.Table;

// Setup hooks exit before touching the profile or starting Forge. The full
// installation health check is a separate normal process, outside hook limits.
internal static class ManagedInstallation
{
    internal static bool WasRestarted { get; private set; }
    internal static void Bootstrap(string[] args) => VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false)
        .OnRestarted(_ => WasRestarted = true).Run();

    internal static string? PackageId
    {
        get {
            if (!OperatingSystem.IsWindows()) return null;
            var locator = VelopackLocator.Current;
            return ValidPackageId(locator.AppId) && locator.CurrentlyInstalledVersion != null && locator.Channel == "win-x64"
                ? locator.AppId : null;
        }
    }

    internal static bool ValidPackageId(string? value) => value == "ManaTable"
        || value != null && Regex.IsMatch(value, @"\AManaTableNative-Smoke-[a-fA-F0-9]{32}\z", RegexOptions.CultureInvariant);

    internal static bool IsMaintenance(string[] args) => args.Contains("--installation-health") || args.Contains("--installation-shortcuts");

    internal static void RequireExternalProfile(string profile, params string[] installationRoots)
    {
        string playerProfile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        foreach (string installation in installationRoots) {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation));
            string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (playerProfile.Equals(root, StringComparison.OrdinalIgnoreCase) || playerProfile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a player profile outside the Mana Table installation before updating. Your current profile has not been moved.");
        }
    }

    internal static async Task<int> MaintainAsync(string[] args, string profile)
    {
        string Arg(string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing installation argument: " + name); }
        var locator = VelopackLocator.Current;
        var build = AppBuild.Load(AppContext.BaseDirectory);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Arg("--managed-root")));
        string expectedId = Arg("--expected-package-id");
        if (!ValidPackageId(expectedId) || PackageId != expectedId || locator.IsPortable
            || !string.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(locator.RootAppDir!)), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.Combine(root, "current"), Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), StringComparison.OrdinalIgnoreCase)
            || !build.CanCheck || build.Version != Arg("--expected-version") || build.SourceRevision != Arg("--expected-source")
            || locator.CurrentlyInstalledVersion?.ToString() != build.Version)
            throw new InvalidDataException("The new installation does not match the verified update.");
        profile = Path.GetFullPath(profile);
        RequireExternalProfile(profile, root);
        if (args.Contains("--installation-shortcuts")) {
            CreateShortcuts(root, expectedId, profile, args.Contains("--desktop-shortcut"));
            return 0;
        }

        string bundle = Path.Combine(AppContext.BaseDirectory, "engine");
        string jar = Path.Combine(bundle, "forge-engine.jar");
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "build.json")));
        string engineHash;
        using (var file = File.OpenRead(jar)) engineHash = Convert.ToHexString(SHA256.HashData(file));
        if (!engineHash.Equals(metadata.RootElement.GetProperty("engineSha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installed rules engine does not match the application build.");
        // Use the same profile root that the real app will open. No deck is
        // selected, edited or imported by this health query.
        await using var engine = new ForgeEngine(new(Path.Combine(bundle, "runtime", "bin", "java.exe"), jar, Path.Combine(bundle, "forge-res"), profile));
        await engine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(45));
        var catalog = await engine.SearchCardsAsync(new("Forest", Limit: 6)).WaitAsync(TimeSpan.FromSeconds(15));
        if (!catalog.Cards.Any(card => card.Card.Name == "Forest")) throw new IOException("The installed rules catalog did not answer its health check.");
        string receipt = Path.GetFullPath(Arg("--health-receipt"));
        Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new {
            healthy = true, packageId = expectedId, build.Version, build.SourceRevision, managedRoot = root,
            applicationDirectory = AppContext.BaseDirectory, profile, engineSha256 = engineHash,
            processPath = Environment.ProcessPath,
            clientSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "ManaTable.dll")))),
            gameSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Mana.Game.dll")))),
            catalogQuery = "Forest", catalogMatches = catalog.Cards.Length, checkedAt = DateTime.UtcNow
        }));
        return 0;
    }

    private static string Quote(string path)
    {
        if (path.Contains('"') || path.Any(char.IsControl)) throw new ArgumentException("Invalid player profile path.");
        return '"' + Regex.Replace(path, @"(\\+)$", "$1$1") + '"';
    }

    private static void CreateShortcuts(string root, string packageId, string profile, bool desktop)
    {
        // Point at Velopack's stable root stub. An isolated smoke package can
        // only create its own GUID-named links, never a player's real links.
        string name = packageId == "ManaTable" ? "Mana Table" : packageId;
        string executable = Path.Combine(root, name + ".exe");
        if (!File.Exists(executable)) throw new IOException("The managed application launcher is missing.");
        var folders = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.Programs) };
        if (desktop) folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcuts are unavailable."))!;
        try {
            foreach (string folder in folders) {
                Directory.CreateDirectory(folder);
                dynamic link = shell.CreateShortcut(Path.Combine(folder, name + ".lnk"));
                try {
                    link.TargetPath = executable;
                    link.WorkingDirectory = root;
                    link.Arguments = "--profile " + Quote(profile);
                    link.Description = "Mana Table";
                    link.IconLocation = Path.Combine(root, "current", "ManaTable.exe") + ",0";
                    link.Save();
                } finally { Marshal.FinalReleaseComObject(link); }
            }
        } finally { Marshal.FinalReleaseComObject(shell); }
    }
}
