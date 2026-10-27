using System.Diagnostics;
using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Mana.Planning;
using Mana.Scenarios;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// The window and graphics-free checks use the same real pointer journey over
// authored checkpoints. The Java tests independently establish payment rules.
internal sealed class ManaPaymentSmoke : ITableAutomation
{
    private readonly TableApplication.Probe view;
    private readonly TableWindow.Probe? window;
    private readonly ExperienceLabSession lab;
    private readonly Point viewport;
    private readonly Queue<(Func<bool>? Ready, Action Action)> steps = [];
    private readonly List<string> checks = [];
    private readonly List<object> observations = [];
    private readonly List<GameCommand> recordedReplies = [];
    private MouseState mouse;
    private double lastStep;
    private Mana.Renderer.Pose? initialRingPose;
    private bool finished;
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;

    internal static void Run(string profile, Point? dimensions)
    {
        var size = dimensions ?? new Point(1000, 740); Prepare(profile);
        var lab = new ExperienceLabSession(ManaPaymentScenario.Id);
        var engine = new ExperienceLabEngine(lab);
        using var app = new TableWindow(TestTableServices.For(engine), profile, null, null, size,
            initialState: lab.State, artSource: new MagicArtSource(profile), enableLiveTableStudy: true);
        app.Automation = new ManaPaymentSmoke(app.TestPort, lab, size, app.TestPort);
        app.Run();
    }

