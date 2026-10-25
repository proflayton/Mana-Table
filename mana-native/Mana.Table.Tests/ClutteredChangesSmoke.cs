using System.Diagnostics;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

// The headless and real-window modes drive the same production application and
// physical pointer script. Projected changes are controlled; rules run in Forge.
internal sealed class ClutteredChangesSmoke : ITableAutomation
{
    private readonly TableApplication.Probe view;
    private readonly TableWindow.Probe? window;
    private readonly InteractionScenarioEngine engine;
    private readonly Point viewport;
    private readonly Queue<(Func<bool>? Ready, Action Action)> steps = [];
    private readonly List<string> checks = [];
    private readonly List<object> observations = [];
    private MouseState mouse;
    private double lastStep;
    private string gameId = "", decisionId = "", cardKey = "";
    private bool finished;
    private int pages;
    private readonly List<string> readRows = [];
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;
    private PresentationFrame FrameData => view.Frame ?? throw new InvalidOperationException("Missing composed frame.");

    internal static void RunHeadless(string profile)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            string directory = Path.Combine(profile, $"{size.X}x{size.Y}"); Prepare(directory);
            var engine = new InteractionScenarioEngine(); engine.Present(ClutteredChangesScenario.Initial());
            using var application = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State);
            application.Automated = true; application.Initialize();
            var driver = new ClutteredChangesSmoke(application.TestPort, engine, size);
            var wall = Stopwatch.StartNew(); double time = 0;
            try {
                while (!driver.finished) {
                    if (wall.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Cluttered production composition/input exceeded 60 seconds.");
                    time += 1.0 / 60;
                    var tick = new GameTime(TimeSpan.FromSeconds(time), TimeSpan.FromSeconds(1.0 / 60));
                    application.Update(tick, driver.ReadMouse(), new(), size);
                    application.ReadPresentation(); driver.Advance(); Thread.Yield();
                }
            } catch (Exception exception) { driver.Save(exception.ToString()); throw; }
        }
    }

    internal static void Run(string profile, Point? size)
    {
        var dimensions = size ?? new Point(1000, 740); Prepare(profile);
        var engine = new InteractionScenarioEngine(); engine.Present(ClutteredChangesScenario.Initial());
        using var game = new TableWindow(TestTableServices.For(engine), profile, null, null, dimensions, initialState: engine.State, artSource: new CardReadingArtFixture());
        game.Automation = new ClutteredChangesSmoke(game.TestPort, engine, dimensions, game.TestPort);
        game.Run();
    }
    private static void Prepare(string profile)
    {
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true, TextScale = 1.3f }.Save(profile);
    }

    private ClutteredChangesSmoke(TableApplication.Probe view, InteractionScenarioEngine engine, Point viewport, TableWindow.Probe? window = null)
    {
        this.view = view; this.engine = engine; this.viewport = viewport; this.window = window;
        Move(new(20, 70));
        Wait(() => view.Loaded && FrameData.Cards.Count(card => card.Zone == "Battlefield") == 480, () => {
            Check(window == null || window.GraphicsDevice.Viewport.Width == viewport.X && window.GraphicsDevice.Viewport.Height == viewport.Y,
                $"Pointer targets use the actual {viewport.X}×{viewport.Y} physical rendering viewport");
            Observe("initial"); Capture("01-all-480"); AssertBattlefield();
            Check(engine.Replies.Count == 0, "Painting the complete 480-permanent table sends no game action");
            Publish(ClutteredChangesScenario.Earthbend(engine.State));
        });
        Step(() => {
            Observe("earthbend"); AssertCause(ClutteredChangesScenario.TophName, 1, 2);
            Check(view.HighlightedChangeTargets.SetEquals([ClutteredChangesScenario.AnimatedLandId])
                && view.HighlightedChangeSource == ClutteredChangesScenario.TophId(0),
                "Earthbend highlights the actual Toph source and exactly its newly animated land");
            Capture("02-toph-earthbend"); Publish(ClutteredChangesScenario.Retreat(engine.State));
        });
        Step(() => {
            Observe("retreat"); AssertCause(ClutteredChangesScenario.RetreatName, 82, 1); AssertBattlefield();
            var targets = view.Match!.Viewer!.Zone("Battlefield").Cards.Where(card => card.Type.Contains("Creature", StringComparison.Ordinal)).Select(CardViews.Identity).ToHashSet();
            Check(view.HighlightedChangeTargets.SetEquals(targets) && view.HighlightedChangeSource == ClutteredChangesScenario.RetreatId(0),
                "Felidar's engine-issued result highlights all 82 affected creatures, including thirteen earthbent lands, and its own source");
            Check(targets.All(id => FrameData.Cards.Single(card => card.Id == id).Card.CombatKeywords.Contains("vigilance")),
                "The current card views retain vigilance on every affected creature");
            Capture("03-felidar-result"); MoveHit("recent-changes");
        }); Click();
        Step(() => {
            Check(view.Overlay == "changes" && Text().Any(text => text.Contains("Felidar Retreat"))
                && Text().Any(text => text.Contains("vigilance", StringComparison.OrdinalIgnoreCase)),
                "Recent changes opens the retained named trigger and vigilance result when requested");
            Check(!view.HighlightedChangeTargets.Any() && view.ChangeCalloutBounds == null,
                "The result reader owns its reserved space without leaving battlefield effect highlights above it");
            Capture("04-result-details"); ReadResultPages();
        });
        // ReadResultPages inserts its page traversal immediately before these actions.
        Step(() => {
            Check(pages > 1 && readRows.Count == 82, "Paging exposes all 82 named before/after result records");
            var expected = view.PublicChanges.Last().Changes.Select(change => change.TargetName + " · " + change.Change.Property
                + " counters " + change.Change.Before + " → " + change.Change.After).Order(StringComparer.Ordinal);
            Check(readRows.Order(StringComparer.Ordinal).SequenceEqual(expected),
                "The complete result pages preserve every affected card's exact counter type and before/after values, including repeated names");
            MoveHit("changes-source-prev");
        }); Click();
        Step(() => {
            Check(Text().Any(text => text.Contains(ClutteredChangesScenario.TophName))
                && Text().Any(text => text.Contains("earthbend 2", StringComparison.OrdinalIgnoreCase)),
                "Earlier effect returns to Toph's distinct earthbend cause instead of merging nearby events");
            Check(engine.Replies.Count == 0, "Reviewing all results and earlier causes sends no game action");
            Capture("05-earlier-earthbend"); MoveHit("close-overlay");
        }); Click();
        Step(() => { MoveCard(ClutteredChangesScenario.ActionCardId); }); Click();
        Wait(() => FrameData.Cards.Any(card => card.Id == ClutteredChangesScenario.ActionCardId && card.Focused)
            && view.Hits.Any(hit => hit.Id == "read-card-action"), () => {
            var reader = FrameData.Cards.Single(card => card.Id == ClutteredChangesScenario.ActionCardId);
            Check(reader.Focused
                && view.Hits.Any(hit => hit.Id == "read-card-action") && engine.Replies.Count == 0,
                "A current crowded Llanowar Elves opens its card reader and explicit activation without an automatic action");
            var current = view.Match!.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == ClutteredChangesScenario.ActionCardId);
            Check(reader.Card.Counters["+1/+1"] == current.Counters["+1/+1"]
                && Text().Contains("+1/+1 ×" + current.Counters["+1/+1"])
                && FrameData.Drawing.Commands.OfType<DrawCommand.CenterText>().Any(text => reader.Display.Bounds.Contains(text.Bounds)
                    && text.Value == $"{current.Power}/{current.Toughness}"),
                "Reading the current permanent shows its full typed counter total and authoritative power/toughness on that same card");
            Check(view.ChangeCalloutBounds == null, "Opening a card reader yields the result callout's space");
            Capture("06-current-activation");
            gameId = view.Match!.Id; decisionId = view.Match.Decision!.Id;
            cardKey = view.Match.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == ClutteredChangesScenario.ActionCardId).Key;
            MoveHit("read-card-action");
        }); Click();
        Wait(() => !view.Busy && engine.Replies.Count == 1, () => {
            Check(engine.Replies.Single() is ActivateCard reply && reply.GameId == gameId
                && reply.DecisionId == decisionId && reply.Card == cardKey
                && view.Match!.Decision is { Task: DecisionTask.AbilityChoice, SourceCard.VisualId: ClutteredChangesScenario.ActionCardId },
                "Only the explicit activation submits one current game/decision/card command and opens that card's ability chooser");
            Capture("07-ability-chooser"); finished = true; Save(null); window?.Exit();
        });
    }

    private void AssertBattlefield()
    {
        var cards = FrameData.Cards.Where(card => card.Zone == "Battlefield").ToArray();
        Check(cards.Length == 480 && cards.Select(card => card.Id).Distinct().Count() == 480,
            "All 480 public battlefield occurrences survive production preparation");
        foreach (var player in view.Match!.Players) {
            var nodes = FrameData.Scene!.Battlefield(player.Id).SelectMany(cluster => cluster.Cards).ToArray();
            Check(nodes.Length == 120 && nodes.All(node => view.CardPaintCount(node.Id) == 1),
                $"Seat {player.Id} paints each of its 120 permanents exactly once");
            foreach (var node in nodes) {
                var canonical = view.Hits.Where(hit => hit.Zone == "Battlefield" && hit.Card != null && CardViews.Identity(hit.Card) == node.Id).ToArray();
                Check(canonical.Length == 1 && Exposed(node.Id) != null,
                    $"{node.Id} has one canonical hit and an exposed physical 2×2 pointer target at {viewport.X}×{viewport.Y}");
            }
            foreach (var node in nodes.Where(node => node.GroupFront)) {
                var labels = view.BattlefieldLabels(node.Id);
                Check(labels.Any(label => label.Kind == "name" && label.Text.Length > 0),
                    $"Group front {node.Id} includes its public name in the composed label commands");
                if (CardPresentation.HasCombatStats(node.Card))
                    Check(labels.Any(label => label.Kind == "pt" && label.Text == $"{node.Card.Power}/{node.Card.Toughness}"
                        && label.MeasuredWidth <= label.AvailableWidth && label.Bounds.Height * Scale >= 7),
                        $"Group front {node.Id} displays its complete current power/toughness");
                if (node.Card.Counters.Count > 0)
                    Check(labels.Count(label => label.Kind == "counter") == 1
                        && labels.Any(label => label.Kind == "counter" && label.Text == node.Card.Counters["+1/+1"].ToString()
                            && label.MeasuredWidth <= label.AvailableWidth && label.Bounds.Height * Scale >= 7),
                        $"Group front {node.Id} displays its exact current counter total once in its attached compact marker");
                foreach (var label in labels.Where(label => label.Kind is "name" or "pt" or "counter")) {
                    var logical = label.Bounds.Center;
                    Check(Hit(Pixel(logical.ToVector2()))?.Card is { } actual && CardViews.Identity(actual) == node.Id,
                        $"The visible {label.Kind} strip belongs to the same occurrence {node.Id} through physical input");
                }
            }
        }
        Check(!view.Hits.Any(hit => hit.Id.StartsWith("rank-next:") || hit.Id.StartsWith("rank-prev:")), "The battlefield never requires paging to expose permanents");
    }

    private void AssertCause(string name, int affected, int delta)
    {
        var group = view.PublicChanges.Last();
        Check(group.Title == name && group.Changes.Select(change => change.TargetId).Distinct().Count() == affected
            && view.EffectTransitions.Sources.Any(source => source.CardId == group.Cause?.CardId)
            && view.EffectTransitions.Targets.Where(target => target.EffectId == group.Id).SelectMany(target => target.AffectedIds).Distinct().Count() == affected,
            $"Reported {name} events feed the on-card source and affected-occurrence drawing commands");
        Check(view.ChangeCalloutBounds == null && !view.Hits.Any(hit => hit.Id is "change-details" or "change-locate")
            && !view.DecisionTextOverflow && view.Hits.Any(hit => hit.Id == "recent-changes"),
            "An already visible cause leaves its counter changes on the field, with retained details only in Recent changes");
        var targets = view.EffectTransitions.Targets.Where(target => target.EffectId == group.Id).ToArray();
        Check(group.Changes.All(change => change.Change.Property == "+1/+1" && change.Change.After - change.Change.Before == delta)
            && targets.All(target => target.Delta.Property == "+1/+1" && target.Delta.PerPermanent == delta
                && target.Delta.AffectedCount == target.AffectedIds.Count && target.Delta.Total == (long)delta * target.AffectedIds.Count)
            && group.Changes.All(change => FrameData.Cards.Single(card => card.Id == change.TargetId).Card.Counters["+1/+1"] == change.Change.After),
            "The reported effect retains each exact typed counter delta and its affected occurrences while the cards display the authoritative new totals");
        Check(view.HighlightedChangeTargets.Append(view.HighlightedChangeSource ?? "").Where(id => id.Length > 0)
            .All(id => view.DisplayedPose(id) is { } pose && !(pose with { Size = pose.Size + new Vector2(6) }).Bounds.Intersects(view.DecisionPanelBounds)),
            "Source and affected-card outlines stay outside the decision dock");
    }

    private void ReadResultPages()
    {
        var tail = steps.ToArray(); steps.Clear();
        Step(() => {
            pages++;
            Check(Text().Any(text => text.Contains("counters") && text.Contains("→")), "A results page exposes named before/after counter values");
            var body = FrameData.Drawing.Commands.OfType<DrawCommand.Text>().Single(text => text.X == 208 && text.Y == 254);
            var metrics = new TextMetrics();
            int fullHeight = metrics.TextSize(body.Value!, body.Size, body.Width, 80, body.Bold, view.TextScale).Y;
            int paintedHeight = metrics.TextSize(body.Value!, body.Size, body.Width, body.MaxLines, body.Bold, view.TextScale).Y;
            Check(fullHeight <= paintedHeight && body.Y + fullHeight < 734,
                "Every result page's CPU-measured text bounds end above the paging controls");
            readRows.AddRange(body.Value!.Split('\n').Where(line => line.Contains(" counters ") && line.Contains('→')));
            var next = view.Hits.LastOrDefault(hit => hit.Id == "changes-page-next");
            if (next != null) {
                if (pages > 12) throw new InvalidOperationException("Result pagination failed to terminate.");
                var pending = steps.ToArray(); steps.Clear();
                MoveHit("changes-page-next"); Click(); Step(ReadResultPages);
                foreach (var step in pending) steps.Enqueue(step);
            }
        });
        foreach (var step in tail) steps.Enqueue(step);
    }
    private void Publish(GameSnapshot state) { engine.Present(state); view.Accept(engine.State); }
    private string[] Text() => FrameData.Drawing.Commands.Select(command => command switch {
        DrawCommand.Text text => text.Value, DrawCommand.CenterText text => text.Value, _ => null
    }).Where(text => text != null).Cast<string>().ToArray();
    private float Scale => Math.Min(viewport.X / 1600f, viewport.Y / (float)view.DesignHeight);
    private Point Pixel(Vector2 logical) => new((int)MathF.Round((viewport.X - 1600 * Scale) / 2 + logical.X * Scale),
        (int)MathF.Round((viewport.Y - view.DesignHeight * Scale) / 2 + logical.Y * Scale));
    private Point Logical(Point physical) => new((int)((physical.X - (viewport.X - 1600 * Scale) / 2) / Scale),
        (int)((physical.Y - (viewport.Y - view.DesignHeight * Scale) / 2) / Scale));
    private TableHit? Hit(Point physical) => view.HitAt(Logical(physical));
    private Point? Exposed(string id)
    {
        var hit = view.Hits.SingleOrDefault(hit => hit.Zone == "Battlefield" && hit.Card != null && CardViews.Identity(hit.Card) == id);
        if (hit == null) return null;
        bool Matches(Point p) => p.X >= 0 && p.Y >= 0 && p.X < viewport.X && p.Y < viewport.Y
            && Hit(p)?.Card is { } card && CardViews.Identity(card) == id;
        bool Patch(Point p) => Matches(p) && Matches(new(p.X + 1, p.Y)) && Matches(new(p.X, p.Y + 1)) && Matches(new(p.X + 1, p.Y + 1));
        var middle = Pixel(hit.Bounds.Center.ToVector2()); if (Patch(middle)) return middle;
        var top = Pixel(new(hit.Bounds.Left, hit.Bounds.Top)); var bottom = Pixel(new(hit.Bounds.Right, hit.Bounds.Bottom));
        for (int y = Math.Max(0, top.Y); y < Math.Min(viewport.Y - 1, bottom.Y); y++)
            for (int x = Math.Max(0, top.X); x < Math.Min(viewport.X - 1, bottom.X); x++)
                if (Patch(new(x, y))) return new(x, y);
        observations.Add(new {
            stage = "occluded-card", id, hit.Bounds, displayed = view.DisplayedPose(id), scale = Scale,
            samples = Enumerable.Range(0, 5).SelectMany(y => Enumerable.Range(0, 5).Select(x => {
                var physical = new Point(top.X + x, top.Y + y);
                var logical = Logical(physical);
                var actual = Hit(physical);
                return new { physical, logical, contains = hit.Contains(logical), topHit = actual?.Id,
                    topCard = actual?.Card == null ? null : CardViews.Identity(actual.Card), topBounds = actual?.Bounds };
            })).ToArray()
        });
        return null;
    }
    private void MoveCard(string id) => Move(Exposed(id) ?? throw new InvalidOperationException("Covered card: " + id));
    private void MoveHit(string id) => Move(Pixel(view.Hits.LastOrDefault(hit => hit.Id == id)?.Bounds.Center.ToVector2()
        ?? throw new InvalidOperationException("Missing current hit: " + id)));
    private void Move(Point point) => mouse = new(point.X, point.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    private void Click() { Step(() => mouse = new(mouse.X, mouse.Y, 0, ButtonState.Pressed, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released)); Step(() => Move(new(mouse.X, mouse.Y))); }
    private void Step(Action action) => steps.Enqueue((null, action));
    private void Wait(Func<bool> ready, Action action) => steps.Enqueue((ready, action));
    private void Check(bool passed, string description) { if (!passed) throw new InvalidOperationException(description); checks.Add(description); }
    private void Observe(string stage) => observations.Add(new { stage, view.Now, viewport.X, viewport.Y,
        cards = FrameData.Cards.Count, view.ChangeCalloutBounds, view.DecisionPanelBounds,
        groups = view.PublicChanges.Select(group => new { group.Id, group.Title, group.Summary, count = group.Changes.Count }).ToArray(),
        nodes = FrameData.Scene!.Seats.SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards).Select(node => new {
            node.Id, node.Card.Name, node.GroupFront, node.GroupCount, node.Pose, node.DenseLabels, labels = view.BattlefieldLabels(node.Id).ToArray()
        }).ToArray() });
    private void Capture(string name)
    {
        if (window == null) return;
        using var output = File.Create(Path.Combine(view.Profile, name + ".png")); window.Surface.SaveAsPng(output, window.Surface.Width, window.Surface.Height);
        window.CaptureWindow(Path.Combine(view.Profile, name + "-window.png"));
    }
    private void Save(string? error) => File.WriteAllText(Path.Combine(view.Profile, "cluttered-changes-results.json"), JsonSerializer.Serialize(new {
        passed = error == null, error, checks, observations, pages, replies = engine.Replies,
        scope = "Actual production composition, public projections, hit precedence and pointer input. Controlled projection fixture, no rules simulation.", gpu = window != null
    }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    private void Advance()
    {
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (!steps.TryPeek(out var step)) return;
        if (step.Ready?.Invoke() == false) {
            if (view.Now - lastStep > 5) throw new TimeoutException("Cluttered interaction did not reach its required current state.");
            return;
        }
        steps.Dequeue(); step.Action(); lastStep = view.Now;
    }
    public void Frame(Texture2D surface)
    {
        try { Advance(); } catch (Exception exception) { Capture("failure"); Save(exception.ToString()); window?.Exit(); throw; }
    }
}
