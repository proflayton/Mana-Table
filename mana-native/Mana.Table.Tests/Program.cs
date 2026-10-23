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
        bool testRun = args.Any(a => a is "--smoke" or "--smoke-ai" or "--smoke-ui-ai" or "--smoke-presentation" or "--smoke-ui-interactions" or "--smoke-table-feel" or "--smoke-card-reading" or "--smoke-card-states" or "--smoke-compact-selection" or "--smoke-cleanup" or "--smoke-deck-building" or "--smoke-workshop" or "--smoke-analysis" or "--smoke-workshop-adapter" or "--smoke-analysis-adapter" or "--smoke-deck-tuning" or "--smoke-lobby" or "--smoke-social" or "--smoke-advance" or "--smoke-auto-dock" or "--smoke-action-feedback" or "--smoke-onboarding" or "--smoke-loading" or "--smoke-engine-startup" or "--smoke-deck-import" or "--smoke-priority-abilities");
        testRun |= args.Contains("--smoke-updates");
        testRun |= args.Contains("--smoke-commander-tax");
        testRun |= args.Contains("--smoke-equipment");
        testRun |= args.Contains("--smoke-lobby-ai-seats");
        testRun |= args.Contains("--smoke-lobby-three-player");
        testRun |= args.Contains("--smoke-full-battlefield");
        testRun |= args.Contains("--smoke-hand-placement");
        testRun |= args.Contains("--smoke-allocation");
        testRun |= args.Contains("--smoke-recovery");
        testRun |= args.Contains("--smoke-state-clarity") || args.Contains("--smoke-deck-experience");
        testRun |= args.Contains("--smoke-printings");
        testRun |= args.Contains("--smoke-headless-presentation");
        testRun |= args.Contains("--smoke-headless-engine");
        testRun |= args.Contains("--smoke-cluttered-changes") || args.Contains("--smoke-headless-clutter");
        testRun |= args.Contains("--smoke-priority-flow") || args.Contains("--smoke-headless-priority-flow");
        testRun |= args.Contains("--capture-deck-workspace");
        var profile = Arg("--profile") ?? (testRun
            ? Path.Combine(Path.GetTempPath(), "ManaTableNative-tests", Guid.NewGuid().ToString())
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManaTableNative"));
        Directory.CreateDirectory(profile);
        try {
            if (args.Contains("--smoke-advance")) { AdvanceRegression.RunAsync(profile).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--smoke-social")) { SocialRegression.Run(profile); return 0; }
            if (args.Contains("--smoke-presentation")) { PresentationSmoke.Run(profile); return 0; }
            if (args.Contains("--smoke-headless-presentation")) { HeadlessPresentationSmoke.Run(profile); return 0; }
            if (args.Contains("--smoke-headless-clutter")) { ClutteredChangesSmoke.RunHeadless(profile); return 0; }
            if (args.Contains("--smoke-headless-priority-flow")) { PriorityFlowSmoke.RunHeadless(profile); return 0; }
            Microsoft.Xna.Framework.Point? size = null;
            if (Arg("--size")?.Split('x') is { Length: 2 } dimensions && int.TryParse(dimensions[0], out int width) && int.TryParse(dimensions[1], out int height)) size = new(Math.Clamp(width, 800, 3840), Math.Clamp(height, 450, 2160));
            if (args.Contains("--smoke-updates")) { UpdateSmoke.Run(profile, size); return 0; }
            if (args.Contains("--smoke-cluttered-changes")) { ClutteredChangesSmoke.Run(profile, size); return 0; }
            if (args.Contains("--smoke-priority-flow")) { PriorityFlowSmoke.Run(profile, size); return 0; }
            if (args.Contains("--smoke-recovery")) { RecoverySmoke.Run(profile, size); return 0; }
            if (args.Contains("--smoke-state-clarity")) { StateClaritySmoke.Run(profile, size); return 0; }
            if (args.Contains("--smoke-hand-placement")) { HandPlacementSmoke.Run(profile, size); return 0; }
            if (args.Contains("--smoke-allocation")) { AllocationSmoke.Run(profile, size); return 0; }
            if (args.Contains("--smoke-onboarding")) { OnboardingSmoke.Run(profile, size); return 0; }
            if (args.Contains("--smoke-loading")) { LoadingSmoke.Run(profile, size); return 0; }
            if (args.Contains("--smoke-full-battlefield")) { FullBattlefieldSmoke.Run(profile, size, args.Contains("--real-art"), args.Contains("--readable-crowd")); return 0; }
            if (args.Contains("--smoke-commander-tax")) {
                var scenario = new InteractionScenarioEngine(); scenario.Present(PresentationSmoke.CommanderTaxExample());
                using var test = new TableWindow(TestTableServices.For(scenario), profile, null, null, size, initialState: scenario.State,
                    artSource: new CommanderTaxSmoke.CostReadableArtFixture());
                test.Automation = new CommanderTaxSmoke(test.TestPort, scenario);
                test.Run(); return 0;
            }
            if (args.Contains("--smoke-equipment")) { EquipmentSmoke.Run(profile, size); return 0; }
            if (args.Contains("--smoke-auto-dock")) {
                var scenario = new InteractionScenarioEngine(); scenario.Present(PresentationSmoke.Example());
                using var test = new TableWindow(TestTableServices.For(scenario), profile, null, "controlled-frames", size, initialState: scenario.State);
                test.Automation = new AutoDockSmoke(test.TestPort, scenario);
                test.Run(); return 0;
            }
            if (args.Contains("--smoke-priority-abilities")) {
                var scenario = new InteractionScenarioEngine(); scenario.Present(PriorityAbilitySmoke.Example());
                using var test = new TableWindow(TestTableServices.For(scenario), profile, null, null, size, initialState: scenario.State,
                    artSource: new CardReadingArtFixture());
                test.Automation = new PriorityAbilitySmoke(test.TestPort, scenario);
                test.Run(); return 0;
            }
            if (args.Contains("--smoke-action-feedback")) {
                var scenario = new InteractionScenarioEngine(); scenario.Present(PresentationSmoke.Example());
                using var test = new TableWindow(TestTableServices.For(scenario), profile, null, null, size, initialState: scenario.State,
                    artSource: new CardReadingArtFixture(Arg("--art-cache")));
                test.Automation = new ActionFeedbackSmoke(test.TestPort, scenario);
                test.Run(); return 0;
            }
            if (args.Contains("--smoke-ui-interactions")) {
                var scenario = new InteractionScenarioEngine();
                using var test = new TableWindow(TestTableServices.For(scenario), profile, null, null, size: size, initialState: scenario.State);
                test.Automation = new SmokeAutomation(test.TestPort, true);
                test.Run(); return 0;
            }
            if (args.Contains("--smoke-table-feel")) {
                var scenario = new InteractionScenarioEngine(); scenario.Present(PresentationSmoke.Example());
                using var test = new TableWindow(TestTableServices.For(scenario), profile, null, null, size: size, initialState: scenario.State);
                test.Automation = new TableFeelSmoke(test.TestPort, scenario);
                test.Run(); return 0;
            }
            if (args.Contains("--smoke-card-reading") || args.Contains("--smoke-card-states") || args.Contains("--smoke-compact-selection")) {
                if (args.Contains("--smoke-card-states")) new PlayPreferences { TextScale = 1.3f, Sounds = false, Automatic = false }.Save(profile);
                var scenario = new InteractionScenarioEngine(); scenario.Present(PresentationSmoke.Example());
                using var test = new TableWindow(TestTableServices.For(scenario), profile, null, null, size: size, initialState: scenario.State, artSource: new CardReadingArtFixture(Arg("--art-cache")));
                test.Automation = new CardReadingSmoke(test.TestPort, scenario, selectionOnly: args.Contains("--smoke-compact-selection"), statesOnly: args.Contains("--smoke-card-states"));
                test.Run(); return 0;
            }
            var bundle = Arg("--engine-resources") ?? Path.Combine(AppContext.BaseDirectory, "engine");
            if (!Directory.Exists(bundle)) throw new IOException("Missing bundled engine. Run the native packaging script first, or pass --engine-resources pointing to the package's resources directory.");
            var backend = Arg("--engine") ?? "forge";
            if (backend != "forge") throw new NotSupportedException($"Engine '{backend}' is not installed. Available: forge.");
            var paths = new ForgePaths(Path.Combine(bundle, "runtime", "bin", "java.exe"), Arg("--engine-jar") ?? Path.Combine(bundle, "forge-engine.jar"), Path.Combine(bundle, "forge-res"), profile);
            if (args.Contains("--smoke-deck-import")) { DeckImportSmoke.Run(paths, Arg("--import-file") ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "annotated-toph.txt"), size); return 0; }
            if (args.Contains("--smoke-engine-startup")) { EngineStartupRegression.RunAsync(paths).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--smoke-headless-engine")) { HeadlessEngineSmoke.RunAsync(paths).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--smoke")) { NativeSmoke.RunAsync(paths).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--smoke-ai")) { AiSmoke.RunAsync(paths).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--smoke-cleanup")) { CleanupSmoke.RunAsync(paths).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--smoke-analysis-adapter")) { AnalysisRegression.RunAsync(paths).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--smoke-workshop-adapter")) { DeckWorkshopRegression.RunAsync(paths).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--smoke-lobby-ai-seats")) { LobbyAiSeatSmoke.Run(paths, size); return 0; }
            if (args.Contains("--smoke-lobby-three-player")) { LobbyThreePlayerSmoke.Run(paths, size); return 0; }
            var engine = new ForgeEngine(paths);
            if (args.Contains("--smoke-deck-experience"))
                (PlayPreferences.Load(profile) with { TextScale = 1.3f, Automatic = false, Sounds = false }).Save(profile);
            try {
                using var game = new TableWindow(TestTableServices.For(engine), profile, Arg("--capture"), Arg("--fixture"), size);
                using var lobbySmoke = args.Contains("--smoke-lobby") ? new LobbySmoke(game.TestPort, paths) : null;
                if (lobbySmoke != null) game.Automation = lobbySmoke;
                if (args.Contains("--smoke-ui-ai")) game.Automation = new SmokeAutomation(game.TestPort, false);
                if (args.Contains("--smoke-deck-building")) game.Automation = new DeckBuildingSmoke(game.TestPort);
                if (args.Contains("--smoke-analysis")) game.Automation = new AnalysisSmoke(game.TestPort);
                if (args.Contains("--smoke-workshop")) game.Automation = new DeckWorkshopSmoke(game.TestPort);
                if (args.Contains("--smoke-printings")) game.Automation = new PrintingsSmoke(game.TestPort);
                if (args.Contains("--smoke-deck-tuning")) game.Automation = new DeckTuningSmoke(game.TestPort);
                if (args.Contains("--smoke-deck-experience")) game.Automation = new DeckExperienceSmoke(game.TestPort);
                if (args.Contains("--capture-deck-workspace")) game.Automation = new DeckWorkspaceCapture(game.TestPort);
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
