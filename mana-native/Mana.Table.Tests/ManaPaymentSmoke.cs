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
using Keys = Microsoft.Xna.Framework.Input.Keys;

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
    private TableHit? previousManaColor;
    private int beforeColorChoiceReplies;
    private bool finished;
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;

    internal static void Run(string profile, Point? dimensions)
    {
        var size = dimensions ?? new Point(1000, 740); Prepare(profile);
        var completed = new List<string>();
        var lab = new ExperienceLabSession(ManaPaymentScenario.Id);
        var engine = new ExperienceLabEngine(lab);
        using (var app = new TableWindow(TestTableServices.For(engine), profile, null, null, size,
            initialState: lab.State, artSource: new MagicArtSource(profile), enableLiveTableStudy: true)) {
            var driver = new ManaPaymentSmoke(app.TestPort, lab, size, app.TestPort);
            app.Automation = driver;
            app.Run();
            if (!driver.finished) throw new InvalidOperationException("The mana pointer journey closed before completing its checks.");
            completed.AddRange(driver.checks);
        }
        foreach (float scale in new[] { 1f, 1.3f }) {
            string directory = Path.Combine(profile, "counted-outputs-" + (int)(scale * 100)); Prepare(directory);
            (PlayPreferences.Load(directory) with { TextScale = scale }).Save(directory);
            var counted = new InteractionScenarioEngine(); counted.Present(CountedSourceOutput(100, false));
            using var app = new TableWindow(TestTableServices.For(counted), directory, null, "controlled-frames", size,
                initialState: counted.State, artSource: new MagicArtSource(profile), enableLiveTableStudy: true);
            var driver = new ManaPaymentSmoke(app.TestPort, new ExperienceLabSession(ManaPaymentScenario.Id), size, counted);
            app.Automation = driver;
            app.Run();
            if (!driver.finished) throw new InvalidOperationException("The counted mana output capture closed before completing its checks.");
            completed.AddRange(driver.checks);
        }
        File.WriteAllText(Path.Combine(profile, "mana-payment-ci-results.json"), JsonSerializer.Serialize(
            completed.Select(description => new { description, passed = true }), new JsonSerializerOptions { WriteIndented = true }));
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
            CheckCountedSourceOutputs(directory, check);
            var child = ManaPaymentScenario.Snapshot(9);
            var options = (OptionsPayload)child.Decision!.Payload;
            var controls = new DecisionDockControlFacts { Automatic = false };
            check(DecisionDockPresentation.From(child, controls).RequiredSurface == DecisionRequiredSurface.Source
                && DecisionDockPresentation.From(child with { Decision = child.Decision with {
                    Payload = options with { ManaPaymentContext = null } } }, controls).RequiredSurface == DecisionRequiredSurface.Gallery,
                "Only an explicit owned payment context uses the source-orb surface; an ordinary same-symbol choice retains its general choice gallery");
            check(!GameCommandPermissions.Allows(child, new PayMana(child.Id, child.Decision.Id))
                && !GameCommandPermissions.Allows(child, new CancelPayment(child.Id, child.Decision.Id)),
                "A child's read-only original bill grants neither payment nor cancellation authority");
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
        var reductionState = ManaPaymentScenario.Snapshot(14);
        var reduction = (EntitySelectionPayload)reductionState.Decision!.Payload;
        engine.Present(reductionState with { Decision = reductionState.Decision with { Payload = reduction with { Complete = new("Done", false) } } });
        view.Accept(engine.State);
        for (int step = 0; step < 10; step++) {
            int frame = nextFrame++;
            app.Update(new(TimeSpan.FromSeconds(frame / 60.0), TimeSpan.FromSeconds(1.0 / 60)), new(), new(), new(1600, 900));
            app.ReadPresentation();
        }
        check(view.ManaJourney is { Parts.IsEmpty: true }
            && view.Frame!.Drawing!.Commands.OfType<Mana.Renderer.DrawCommand.Text>().Any(text => text.Value == "COST COVERED")
            && !view.Hits.Any(hit => hit.Id is "confirm" or "mana-auto" or "mana-cancel"),
            "An exact zero contribution bill remains readable but cannot fabricate Done authority when the current completion reply is disabled");
    }

    private static GameSnapshot CountedSourceOutput(int count, bool mixed)
    {
        var original = ManaPaymentScenario.Snapshot(1) with { DecisionOwnerId = 0 };
        string id = "counted-source-" + count + (mixed ? "-mixed" : "");
        var producer = original.Viewer!.Zone("Battlefield").Cards[0] with {
            VisualId = id, CombatId = id, Name = mixed ? "Empowered mana source" : "Everflowing Chalice",
            ArtName = mixed ? "Sol Ring" : "Everflowing Chalice", Selectable = true, Tapped = false,
            Counters = ImmutableDictionary<string, int>.Empty.Add("CHARGE", count)
        };
        var output = ImmutableDictionary<string, int>.Empty.Add("C", count);
        if (mixed) output = output.Add("U", 3);
        var funding = new ManaFundingContext(SemanticKnowledge.Known, ImmutableDictionary<string, int>.Empty,
            [new(id, [new([new(ActionCostKind.Tap, 1, true, "")],
                new(SemanticKnowledge.Known, [new(output)]), SemanticKnowledge.Known)])]);
        return original with {
            Players = original.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name == "Battlefield" ? zone with { Count = 1, Cards = [producer] } : zone).ToImmutableArray()
            }).ToImmutableArray(),
            Decision = original.Decision! with { Payload = ((PaymentPayload)original.Decision!.Payload) with {
                UnpaidMana = [], UnpaidManaParts = mixed ? [new("{1}", 1), new("{U}", 1)] : [new("{1}", 1)],
                FundingContext = funding, PayerId = 0, UsablePoolManaColors = []
            } }
        };
    }

    private static GameSnapshot FloatingPoolPreview()
    {
        var original = ManaPaymentScenario.Snapshot(0);
        var island = original.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == ManaPaymentScenario.Dual) with {
            VisualId = ManaPaymentScenario.Island, CombatId = ManaPaymentScenario.Island, Name = "Island", ArtName = "Island",
            Type = "Basic Land — Island", Text = "{T}: Add {U}."
        };
        return original with { DecisionOwnerId = 0, PriorityPlayerId = 0,
            Players = original.Players.Select(player => player.Id != 0 ? player : player with {
                Mana = ImmutableDictionary<string, int>.Empty.Add("C", 4),
                Zones = player.Zones.Select(zone => zone.Name == "Battlefield" ? zone with { Count = 1, Cards = [island] } : zone).ToImmutableArray()
            }).ToImmutableArray()
        };
    }

    private static void CheckCountedSourceOutputs(string profile, Action<bool, string> check)
    {
        var metrics = new Mana.Renderer.TextMetrics();
        foreach (var viewport in new[] { new Point(1000, 740), new Point(1600, 900) })
        foreach (float scale in new[] { 1f, 1.3f }) {
            string directory = Path.Combine(profile, $"source-outputs-{viewport.X}x{viewport.Y}-{(int)(scale * 100)}");
            Prepare(directory);
            (PlayPreferences.Load(directory) with { TextScale = scale }).Save(directory);
            var engine = new InteractionScenarioEngine();
            engine.Present(CountedSourceOutput(100, false));
            using var run = new PresentationRun(TestTableServices.For(engine), directory, engine.State, viewport);
            run.EnableLiveTableStudy();
            var trace = new List<object>();
            try {
                foreach (var example in new[] { (Count: 100, Mixed: false), (Count: 1024, Mixed: false), (Count: 129, Mixed: true) }) {
                    if (example.Count != 100) { engine.Present(CountedSourceOutput(example.Count, example.Mixed)); run.QueueObservation(engine.State); }
                    var ready = Stopwatch.StartNew();
                    do {
                        run.Tick(TimeSpan.FromSeconds(1.0 / 60), new()); Thread.Yield();
                    } while ((!run.View.Loaded || run.View.Match?.Revision != engine.State.Revision || run.View.ManaJourney == null)
                        && ready.Elapsed < TimeSpan.FromSeconds(5));
                    for (int frame = 0; frame < 10; frame++) run.Tick(TimeSpan.FromSeconds(1.0 / 60), new());
                    var view = run.View;
                    var drawing = run.Frame.Drawing;
                    string label = $"{example.Count}{(example.Mixed ? "+U3" : "")} at {scale:P0}, {viewport.X}x{viewport.Y}";
                    var expected = ((PaymentPayload)engine.State.Decision!.Payload).FundingContext!.Sources.Single();
                    check(view.ManaJourney is { Payment: true, FundingStatus: ManaFundingStatus.Funded }
                        && view.ManaSources.Count == 1 && view.ManaSources[0].SourceId == expected.SourceCardId
                        && view.ManaSources[0].Output.OrderBy(part => part.Key).SequenceEqual(expected.Routes[0].Production.Alternatives[0].Colors.OrderBy(part => part.Key)),
                        "The current small bill retains one exact full-output source witness: " + label);
                    var source = view.ManaSources[0];
                    var amounts = drawing.Commands.OfType<Mana.Renderer.DrawCommand.Text>()
                        .Where(text => text.Bold && text.Size == .64f && source.OutputBounds.Contains(new Point(text.X, text.Y))).ToArray();
                    var counts = source.Output.Values.Where(amount => amount > 1).Select(amount => amount.ToString()).ToArray();
                    var textBounds = amounts.Select(text => {
                        int natural = metrics.TextWidth(text.Value!, text.Size, text.Bold, drawing.TextScale);
                        var measured = metrics.TextSize(text.Value!, text.Size, natural, text.MaxLines, text.Bold, drawing.TextScale);
                        return new Microsoft.Xna.Framework.Rectangle(text.X, text.Y, measured.X, measured.Y);
                    }).ToArray();
                    var glyphs = drawing.Commands.Select(command => command switch {
                        Mana.Renderer.DrawCommand.Circle circle when circle.Radius == 13 => new Mana.Renderer.Pose(circle.Center, new Vector2(26)).Bounds,
                        Mana.Renderer.DrawCommand.Sprite sprite when sprite.Size == new Vector2(26) => new Mana.Renderer.Pose(sprite.Center, sprite.Size).Bounds,
                        _ => Microsoft.Xna.Framework.Rectangle.Empty
                    }).Where(bounds => !bounds.IsEmpty && source.OutputBounds.Contains(bounds)).ToArray();
                    var footprints = glyphs.Concat(textBounds).ToArray();
                    trace.Add(new { label, source, counts, amounts, glyphs, textBounds, bill = view.ManaJourney, engine.State.Revision, replies = engine.Replies.Count });
                    check(drawing.TextScale == scale && counts.Order().SequenceEqual(amounts.Select(text => text.Value!).Order())
                        && amounts.All(text => text.Width >= metrics.TextWidth(text.Value!, text.Size, text.Bold, drawing.TextScale))
                        && textBounds.All(source.OutputBounds.Contains),
                        "Every full source quantity fits its actual painted text allocation and reserved output footprint: " + label);
                    check(glyphs.Length == source.Output.Count && footprints.SelectMany((bounds, index) => footprints.Skip(index + 1)
                            .Select(other => !bounds.Intersects(other))).All(clear => clear),
                        "Current output glyphs and complete quantity labels remain distinct without overlap: " + label);
                    var visible = new Microsoft.Xna.Framework.Rectangle(0, 0, 1600, view.DesignHeight);
                    var fitted = Mana.Renderer.RenderViewport.Fit(viewport, view.DesignHeight);
                    var topLeft = fitted.ToPhysical(source.OutputBounds.Location.ToVector2());
                    var bottomRight = fitted.ToPhysical(new Vector2(source.OutputBounds.Right, source.OutputBounds.Bottom));
                    check(visible.Contains(source.OutputBounds) && topLeft.X >= 0 && topLeft.Y >= 0
                        && bottomRight.X <= viewport.X && bottomRight.Y <= viewport.Y
                        && !view.ManaJourney!.Bounds.Intersects(source.OutputBounds),
                        "The current full output stays inside the fitted viewport and outside the exact bill: " + label);
                    check(engine.Replies.Count == 0 && view.Match!.Decision!.Id == engine.State.Decision!.Id
                        && view.Match.Viewer!.Mana.Count == 0 && view.Match.Viewer.Zone("Battlefield").Cards.All(card => !card.Tapped),
                        "Reading and refreshing the current source witness submits no command and spends no resources: " + label);
                    if (example.Mixed && scale == 1.3f && viewport.X == 1000) {
                        var beforeCamera = view.Match;
                        for (int step = 0; step < 40 && view.ManaSources.Single().Bounds.Right <= visible.Right; step++)
                            run.Tick(TimeSpan.FromSeconds(.05), new(), new(Keys.A, Keys.LeftShift));
                        run.Tick(TimeSpan.FromSeconds(1.0 / 60), new());
                        var edge = view.ManaSources.Single();
                        trace.Add(new { label = "partly-visible-source", source = edge, view.BoardView, state = view.Match });
                        check(edge.Bounds.Left < visible.Right && edge.Bounds.Right > visible.Right
                            && visible.Contains(edge.OutputBounds),
                            "Panning a current source partly past the right edge keeps its complete mixed output inside the viewport");
                        for (int step = 0; step < 10 && visible.Intersects(view.ManaSources.Single().Bounds); step++)
                            run.Tick(TimeSpan.FromSeconds(.05), new(), new(Keys.A, Keys.LeftShift));
                        run.Tick(TimeSpan.FromSeconds(1.0 / 60), new());
                        var offscreen = view.ManaSources.Single();
                        trace.Add(new { label = "offscreen-source", source = offscreen, view.BoardView, state = view.Match });
                        check(!visible.Intersects(offscreen.Bounds) && !visible.Intersects(offscreen.OutputBounds)
                            && offscreen.OutputBounds.Left >= visible.Right,
                            "Continuing the same camera pan moves a fully offscreen source's output with it instead of pinning an orphan label to the edge");
                        check(ReferenceEquals(view.Match, beforeCamera) && engine.Replies.Count == 0,
                            "The edge and offscreen output journey changes only the presentation camera, preserving the exact current observation and zero commands");
                    }
                }
            } finally {
                File.WriteAllText(Path.Combine(directory, "counted-source-output-diagnostic.json"), JsonSerializer.Serialize(trace,
                    new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            }
        }
    }

    private static void Prepare(string profile)
    {
        Directory.CreateDirectory(profile);
        (PlayPreferences.Load(profile) with { Automatic = false, Sounds = false, ReducedMotion = true }).Save(profile);
    }

    private ManaPaymentSmoke(TableWindow.Probe view, ExperienceLabSession lab, Point viewport, InteractionScenarioEngine engine)
    {
        this.view = view; this.lab = lab; this.viewport = viewport; window = view;
        Move(new(12, 65));
        foreach (var example in new[] { (Count: 100, Mixed: false), (Count: 1024, Mixed: false), (Count: 129, Mixed: true) }) {
            Step(() => { engine.Present(CountedSourceOutput(example.Count, example.Mixed)); view.Accept(engine.State); });
            Wait(() => view.Loaded && view.Match?.Revision == engine.State.Revision && view.ManaJourney != null
                && view.Match.Viewer!.Zones.SelectMany(zone => zone.Cards).All(card => view.Art(card) != null)
                && view.Match.Decision!.Context.Source?.Card is { } pendingCard && view.Art(pendingCard) != null
                && view.Now - lastStep >= .3, () => {
                var output = ((PaymentPayload)engine.State.Decision!.Payload).FundingContext!.Sources.Single();
                Check(view.ManaJourney is { FundingStatus: ManaFundingStatus.Funded }
                    && view.ManaSources.Count == 1 && view.ManaSources[0].SourceId == output.SourceCardId
                    && view.ManaSources[0].Output.OrderBy(part => part.Key)
                        .SequenceEqual(output.Routes[0].Production.Alternatives[0].Colors.OrderBy(part => part.Key))
                    && engine.Replies.Count == 0,
                    "The GPU capture paints the current complete source output without submitting a command: " + example.Count);
                string name = "source-output-" + example.Count + (example.Mixed ? "-blue3" : "");
                Capture(name);
                File.WriteAllText(Path.Combine(view.Profile, name + ".json"), JsonSerializer.Serialize(new {
                    state = view.Match, frame = view.Frame!.Observe(), source = view.ManaSources.Single(), view.ManaJourney,
                    viewport, view.TextScale, view.TableStudyEnabled, replies = engine.Replies.Count,
                    scope = "Actual GPU surface and full window with loaded Magic card artwork; authored output, no rules simulation or human approval."
                }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            });
        }
        bool previewKnown = true;
        string inspectedHandKey = "";
        string inspectedDecision = "";
        Step(() => {
            engine.SourceActionRead = (game, decision, key) => {
                var state = engine.State;
                var funding = new ManaFundingContext(SemanticKnowledge.Known, state.Viewer!.Mana,
                    [new(ManaPaymentScenario.Island, [new([new(ActionCostKind.Tap, 1, true, "")],
                        new(SemanticKnowledge.Known, [new(ImmutableDictionary<string, int>.Empty.Add("U", 1))]), SemanticKnowledge.Known)])]);
                return new(game, decision, key, ManaPaymentScenario.Spell,
                    [new("floating-preview-cast", "cast", "Cast Faerie Vandal") { CostPreview = previewKnown
                        ? new([new(ActionCostKind.Mana, 1, false, "") { ManaSymbols = ["{U}"] }], SemanticKnowledge.Known, funding) : null }]) {
                    Revision = state.Revision
                };
            };
            engine.Present(FloatingPoolPreview()); view.Accept(engine.State); Move(new(12, 65));
        });
        Wait(() => view.Loaded && view.Match?.Revision == engine.State.Revision
            && view.DisplayedPose(ManaPaymentScenario.Spell) != null
            && view.Match.Viewer!.Zones.SelectMany(zone => zone.Cards).All(card => view.Art(card) != null),
            () => MoveCard(ManaPaymentScenario.Spell));
        bool PoolAccent() => view.ManaPoolBadge(0)?.Parts.SingleOrDefault(part => part.Key == "C") is { } pip
            && view.Frame!.Drawing!.Commands.OfType<Mana.Renderer.DrawCommand.Ring>().Count(ring => ring.Radius == 14 && ring.Width == 1
                && ring.Center == new Vector2(pip.Bounds.X + 11, pip.Bounds.Center.Y)) == 1;
        Wait(() => view.ManaJourney is { Payment: false, FundingStatus: ManaFundingStatus.Funded }, () => {
            Check(PoolAccent() && view.ManaSources is [{ SourceId: ManaPaymentScenario.Island }]
                && view.ManaPoolBadge(0) is { Total: 4, Parts: [{ Key: "C", Amount: "4" }] }
                && view.Hits.Single(hit => hit.Id == "mana-pool:0:C").Intent is MatchIntent.OpenManaPool
                && engine.Replies.Count == 0 && CaptionClearsSeat(),
                "The loaded physical hand preview connects its funded {1}{U} cost to the actual C4 pip and Island while preserving the full count, inspection gesture and portrait controls");
            Capture("pool-preview-funded");
            File.WriteAllText(Path.Combine(view.Profile, "pool-preview-funded.json"), JsonSerializer.Serialize(new {
                state = view.Match, frame = view.Frame!.Observe(), view.ManaJourney, sources = view.ManaSources,
                pool = view.ManaPoolBadge(0), viewport, view.TextScale, replies = engine.Replies.Count,
                scope = "Actual GPU hand-hover preview with loaded Magic card artwork; nominal witness, no payment or human UX approval."
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            MoveCard(ManaPaymentScenario.Spell);
        }); Click();
        Wait(() => ReaderReady(ManaPaymentScenario.Spell), () => {
            Check(view.ManaJourney is { Payment: false } && engine.Replies.Count == 0,
                "The physical hand-card click pins the actual current cost reader before inspecting its floating mana");
            previewKnown = false; engine.Present(engine.State); view.Accept(engine.State);
        });
        Wait(() => view.Match?.Revision == engine.State.Revision && view.ManaJourney is { FundingStatus: null, Advice: "Choose an action" }, () => {
            Check(!PoolAccent() && view.ManaSources.Count == 0 && view.ManaPoolBadge(0) is { Total: 4, Parts: [{ Amount: "4" }] }
                && view.Hits.Single(hit => hit.Id == "mana-pool:0:C").Intent is MatchIntent.OpenManaPool && engine.Replies.Count == 0,
                "Replacing that exact cost preview with an unreported cost withdraws the pool and source estimates while preserving the actual C4 count and read-only pip action");
            var bill = view.ManaJourney!;
            var field = view.Frame!.Cards.Where(card => card.Zone == "Battlefield").ToArray();
            Check(view.TableStudyEnabled && field.Length > 0
                && new Microsoft.Xna.Framework.Rectangle(0, 0, 1600, view.DesignHeight).Contains(bill.Bounds)
                && field.All(card => !bill.Bounds.Intersects(card.Display.Bounds)),
                "The unreported-cost bill reserves every current physical battlefield face and stays inside the viewport after its funding metadata disappears");
            File.WriteAllText(Path.Combine(view.Profile, "pool-preview-withdrawn.json"), JsonSerializer.Serialize(new {
                state = view.Match, frame = view.Frame!.Observe(), bill,
                battlefield = field.Select(card => new { card.Id, Bounds = card.Display.Bounds }).ToArray(),
                pool = view.ManaPoolBadge(0), viewport, view.TextScale, replies = engine.Replies.Count,
                scope = "Current-frame physical face and bill geometry after metadata withdrawal; sparse authored table, no packed-table guarantee or human approval."
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            inspectedHandKey = view.Frame.Cards.Single(card => card.Id == ManaPaymentScenario.Spell && card.Focused).Card.Key;
            inspectedDecision = view.Match!.Decision!.Id;
            Capture("pool-preview-withdrawn"); MoveHit("mana-pool:0:C");
        }); Click();
        Wait(() => view.ManaPoolDetail != null, () => {
            var currentFrame = view.Frame ?? throw new InvalidOperationException("Mana reading has no current presentation frame.");
            Check(view.ManaPoolDetail is { Total: 4 } && ReaderReady(ManaPaymentScenario.Spell)
                && currentFrame.Cards.Single(card => card.Id == ManaPaymentScenario.Spell && card.Focused).Card.Key == inspectedHandKey
                && view.Match!.Decision!.Id == inspectedDecision && view.ManaJourney is { Payment: false, FundingStatus: null, Advice: "Choose an action" }
                && currentFrame.Cards.Any(card => card.Id == ManaPaymentScenario.Island && card.Zone == "Battlefield")
                && PoolReadingClearsContext(view) && PoolReadingIsolated(view) && engine.Replies.Count == 0,
                "The physical C4 inspection expands beside its own portrait while the exact pinned hand occurrence, physical Island and current unknown-funding bill remain visible with passive hits and zero commands");
            File.WriteAllText(Path.Combine(view.Profile, "pool-preview-inspect.json"), JsonSerializer.Serialize(new {
                state = view.Match, frame = currentFrame.Observe(), view.ManaJourney,
                pool = view.ManaPoolDetail, pinnedKey = inspectedHandKey, viewport, view.TextScale, replies = engine.Replies.Count,
                scope = "Current expanded same-seat mana, retained current reader and bill, measured physical geometry and isolated input; authored presentation, no human acceptance."
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            Capture("pool-preview-inspect");
            MoveHit("close-overlay");
        }); Click();
        Wait(() => view.Overlay.Length == 0, () => {
            Check(ReaderReady(ManaPaymentScenario.Spell)
                && view.Frame!.Cards.Single(card => card.Id == ManaPaymentScenario.Spell && card.Focused).Card.Key == inspectedHandKey
                && view.Match!.Decision!.Id == inspectedDecision && view.ManaJourney is { Payment: false, Advice: "Choose an action" }
                && engine.Replies.Count == 0,
                "Dismissing physical mana reading preserves the same valid pinned card and bill without a second hand click or any game command");
            Capture("pool-preview-after-close");
        });
        Step(() => { finished = true; Save(null); window.Exit(); });
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
            Capture("04-rock-tapped-blue-remains"); MoveHit("mana-pool:0:C");
        });
        Wait(() => HasText("Colorless mana · 1") && HasText("Not usable for the remaining cost"), () => {
            Check(lab.StageIndex == 2 && lab.ManaPaymentReplies.Count == 2
                && view.ManaJourney?.Symbols.SequenceEqual(["{U}"]) == true
                && view.Hits.Single(hit => hit.Id == "mana-pool:0:C").Intent is MatchIntent.OpenManaPool,
                "Hover explains the actual colorless pip's exclusion from the remaining blue bill without spending mana or changing the pending card");
            Check(view.ManaSources.Any(source => source.SourceId == ManaPaymentScenario.Dual && !source.OutputBounds.IsEmpty)
                && PoolCaptionClearsPayment(view, "Colorless mana · 1", "Not usable for the remaining cost", out _),
                "The complete hover caption and backing clear the current blue source face and output symbol, pending card and exact bill");
            Capture("04a-colorless-cannot-pay-blue");
        }); Click();
        Wait(() => view.ManaPoolDetail != null, () => {
            Check(lab.StageIndex == 2 && lab.ManaPaymentReplies.Count == 2 && view.ManaPoolDetail is { Total: 1 }
                && view.ManaJourney is { Payment: true, SourceId: ManaPaymentScenario.Spell } current
                && current.Symbols.SequenceEqual(["{U}"]) && PoolReadingClearsContext(view) && PoolReadingIsolated(view),
                "An excluded C1 payment pip expands in place with the same unpaid blue bill and physical source visible while payment actions stay passive");
            Capture("04b-read-unspent-colorless"); MoveHit("close-overlay");
        }); Click();
        Wait(() => view.Overlay.Length == 0, () => {
            Check(lab.StageIndex == 2 && lab.ManaPaymentReplies.Count == 2
                && view.ManaJourney is { Payment: true } current && current.Symbols.SequenceEqual(["{U}"]),
                "Closing the contextual pool reading preserves the same unpaid blue decision before the existing source-payment journey continues");
            MoveCard(ManaPaymentScenario.Dual);
        }); Click();
        Wait(() => ReaderReady(ManaPaymentScenario.Dual) && view.Hits.Any(hit => hit.Id == "read-card-action"), () => {
            Check(view.Frame!.Drawing!.Commands.OfType<Mana.Renderer.DrawCommand.Text>().Any(text => text.Value?.Contains("Add") == true),
                "A flexible mana source exposes its current symbol-bearing choices on its own readable card");
            Check(view.ManaJourney is { SourceZone: "Stack" } bill
                && !bill.Bounds.Intersects(view.DisplayedPose(ManaPaymentScenario.Dual)!.Value.Bounds)
                && !view.Hits.Where(hit => hit.Id == "source-actions-surface").Any(hit => hit.Bounds.Intersects(bill.Bounds)),
                "Reading a dual source keeps its current color choices and the pending spell's exact bill visible in separate adjacent space");
            Capture("04c-dual-source-color-choices");
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
        });
        Wait(() => HasText("Colorless mana · 1") && HasText("Spend"), () => {
            Check(!HasText("Not usable for the remaining cost") && lab.ManaPaymentReplies.Count == 0,
                "A current usable colorless pip retains its Spend guidance and hover alone never pays the exact colorless bill");
            Check(PoolCaptionClearsPayment(view, "Colorless mana · 1", "Spend", out _),
                "The usable-pip caption keeps the current bill, source faces and output quantities unobscured before payment");
            Capture("06a-hover-current-spend");
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
            Capture("07-uncertain-manual-route"); Load(8);
        });
        Wait(() => view.ManaJourney is { SourceId: ManaPaymentScenario.ColorSpell, Payment: true }
            && view.DisplayedPose(ManaPaymentScenario.Temple) != null
            && (window == null || lab.State.Viewer!.Zones.SelectMany(zone => zone.Cards).All(card => view.Art(card) != null)
                && view.Art(lab.State.Decision!.Context.Source!.Card!) != null), () => {
            Check(view.ManaJourney!.Symbols.SequenceEqual(["{U}", "{R}"]),
                "A two-color bill stays on the pending paying card before activating a flexible source");
            MoveCard(ManaPaymentScenario.Temple);
        }); Click();
        Wait(() => ReaderReady(ManaPaymentScenario.Temple) && view.Hits.Count(hit => hit.Id == "read-card-action") == 1, () => {
            Capture("08-single-flexible-source"); MoveHit("read-card-action");
        }); Click();
        Wait(() => lab.StageIndex == 9 && !view.Busy && view.ManaColors.Count == 2
            && view.ManaJourney is { SourceId: ManaPaymentScenario.ColorSpell, SourceZone: "Stack" }
            && SettledTapped(ManaPaymentScenario.Temple), () => {
            Check(view.DecisionDock?.RequiredSurface == DecisionRequiredSurface.Source
                && view.ManaJourney!.Symbols.SequenceEqual(["{U}", "{R}"])
                && view.Frame!.Cards.Count(card => CardViews.Identity(card.Card) == ManaPaymentScenario.ColorSpell) == 1
                && view.ManaColors.All(choice => choice.ProducerId == ManaPaymentScenario.Temple),
                "A genuine owned mana-color callback keeps the original pending spell and exact bill while its offered orbs remain attached to the actual producer");
            Check(!view.Hits.Any(hit => hit.Id is "mana-auto" or "mana-cancel" or "confirm" or "cancel" or "open-decision")
                && view.ManaColors.All(choice => !choice.Bounds.Intersects(view.ManaJourney!.Bounds)
                    && !choice.Bounds.Intersects(view.ManaJourney.SourceBounds)) && CaptionClearsSeat() && ProducerStemClearsBill(),
                "The child color choice has no parent payment controls or modal and preserves the paying card, symbols and portrait space");
            var producerBounds = view.DisplayedPose(ManaPaymentScenario.Temple)!.Value.Bounds;
            Check(view.ManaColors.Average(choice => choice.Bounds.Center.X) >= producerBounds.Left
                && view.ManaColors.Average(choice => choice.Bounds.Center.X) <= producerBounds.Right,
                "The color group is centered on its actual tapped producer, rather than visually attributing the choice to a neighboring land");
            previousManaColor = view.Hits.Single(hit => hit.Id == "mana-color:11");
            beforeColorChoiceReplies = lab.ManaPaymentReplies.Count;
            Capture("09-color-at-producer"); MoveHit("mana-color:11");
        });
        Step(() => { }); Step(() => {
            Check(lab.StageIndex == 9 && lab.ManaPaymentReplies.Count == beforeColorChoiceReplies
                && view.ManaColors.Select(choice => choice.Mana).SequenceEqual(["{U}", "{R}"]),
                "Hovering a current mana-color orb previews its exact offered symbol without selecting it or spending mana");
            Capture("10-hover-red-choice");
        }); Click();
        Wait(() => lab.StageIndex == 10 && !view.Busy && view.ManaJourney?.Symbols.SequenceEqual(["{U}"]) == true, () => {
            Check(lab.ManaPaymentReplies[^1] is ChooseOptions { Choices: var picked } && picked.SequenceEqual([11])
                && view.ManaJourney!.SourceId == ManaPaymentScenario.ColorSpell && view.ManaColors.Count == 0
                && view.Hits.Any(hit => hit.Id == "mana-auto")
                && view.Frame!.Cards.Count(card => CardViews.Identity(card.Card) == ManaPaymentScenario.ColorSpell) == 1,
                "Clicking red sends its current offered index directly, retires the child orbs and restores the same spell's exact remaining blue bill");
            Check(previousManaColor != null && !view.Activate(previousManaColor)
                && lab.ManaPaymentReplies.Count == beforeColorChoiceReplies + 1,
                "A retained color-orb gesture cannot answer the replacement payment scope");
            Capture("11-red-paid-blue-remains"); MoveCard(ManaPaymentScenario.Island);
        }); Click();
        Wait(() => ReaderReady(ManaPaymentScenario.Island) && view.Hits.Any(hit => hit.Id == "read-card-action"), () => MoveHit("read-card-action")); Click();
        Wait(() => lab.StageIndex == 11 && !view.Busy && SettledTapped(ManaPaymentScenario.Island), () => {
            Check(view.ManaJourney == null && view.Match!.Stack.Single().Card?.VisualId == ManaPaymentScenario.ColorSpell
                && lab.ManaPaymentReplies.Count == 3 && lab.ManaPaymentReplies.Count(reply => reply is ChooseOptions) == 1,
                "The remaining blue source turns and the same paid spell reaches the stack after one activation, one scoped color choice and one final source activation");
            Capture("12-both-colors-paid"); Load(12);
        });
        Wait(() => view.ManaJourney is { SourceId: ManaPaymentScenario.ConvokeSpell }
            && view.DisplayedPose(ManaPaymentScenario.ConvokeOne) != null
            && (window == null || lab.State.Viewer!.Zones.SelectMany(zone => zone.Cards).All(card => view.Art(card) != null)
                && view.Art(lab.State.Decision!.Context.Source!.Card!) != null), () => {
            Check(view.ManaJourney!.Symbols.SequenceEqual(["{1}", "{G}"])
                && view.Hits.Any(hit => hit.Id == "confirm" && hit.Intent is MatchIntent.ConfirmDecision)
                && !view.Hits.Any(hit => hit.Id is "mana-auto" or "mana-cancel"),
                "An optional contribution choice keeps the exact remaining bill on the pending spell and preserves the current Done action before the full cost is covered");
            Capture("13-convoke-cost-selection"); MoveCard(ManaPaymentScenario.ConvokeOne);
        }); Click();
        Wait(() => lab.StageIndex == 13 && !view.Busy && view.ManaJourney?.Symbols.SequenceEqual(["{1}"]) == true, () => {
            Check(lab.ManaPaymentReplies[^1] is SelectCard
                && lab.State.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == ManaPaymentScenario.ConvokeOne) is { Highlighted: true, Tapped: false }
                && view.ManaJourney!.SourceId == ManaPaymentScenario.ConvokeSpell,
                "Selecting a current highlighted creature marks its contribution and shrinks the same spell's bill without prematurely tapping the creature");
            Capture("14-contribution-chosen"); MoveCard(ManaPaymentScenario.ConvokeTwo);
        }); Click();
        Wait(() => lab.StageIndex == 14 && !view.Busy && view.ManaJourney is { Parts.IsEmpty: true }
            && view.Hits.Any(hit => hit.Id == "confirm"), () => {
            Check(view.Frame!.Drawing!.Commands.OfType<Mana.Renderer.DrawCommand.Text>().Any(text => text.Value == "COST COVERED")
                && view.Hits.Single(hit => hit.Id == "confirm").Bounds.Height >= 40
                && lab.State.Viewer!.Zone("Battlefield").Cards.All(card => card.Highlighted && !card.Tapped)
                && !view.Hits.Any(hit => hit.Id is "mana-auto" or "mana-cancel") && CaptionClearsSeat(),
                "Exact zero contributions display Cost covered and a clear current Done gesture while preserving selection marks and the untapped creatures");
            Check(GameCommandPermissions.Allows(lab.State, new CompleteSelection(lab.State.Id, lab.State.Decision!.Id))
                && !GameCommandPermissions.Allows(lab.State, new PayMana(lab.State.Id, lab.State.Decision.Id)),
                "The covered-cost surface answers only the current entity-selection continuation, never a parent PayMana input");
            Capture("15-convoke-cost-covered"); MoveHit("confirm");
        }); Click();
        Wait(() => lab.StageIndex == 15 && !view.Busy && SettledTapped(ManaPaymentScenario.ConvokeOne)
            && SettledTapped(ManaPaymentScenario.ConvokeTwo), () => {
            Check(view.ManaJourney == null && view.Match!.Stack.Single().Card?.VisualId == ManaPaymentScenario.ConvokeSpell
                && lab.ManaPaymentReplies.Count == 3 && lab.ManaPaymentReplies[^1] is CompleteSelection,
                "The actual current Done gesture commits the authored contribution selection, turns both creatures and continues the same card to the stack");
            Capture("16-convoke-committed"); Load(12);
        });
        Wait(() => view.ManaJourney is { SourceId: ManaPaymentScenario.ConvokeSpell }
            && lab.StageIndex == 12 && !view.Busy, () => MoveCard(ManaPaymentScenario.ConvokeTwo)); Click();
        Wait(() => lab.StageIndex == 16 && !view.Busy, () => {
            Check(view.ManaJourney?.Symbols.SequenceEqual(["{1}"]) == true
                && lab.State.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == ManaPaymentScenario.ConvokeTwo) is { Highlighted: true, Tapped: false },
                "Either offered creature can contribute first, reducing the same spell's exact bill without tapping before Done");
            Capture("17-other-contributor-first"); MoveCard(ManaPaymentScenario.ConvokeTwo);
        }); Click();
        Wait(() => lab.StageIndex == 12 && !view.Busy, () => {
            Check(view.ManaJourney?.Symbols.SequenceEqual(["{1}", "{G}"]) == true
                && lab.State.Viewer!.Zone("Battlefield").Cards.All(card => !card.Highlighted && !card.Tapped),
                "Clicking a selected contributor undoes its contribution and restores the exact unpaid symbols");
            MoveCard(ManaPaymentScenario.ConvokeTwo);
        }); Click();
        Wait(() => lab.StageIndex == 16 && !view.Busy, () => MoveCard(ManaPaymentScenario.ConvokeOne)); Click();
        Wait(() => lab.StageIndex == 14 && !view.Busy, () => {
            Check(view.ManaJourney is { Parts.IsEmpty: true }
                && lab.State.Viewer!.Zone("Battlefield").Cards.All(card => card.Highlighted && !card.Tapped),
                "Choosing the same contributors in reverse order reaches the identical covered bill and untapped selection");
            MoveCard(ManaPaymentScenario.ConvokeOne);
        }); Click();
        Wait(() => lab.StageIndex == 16 && !view.Busy, () => MoveHit("confirm")); Click();
        Wait(() => lab.StageIndex == 19 && !view.Busy && SettledTapped(ManaPaymentScenario.ConvokeTwo)
            && view.ManaJourney?.Symbols.SequenceEqual(["{1}"]) == true, () => {
            Check(view.Match!.Stack.IsEmpty && lab.State.Decision?.Payload is PaymentPayload
                && lab.State.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == ManaPaymentScenario.ConvokeOne) is { Tapped: false, Highlighted: false }
                && !view.Hits.Any(hit => hit.Id == "mana-cancel"),
                "Done before zero commits only the chosen contribution and retains a real remaining-payment checkpoint before the spell can reach the stack");
            Capture("18-optional-stop-remaining-payment"); MoveCard(ManaPaymentScenario.ConvokeOne);
        }); Click();
        Wait(() => ReaderReady(ManaPaymentScenario.ConvokeOne) && view.Hits.Any(hit => hit.Id == "read-card-action"), () => MoveHit("read-card-action")); Click();
        Wait(() => lab.StageIndex == 15 && !view.Busy && SettledTapped(ManaPaymentScenario.ConvokeOne)
            && SettledTapped(ManaPaymentScenario.ConvokeTwo), () => {
            Check(view.ManaJourney == null && view.Match!.Stack.Single().Card?.VisualId == ManaPaymentScenario.ConvokeSpell
                && lab.ManaPaymentReplies.Count == 7 && lab.ManaPaymentReplies[^1] is UseOfferedAction,
                "After an early Done, the remaining untapped creature's current mana action pays the final generic bill before the same spell reaches the stack");
            Capture("19-remaining-source-paid"); Load(12);
        });
        Wait(() => lab.StageIndex == 12 && !view.Busy && view.Hits.Any(hit => hit.Id == "confirm"), () => MoveHit("confirm")); Click();
        Wait(() => lab.StageIndex == 17 && !view.Busy && view.ManaJourney?.Symbols.SequenceEqual(["{1}", "{G}"]) == true, () => {
            Check(view.Match!.Stack.IsEmpty && lab.State.Decision?.Payload is PaymentPayload
                && lab.State.Viewer!.Zone("Battlefield").Cards.All(card => !card.Tapped && !card.Highlighted)
                && view.Hits.Any(hit => hit.Id == "mana-auto"),
                "Done with no contributors preserves the entire exact bill and both untapped mana sources instead of treating optional contribution as automatic payment");
            Capture("20-no-contribution-payment"); MoveCard(ManaPaymentScenario.ConvokeOne);
        }); Click();
        Wait(() => ReaderReady(ManaPaymentScenario.ConvokeOne) && view.Hits.Any(hit => hit.Id == "read-card-action"), () => MoveHit("read-card-action")); Click();
        Wait(() => lab.StageIndex == 18 && !view.Busy && SettledTapped(ManaPaymentScenario.ConvokeOne)
            && view.ManaJourney?.Symbols.SequenceEqual(["{1}"]) == true, () => {
            Check(view.Match!.Stack.IsEmpty
                && lab.State.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == ManaPaymentScenario.ConvokeTwo).Tapped == false,
                "A current green mana action pays only the colored shard of the full remaining bill and leaves the other creature available for generic mana");
            Capture("21-first-mana-source-paid"); MoveCard(ManaPaymentScenario.ConvokeTwo);
        }); Click();
        Wait(() => ReaderReady(ManaPaymentScenario.ConvokeTwo) && view.Hits.Any(hit => hit.Id == "read-card-action"), () => MoveHit("read-card-action")); Click();
        Wait(() => lab.StageIndex == 15 && !view.Busy && SettledTapped(ManaPaymentScenario.ConvokeOne)
            && SettledTapped(ManaPaymentScenario.ConvokeTwo), () => {
            Check(view.ManaJourney == null && view.Match!.Stack.Single().Card?.VisualId == ManaPaymentScenario.ConvokeSpell
                && lab.ManaPaymentReplies.Count == 3 && lab.ManaPaymentReplies[0] is CompleteSelection
                && lab.ManaPaymentReplies.Skip(1).All(reply => reply is UseOfferedAction),
                "Skipping optional contributions then using two current mana actions pays the full bill before continuing the same card to the stack");
            Capture("22-no-contribution-fully-paid"); Save(null); finished = true; window?.Exit();
        });
    }

    private bool ReaderReady(string id) => view.DisplayedPose(id) is { Size.X: >= 300 } pose
        && view.Frame!.Cards.Any(card => card.Id == id && card.Focused && Vector2.Distance(card.Display.Center, (card.ReaderTarget ?? card.Rest).Center) < 2);
    private bool HasText(string value) => view.Frame!.Drawing!.Commands.OfType<Mana.Renderer.DrawCommand.Text>().Any(text => text.Value == value);
    internal static bool PoolReadingIsolated(TableApplication.Probe view) => view.Overlay == "mana"
        && view.Hits.Count(hit => hit.Intent is MatchIntent.CloseView) == 1
        && view.Hits.All(hit => hit.Intent is UiIntent.NoInteraction or MatchIntent.CloseView);

    // Read the current rendered tokens and physical context, independently of
    // the expansion's placement rule. These sparse authored frames are not a
    // guarantee that a fully packed table always contains a clear region.
    internal static bool PoolReadingClearsContext(TableApplication.Probe view, int playerId = 0)
    {
        if (view.Frame is not { Scene: { } scene } frame || view.Match is not { } state
            || view.ManaPoolDetail is not { } detail || detail.Parts.Length < 6) return false;
        var selected = state.Players.SingleOrDefault(player => player.Id == playerId && PublicResources.ManaTotal(player) == detail.Total
            && PublicResources.Mana(player, includeEmpty: true).Select(part => (part.Key, part.Name, Amount: part.Amount.ToString()))
                .SequenceEqual(detail.Parts.Select(part => (part.Key, part.Name, part.Amount))));
        if (selected == null) return false;
        var viewport = new Microsoft.Xna.Framework.Rectangle(0, 0, 1600, view.DesignHeight);
        var obstacles = frame.Cards.Select(card => card.Display.Bounds).ToList();
        if (view.ManaJourney is { } bill) { obstacles.Add(bill.Bounds); obstacles.Add(bill.SourceBounds); }
        obstacles.AddRange(view.ManaSources.SelectMany(source => new[] { source.Bounds, source.OutputBounds }));
        foreach (var player in state.Players) {
            var hero = scene.Seat(player.Id).Geometry.Hero;
            obstacles.Add(new((int)hero.X - 58, (int)hero.Y - 58, 116, 116));
            obstacles.AddRange(Mana.Renderer.TurnControlLayout.For(hero).Reservations);
            if (player.Id != selected.Id && view.ManaPoolBadge(player.Id) is { } other) obstacles.Add(other.Bounds);
        }
        var texts = frame.Drawing.Commands.OfType<Mana.Renderer.DrawCommand.Text>().ToArray();
        var metrics = new Mana.Renderer.TextMetrics();
        bool Fits(Mana.Renderer.DrawCommand.Text text, Microsoft.Xna.Framework.Rectangle? token = null) {
            var measured = metrics.TextSize(text.Value!, text.Size, text.Width, text.MaxLines, text.Bold, frame.Drawing.TextScale);
            var bounds = new Microsoft.Xna.Framework.Rectangle(text.X, text.Y, measured.X, measured.Y);
            return metrics.TextWidth(text.Value!, text.Size, text.Bold, frame.Drawing.TextScale) <= text.Width
                && viewport.Contains(bounds) && (token == null || token.Value.Contains(bounds));
        }
        var anchor = scene.Seat(selected.Id).Geometry.Hero;
        return texts.Any(text => text.Value == "Unspent mana" && Fits(text))
            && !texts.Any(text => text.Value == PublicResources.ManaDetail || text.Value?.Contains("READ ONLY") == true || text.Value?.Contains("CURRENT POOL") == true)
            && detail.Parts.All(part => viewport.Contains(part.Bounds) && part.MeasuredWidth <= part.AvailableWidth
                && Vector2.Distance(part.Bounds.Center.ToVector2(), anchor) < 500
                && obstacles.Where(area => !area.IsEmpty).All(area => !area.Intersects(part.Bounds))
                && texts.Any(text => text.Value == part.Name && Fits(text, part.Bounds))
                && texts.Any(text => text.Value == part.Amount && Fits(text, part.Bounds)))
            && detail.Parts.SelectMany((part, index) => detail.Parts.Skip(index + 1).Select(other => (part, other)))
                .All(pair => !pair.part.Bounds.Intersects(pair.other.Bounds));
    }
    internal static bool PoolCaptionClearsPayment(TableApplication.Probe view, string heading, string guidance,
        out Microsoft.Xna.Framework.Rectangle caption)
    {
        caption = Microsoft.Xna.Framework.Rectangle.Empty;
        if (view.Frame?.Drawing is not { } drawing) return false;
        var lines = drawing.Commands.Select((command, index) => (Command: command, Index: index))
            .Where(item => item.Command is Mana.Renderer.DrawCommand.Text text && (text.Value == heading || text.Value == guidance)).ToArray();
        if (lines.Length != 2) return false;
        var metrics = new Mana.Renderer.TextMetrics();
        var textBounds = lines.Select(item => {
            var text = (Mana.Renderer.DrawCommand.Text)item.Command;
            var size = metrics.TextSize(text.Value!, text.Size, text.Width, text.MaxLines, text.Bold, drawing.TextScale);
            return new Microsoft.Xna.Framework.Rectangle(text.X, text.Y, size.X, size.Y);
        }).Aggregate(Microsoft.Xna.Framework.Rectangle.Union);
        // Read the actual painted backing, rather than reconstructing the
        // production placement algorithm or checking only the text's origin.
        var backing = drawing.Commands.Take(lines.Min(item => item.Index)).OfType<Mana.Renderer.DrawCommand.Rounded>()
            .LastOrDefault(item => item.Bounds.Contains(textBounds));
        if (backing == null) return false;
        caption = backing.Bounds;
        var protectedAreas = view.ManaSources.SelectMany(source => new[] { source.Bounds, source.OutputBounds }).ToList();
        if (view.ManaJourney is { } bill) { protectedAreas.Add(bill.Bounds); protectedAreas.Add(bill.SourceBounds); }
        if (view.Match is { } state) foreach (var player in state.Players)
            if (view.ManaPoolBadge(player.Id) is { } pool) protectedAreas.Add(pool.Bounds);
        var area = caption;
        return area.X >= 0 && area.Y >= 0 && area.Right <= 1600 && area.Bottom <= view.DesignHeight
            && protectedAreas.Where(bounds => !bounds.IsEmpty).All(bounds => !area.Intersects(bounds));
    }
    private bool SettledTapped(string id) => view.Frame!.Cards.FirstOrDefault(card => card.Id == id) is { Focused: false } card
        && Math.Abs(MathHelper.WrapAngle(card.Display.Angle - card.Rest.Angle)) < .01f
        && Vector2.Distance(card.Display.Size, card.Rest.Size) < 1;
    private bool ProducerStemClearsBill()
    {
        var source = view.DisplayedPose(ManaPaymentScenario.Temple)!.Value;
        var area = view.DecisionPanelBounds;
        var end = new Mana.Renderer.Pose(area.Center.ToVector2(), area.Size.ToVector2()).EdgeToward(source.Center, 5);
        var start = source.EdgeToward(end, 5);
        return Enumerable.Range(0, 65).All(step => !view.ManaJourney!.Bounds.Contains(Vector2.Lerp(start, end, step / 64f)));
    }
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
        observations.Add(new { name, lab.StageIndex, view.ManaJourney, sources = view.ManaSources.ToArray(), colors = view.ManaColors.ToArray(), view.Now });
        if (window == null) return;
        using var stream = File.Create(Path.Combine(view.Profile, name + ".png")); window.Surface.SaveAsPng(stream, window.Surface.Width, window.Surface.Height);
        window.CaptureWindow(Path.Combine(view.Profile, name + "-window.png"));
    }
    private void Save(string? error) => File.WriteAllText(Path.Combine(view.Profile, "mana-payment-results.json"), JsonSerializer.Serialize(new {
        passed = error == null, error, checks, observations, currentManaJourney = view.ManaJourney, currentManaColors = view.ManaColors.ToArray(),
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
