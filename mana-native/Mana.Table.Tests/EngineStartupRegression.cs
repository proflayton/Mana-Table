using Mana.Forge;
using System.Diagnostics;
using System.Text.Json;

namespace Mana.Table;

internal static class EngineStartupRegression
{
    public static async Task RunAsync(ForgePaths paths)
    {
        string isolated = Path.Combine(paths.Profile, "engine-startup");
        Directory.CreateDirectory(isolated);
        string jar = Path.Combine(isolated, "startup-engine.jar");
        await File.WriteAllTextAsync(jar, "Deliberately invalid JAR for startup retry regression.");
        var checks = new List<string>();
        void Check(bool valid, string description) { if (!valid) throw new InvalidOperationException(description); checks.Add(description); }
        var engine = new ForgeEngine(paths with { Jar = jar, Profile = isolated });
        try {
            var elapsed = Stopwatch.StartNew();
            try {
                await engine.InitializeAsync();
                throw new InvalidOperationException("An invalid JAR must fail engine initialization.");
            } catch (IOException) {
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(15), "A failed Java startup reports its error without waiting for the readiness timeout");
            }
            Check(File.ReadAllText(Path.Combine(isolated, "engine.log")).Contains("Invalid or corrupt jarfile"),
                "The failed attempt launched bundled Java against the intentionally invalid JAR");
            File.Copy(paths.Jar, jar, true);
            await engine.InitializeAsync();
            Check((await engine.DecksAsync()).Length == 0, "The same engine instance initializes successfully after its JAR is repaired");
            Check((await engine.PresetsAsync()).Length > 0, "The retried engine serves the real card library and precons");

            var deck = await engine.CreateDeckAsync("Startup retry deck");
            deck = await engine.RenameDeckAsync(deck.Id, deck.Revision, "Renamed startup retry deck");
            Check(deck.CanUndo, "A healthy engine retains a document with undo history");
            try {
                await engine.ExportDeckAsync(deck.Id, deck.Revision - 1);
                throw new InvalidOperationException("The stale read-only export request must fail.");
            } catch (InvalidOperationException ex) when (!ex.Message.StartsWith("The stale read-only")) {
                Check(true, "A request-specific load failure leaves the ready engine available for retry");
            }
            await engine.InitializeAsync();
            await engine.DecksAsync();
            await engine.PresetsAsync();
            await engine.InitializeAsync();
            deck = await engine.UndoDeckAsync(deck.Id, deck.Revision);
            Check(deck.Name == "Startup retry deck", "Repeated healthy initialization preserves the same process, active document, and undo history");
        } finally { await engine.DisposeAsync(); }
        await engine.DisposeAsync();
        Check(true, "Disposal remains safe after failed startup, successful retry, and repeated initialization");
        try {
            await engine.InitializeAsync();
            throw new InvalidOperationException("A disposed engine must not restart.");
        } catch (ObjectDisposedException) { Check(true, "Initialization rejects a disposed engine"); }
        await File.WriteAllTextAsync(Path.Combine(paths.Profile, "engine-startup-regression.json"),
            JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
}
