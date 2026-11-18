using System.IO.Compression;
using System.Security.Cryptography;
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
        Archive(fixture, "forge/api/AiReleaseFixtureEngine.class", "forge/api/AiReleaseFixtureEngine$PolledFanout.class",
            "forge/api/AiReleaseFixtureEngine$PolledManaOutput.class", "forge/api/LibraryAcquisitionFixtureEngine.class",
            "forge/api/LibraryAcquisitionFixtureEngine$Bundle.class");
        var paths = new ForgePaths("unused-java", fixture, directory, directory);
        var runtime = AiReleaseEvaluation.VerifyRuntime(paths, production);
        foreach (string bootstrap in new[] { "PolledFanout", "PolledManaOutput" }) {
            var derived = PolledFanoutEngineJourney.PrepareLauncher(paths, Path.Combine(directory, bootstrap), runtime, bootstrap);
            using var source = ZipFile.OpenRead(fixture);
            using var target = ZipFile.OpenRead(derived.Jar);
            var original = source.Entries.Where(entry => entry.FullName.EndsWith(".class", StringComparison.Ordinal)).ToArray();
            check(target.Entries.Count == original.Length + 1 && original.All(entry => {
                using var input = entry.Open(); using var output = target.GetEntry(entry.FullName)!.Open();
                return SHA256.HashData(input).SequenceEqual(SHA256.HashData(output));
            }), "Both explicitly declared private fixture families retain every exact class byte in the derived " + bootstrap + " launcher");
            using var manifestReader = new StreamReader(target.GetEntry("META-INF/MANIFEST.MF")!.Open());
            check(manifestReader.ReadToEnd().Contains("Main-Class: forge.api.AiReleaseFixtureEngine$" + bootstrap + "\r\n", StringComparison.Ordinal)
                    && SHA256.HashData(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(derived.Jar)!, "forge-engine.jar")))
                        .SequenceEqual(SHA256.HashData(File.ReadAllBytes(production))),
                "Derived " + bootstrap + " changes only its declared private entry point and retains the exact production dependency");
        }
        bool Derived(string name, AiReleaseRuntime evidence) {
            try { _ = PolledFanoutEngineJourney.PrepareLauncher(paths, Path.Combine(directory, name), evidence); return true; }
            catch (InvalidDataException) { return false; }
        }
        Archive(fixture, "forge/api/AiReleaseFixtureEngine.class", "forge/api/AiReleaseFixtureEngine$PolledFanout.class");
        check(!Derived("changed-fixture", runtime), "Derivation rejects fixture bytes changed after runtime verification");
        foreach (string injected in new[] { "forge/api/AiReleaseFixtureEngineImposter.class", "forge/api/LibraryAcquisitionFixtureEngineImposter.class",
                     "forge/api/DesktopEngine.class", "other/AiReleaseFixtureEngine$PolledFanout.class" }) {
            Archive(fixture, "forge/api/AiReleaseFixtureEngine.class", "forge/api/AiReleaseFixtureEngine$PolledFanout.class", injected);
            var changed = runtime with { FixtureLauncherSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))) };
            check(!Derived("undeclared-" + Path.GetFileNameWithoutExtension(injected), changed),
                "Even a matching archive hash cannot admit an undeclared class family or package into a derived launcher: " + injected);
        }
        Archive(fixture, "forge/api/AiReleaseFixtureEngine.class", "forge/api/AiReleaseFixtureEngine$PolledFanout.class");
        runtime = AiReleaseEvaluation.VerifyRuntime(paths, production);
        Archive(production, "forge/api/DesktopEngine.class", "changed.class");
        check(!Derived("changed-production", runtime), "Derivation rejects a production dependency changed after runtime verification");
        Archive(production, "forge/api/DesktopEngine.class");
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
