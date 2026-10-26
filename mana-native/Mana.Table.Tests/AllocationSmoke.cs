using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Actual pointer input against controlled, engine-owned allocation metadata.
// This fixture checks presentation/commands; real rules are verified separately.
internal sealed class AllocationSmoke : ITableAutomation
{
    private readonly TableWindow.Probe view;
    private readonly InteractionScenarioEngine scenario;
    private readonly Queue<(double Delay, Func<bool>? Ready, Action Action)> steps = [];
    private readonly List<string> checks = [];
    private MouseState mouse;
    private double lastStep;
    private int before;
    private string expectedScope = "", expectedGame = "", expectedDecision = "";
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;

    internal static void Run(string profile, Point? size)
    {
        new PlayPreferences { Automatic = false, Sounds = false }.Save(profile);
        var scenario = new InteractionScenarioEngine();
        scenario.Present(Example(scenario.State, true));
        using var game = new TableWindow(TestTableServices.For(scenario), profile, null, null, size, initialState: scenario.State, artSource: new CardReadingArtFixture());
        game.Automation = new AllocationSmoke(game.TestPort, scenario);
        game.Run();
    }

    private AllocationSmoke(TableWindow.Probe view, InteractionScenarioEngine scenario)
    {
        this.view = view; this.scenario = scenario;
        Wait(() => view.Now >= 1.2 && Has("alloc-inspect:5"), 5, () => {
            Check(Has("alloc-inspect-source"), "The source card has its own inspectable art");
            Check(Enumerable.Range(0, 6).All(i => Has("alloc-inspect:" + i)), "Six individual recipient portraits are reachable on the first page");
            Check(!Has("alloc-inspect:6") && Has("alloc-next"), "A seventh recipient has an explicit next page");
            Check(view.Match!.Decision is { Payload: AllocationPayload { Amount: 10 }, Context.Source.Card: { Power: 1, Toughness: 10 } }, "The engine's damage amount stays separate from the source's current power and toughness");
            var allocation = (AllocationPayload)view.Match.Decision!.Payload;
            Check(allocation.Recipients[0].Card is { Power: 2, Toughness: 5, Damage: 3 }, "Current recipient toughness and already marked damage remain separate");
            Check(allocation.Recipients[0].Label == allocation.Recipients[1].Label && allocation.Recipients[0].Id != allocation.Recipients[1].Id,
                "Same-name recipients retain distinct prompt occurrences");
            Check(allocation.Recipients[2].Card is { FaceDown: true, Name: "Face-down card", Power: 2, Toughness: 2 }, "An anonymous recipient keeps authorized public combat stats without exposing its identity");
            Check(!Has("allocate") && view.AllocationAmounts.All(a => a == 0), "The untouched draft has no enabled Confirm action");
            Capture("01-combat-overview"); MoveInspectCaption("alloc-inspect:0"); before = scenario.Replies.Count;
        }); Click();
        Wait(() => view.AllocationDetail is AllocationRecipientInspection { Index: 0 }, 2, () => {
            Check(view.AllocationDetail is AllocationRecipientInspection { Index: 0 } && scenario.Replies.Count == before, "Clicking the visible Inspect caption inspects the exact recipient without submitting or discarding the allocation");
            Capture("02-recipient-current-state"); MoveHit("alloc-back");
        }); Click();
        Step(.12, () => MoveHit("alloc-required:0")); Click();
        Step(.12, () => {
            Check(view.AllocationAmounts.SequenceEqual([2, 0, 0, 0, 0, 0, 0]), "Required stages only the engine-supplied amount on the chosen occurrence");
            MoveHit("alloc-inspect-source");
        }); Click();
        Step(.12, () => {
            Check(view.AllocationDetail is AllocationSourceInspection && view.AllocationAmounts[0] == 2, "Source inspection preserves the recipient draft");
            MoveHit("alloc-back");
        }); Click();
        Step(.12, () => MoveHit("alloc-set:0")); Click();
        Step(.12, () => { view.TypeText("\b5"); MoveHit("alloc-apply"); }); Click();
        Step(.12, () => {
            Check(view.AllocationAmounts[0] == 5 && scenario.Replies.Count == before, "Typing an amount updates the draft without confirming it");
            MoveHit("alloc-reset");
        }); Click();
        Step(.12, () => {
            Check(view.AllocationAmounts.All(a => a == 0), "Reset restores the engine minimums across every page"); MoveHit("alloc-next");
        }); Click();
        Step(.12, () => {
            Check(Has("alloc-fill:6") && !Has("alloc-inspect:0"), "The seventh recipient's own controls are reachable without losing the first page");
            MoveHit("alloc-fill:6");
        }); Click();
        Step(.12, () => {
            Check(view.AllocationAmounts[6] == 10 && AllocationGuide.Total(view.AllocationAmounts) == 10, "Assign remaining stages the full available amount on a selected player");
            Check(!Has("allocate") && view.AllocationProblem?.Contains("before the defender", StringComparison.Ordinal) == true,
                "An exact total still requires the engine's blocker thresholds before a defender assignment");
            Capture("03-required-before-defender"); MoveHit("alloc-reset");
        }); Click();
        Step(.12, () => MoveHit("alloc-prev")); Click();
        Step(.12, () => MoveHit("alloc-required:0")); Click();
        Step(.12, () => MoveHit("alloc-required:1")); Click();
        Step(.12, () => MoveHit("alloc-required:2")); Click();
        Step(.12, () => {
            Check(view.AllocationAmounts.Take(3).SequenceEqual([2, 1, 2]), "Different same-name and anonymous blockers retain their separate required amounts");
            MoveHit("alloc-next");
        }); Click();
        Step(.12, () => MoveHit("alloc-fill:6")); Click();
        Step(.12, () => {
            Check(view.AllocationAmounts.SequenceEqual([2, 1, 2, 0, 0, 0, 5]) && Has("allocate"), "The complete legal draft enables Confirm with every page included");
            Capture("04-ready-defender"); MoveHit("close-overlay");
        }); Click();
        Step(.12, () => {
            Check(Has("open-decision") && view.AllocationAmounts[6] == 5, "Viewing the battlefield preserves the complete staged assignment"); MoveHit("open-decision");
        }); Click();
        Step(.12, () => {
            Check(view.AllocationAmounts.SequenceEqual([2, 1, 2, 0, 0, 0, 5]) && Has("allocate"), "Returning to the allocation preserves amounts and validation");
            Expect(); MoveHit("allocate");
        }); Click();
        Wait(ReplySettled, 3, () => {
            CheckReply([2, 1, 2, 0, 0, 0, 5], "Confirm sends one exact scoped allocation in original recipient order");
            Publish(false); MoveAway();
        });
        Wait(() => Has("alloc-fill:1"), 3, () => {
            Check(view.AllocationAmounts.SequenceEqual([1, 0]), "Generic allocation initializes each explicit engine minimum");
            var allocation = (AllocationPayload)view.Match!.Decision!.Payload;
            Check(AllocationGuide.Unit(allocation) == "+1/+1 counters" && !Has("alloc-required:0"), "Generic counter allocation uses its actual unit without invented combat thresholds");
            Check(AllocationGuide.Stats(allocation.Recipients[1].Card).Length == 0 && allocation.Recipients[1].Card!.Counters["LOYALTY"] == 5,
                "A planeswalker shows its public loyalty instead of invented creature stats");
            MoveHit("alloc-fill:0");
        }); Click();
        Step(.12, () => {
            Check(view.AllocationAmounts.SequenceEqual([2, 0]), "Remaining respects a recipient's engine maximum"); MoveHit("alloc-fill:1");
        }); Click();
        Step(.12, () => {
            Check(view.AllocationAmounts.SequenceEqual([2, 1]) && Has("allocate"), "Generic counters can be distributed across distinct current card states");
            Capture("05-counter-allocation"); MoveHit("alloc-inspect:1");
        }); Click();
        Step(.12, () => {
            Check(view.AllocationDetail is AllocationRecipientInspection { Index: 1 } && view.AllocationAmounts.SequenceEqual([2, 1]), "Inspecting the planeswalker leaves all counters staged");
            Capture("06-planeswalker-detail"); MoveHit("alloc-back");
        }); Click();
        Step(.12, () => { Expect(); MoveHit("allocate"); });
        Step(.03, () => Buttons(left: ButtonState.Pressed));
        Step(.04, () => Publish(false));
        Step(.04, () => Buttons());
        Step(.25, () => {
            Check(view.Scope != expectedScope && scenario.Replies.Count == before, "A prompt replacement between press and release discards the stale Confirm");
            Check(view.AllocationAmounts.SequenceEqual([1, 0]), "A replacement scope starts a fresh draft rather than retaining old amounts");
            MoveHit("alloc-fill:0");
        }); Click();
        Step(.12, () => MoveHit("alloc-fill:1")); Click();
        Step(.12, () => { Expect(); MoveHit("allocate"); }); Click();
        Wait(ReplySettled, 3, () => {
            CheckReply([2, 1], "The fresh generic assignment retains its exact game, decision and values");
            Publish(false, true); MoveAway();
        });
        Wait(() => Has("skip"), 3, () => { Expect(); MoveHit("skip"); }); Click();
        Wait(ReplySettled, 3, () => {
            CheckReply(null, "Assign later is available only when the engine permits it and submits the current scope");
            File.WriteAllText(Path.Combine(view.Profile, "allocation-ui-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            view.Exit();
        });
    }

    internal static GameSnapshot Example(GameSnapshot state, bool combat, bool maySkip = false)
    {
        Card Body(string id, string name, int power, int toughness) => new() {
            VisualId = id, CombatId = id, Name = name, Type = "Creature", Power = power, Toughness = toughness,
            Text = "This controlled fixture uses engine-projected current characteristics."
        };
        var source = Body("allocation-source", "Ancient Stoneguard", 1, 10) with { CombatKeywords = combat ? ["Trample"] : [], Type = combat ? "Creature" : "Artifact", Power = combat ? 1 : null, Toughness = combat ? 10 : null };
        AllocationRecipient Recipient(int index, Card card, int controller, int? lethal = null, int minimum = 0, int? maximum = null)
            => new(index * 3 + 4, "recipient-" + index, card.Name, card, card.VisualId, null,
                controller, controller, minimum, maximum ?? (combat ? 10 : 3), lethal);
        AllocationRecipient[] choices = combat ? [
            Recipient(0, Body("soldier-a", "Soldier", 2, 5) with { Damage = 3, CombatKeywords = ["Indestructible"], Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 1) }, 1, 2),
            Recipient(1, Body("soldier-b", "Soldier", 1, 1) with { CombatKeywords = ["Deathtouch"] }, 2, 1),
            Recipient(2, Body("anonymous-public", "Face-down card", 2, 2) with { FaceDown = true, Text = "", CombatKeywords = ["Ward 2"] }, 1, 2),
            Recipient(3, new() { VisualId = "walker", Name = "Wandering Adept", Type = "Planeswalker", Counters = ImmutableDictionary<string, int>.Empty.Add("LOYALTY", 4) }, 2),
            Recipient(4, Body("angel", "Serra Angel", 4, 4) with { CombatKeywords = ["Flying", "Vigilance"] }, 3),
            Recipient(5, Body("elf", "Llanowar Elves", 1, 1) with { Tapped = true }, 3),
            new(83, "defending-player", "Opponent 1", null, "", 1, null, null, 0, 10, null, true)
        ] : [
            Recipient(0, Body("counter-body", "Llanowar Elves", 3, 3) with { Damage = 1, Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 2) }, 0, minimum: 1, maximum: 2),
            Recipient(1, new() { VisualId = "counter-walker", Name = "Wandering Adept", Type = "Planeswalker", Counters = ImmutableDictionary<string, int>.Empty.Add("LOYALTY", 5) }, 0)
        ];
        return state with {
            Phase = combat ? "Combat damage" : "Main phase", PhaseKey = combat ? "COMBAT_DAMAGE" : "MAIN2", Combat = null, Stack = [],
            Players = state.Players.Select(p => p with { Priority = false, Zones = p.Zones.Select(z => z.Name == "Battlefield" ? z with { Cards = [], Count = 0 } : z).ToImmutableArray() }).ToImmutableArray(),
            Decision = new Decision("", new DecisionContext(Message: combat ? "Ancient Stoneguard assigns damage using toughness. Choose the amount for each recipient." : "Choose where these counters go. Current power and toughness already include existing counters.", Title: combat ? "Assign combat damage" : "Distribute +1/+1 counters", Source: new DecisionSource(CardId: source.VisualId, Card: source, ControllerId: 0)), new AllocationPayload(combat ? 10 : 3, combat ? AllocationKind.Combat : AllocationKind.Generic, combat ? "combat damage" : "+1/+1 counters", maySkip, choices.ToImmutableArray()))
        };
    }

