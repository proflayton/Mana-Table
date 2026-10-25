using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Controlled snapshots exercise actual production rendering and press/release
// input. Java regressions separately establish real rules and public projection.
internal sealed class StateClaritySmoke : ITableAutomation
{
    private readonly TableWindow.Probe view;
    private readonly InteractionScenarioEngine scenario;
    private readonly Queue<(double Delay, Func<bool>? Ready, Action Action)> steps = [];
    private readonly List<string> checks = [];
    private MouseState mouse;
    private double lastStep;
    private int step;
    private string expectedDecision = "";
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;

    internal static void Run(string profile, Point? size)
    {
        new PlayPreferences { Automatic = false, Sounds = false }.Save(profile);
        var scenario = new InteractionScenarioEngine(); scenario.Present(Example());
        using var game = new TableWindow(TestTableServices.For(scenario), profile, null, null, size, initialState: scenario.State, artSource: new CardReadingArtFixture());
        game.Automation = new StateClaritySmoke(game.TestPort, scenario); game.Run();
    }

    private StateClaritySmoke(TableWindow.Probe view, InteractionScenarioEngine scenario)
    {
        this.view = view; this.scenario = scenario;
        // The capture should show the settled reveal opacity, not the first
        // frame's intentional scene fade. Input/readiness still use real Draws.
        Step(.35, () => { });
        Wait(() => RecipientHits() == 3 && view.Hits.Any(h => h.Id == "player-counters:0"), 3, () => {
            Check(RecipientHits() == 3 && StateClarity.Recipients(view.Match!).Select(p => p.Id).SequenceEqual([0, 1, 2]),
                "The current decision visibly offers exactly its three legal named recipients, including You");
            Check(scenario.Replies.Count == 0, "Showing a triggered recipient choice never automatically selects yourself");
            Check(view.Hits.Count(h => h.Id.StartsWith("player-counters:", StringComparison.Ordinal)) == 3,
                "Every seat with nonzero public counters has a persistent inspectable resource badge");
            Check(StateClarity.Purpose(view.Match!).Contains("target player", StringComparison.OrdinalIgnoreCase)
                && view.Match!.Decision!.SourceCard?.Name == "Toothy, Imaginary Friend",
                "The source and engine-owned target purpose remain available beside the named controls");
            Capture("01-public-state-and-recipients");
        });
        ClickHit("settings");
        Wait(() => view.Hits.Any(h => h.Id == "text-size"), 3, () => { });
        ClickHit("text-size"); ClickHit("text-size"); ClickHit("close-overlay");
        Wait(() => RecipientHits() == 3, 3, () => {
            Check(view.TextScale == 1.3f && PlayPreferences.Load(view.Profile).TextScale == 1.3f,
                "Real settings gestures persist130% text before the decision and public-state walkthrough");
            var targets = view.Hits.Where(h => h.Id.StartsWith("recipient:", StringComparison.Ordinal)).ToArray();
            Check(targets.Length == 3 && targets.All(h => view.HitAt(SmokePointer.Table(view, SmokePointer.Hit(view, h))) is { } actual
                && actual.Id == h.Id && actual.Scope == h.Scope),
                "Larger decision text preserves an exposed physical target for every legal named recipient");
            Capture("01b-larger-decision-text");
            Publish(view.Match! with { Players = view.Match!.Players.Select(p => p.Id != 0 ? p : p with {
                Counters = ImmutableDictionary<string, int>.Empty.Add("Poison", 9).Add("Energy", 2).Add("Experience", 3).Add("Rad", 4).Add("Ticket", 5).Add("Acorn", 6).Add("Shield", 7).Add("Time", 8).Add("Quest", 9)
            }).ToImmutableArray() });
        });
        Wait(() => view.CounterBadge(0) is { HiddenTypes: 8 }, 3, () => {
            Check(view.CounterBadge(0) is { Text: "Poison 9 +8 types", HiddenTypes: 8 } badge
                && badge.MeasuredWidth <= badge.AvailableWidth && view.TextScale == 1.3f,
                "At130% the painted own-seat badge fits the complete Poison total and explicit overflow affordance");
            Capture("01c-counter-badge-overflow");
        });
        ClickHit("player-counters:0");
        Wait(() => view.Overlay == "player", 3, () => {
            Check(view.Overlay == "player" && StateClarity.CounterSummary(view.Match!.Viewer!).Contains("Poison 9"),
                "A public resource badge opens the player's current totals without choosing that player");
            Check(scenario.Replies.Count == 0, "Inspecting player resources submits no game action");
            Capture("02-player-counters");
        });
        Wait(() => view.Hits.Any(h => h.Id == "counter-details-next"), 3, () => {
            Check(view.Match!.Viewer!.Counters.Count == 9 && view.Hits.Any(h => h.Id == "counter-details-next"),
                "Every extra public counter type remains accessible through whole-entry pages at130% text");
            Capture("02a-counter-first-page");
        });
        ClickHit("counter-details-next");
        Step(.1, () => {
            Check(view.Hits.Any(h => h.Id == "counter-details-prev") && scenario.Replies.Count == 0,
                "Browsing another counter page changes no game decision or current public totals");
            Capture("02b-counter-next-page");
            Publish(view.Match! with { Players = view.Match!.Players.Select(p => p.Id != 0 ? p : p with { Counters = ImmutableDictionary<string, int>.Empty.Add("Poison", 9).Add("Energy", 2) }).ToImmutableArray() });
        });
        ClickHit("close-overlay");
        Wait(() => RecipientHits() == 3, 3, () => Move(new(35, 105))); Click();
        Step(.1, () => {
            Check(scenario.Replies.Count == 0, "Clicking decision explanation text cannot activate a card hidden behind its panel");
            expectedDecision = view.Match!.Decision!.Id;
        });
        ClickHit("recipient:2");
        Wait(() => !view.Busy && scenario.Replies.Count == 1, 3, () => {
            Check(scenario.Replies.Single() is SelectPlayer { Player: 2 } reply && reply.DecisionId == expectedDecision,
                "The named other-player recipient sends the exact current scoped player choice");
            Publish(view.Match! with { Decision = null, DecisionOwnerId = 2, PriorityPlayerId = 1, ActionClass = "choice" });
        });
        Step(.15, () => {
            Check(RecipientHits() == 0 && StateClarity.WaitingTitle(view.Match!).Contains("Seat 3"),
                "After choosing, only the public waiting role remains and no other player's private recipients are rendered");
            Check(view.Match!.ActivePlayerId == 0 && view.Match.PriorityPlayerId == 1 && view.Match.DecisionOwnerId == 2,
                "Active turn, priority and private chooser can remain three different seats");
            Capture("03-waiting-for-another-seat");
            var source = view.Match!.Viewer!.Zone("Battlefield").Cards.Single(c => c.VisualId == "clarity-source");
            Publish(view.Match with { DecisionOwnerId = 0, PriorityPlayerId = 0, ActionClass = "priority",
                Decision = new() { Task = DecisionTask.Priority, OkEnabled = true },
                Stack = [new("Same source", "First public trigger.", true, source) { Id = "trigger-a", ControllerId = 1 },
                    new("Same source", "Second public trigger.", true, source) { Id = "trigger-b", ControllerId = 2 }] });
        });
        Wait(() => view.Hits.Any(h => h.Id == "stack-source:trigger-a")
            && view.Hits.Any(h => h.Id == "stack-effect:trigger-b"), 3, () => {
            Check(StateClarity.StackController(view.Match!, view.Match!.Stack[0]).Contains("Seat 2")
                && StateClarity.StackController(view.Match, view.Match.Stack[1]).Contains("Seat 3"),
                "Identically named stack occurrences retain different controlling seats");
        });
        ClickHit("stack-effect:trigger-b");
        Wait(() => view.ZoneName == "Stack" && view.InspectedStack == "trigger-b", 3, () => {
            Check(view.InspectedStack == "trigger-b"
                && StateClarity.StackController(view.Match!, view.Match!.Stack.Single(entry => entry.Id == view.InspectedStack)).Contains("Seat 3")
                && scenario.Replies.Count == 1,
                "The current collapsed effect opens its exact stack occurrence and controller without submitting a game action");
        });
        ClickHit("stack-newer");
        Wait(() => view.ZoneName == "Stack" && view.InspectedStack == "trigger-a", 3, () => {
            Check(view.ZoneName == "Stack" && view.InspectedStack == "trigger-a"
                && StateClarity.StackController(view.Match!, view.Match!.Stack.Single(entry => entry.Id == view.InspectedStack)).Contains("Seat 2")
                && scenario.Replies.Count == 1,
                "Stack inspection follows Above to the other identically named occurrence while preserving its distinct controller");
            Capture("04-stack-controller");
        });
        ClickHit("close-overlay");
        Step(.1, () => Publish(view.Match! with { Stack = [], Activity = [new(1, 5, "MAIN1", "resolved", 1,
            "Same source's ability did not resolve.", "clarity-source", "Same source") { Outcome = "notResolved", Detail = "The engine reported that this ability did not resolve." }] }));
        Wait(() => view.LastResolution?.Outcome == "notResolved", 3, () => {
            Check(view.LastResolution?.Heading == "DID NOT RESOLVE"
                && view.Frame!.Observe().Text.Any(text => text.Value == "DID NOT RESOLVE")
                && view.Hits.Any(hit => hit.Id == "action-details"),
                "An engine-reported failed resolution retains its DID NOT RESOLVE notice and review control");
            Capture("05-did-not-resolve");
            Publish(view.Match! with { Activity = [.. view.Match!.Activity,
                new(2, 5, "MAIN1", "draw", 2, "Alex drew 9 cards.", null, null) { Count = 9 },
                new(3, 5, "MAIN1", "shuffle", 2, "Alex shuffled their library.", null, null) { Count = 1 }] });
        });
        Wait(() => view.Match!.Activity.Any(entry => entry.Id == 2 && entry.Kind == "draw" && entry.Count == 9)
            && view.Match.Activity.Any(entry => entry.Id == 3 && entry.Kind == "shuffle" && entry.Count == 1), 3, () => {
            Check(view.Match!.Activity.Any(entry => entry.Kind == "draw" && entry.Count == 9)
                && view.Match.Activity.Any(entry => entry.Kind == "shuffle" && entry.Count == 1)
                && view.Hits.All(hit => hit.Id is not ("public-outcome-panel" or "public-outcome-history"))
                && view.Hits.Any(hit => hit.Id == "history"),
                "Authoritative draw and shuffle outcomes remain available in history without adding a detached prose panel");
            Check(view.Match!.Activity.Where(e => e.Kind is "draw" or "shuffle").All(e => e.CardId == null && e.CardName == null),
                "Public draw and shuffle outcomes carry no private card identities");
            expectedDecision = view.Match.Decision!.Id;
            Capture("06-draw-and-shuffle");
        });
        ClickHit("history");
        Wait(() => view.Overlay == "history", 3, () => {
            Check(view.Overlay == "history" && scenario.Replies.Count == 1 && view.Match!.Decision!.Id == expectedDecision
                && view.Frame!.Observe().Text.Any(text => text.Value.Contains("Alex drew 9 cards.", StringComparison.Ordinal))
                && view.Frame.Observe().Text.Any(text => text.Value.Contains("Alex shuffled their library.", StringComparison.Ordinal)),
                "The existing header history exposes both public outcomes without another reply or a decision change");
            Capture("07-public-outcome-history");
        });
        ClickHit("close-overlay");
        Step(.1, () => {
            Publish(view.Match! with { Players = view.Match!.Players.Select(p => p.Id == 0 ? p with { Counters = ImmutableDictionary<string, int>.Empty } : p).ToImmutableArray() });
            Move(new(1500, 70));
        });
        Wait(() => view.Hits.All(h => h.Id != "player-counters:0"), 3, () => {
            Check(view.Hits.All(h => h.Id != "player-counters:0"), "Removing the last player counter removes its persistent badge");
            var anonymous = view.Match!.Players[1].Zone("Battlefield").Cards.Single();
            Check(anonymous is { FaceDown: true, Power: 3, Toughness: 3, Damage: 1, Name: "Face-down card", ArtName: "", Text: "", OtherFace: null }
                && CardPresentation.HasAbility(anonymous, "flying"), "The anonymous battlefield occurrence retains public modified stats and ability while its face remains masked");
            MoveHit(view.Hits.First(h => h.Card?.VisualId == "anonymous-public"));
        });
        Step(.2, () => mouse = Mouse(ButtonState.Released, ButtonState.Pressed));
        Step(0, () => mouse = Mouse(ButtonState.Released));
        Wait(() => view.Overlay == "inspect", 3, () => {
            Check(view.Overlay == "inspect" && scenario.Replies.Count == 1, "An anonymous permanent's public characteristics can be inspected without revealing or selecting its identity");
            Capture("08-anonymous-public-state");
        });
        ClickHit("close-overlay");
        Step(.1, () => Publish(Example() with { Players = view.Match!.Players, Decision = Example().Decision! with { PlayerChoices = [0, 1] } }));
        Wait(() => RecipientHits() == 2, 3, () => MoveHit(view.Hits.Single(h => h.Id == "recipient:1")));
        Step(0, () => mouse = Mouse(ButtonState.Pressed));
        Step(0, () => Publish(view.Match! with { Decision = view.Match!.Decision! with { PlayerChoices = [0] } }));
        Step(0, () => mouse = Mouse(ButtonState.Released));
        Step(.15, () => {
            Check(scenario.Replies.Count == 1, "A recipient revoked between real press and release cannot submit through stale geometry");
            Check(RecipientHits() == 1, "The refreshed decision exposes only its remaining legal recipient");
            expectedDecision = view.Match!.Decision!.Id;
        });
        ClickHit("recipient:0");
        Wait(() => !view.Busy && scenario.Replies.Count == 2, 3, () => {
            Check(scenario.Replies.Last() is SelectPlayer { Player: 0 } reply && reply.DecisionId == expectedDecision,
                "Choosing You remains an explicit scoped action after another recipient was revoked");
            Capture("09-explicit-self-choice");
        });
        Step(.1, () => {
            File.WriteAllText(Path.Combine(view.Profile, "state-clarity-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            view.Exit();
        });
    }

    private static GameSnapshot Example()
    {
        var source = new Card { VisualId = "clarity-source", CombatId = "clarity-source", Name = "Toothy, Imaginary Friend", Type = "Legendary Creature — Illusion", Power = 1, Toughness = 1 };
        var players = Enumerable.Range(0, 4).Select(id => new Player(id, id == 0 ? "You" : "Alex", 40, id == 0, false, ImmutableDictionary<string, int>.Empty, [
            new("Battlefield", id is 0 or 1 ? 1 : 0, id == 0 ? [source] : id == 1 ? [new() { VisualId = "anonymous-public", CombatId = "anonymous-public", Name = "Face-down card",
                FaceDown = true, Type = "Creature", Power = 3, Toughness = 3, Damage = 1, CombatKeywords = ["flying"], Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 1) }] : [], null),
            new("Hand", id == 0 ? 0 : 7, [], null), new("Library", 70, [], null), new("Graveyard", 0, [], null), new("Exile", 0, [], null), new("Command", 0, [], null)
        ]) { Counters = id == 0 ? ImmutableDictionary<string, int>.Empty.Add("Poison", 9).Add("Energy", 2) : id == 1 ? ImmutableDictionary<string, int>.Empty.Add("Experience", 3) : id == 2 ? ImmutableDictionary<string, int>.Empty.Add("Poison", 1) : ImmutableDictionary<string, int>.Empty }).ToArray();
        return new("clarity-scenario", "playing", null, null, 0, 5, "Main phase", "MAIN1", 0, players.ToImmutableArray(), [], null,
            new() { Task = DecisionTask.EntitySelection, PlayerChoices = [0, 1, 2], SourceCard = source, SourceControllerId = 0,
                Message = "Partner with Pir — target player may put Pir into their hand from their library, then shuffle. Choose the recipient." }) {
            DecisionOwnerId = 0, PriorityPlayerId = 0, ActionClass = "selection"
        };
    }

    private int RecipientHits() => view.Hits.Count(h => h.Id.StartsWith("recipient:", StringComparison.Ordinal));
    private void Publish(GameSnapshot state) { scenario.Present(state); view.Accept(scenario.State); }
    private void Step(double delay, Action action) => steps.Enqueue((delay, null, action));
    private void Wait(Func<bool> ready, double maximum, Action action) => steps.Enqueue((maximum, ready, action));
    private void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); checks.Add(description); }
    private void Move(Point logical) { var physical = SmokePointer.Screen(view, logical); mouse = new(physical.X, physical.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released); }
    private void MoveHit(Mana.Table.Interaction.TableHit hit) { var physical = SmokePointer.Hit(view, hit); mouse = new(physical.X, physical.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released); }
    private MouseState Mouse(ButtonState left, ButtonState right = ButtonState.Released) => new(mouse.X, mouse.Y, 0, left, ButtonState.Released, right, ButtonState.Released, ButtonState.Released);
    private void Click() { Step(0, () => mouse = Mouse(ButtonState.Pressed)); Step(0, () => mouse = Mouse(ButtonState.Released)); }
    private void ClickHit(string id) { Step(0, () => MoveHit(view.Hits.Single(h => h.Id == id))); Click(); }
    private void Capture(string name) {
        using var file = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(file, view.Surface.Width, view.Surface.Height);
        view.CaptureWindow(Path.Combine(view.Profile, name + "-window.png"));
    }
    public void Frame(Texture2D surface)
    {
        try {
            if (view.Now > 45) throw new TimeoutException("State clarity journey exceeded45s at step " + step);
            if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
            if (steps.Count == 0) return;
            var next = steps.Peek();
            double elapsed = view.Now - lastStep;
            if (next.Ready != null) {
                if (!next.Ready()) {
                    if (elapsed >= next.Delay)
                        throw new TimeoutException($"State clarity step {step + 1} did not become ready within {next.Delay}s (overlay '{view.Overlay}', zone '{view.ZoneName}', scope '{view.Scope}').");
                    return;
                }
            } else if (elapsed < next.Delay) return;
            steps.Dequeue(); step++; next.Action(); lastStep = view.Now;
        } catch {
            File.WriteAllText(Path.Combine(view.Profile, "state-clarity-failure.json"), JsonSerializer.Serialize(new { step, view.Now, checks,
                view.Scope, view.Overlay, view.ZoneName, counterBadge = view.CounterBadge(0), replies = scenario.Replies, state = view.Match,
                hits = view.Hits.Select(h => new { h.Id, h.Scope, h.Bounds }) }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            Capture("failure"); throw;
        }
    }
}
