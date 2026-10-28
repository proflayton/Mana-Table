using System.Diagnostics;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Mana.Scenarios;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed class DrawTriggersSmoke : ITableAutomation
{
    private readonly TableApplication.Probe view;
    private readonly TableWindow.Probe? window;
    private readonly ExperienceLabSession lab;
    private readonly Point viewport;
    private readonly Queue<(Func<bool> Ready, Action Action)> steps = [];
    private readonly List<string> checks = [];
    private readonly List<object> observations = [];
    private MouseState mouse;
    private double lastStep;
    private bool finished;
    private TableHit? retainedPile;
    private double armedAt;
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;

    internal static void Run(string profile, Point? dimensions)
    {
        Prepare(profile); var lab = new ExperienceLabSession(StackTriggersScenario.Id);
        var size = dimensions ?? new Point(1000, 740);
        using var app = new TableWindow(TestTableServices.For(new ExperienceLabEngine(lab)), profile, null, null, size,
            initialState: lab.State, artSource: new MagicArtSource(profile), enableLiveTableStudy: true);
        app.Automation = new DrawTriggersSmoke(app.TestPort, lab, size, app.TestPort); app.Run();
    }
    internal static void RunHeadless(string profile, Action<bool, string> check)
    {
        string path = Path.Combine(profile, "draw-triggers"); Prepare(path);
        var lab = new ExperienceLabSession(StackTriggersScenario.Id);
        using var app = new TableApplication(TestTableServices.For(new ExperienceLabEngine(lab)), path, initialState: lab.State) { Automated = true };
        app.EnableLiveTableStudy(); app.Initialize();
        var driver = new DrawTriggersSmoke(app.TestPort, lab, new(1600, 900));
        var clock = Stopwatch.StartNew(); double now = 0;
        try {
            while (!driver.finished) {
                if (clock.Elapsed > TimeSpan.FromSeconds(35)) throw new TimeoutException("The draw-trigger pointer journey did not finish.");
                now += 1d / 60;
                app.Update(new(TimeSpan.FromSeconds(now), TimeSpan.FromSeconds(1d / 60)), driver.ReadMouse(), new(), new(1600, 900));
                app.ReadPresentation(); driver.Advance(); Thread.Yield();
            }
            foreach (string description in driver.checks) check(true, description);
            var original = StackTriggersScenario.Snapshot(1);
            var altered = original with { Stack = original.Stack.SetItem(2, original.Stack[2] with { Targets = [new("player", "1", "Other", 1)] }) };
            check(StackWorkloads.From(altered.Stack).Length == 3, "A different target splits a repeated source workload without merging distinct effects");
            altered = original with { Stack = original.Stack.SetItem(2, original.Stack[2] with {
                Card = original.Stack[2].Card! with { VisualId = "other-physical-skulker", CombatId = "other-physical-skulker" } }) };
            check(StackWorkloads.From(altered.Stack).Length == 3, "Same-name permanents remain different source workloads when their physical occurrence IDs differ");
        } catch (Exception error) { driver.Save(error.ToString()); throw; }
    }
    private static void Prepare(string profile)
    {
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true }.Save(profile);
    }
    private DrawTriggersSmoke(TableApplication.Probe view, ExperienceLabSession lab, Point viewport, TableWindow.Probe? window = null)
    {
        this.view = view; this.lab = lab; this.viewport = viewport; this.window = window; Move(new(12, 65));
        Wait(() => view.Loaded && view.DisplayedPose(StackTriggersScenario.Skulker) != null
            && (window == null || lab.State.Viewer!.Zones.SelectMany(zone => zone.Cards).All(card => view.Art(card) != null)), () => {
            lab.AdvanceTo(1); view.Accept(lab.State);
        });
        Wait(() => view.Loaded && view.StackWorkloads.Count == 2 && view.Hits.Any(hit => hit.Id == "stack-resolution-arm")
            && (window == null || lab.State.Viewer!.Zones.SelectMany(zone => zone.Cards).All(card => view.Art(card) != null)), () => {
            var groups = view.StackWorkloads;
            Check(groups.SelectMany(group => group.MemberIds).Distinct().Count() == 71
                && groups.Select(group => group.MemberIds.Length).Order().SequenceEqual([35, 36])
                && groups.Count(group => group.IsNext) == 1 && groups.Single(group => group.IsNext).MemberIds[0] == lab.State.Stack[0].Id
                && groups.Single(group => group.IsNext).Positions.Take(3).SequenceEqual([1, 3, 5]),
                "Source piles retain all 71 real interleaved occurrences and their actual positions while exactly one pile marks the next stack member");
            Check(groups.All(group => group.Bounds.Height <= 165) && view.Match!.Stack.Length == 71,
                "The repeated workload uses bounded source-art piles without shrinking, reordering or replacing the actual engine stack");
            retainedPile = view.Hits.Single(hit => hit.Id == "stack-workload:draw-trigger-0");
            Capture("01-interleaved-source-piles"); MoveHit(retainedPile.Id);
        });
        Wait(() => view.ReferencePreviewStackId == "draw-trigger-0", () => {
            Check(lab.StageIndex == 1 && view.ReferencePreviewCardId == StackTriggersScenario.Skulker,
                "Hovering a source pile reads its exact current member and source without spending priority or predicting Pir's counter result");
            Capture("02-exact-trigger-hover"); MoveHit("stack-workload:draw-trigger-0");
        }); Click();
        Wait(() => view.Hits.Any(hit => hit.Id == "stack-workload-member:draw-trigger-40"), () => {
            Check(view.StackRows.Count == 36 && view.StackRows.Select(row => row.Position).SequenceEqual(Enumerable.Range(0, 36).Select(index => index * 2 + 1)),
                "Opening a physical source pile fans its separate current references with absolute stack positions, preserving the interleaved order");
            Capture("03-source-pile-open"); MoveHit("stack-workload-member:draw-trigger-40");
        });
        Wait(() => view.ReferencePreviewStackId == "draw-trigger-40", () => {
            Check(lab.StageIndex == 1, "A deeper fanned member remains read-only and identifies the exact current occurrence rather than choosing a trigger");
            Capture("04-member-41-reading"); MoveHit("stack-workload-close");
        }); Click();
        Wait(() => view.StackWorkloads.Count == 2 && view.Hits.Any(hit => hit.Id == "stack-resolution-arm"), () => MoveHit("stack-resolution-arm")); Click();
        Wait(() => view.ResolvingCapturedStack && view.Hits.Any(hit => hit.Id == "stack-resolution-stop"), () => {
            Check(view.StackResolutionRemaining > 0 && !view.Hits.Any(hit => hit.Id == "stack-resolution-arm"),
                "Resolve these keeps one stable Take control action and a captured remaining workload instead of a flashing passing status");
            armedAt = view.Now; Capture("05-resolving-captured-workload"); Move(new(12, 65));
        });
        Wait(() => lab.StageIndex == 2 && !view.Busy, () => {
            Check(lab.State.Stack.Length == 70 && lab.State.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == StackTriggersScenario.Skulker).Counters["+1/+1"] == 30
                && view.ResolvingCapturedStack && !view.Activate(retainedPile!) && view.Now - armedAt < 8,
                "One actual continuation shrinks the workload by one while the independently reported Skulker counter result grows by two and retires the old gesture");
            Capture("06-one-trigger-settled"); MoveHit("stack-resolution-stop");
        }); Click();
        Wait(() => !view.ResolvingCapturedStack && !view.Busy && view.Hits.Any(hit => hit.Id == "stack-resolution-arm"), () => {
            Check(lab.StageIndex == 2 && view.Held,
                "Take control holds this turn and stops the captured continuation before a second trigger is passed");
            Capture("07-take-control"); MoveHit("stack-resolution-arm");
        }); Click();
        Wait(() => lab.StageIndex == 3 && !view.Busy, () => {
            Check(lab.State.Stack.Length == 69 && lab.State.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == StackTriggersScenario.Toothy).Counters["+1/+1"] == 26,
                "The next real trigger gets its own visible settled result on the other source rather than a guessed aggregate counter total");
            Check(!view.Held,
                "Deliberately resolving the newly captured stack overrides Hold without asking the player to discover a second timing control");
            Capture("08-other-source-settled"); Move(new(12, 65));
        });
        Wait(() => lab.StageIndex == 4 && !view.Busy && !view.ResolvingCapturedStack
            && (window == null || view.Art(lab.State.Stack[0].Card!) != null), () => {
            Check(lab.State.Stack[0].Id == "burst-opponent-response" && view.StackWorkloads.Single(group => group.IsNext).MemberIds[0] == "burst-opponent-response",
                "A new opposing response revokes the captured workload and becomes the actual next object without silently yielding it");
            Capture("09-new-response-interrupts"); MoveHit("confirm");
        }); Click();
        Wait(() => lab.StageIndex == 5 && !view.Busy, () => {
            Check(lab.State.Decision?.Payload is ConfirmationPayload && !view.ResolvingCapturedStack
                && !view.Hits.Any(hit => hit.Id is "stack-resolution-arm" or "stack-resolution-stop"),
                "A required choice takes foreground with only its own current continuation and no inherited stack-yield controls");
            Capture("10-required-choice-current"); Save(null); finished = true; window?.Exit();
        });
    }
    private void Move(Point point) => mouse = new(point.X, point.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    private void MoveHit(string id)
    {
        float scale = Math.Min(viewport.X / 1600f, viewport.Y / (float)view.DesignHeight);
        var center = view.Hits.Last(hit => hit.Id == id).Bounds.Center;
        Move(new((int)MathF.Round((viewport.X - 1600 * scale) / 2 + center.X * scale),
            (int)MathF.Round((viewport.Y - view.DesignHeight * scale) / 2 + center.Y * scale)));
    }
    private void Wait(Func<bool> ready, Action action) => steps.Enqueue((ready, action));
    private void Click()
    {
        Wait(() => true, () => mouse = new(mouse.X, mouse.Y, 0, ButtonState.Pressed, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released));
        Wait(() => true, () => Move(new(mouse.X, mouse.Y)));
    }
    private void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); checks.Add(description); }
    private void Capture(string name)
    {
        observations.Add(new { name, lab.StageIndex, workloads = view.StackWorkloads.ToArray(), view.StackResolutionRemaining,
            view.AnnouncementId, view.AnnouncementCount, view.ReferencePreviewStackId, view.Now });
        if (window == null) return;
        using var stream = File.Create(Path.Combine(view.Profile, name + ".png")); window.Surface.SaveAsPng(stream, window.Surface.Width, window.Surface.Height);
    }
    private void Save(string? error) => File.WriteAllText(Path.Combine(view.Profile, "draw-triggers-results.json"), JsonSerializer.Serialize(new {
        passed = error == null, error, checks, observations, stage = lab.StageIndex,
        scope = "Production composition and pointer journey over declared public checkpoints; no rules proof or human UX approval.", realArtwork = window != null
    }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    private void Advance()
    {
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (!steps.TryPeek(out var step)) return;
        if (!step.Ready()) {
            if (view.Now - lastStep > (window == null ? 12 : 45)) throw new TimeoutException("Draw trigger journey stopped at checkpoint " + lab.StageIndex);
            return;
        }
        steps.Dequeue(); step.Action(); lastStep = view.Now;
    }
    public void Frame(Texture2D surface)
    {
        try { Advance(); } catch (Exception error) { Capture("failure"); Save(error.ToString()); window?.Exit(); throw; }
    }
}
