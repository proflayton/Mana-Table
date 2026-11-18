using System.IO.Compression;
using Mana.Forge;

namespace Mana.Table;

internal static class AiFixtureIsolationChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        string directory = Path.GetFullPath(Path.Combine(profile, "fixture-class-isolation"));
        Directory.CreateDirectory(directory);
        string production = Path.Combine(directory, "forge-engine.jar");
        string fixture = Path.Combine(directory, "forge-engine-fixtures.jar");
        const string manifest = "Manifest-Version: 1.0\nMain-Class: forge.api.AiReleaseFixtureEngine\nClass-Path: forge-engine.jar\n\n";
        void Archive(string path, params string[] classes) {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            if (path == fixture) { using var writer = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open()); writer.Write(manifest); }
            foreach (string name in classes) { using var member = zip.CreateEntry(name).Open(); member.WriteByte(0); }
        }
        bool Admitted() {
            try { _ = AiReleaseEvaluation.VerifyRuntime(new("unused-java", fixture, directory, directory), production); return true; }
            catch (InvalidDataException) { return false; }
        }
        Archive(production, "forge/api/DesktopEngine.class");
        Archive(fixture, "forge/api/AiReleaseFixtureEngine.class", "forge/api/AiReleaseFixtureEngine$PolledFanout.class");
        check(Admitted(), "The existing private fixture family retains its production dependency verification");
        Archive(fixture, "forge/api/AiReleaseFixtureEngine.class", "forge/api/LibraryAcquisitionFixtureEngine.class",
            "forge/api/LibraryAcquisitionFixtureEngine$Bundle.class");
        check(Admitted(), "The explicit acquisition pregame factories are admitted only in the private fixture archive");
        foreach (string injected in new[] { "forge/api/LibraryAcquisitionFixtureEngineImposter.class", "forge/api/DesktopEngine.class" }) {
            Archive(fixture, "forge/api/AiReleaseFixtureEngine.class", injected);
            check(!Admitted(), "A private fixture archive cannot smuggle a lookalike factory or production implementation: " + injected);
        }
        Archive(fixture, "forge/api/AiReleaseFixtureEngine.class", "forge/api/LibraryAcquisitionFixtureEngine$Bundle.class");
        foreach (string leaked in new[] { "forge/api/AiReleaseFixtureEngine.class", "forge/api/LibraryAcquisitionFixtureEngine$Bundle.class" }) {
            Archive(production, "forge/api/DesktopEngine.class", leaked);
            check(!Admitted(), "Test-only factory classes cannot enter the production engine: " + leaked);
        }
    }
}