    internal static void RunHeadless(string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "mana-payment"); Prepare(directory);
        var lab = new ExperienceLabSession(ManaPaymentScenario.Id);
        var engine = new ExperienceLabEngine(lab);
        using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: lab.State) { Automated = true };
        app.EnableLiveTableStudy(); app.Initialize();
        var driver = new ManaPaymentSmoke(app.TestPort, lab, new(1600, 900));
        var elapsed = Stopwatch.StartNew(); double seconds = 0;
        try {
            while (!driver.finished) {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(35)) throw new TimeoutException("Mana card/source/payment pointer continuation did not finish.");
                seconds += 1.0 / 60;
                app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60)), driver.ReadMouse(), new(), new(1600, 900));
                app.ReadPresentation(); driver.Advance(); Thread.Yield();
            }
            foreach (string description in driver.checks) check(true, description);
            CheckCountedBills(directory, check);
        } catch (Exception error) { driver.Save(error.ToString()); throw; }
    }

    private static void CheckCountedBills(string profile, Action<bool, string> check)
    {
        var state = ManaPaymentScenario.Snapshot(1);
        var payload = (PaymentPayload)state.Decision!.Payload;
        var parts = ImmutableArray.Create(new ManaBillPart("{129}", 1), new ManaBillPart("{U}", 2), new ManaBillPart("{W}", 1_000_000_000));
        var engine = new InteractionScenarioEngine();
        engine.Present(state with { Decision = state.Decision with { Payload = payload with { UnpaidMana = [], UnpaidManaParts = parts } } });
        using var app = new TableApplication(TestTableServices.For(engine), profile, initialState: engine.State) { Automated = true };
        app.EnableLiveTableStudy(); app.Initialize();
        var loading = Stopwatch.StartNew();
        int nextFrame = 1;
        while (app.TestPort.ManaJourney == null && loading.Elapsed < TimeSpan.FromSeconds(5)) {
            int frame = nextFrame++;
            app.Update(new(TimeSpan.FromSeconds(frame / 60.0), TimeSpan.FromSeconds(1.0 / 60)), new(), new(), new(1600, 900));
            app.ReadPresentation(); Thread.Yield();
        }
        var view = app.TestPort;
        File.WriteAllText(Path.Combine(profile, "counted-payment-diagnostic.json"), JsonSerializer.Serialize(new {
            view.Loaded, view.Error, view.ManaJourney, commands = view.Frame?.Drawing?.Commands.Count,
            state = view.Match?.Decision, cards = view.Frame?.Cards.Select(card => new { card.Id, card.Zone, card.Display.Size }).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        check(view.ManaJourney is { Payment: true } bill && bill.Parts.SequenceEqual(parts),
            "A large exact bill preserves generic 129 and repeated colored counts directly without allocating or drawing one object per mana unit");
        check(view.ManaJourney!.FundingStatus != ManaFundingStatus.Funded && view.Hits.Any(hit => hit.Id == "mana-auto"),
            "A bill beyond the bounded preview allowance keeps the current manual payment controls reachable without a confident funding cue");
        int largeBillCommands = view.Frame!.Drawing!.Commands.Count;
        engine.Present(state with { Decision = state.Decision with {
            Payload = payload with { UnpaidMana = [], UnpaidManaParts = [parts[0], parts[1], new("{W}", 2)] }
        } });
        view.Accept(engine.State);
        for (int step = 0; step < 10; step++) {
            int frame = nextFrame++;
            app.Update(new(TimeSpan.FromSeconds(frame / 60.0), TimeSpan.FromSeconds(1.0 / 60)), new(), new(), new(1600, 900));
            app.ReadPresentation();
        }
        check(view.ManaJourney?.Parts.Length == 3 && Math.Abs(largeBillCommands - view.Frame!.Drawing!.Commands.Count) <= 32,
            "Changing two white requirements to a billion retains bounded composition work instead of adding per-unit drawing commands");
        var many = Enumerable.Range(1, 128).Select(value => new ManaBillPart("{" + value + "}", 1)).ToImmutableArray();
        engine.Present(state with { Decision = state.Decision with {
            Context = state.Decision.Context with { Message = "Pay {8256}." },
            Payload = payload with { UnpaidMana = [], UnpaidManaParts = many }
        } });
        view.Accept(engine.State);
        for (int step = 0; step < 10; step++) {
            int frame = nextFrame++;
            app.Update(new(TimeSpan.FromSeconds(frame / 60.0), TimeSpan.FromSeconds(1.0 / 60)), new(), new(), new(1600, 900));
            app.ReadPresentation();
        }
        check(view.ManaJourney == null && view.Hits.Any(hit => hit.Id == "confirm")
            && view.Frame!.Drawing!.Commands.Count < 2000,
            "A bill too complex for four readable rows safely keeps the exact engine instruction and normal manual controls instead of clipping, crashing or drawing an unbounded overlay");
        var poolState = ManaPaymentScenario.Snapshot(5);
        engine.Present(poolState with { Decision = poolState.Decision! with {
            Payload = ((PaymentPayload)poolState.Decision!.Payload) with { UnpaidManaParts = [new("{C}", 1)] }
        } });
        view.Accept(engine.State);
        for (int step = 0; step < 10; step++) {
            int frame = nextFrame++;
            app.Update(new(TimeSpan.FromSeconds(frame / 60.0), TimeSpan.FromSeconds(1.0 / 60)), new(), new(), new(1600, 900));
            app.ReadPresentation();
        }
        check(view.ManaJourney is { Advice: "Use floating mana", FundingStatus: ManaFundingStatus.Funded }
            && view.ManaSources.Count == 0 && view.Hits.Any(hit => hit.Id == "mana-pool:0:C" && hit.Intent is MatchIntent.SpendFloatingMana),
            "A pool-only witness points to the actual usable glowing pip instead of asking for nonexistent lit source cards");
    }

    private static void Prepare(string profile)
    {
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true }.Save(profile);
    }

    private ManaPaymentSmoke(TableApplication.Probe view, ExperienceLabSession lab, Point viewport, TableWindow.Probe? window = null)
    {
        this.view = view; this.lab = lab; this.viewport = viewport; this.window = window;
        Move(new(12, 65));
        Wait(() => view.Loaded && view.DisplayedPose(ManaPaymentScenario.Spell) != null
            && (window == null || lab.State.Viewer!.Zones.SelectMany(zone => zone.Cards).All(card => view.Art(card) != null)), () => MoveCard(ManaPaymentScenario.Spell));
        Wait(() => view.ManaJourney is { Payment: false, FundingStatus: ManaFundingStatus.Funded }, () => {
            Check(lab.ManaPaymentReplies.Count == 0 && view.HoverPreview == null,
                "Hovering a hand cost reads current funding without enlarging the card, spending mana or submitting any action");
            Check(view.ManaJourney!.Symbols.SequenceEqual(["{1}", "{U}"])
                && view.ManaSources.Count == 2
                && view.ManaSources.Count(source => source.SourceId == ManaPaymentScenario.Dual) == 1
                && view.ManaSources.Single(source => source.SourceId == ManaPaymentScenario.Dual).Output.GetValueOrDefault("U") == 1,
                "A lifted cost points at one physical dual-land contribution; its two alternatives do not become two available mana");
            Check(view.ManaPoolBadge(0) is { Total: 0, Parts.Length: 0 },
                "Suggested source output never appears as floating mana beside the portrait");
            Check(CaptionClearsSeat(), "The lifted hand cost stays clear of the portrait, turn controls and actual floating-pool area");
            initialRingPose = view.DisplayedPose(ManaPaymentScenario.Ring);
            Capture("01-hover-cost-sources"); MoveCard(ManaPaymentScenario.Spell);
        }); Click();
        Wait(() => ReaderReady(ManaPaymentScenario.Spell) && view.Hits.Any(hit => hit.Id == "read-card-action"), () => {
            Check(view.ManaJourney is { } cost && !cost.Bounds.Intersects(view.DisplayedPose(ManaPaymentScenario.Spell)!.Value.Bounds)
                && lab.ManaPaymentReplies.Count == 0,
                "The readable full card retains its rules and a separate adjacent cost; opening it remains noncommitting");
            Capture("02-card-cost-sources"); MoveHit("read-card-action");
        }); Click();
        Wait(() => lab.StageIndex == 1 && !view.Busy && view.ManaJourney is { Payment: true }, () => {
            Check(view.ManaJourney!.SourceId == ManaPaymentScenario.Spell && view.ManaJourney.Symbols.SequenceEqual(["{1}", "{U}"])
                && view.Frame!.Cards.Count(card => CardViews.Identity(card.Card) == ManaPaymentScenario.Spell) == 1
                && !view.Hits.Any(hit => hit.Id is "confirm" or "cancel") && view.Hits.Any(hit => hit.Id == "mana-auto"),
                "Committing the card keeps one source occurrence and moves the exact unpaid bill and payment controls onto that card");
            Capture("03-exact-bill"); MoveCard(ManaPaymentScenario.Ring);
        }); Click();
        Wait(() => ReaderReady(ManaPaymentScenario.Ring) && view.Hits.Any(hit => hit.Id == "read-card-action"), () => MoveHit("read-card-action")); Click();
        Wait(() => lab.StageIndex == 2 && !view.Busy && view.ManaJourney?.Symbols.SequenceEqual(["{U}"]) == true
            && SettledTapped(ManaPaymentScenario.Ring), () => {
            Check(lab.State.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == ManaPaymentScenario.Ring).Tapped
                && view.ManaPoolBadge(0)?.Parts.SingleOrDefault(part => part.Key == "C")?.Amount == "1"
                && view.Hits.Single(hit => hit.Id == "mana-pool:0:C").Intent is MatchIntent.OpenManaPool,
                "The rock visibly turns, generic debt disappears, and its actual leftover colorless pip stays distinct from the unpaid blue");
            var tappedRing = view.DisplayedPose(ManaPaymentScenario.Ring)!.Value;
            Check(initialRingPose is { } untappedRing && untappedRing.Size == tappedRing.Size
                && untappedRing.Bounds.Height > untappedRing.Bounds.Width
                && tappedRing.Bounds.Width > tappedRing.Bounds.Height
                && Math.Abs(Math.Abs(MathHelper.WrapAngle(tappedRing.Angle - untappedRing.Angle)) - MathHelper.PiOver2) < .01f,
                "A settled rock keeps the same physical face and makes a visible quarter-turn from tall to wide rather than cancelling the tap by swapping dimensions");
            Capture("04-rock-tapped-blue-remains"); MoveCard(ManaPaymentScenario.Dual);
        }); Click();
        Wait(() => ReaderReady(ManaPaymentScenario.Dual) && view.Hits.Any(hit => hit.Id == "read-card-action"), () => {
            Check(view.Frame!.Drawing!.Commands.OfType<Mana.Renderer.DrawCommand.Text>().Any(text => text.Value?.Contains("Add") == true),
                "A flexible mana source exposes its current symbol-bearing choices on its own readable card");
            Check(view.ManaJourney is { SourceZone: "Stack" } bill
                && !bill.Bounds.Intersects(view.DisplayedPose(ManaPaymentScenario.Dual)!.Value.Bounds)
                && !view.Hits.Where(hit => hit.Id == "source-actions-surface").Any(hit => hit.Bounds.Intersects(bill.Bounds)),
                "Reading a dual source keeps its current color choices and the pending spell's exact bill visible in separate adjacent space");
            Capture("04b-dual-source-color-choices");
            MoveHit("read-card-action");
        }); Click();
        Wait(() => lab.StageIndex == 3 && !view.Busy && view.Match?.Stack.Length == 1, () => {
            Check(view.ManaJourney == null && view.Match!.Stack[0].Card?.VisualId == ManaPaymentScenario.Spell
                && lab.State.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == ManaPaymentScenario.Dual).Tapped
                && view.ManaPoolBadge(0)?.Parts.Single(part => part.Key == "C")?.Amount == "1",
                "The blue source turns, the exact bill retires, and the same paid card reaches the stack while real unspent colorless remains");
            Capture("05-paid-card-on-stack"); MoveHit("confirm");
        }); Click();
        Wait(() => lab.StageIndex == 4 && !view.Busy, () => {
            Check(lab.State.Viewer!.Zone("Battlefield").Cards.Any(card => card.VisualId == ManaPaymentScenario.Spell)
                && lab.ManaPaymentReplies.Count == 4,
                "The authored response window settles into the same battlefield card after exactly the intended scoped gestures");
            Load(5);
        });
        Wait(() => view.ManaJourney?.Symbols.SequenceEqual(["{1}", "{C}"]) == true
            && view.Hits.Any(hit => hit.Id == "mana-pool:0:C" && hit.Intent is MatchIntent.SpendFloatingMana), () => {
            Check(view.ManaJourney!.Symbols.Contains("{C}") && view.ManaJourney.Symbols.Contains("{1}"),
                "An exact colorless requirement and a generic requirement retain different printed symbols");
            Check(view.ManaJourney.SourceZone == "Stack" && CaptionClearsSeat(),
                "A payment source still present in a zone anchors its bill to the pending source reference and keeps the portrait and usable pip unobscured");
            Capture("06-colorless-pip-can-pay"); MoveHit("mana-pool:0:C");
        }); Click();
        Wait(() => lab.StageIndex == 6 && !view.Busy, () => {
            Check(lab.ManaPaymentReplies[^1] is SpendPoolMana { Color: "C" }
                && view.ManaJourney?.Symbols.SequenceEqual(["{1}"]) == true
                && view.ManaPoolBadge(0) is { Total: 0, Parts.Length: 0 },
                "Only a current usable floating pip spends one exact color, immediately shrinking the actual bill and pool");
            Load(7);
        });
        Wait(() => view.DisplayedPose(ManaPaymentScenario.Spell) != null, () => MoveCard(ManaPaymentScenario.Spell));
        Wait(() => view.ManaJourney is { Payment: false, FundingStatus: ManaFundingStatus.Unknown, Advice: "Check sources manually" }, () => {
            Check(lab.State.Viewer!.Zone("Hand").Cards.Single(card => card.VisualId == ManaPaymentScenario.Spell).Selectable
                && view.ManaJourney!.Advice == "Check sources manually",
                "An uncertain funding preview keeps the engine's real manual action reachable instead of claiming the spell cannot be played");
            Check(!view.ManaJourney!.Bounds.Intersects(view.DisplayedPose(ManaPaymentScenario.Dual)!.Value.Bounds),
                "An uncertain cost caption preserves the actual resource face even when it cannot suggest a confident source witness");
            Capture("07-uncertain-manual-route"); Save(null); finished = true; window?.Exit();
        });
    }

    private bool ReaderReady(string id) => view.DisplayedPose(id) is { Size.X: >= 300 } pose
        && view.Frame!.Cards.Any(card => card.Id == id && card.Focused && Vector2.Distance(card.Display.Center, (card.ReaderTarget ?? card.Rest).Center) < 2);
    private bool SettledTapped(string id) => view.Frame!.Cards.FirstOrDefault(card => card.Id == id) is { Focused: false } card
        && Math.Abs(MathHelper.WrapAngle(card.Display.Angle - card.Rest.Angle)) < .01f
        && Vector2.Distance(card.Display.Size, card.Rest.Size) < 1;
    private bool CaptionClearsSeat()
    {
        if (view.ManaJourney is not { } journey) return false;
        var hero = view.Frame!.Scene!.Seat(view.Match!.ViewerId).Geometry.Hero;
        return !Mana.Renderer.TurnControlLayout.For(hero).Reservations.Any(area => area.Intersects(journey.Bounds))
            && !new Microsoft.Xna.Framework.Rectangle((int)hero.X - 58, (int)hero.Y - 58, 116, 116).Intersects(journey.Bounds)
            && (view.ManaPoolBadge(view.Match.ViewerId) is not { } pool || !pool.Bounds.Intersects(journey.Bounds));
    }
    private void Load(int stage) { recordedReplies.AddRange(lab.ManaPaymentReplies); lab.Replay(stage); view.Accept(lab.State); Move(new(12, 65)); }
    private float Scale => Math.Min(viewport.X / 1600f, viewport.Y / (float)view.DesignHeight);
    private Point Pixel(Vector2 logical) => new((int)MathF.Round((viewport.X - 1600 * Scale) / 2 + logical.X * Scale),
        (int)MathF.Round((viewport.Y - view.DesignHeight * Scale) / 2 + logical.Y * Scale));
    private void Move(Point point) => mouse = new(point.X, point.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    private void MoveCard(string id) => Move(Pixel(view.DisplayedPose(id)!.Value.Center));
    private void MoveHit(string id) => Move(Pixel(view.Hits.Last(hit => hit.Id == id).Bounds.Center.ToVector2()));
    private void Step(Action action) => steps.Enqueue((null, action));
    private void Wait(Func<bool> ready, Action action) => steps.Enqueue((ready, action));
    private void Click() { Step(() => mouse = new(mouse.X, mouse.Y, 0, ButtonState.Pressed, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released)); Step(() => Move(new(mouse.X, mouse.Y))); }
    private void Check(bool passed, string description) { if (!passed) throw new InvalidOperationException(description); checks.Add(description); }
    private void Capture(string name)
    {
        observations.Add(new { name, lab.StageIndex, view.ManaJourney, sources = view.ManaSources.ToArray(), view.Now });
        if (window == null) return;
        using var stream = File.Create(Path.Combine(view.Profile, name + ".png")); window.Surface.SaveAsPng(stream, window.Surface.Width, window.Surface.Height);
        window.CaptureWindow(Path.Combine(view.Profile, name + "-window.png"));
    }
    private void Save(string? error) => File.WriteAllText(Path.Combine(view.Profile, "mana-payment-results.json"), JsonSerializer.Serialize(new {
        passed = error == null, error, checks, observations,
        replies = recordedReplies.Concat(lab.ManaPaymentReplies).Select(reply => new {
            kind = reply.GetType().Name, payload = JsonSerializer.SerializeToElement(reply, reply.GetType())
        }).ToArray(),
        scope = "Production pointer/composition journey over authored payment checkpoints, not a rules simulation or human UX approval.", realArtwork = window != null
    }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    private void Advance()
    {
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (!steps.TryPeek(out var step)) return;
        if (step.Ready?.Invoke() == false) {
            if (view.Now - lastStep > (window == null ? 6 : 30)) throw new TimeoutException("Mana journey did not reach the next real gesture: " + lab.StageIndex);
            return;
        }
        steps.Dequeue(); step.Action(); lastStep = view.Now;
    }
    public void Frame(Texture2D surface)
    {
        try { Advance(); } catch (Exception error) { Capture("failure"); Save(error.ToString()); window?.Exit(); throw; }
    }
}