    private void Publish(bool combat, bool maySkip = false) { scenario.Present(Example(scenario.State, combat, maySkip)); view.Accept(scenario.State); }
    private bool Has(string id) => view.Hits.Any(h => h.Id == id);
    private void Expect() { before = scenario.Replies.Count; expectedScope = view.Scope; expectedGame = view.Match!.Id; expectedDecision = view.Match.Decision!.Id; }
    private bool ReplySettled() => scenario.Replies.Count > before && !view.Busy;
    private void CheckReply(int[]? amounts, string description)
    {
        var reply = scenario.Replies.LastOrDefault();
        Check(scenario.Replies.Count == before + 1 && reply is DecisionCommand scoped && scoped.GameId == expectedGame && scoped.DecisionId == expectedDecision
            && (amounts == null ? reply is SkipAllocation : reply is AllocateAmounts allocation && allocation.Amounts.SequenceEqual(amounts)), description);
    }
    private void MoveHit(string id)
    {
        var hit = view.Hits.Single(h => h.Id == id);
        Check(view.HitAt(hit.Bounds.Center)?.Id == id, "The visible " + id + " control has unobstructed pointer geometry");
        Move(hit.Bounds.Center);
    }
    private void MoveInspectCaption(string id)
    {
        var hit = view.Frame!.Hits.Single(candidate => candidate.Id == id);
        var captionCenter = new Point(hit.Bounds.Center.X, hit.Bounds.Bottom - 8);
        bool reachable = SmokePointer.TryPoint(view, hit, captionCenter, out var physical);
        var actual = SmokePointer.Table(view, physical);
        Check(reachable && actual.Y >= hit.Bounds.Top + 112 && actual.Y >= hit.Bounds.Bottom - 16,
            "The Inspect caption has an exposed physical pointer target below the recipient's card art");
        mouse = new(physical.X, physical.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    private void MoveAway() => Move(new(1590, 890));
    private void Move(Point logical)
    {
        var viewport = view.GraphicsDevice.Viewport; float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        mouse = new((int)((viewport.Width - 1600 * scale) / 2 + logical.X * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + logical.Y * scale),
            0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    private void Buttons(ButtonState left = ButtonState.Released, ButtonState right = ButtonState.Released) => mouse = new(mouse.X, mouse.Y, 0, left, ButtonState.Released, right, ButtonState.Released, ButtonState.Released);
    private void Click() { Step(.03, () => Buttons(left: ButtonState.Pressed)); Step(.04, () => Buttons()); }
    private void RightClick() { Step(.03, () => Buttons(right: ButtonState.Pressed)); Step(.04, () => Buttons()); }
    private void Step(double delay, Action action) => steps.Enqueue((delay, null, action));
    private void Wait(Func<bool> ready, double deadline, Action action) => steps.Enqueue((deadline, ready, () => { Check(ready(), "The expected allocation screen arrived within the bounded wait"); action(); }));
    private void Check(bool value, string description) { if (!value) { Capture("failure"); throw new InvalidOperationException(description); } checks.Add(description); }
    private void Capture(string name) { using var output = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height); }
    public void Frame(Texture2D surface)
    {
        if (view.Now > 65) throw new TimeoutException("Allocation journey did not finish");
        if (view.Error.Length > 0) Check(false, view.Error);
        if (steps.TryPeek(out var next) && (next.Ready?.Invoke() == true || view.Now - lastStep >= next.Delay)) { steps.Dequeue(); next.Action(); lastStep = view.Now; }
    }
}
