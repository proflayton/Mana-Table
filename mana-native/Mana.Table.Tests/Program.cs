using Mana.Forge;
using System.Text.Json;

namespace Mana.Table;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        string? Arg(string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
        bool testRun = args.Any(a => a is "--smoke" or "--smoke-ai" or "--smoke-ui-ai" or "--smoke-presentation" or "--smoke-ui-interactions" or "--smoke-table-feel" or "--smoke-card-reading" or "--smoke-cleanup" or "--smoke-deck-building" or "--smoke-workshop" or "--smoke-workshop-adapter" or "--smoke-lobby" or "--smoke-social" or "--smoke-advance" or "--smoke-auto-dock" or "--smoke-action-feedback");
        var profile = Arg("--profile") ?? (testRun
            ? Path.Combine(Path.GetTempPath(), "ManaTableNative-tests", Guid.NewGuid().ToString())
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManaTableNative"));
        Directory.CreateDirectory(profile);
        try {
            if (args.Contains("--smoke-advance")) { AdvanceRegression.RunAsync(profile).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--smoke-social")) { SocialRegression.Run(profile); return 0; }
            if (args.Contains("--smoke-presentation")) { PresentationSmoke.Run(profile); return 0; }
            Microsoft.Xna.Framework.Point? size = null;
            if (Arg("--size")?.Split('x') is { Length: 2 } dimensions && int.TryParse(dimensions[0], out int width) && int.TryParse(dimensions[1], out int height)) size = new(Math.Clamp(width, 800, 3840), Math.Clamp(height, 450, 2160));
            if (args.Contains("--smoke-auto-dock")) {
                var scenario = new InteractionScenarioEngine(); scenario.Present(PresentationSmoke.Example());
                using var test = new TableGame(scenario, profile, null, "controlled-frames", size, initialState: scenario.State);
                test.Automation = new AutoDockSmoke(test.TestPort, scenario);
                test.Run(); return 0;
            }
            if (args.Contains("--smoke-action-feedback")) {
                var scenario = new InteractionScenarioEngine(); scenario.Present(PresentationSmoke.Example());
                using var test = new TableGame(scenario, profile, null, null, size, initialState: scenario.State);
                test.Automation = new ActionFeedbackSmoke(test.TestPort, scenario);
                test.Run(); return 0;
            }
            if (args.Contains("--smoke-ui-interactions")) {
                var scenario = new InteractionScenarioEngine();
                using var test = new TableGame(scenario, profile, null, null, size: size, initialState: scenario.State);
                test.Automation = new SmokeAutomation(test.TestPort, true);
                test.Run(); return 0;
            }
            if (args.Contains("--smoke-table-feel")) {
                var scenario = new InteractionScenarioEngine(); scenario.Present(PresentationSmoke.Example());
                using var test = new TableGame(scenario, profile, null, null, size: size, initialState: scenario.State);
                test.Automation = new TableFeelSmoke(test.TestPort, scenario);
                test.Run(); return 0;
            }
            if (args.Contains("--smoke-card-reading")) {
                var scenario = new InteractionScenarioEngine(); scenario.Present(PresentationSmoke.Example());
                using var test = new TableGame(scenario, profile, null, null, size: size, initialState: scenario.State);
                test.Automation = new CardReadingSmoke(test.TestPort, scenario);
                test.Run(); return 0;
            }
            var bundle = Arg("--engine-resources") ?? Path.Combine(AppContext.BaseDirectory, "engine");
            if (!Directory.Exists(bundle)) throw new IOException("Missing bundled engine. Run the native packaging script first, or pass --engine-resources pointing to the package's resources directory.");
            var backend = Arg("--engine") ?? "forge";
            if (backend != "forge") throw new NotSupportedException($"Engine '{backend}' is not installed. Available: forge.");
            var paths = new ForgePaths(Path.Combine(bundle, "runtime", "bin", "java.exe"), Arg("--engine-jar") ?? Path.Combine(bundle, "forge-engine.jar"), Path.Combine(bundle, "forge-res"), profile);
            if (args.Contains("--smoke")) { NativeSmoke.RunAsync(paths).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--smoke-ai")) { AiSmoke.RunAsync(paths).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--smoke-cleanup")) { CleanupSmoke.RunAsync(paths).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--smoke-workshop-adapter")) { DeckWorkshopRegression.RunAsync(paths).GetAwaiter().GetResult(); return 0; }
            var engine = new ForgeEngine(paths);
            try {
                using var game = new TableGame(engine, profile, Arg("--capture"), Arg("--fixture"), size);
                using var lobbySmoke = args.Contains("--smoke-lobby") ? new LobbySmoke(game.TestPort, paths) : null;
                if (lobbySmoke != null) game.Automation = lobbySmoke;
                if (args.Contains("--smoke-ui-ai")) game.Automation = new SmokeAutomation(game.TestPort, false);
                if (args.Contains("--smoke-deck-building")) game.Automation = new DeckBuildingSmoke(game.TestPort);
                if (args.Contains("--smoke-workshop")) game.Automation = new DeckWorkshopSmoke(game.TestPort);
                game.Run();
            } finally { engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            return 0;
        } catch (Exception ex) {
            File.WriteAllText(Path.Combine(profile, "native-error.log"), ex.ToString());
            if (!testRun && !args.Contains("--capture")) MessageBox.Show(ex.Message, "Mana Table", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
