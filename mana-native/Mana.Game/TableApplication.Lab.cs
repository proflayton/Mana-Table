using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private bool ExperienceLabOpen => services.Lab != null;
    private ExperienceLabSession Lab => services.Lab ?? throw new InvalidOperationException("No experience lab session is attached.");
    private int labGalleryPage;
    private void TraceLabInput(Microsoft.Xna.Framework.Input.MouseState mouse, Microsoft.Xna.Framework.Point viewport)
    {
        if (!ExperienceLabOpen || mouse.LeftButton == previousMouse.LeftButton && mouse.RightButton == previousMouse.RightButton) return;
        var hit = HitAt(pointer);
        try {
            File.AppendAllText(Path.Combine(profile, "input-trace.jsonl"), System.Text.Json.JsonSerializer.Serialize(new {
                time = now, stage = Lab.StageIndex, viewport = new { viewport.X, viewport.Y },
                raw = new { mouse.X, mouse.Y, left = mouse.LeftButton.ToString(), right = mouse.RightButton.ToString() },
                logical = new { pointer.X, pointer.Y }, hit = hit?.Id, intent = hit?.Intent.GetType().Name,
                accepted = AcceptedHit, reader = readingId, actions = sourceActions.View.Status.ToString(), error
            }) + Environment.NewLine);
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
    private bool TurnOrientationLab => ExperienceLabOpen && Lab.Scenario.Id.StartsWith("turn-orientation", StringComparison.Ordinal);
    private void HandleLabIntent(ShellIntent.Lab intent)
    {
        if (!ExperienceLabOpen) return;
        switch (intent) {
            case ShellIntent.ToggleTableStudy: SetTableStudy(!TableStudyEnabled); break;
            case ShellIntent.ToggleTurnFollow: SetTurnFollow(!followTurnEnabled); break;
            case ShellIntent.ShowWholeTable: SetTurnFollow(false); break;
            case ShellIntent.OpenLabGallery:
                overlay = "experience-lab"; CollapseCardReading();
                labGalleryPage = Math.Max(0, ExperienceScenarios.All.ToList().FindIndex(item => item.Id == Lab.Scenario.Id)) / 6;
                break;
            case ShellIntent.CloseLabGallery: overlay = ""; break;
            case ShellIntent.ReplayLabStage replay when !busy && replay.Stage >= 0 && replay.Stage < Lab.Stages.Count: ReplayLabStage(replay.Stage); break;
            case ShellIntent.SelectLabScenario selected when !busy && ExperienceScenarios.All.Any(scenario => scenario.Id == selected.ScenarioId): PresentLab(Lab.Select(selected.ScenarioId)); break;
            case ShellIntent.ExplainLabChange:
                if (matchPresentation.Changes.Count > 0) OpenChanges(); else { overlay = "history"; historyPage = 0; }
                break;
            case ShellIntent.ExitLab: RequestExit(); break;
        }
    }

    // Composed last so modal hit replacement cannot hide the reserved header controls.
    private void DrawExperienceLabHeader()
    {
        if (!ExperienceLabOpen) return;
        canvas.Panel(new(0, 0, 1600, 54), new Color(12, 25, 33), new Color(70, 108, 119), 0);
        canvas.Text("SCRIPTED LAB", 18, 7, Gold, .5f, 180, 1, true);
        if (!TurnOrientationLab) Button("lab-table-study", TableStudyEnabled ? "Table study: On" : "Table study: Off", 210, 3, 210,
            new ShellIntent.ToggleTableStudy(), accent: TableStudyEnabled, height: 22);
        if (TurnOrientationLab) {
            string followLabel = !followTurnEnabled ? "Try turn follow"
                : preferences.ReducedMotion ? "Follow: reduced motion"
                : boardViewMotion.LimitedByLayout ? "Follow: limited space" : "Follow turn: On";
            Button("lab-follow-turn", followLabel, 210, 3, 210,
                new ShellIntent.ToggleTurnFollow(), accent: followTurnEnabled, height: 22);
            Button("lab-whole-table", "Whole table", 620, 3, 200, new ShellIntent.ShowWholeTable(), height: 22);
        }
        Button("lab-battlefield", "Battlefields", 436, 3, 176, new MatchIntent.OpenZone(match!.ViewerId, "Battlefield"), height: 22);
        if (!TurnOrientationLab) DrawTableStates(match!, new Rectangle(620, 3, 220, 21));
        canvas.Text(Lab.Scenario.Name, 18, 29, Ink, .64f, 410, 1, true);
        canvas.Text($"{Lab.StageIndex + 1}/{Lab.Stages.Count} · {Lab.Stage.Name}", 436, 28, Muted, .57f, 402, 1);
        Button("lab-scenarios", "Scenarios", 850, 11, 140, new ShellIntent.OpenLabGallery(), !busy, height: 34);
        Button("lab-reset", "Reset", 1000, 11, 100, new ShellIntent.ReplayLabStage(Lab.StageIndex), !busy, height: 34);
        Button("lab-previous", "Previous", 1110, 11, 125, new ShellIntent.ReplayLabStage(Lab.StageIndex - 1), !busy && Lab.StageIndex > 0, height: 34);
        Button("lab-next", "Next", 1245, 11, 100, new ShellIntent.ReplayLabStage(Lab.StageIndex + 1), !busy && Lab.StageIndex + 1 < Lab.Stages.Count, height: 34);
        Button("lab-why", matchPresentation.Changes.Count > 0 ? "Why?" : "History", 1355, 11, 80,
            new ShellIntent.ExplainLabChange(), height: 34);
        Button("lab-exit", "Exit lab", 1445, 11, 130, new ShellIntent.ExitLab(), height: 34);
    }
    private void ReplayLabStage(int stage) => PresentLab(stage == Lab.StageIndex + 1 && stage < Lab.Stages.Count
        ? [Lab.AdvanceTo(stage)] : Lab.Replay(stage), BattlefieldFocused ? zonePlayer : null);
    private void PresentLab(IReadOnlyList<GameSnapshot> replay, int? focusOwner = null)
    {
        StopQuietPriority(); StopStackResolution(); expandedStackWorkload = ""; CollapseCardReading();
        automatic = false; overlay = ""; zoneName = null; selectedChangeId = null; changePage = 0;
        error = ""; commandFailure = false; notice = "";
        foreach (var state in replay) Accept(state);
        if (focusOwner is { } owner && match?.Players.Any(player => player.Id == owner) == true) OpenZone(owner, "Battlefield");
    }
    private void DrawExperienceLabGallery()
    {
        ModalFrame("Experience lab · authored presentation scenarios", new ShellIntent.CloseLabGallery());
        canvas.Text("These snapshots use the normal game renderer and input. They do not run Forge, connect to a table, or edit your real decks.",
            208, 183, Muted, .77f, 1150, 2);
        const int pageSize = 6;
        int pages = (ExperienceScenarios.All.Count + pageSize - 1) / pageSize;
        var visible = ExperienceScenarios.All.Skip(labGalleryPage * pageSize).Take(pageSize).ToArray();
        for (int index = 0; index < visible.Length; index++) {
            var scenario = visible[index];
            int x = 208 + index % 2 * 588, y = 238 + index / 2 * 125;
            canvas.Panel(new(x, y, 560, 118), new Color(17, 30, 39), scenario.Id == Lab.Scenario.Id ? Gold : Muted * .45f, 7);
            canvas.Text(scenario.Name, x + 14, y + 10, Ink, .8f, 530, 1, true);
            canvas.Text(scenario.Description, x + 14, y + 41, Muted, .61f, 530, 2);
            Button("lab-load:" + scenario.Id, "Open " + scenario.Id, x + 14, y + 82, 530,
                new ShellIntent.SelectLabScenario(scenario.Id), !busy, height: 28);
        }
        canvas.Text("Reset replays the baseline and its transitions. These cases use an isolated temporary profile.",
            208, 650, Muted, .6f, 1150, 2);
        Button("lab-gallery-prev", "Previous cases", 208, 734, 185, new ShellIntent.Page(ShellPage.LabGallery, -1), labGalleryPage > 0);
        Button("lab-gallery-next", "More cases", 405, 734, 185, new ShellIntent.Page(ShellPage.LabGallery, 1), labGalleryPage + 1 < pages);
        canvas.Text($"Page {labGalleryPage + 1} / {pages}", 620, 747, Muted, .65f, 260, 1);
    }
}

public static class ExperienceLabLauncher
{
    public static int Run(string[] args) => Run(args, null);

    internal static int Run(string[] args, Action<TableWindow, ExperienceLabSession>? configure)
    {
        string profile = Path.Combine(Path.GetTempPath(), "ManaTableExperienceLab", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try {
            string? Arg(string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
            int flag = Array.FindIndex(args, arg => arg == "--experience-lab" || arg.StartsWith("--experience-lab=", StringComparison.Ordinal));
            string id = flag >= 0 && args[flag].Contains('=') ? args[flag][(args[flag].IndexOf('=') + 1)..]
                : flag >= 0 && flag + 1 < args.Length && !args[flag + 1].StartsWith("--", StringComparison.Ordinal) ? args[flag + 1] : ExperienceScenarios.DefaultId;
            if (id.Length == 0) id = ExperienceScenarios.DefaultId;
            var lab = new ExperienceLabSession(id);
            int finalStage = lab.Stages.Count - 1;
            if (Arg("--stage") is { } requestedStage) {
                if (!int.TryParse(requestedStage, out int stage) || stage < 0 || stage >= lab.Stages.Count)
                    throw new ArgumentException("Use a zero-based --stage index from 0 to " + (lab.Stages.Count - 1) + ".");
                lab.Replay(stage);
                finalStage = stage;
            }
            Microsoft.Xna.Framework.Point? size = null;
            if (Arg("--size") is { } requestedSize) {
                var dimensions = requestedSize.Split('x');
                if (dimensions.Length != 2 || !int.TryParse(dimensions[0], out int width) || !int.TryParse(dimensions[1], out int height))
                    throw new ArgumentException("Use --size WIDTHxHEIGHT, for example 1600x900.");
                size = new(Math.Clamp(width, 800, 3840), Math.Clamp(height, 450, 2160));
            }
            string? capture = Arg("--capture") is { } target ? Path.GetFullPath(target) : null;
            string? replayCapture = Arg("--capture-replay") is { } replayTarget ? Path.GetFullPath(replayTarget) : null;
            if (capture != null && replayCapture != null) throw new ArgumentException("Choose either a still capture or a replay capture.");
            if (replayCapture != null) lab.Replay(0);
            if (capture != null) Directory.CreateDirectory(Path.GetDirectoryName(capture)!);
            var engine = new ExperienceLabEngine(lab);
            new PlayPreferences { Automatic = false, Sounds = false }.Save(profile);
            File.WriteAllText(Path.Combine(profile, "experience-lab.json"), System.Text.Json.JsonSerializer.Serialize(new {
                scenarioId = lab.Scenario.Id, stage = lab.StageIndex, scripted = true, profile, realDecks = false, forgeStarted = false
            }));
            string artCache = Arg("--art-cache") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManaTableNative", "art");
            try {
                var services = new TableServices(engine, engine, engine, engine.InitializeAsync) { Lab = lab, SourceActions = engine };
                using var window = new TableWindow(services, profile, capture, null, size, initialState: lab.State, artSource: new OfflineArt(artCache));
                window.Window.Title = "Mana Table — Experience Lab";
                if (args.Contains("--follow-turn")) window.TestPort.Post(() => window.TestPort.SetTurnFollow(true));
                if (args.Contains("--table-study")) window.TestPort.Post(() => window.TestPort.SetTableStudy(true));
                if (Arg("--focus-player") is { } requestedOwner) {
                    if (!int.TryParse(requestedOwner, out int owner) || !lab.State.Players.Any(player => player.Id == owner))
                        throw new ArgumentException("Choose a public player ID for --focus-player.");
                    window.TestPort.Post(() => window.TestPort.OpenBattlefield(owner));
                }
                if (replayCapture != null) window.Automation = new ExperienceLabReplayCapture(window, lab, replayCapture, finalStage);
                configure?.Invoke(window, lab);
                window.Run();
            } finally { engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            return 0;
        } catch (Exception error) {
            File.WriteAllText(Path.Combine(profile, "experience-lab-error.log"), error.ToString());
            if (!args.Contains("--capture") && !args.Contains("--capture-replay"))
                MessageBox.Show(error.Message, "Experience lab", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
    private sealed class OfflineArt(string directory) : ICardArtSource
    {
        public async Task<byte[]?> LoadAsync(ArtRequest request, CancellationToken cancellationToken)
        {
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Key)));
            string path = Path.Combine(directory, key + ".jpg");
            try { return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }
        public void Dispose() { }
    }
}
