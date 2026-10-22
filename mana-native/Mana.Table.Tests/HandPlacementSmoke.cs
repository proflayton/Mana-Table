using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Controlled engine permissions; real production gestures and overlays.
internal sealed class HandPlacementSmoke : ITableAutomation
{
    private readonly TableWindow.Probe view;
    private readonly InteractionScenarioEngine scenario;
    private readonly Queue<(double Delay, Func<bool>? Ready, Action Action)> steps = [];
    private readonly List<string> checks = [];
    private MouseState mouse;
    private MouseState consumedMouse;
    private (string Id, string Scope)? pointerTarget;
    private readonly List<object> gestures = [];
    private bool validatePointer = true;
    private int stepNumber, frameNumber;
    private string stage = "Starting hand-placement journey";
    private double lastStep;
    private int before;
    private string expectedGame = "", expectedDecision = "", expectedCard = "", expectedAbility = "";
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse()
    {
        bool leftPress = mouse.LeftButton == ButtonState.Pressed && consumedMouse.LeftButton == ButtonState.Released;
        bool leftRelease = mouse.LeftButton == ButtonState.Released && consumedMouse.LeftButton == ButtonState.Pressed;
        bool rightPress = mouse.RightButton == ButtonState.Pressed && consumedMouse.RightButton == ButtonState.Released;
        bool rightRelease = mouse.RightButton == ButtonState.Released && consumedMouse.RightButton == ButtonState.Pressed;
        if (leftPress || leftRelease || rightPress || rightRelease) {
            var physical = new Point(mouse.X, mouse.Y);
            var logical = SmokePointer.Table(view, physical);
            var actual = view.HitAt(logical);
            gestures.Add(new { time = view.Now, stepNumber, frameNumber, stage, physical, logical,
                edge = leftPress ? "left-press" : leftRelease ? "left-release" : rightPress ? "right-press" : "right-release",
                expected = pointerTarget?.Id, expectedScope = pointerTarget?.Scope, actual = actual?.Id, actualScope = actual?.Scope,
                validatePointer, replies = scenario.Replies.Count });
            if (gestures.Count > 200) gestures.RemoveAt(0);
            // Right-click acts on press and may open an inspector before release.
            // Drag releases have a free table destination rather than a card target.
            if (!rightRelease && validatePointer && pointerTarget is { } target
                && (actual is null || actual.Id != target.Id || actual.Scope != target.Scope || !view.CurrentHit(actual)))
                DriverFailure("The physical gesture no longer reaches its expected current hit: " + target.Id);
        }
        consumedMouse = mouse;
        return mouse;
    }

    internal static void Run(string profile, Point? size)
    {
        new PlayPreferences { Automatic = false, Sounds = false }.Save(profile);
        var scenario = new InteractionScenarioEngine();
        scenario.Present(Example(scenario.State, "single"));
        using var game = new TableWindow(TestTableServices.For(scenario), profile, null, null, size, initialState: scenario.State, artSource: new CardReadingArtFixture());
        game.Automation = new HandPlacementSmoke(game.TestPort, scenario);
        game.Run();
    }
    private HandPlacementSmoke(TableWindow.Probe view, InteractionScenarioEngine scenario)
    {
        this.view = view; this.scenario = scenario;
        Step(1.5, () => {
            Check(view.Match!.Viewer is { LandPlaysUsed: 1, LandPlayLimit: 1 }, "The normal land-play allowance is already used");
            Check(HandPlacements.Options(view.Match, Forest()).Length == 1 && !Forest().Selectable,
                "A separate engine-projected hand placement remains available without a normal card action");
            before = scenario.Replies.Count; MoveCard("hand-forest");
        });
        Click();
        Wait(() => Reading("hand-forest") && view.Hits.Any(hit => hit.Id == "read-card-action"), 3, () => {
            Check(Reading("hand-forest") && view.Hits.Any(hit => hit.Id == "read-card-action"),
                "An eligible hand card exposes its reader action before committing a placement");
            Check(scenario.Replies.Count == before, "A single hand click reads without placing or activating anything");
            Capture("01-placement-hover");
            Expect("placement-a"); MoveHit("read-card-action");
        });
        Click();
        Wait(ReplySettled, 3, () => {
            CheckPlacement("The reader's explicit placement action uses the exact projected source directly");
            Check(view.Match!.Viewer!.Zone("Hand").Cards.Any(c => c.VisualId == "hand-forest") && view.Match.Stack is [{ Id: "placement-on-stack" }],
                "The exact placement ability enters the stack while the hand card remains uncommitted for the response window");
            Capture("02-placement-response-window");
            var state = scenario.State;
            scenario.PresentHandPlacement(state with { Stack = [], Decision = new() { Kind = "choice", Min = 0, Max = 1,
                Message = "Choose a basic Forest card from your hand, or choose none.", Choices = [new(0, "Forest", Forest())] } });
            view.Accept(scenario.State); before = scenario.Replies.Count; MoveAway();
        });
        Step(.3, () => {
            Check(view.Match?.Decision is { Kind: "choice", Min: 0, Max: 1, Choices.Length: 1 },
                "When the ability resolves, the engine presents the optional hand-card decision");
            MoveHit("choice:0");
        }); Click();
        Step(.15, () => MoveHit("choicesdone")); Click();
        Wait(ReplySettled, 3, () => {
            Check(view.Match!.Viewer!.Zone("Battlefield").Cards.Any(c => c.VisualId == "hand-forest") && view.Match.Viewer.LandPlaysUsed == 1,
                "Only the later resolved choice places the Forest, preserving ordinary land-play usage");
            Publish("single"); MoveAway();
        });
        Step(.7, () => { Expect("placement-a"); MoveCard("hand-forest"); });
        Drag();
        Wait(ReplySettled, 3, () => {
            CheckPlacement("Dragging onto the table uses the same exact projected placement");
            Capture("02-drag-placement"); Publish("multi"); MoveAway();
        });
        Step(.7, () => { before = scenario.Replies.Count; MoveCard("hand-forest"); }); ReadAndUse();
        Wait(() => view.Overlay == "hand-placement", 3, () => {
            Check(view.Overlay == "hand-placement" && scenario.Replies.Count == before,
                "Multiple placement sources require an explicit choice without activating a default source");
            Check(view.Hits.Any(h => h.Id == "placement-use:placement-a") && view.Hits.Any(h => h.Id == "placement-use:placement-b"),
                "The chooser exposes each independent source");
            Capture("03-source-choice"); Expect("placement-b"); MoveHit("placement-use:placement-b");
        }); Click();
        Wait(ReplySettled, 3, () => {
            CheckPlacement("Choosing the second source submits only its exact ability and hand card");
            Publish("multi"); MoveAway();
        });
        Step(.7, () => { before = scenario.Replies.Count; MoveCard("hand-forest"); }); Drag();
        Wait(() => view.Overlay == "hand-placement", 3, () => {
            Check(view.Overlay == "hand-placement" && scenario.Replies.Count == before, "Dragging with multiple sources also opens the explicit chooser");
            MoveHit("placement-use:placement-a");
        });
        Step(.03, () => Buttons(left: ButtonState.Pressed));
        Step(.06, () => Publish("revoked"));
        // The deliberately stale release must reach production's rejection guard.
        Step(.06, () => Buttons(validateTarget: false));
        Step(.25, () => {
            Check(view.Overlay.Length == 0 && scenario.Replies.Count == before,
                "Revoking the placement between press and release closes the chooser and discards the stale action");
            Publish("normal"); MoveAway();
        });
        Step(.7, () => { before = scenario.Replies.Count; MoveCard("hand-forest"); }); ReadAndUse();
        Wait(ReplySettled, 3, () => {
            Check(scenario.Replies.Count == before + 1 && scenario.Replies[^1] is PlayCard,
                "When ordinary play is legal, the reader's Play card action keeps the ordinary engine route");
            Publish("normal"); MoveAway();
        });
        Step(.7, () => MoveCard("hand-forest")); RightClick();
        Wait(() => view.Overlay == "inspect", 3, () => {
            Check(view.Hits.Any(h => h.Id == "inspect-placement"), "The hand inspector exposes the alternate placement even when normal play is legal");
            Capture("04-inspector-alternative"); Expect("placement-a"); MoveHit("inspect-placement");
        }); Click();
        Wait(ReplySettled, 3, () => {
            CheckPlacement("The inspector's explicit alternate action uses the requested placement source");
            Publish("phased"); MoveAway();
        });
        Step(.7, () => { before = scenario.Replies.Count; MoveCard("hand-forest"); }); Click();
        Wait(() => Reading("hand-forest"), 3, () => {
            Check(Reading("hand-forest") && scenario.Replies.Count == before && view.Overlay.Length == 0
                && HandPlacements.Options(view.Match!, Forest()).Length == 0 && !view.Hits.Any(h => h.Id == "read-card-action"),
                "A phased-out source leaves a readable hand card without an available action or accidental placement");
            MoveCard(view.Frame!.Cards.Single(card => card.Focused).Id);
        }); Click();
        Step(.1, () => {
            Publish("single"); MoveAway();
        });
        Step(.7, () => { Expect("placement-a"); MoveCard("hand-forest"); }); ReadAndUse();
        Wait(ReplySettled, 3, () => {
            CheckPlacement("An optional placement begins through the same exact source route");
            var state = scenario.State;
            scenario.PresentHandPlacement(state with { Stack = [], Decision = new() { Kind = "choice", Min = 0, Max = 1,
                Message = "Choose a basic Forest card from your hand, or choose none.", Choices = [new(0, "Forest", Forest())] } });
            view.Accept(scenario.State); before = scenario.Replies.Count; MoveAway();
        });
        Step(.3, () => {
            Check(view.Hits.Any(h => h.Id == "choice-cancel"), "The resolving placement retains the engine's explicit Choose none action");
            MoveHit("choice-cancel");
        }); Click();
        Wait(ReplySettled, 3, () => {
            Check(scenario.Replies[^1] is ChooseOptions { Choices.Length: 0 } && view.Match!.Viewer!.Zone("Hand").Cards.Any(c => c.VisualId == "hand-forest")
                && view.Match.Viewer.LandPlaysUsed == 1, "Choosing none leaves the Forest in hand and never consumes an ordinary land play");
            Publish("zones"); MoveAway();
        });
        Step(.7, () => {
            Check(view.Hits.Any(h => h.Id == "zone-actions:0:Graveyard") && view.Hits.Any(h => h.Id == "zone-actions:0:Exile")
                && view.Hits.Any(h => h.Id == "zone-actions:0:Command"), "Your graveyard, exile and command zone visibly signal currently available cards");
            Check(!view.Hits.Any(h => h.Id.StartsWith("zone-actions:1:")), "Opponent zones never receive private action availability cues");
            Capture("05-zone-availability"); MoveHit("zone-actions:0:Graveyard");
        }); Click();
        Step(.2, () => {
            Check(view.ZoneName == "Graveyard" && view.Hits.Any(h => h.Id == "zone-use:own-grave"),
                "Opening the graveyard exposes an explicit use action on the available card");
            before = scenario.Replies.Count; Capture("06-graveyard-action"); MoveCard("own-grave");
        }); Click();
        Wait(() => view.Overlay == "inspect", 3, () => {
            Check(scenario.Replies.Count == before && view.ZoneName == "Graveyard" && view.Overlay == "inspect",
                "A single graveyard-card click opens its inspector without committing its available action");
            Check(view.Hits.Any(h => h.Id == "inspect-zone-use"), "The graveyard inspector offers the legitimate current zone action");
            Capture("07-graveyard-inspector"); MoveHit("inspect-zone-use");
        }); Click();
        Wait(ReplySettled, 3, () => {
            Check(scenario.Replies.Count == before + 1 && scenario.Replies[^1] is PlayCard && view.Match?.Decision is { Purpose: DecisionPurpose.AbilitySelection, SourceZone: "Graveyard" },
                "Use from graveyard opens the engine's current ability and cost choice");
            Check(view.Match!.Decision!.Choices.Any(c => c.Label.StartsWith("Flashback", StringComparison.Ordinal)),
                "The engine-provided chooser supplies the actual Flashback label rather than guessing from printed text");
            Capture("08-engine-ability-choice"); Publish("zones"); MoveAway();
        });
        Step(.7, () => MoveHit("zone-actions:0:Exile")); Click();
        Step(.2, () => {
            Check(view.Hits.Any(h => h.Id == "zone-use:own-exile"), "Exile uses the same explicit current-action control");
            before = scenario.Replies.Count; MoveHit("zone-use:own-exile");
        }); Click();
        Wait(ReplySettled, 3, () => {
            Check(scenario.Replies.Count == before + 1 && view.Match?.Decision is { Purpose: DecisionPurpose.AbilitySelection, SourceZone: "Exile" },
                "The exile action uses the normal engine route exactly once");
            Publish("zones"); MoveAway();
        });
        Step(.7, () => MoveHit("zone-actions:0:Exile")); Click();
        Step(.2, () => { before = scenario.Replies.Count; MoveHit("zone-use:own-exile"); });
        Step(.03, () => Buttons(left: ButtonState.Pressed));
        Step(.06, () => Publish("revoked"));
        // The deliberately stale release must reach production's rejection guard.
        Step(.06, () => Buttons(validateTarget: false));
        Step(.2, () => {
            Check(scenario.Replies.Count == before && !view.Hits.Any(h => h.Id == "zone-use:own-exile"),
                "Revoking a zone action between press and release discards the stale use button");
            MoveAway();
        });
        Step(.7, () => {
            Check(!view.Hits.Any(h => h.Id.StartsWith("zone-actions:")), "Unavailable zones lose their action cues on the next authoritative snapshot");
            MoveHit("close-overlay");
        }); Click();
        (string Game, string Prompt, string Key) sourceRead = default;
        Step(.2, () => {
            scenario.SourceActionRead = (game, prompt, key) => {
                var current = scenario.State; var card = current.Viewer!.Zones.SelectMany(zone => zone.Cards).Single(card => card.Key == key);
                sourceRead = (game, prompt, key);
                return new(current.Id, current.Decision!.Id, card.Key, card.VisualId, []);
            };
            Publish("single"); MoveAway();
        });
        Step(.7, () => { Expect("placement-a"); MoveCard("hand-forest"); }); Click();
        Wait(() => Reading("hand-forest") && view.Hits.Any(hit => hit.Id == "read-card-action"), 3, () => {
            Check(scenario.Replies.Count == before && sourceRead == (expectedGame, expectedDecision, expectedCard)
                && ReaderActionText() == HandPlacements.Options(view.Match!, Forest()).Single().Text,
                "A valid empty source-action read preserves the hand card's concrete engine-reported placement action without submitting anything");
            Capture("09-source-aware-hand-placement"); MoveHit("read-card-action");
        }); Click();
        Wait(ReplySettled, 3, () => {
            CheckPlacement("The source-aware reader submits the existing exact hand-placement permission even when the source-action list is empty");
            scenario.PresentHandPlacement(scenario.State with { Stack = [], Decision = new() { Kind = "choice", Min = 0, Max = 1,
                Choices = [new(0, "Forest", Forest())] } });
            view.Accept(scenario.State); before = scenario.Replies.Count; MoveAway();
        });
        Step(.3, () => MoveHit("choice:0")); Click();
        Step(.15, () => MoveHit("choicesdone")); Click();
        Wait(ReplySettled, 3, () => {
            Check(view.Match!.Viewer!.Zone("Battlefield").Cards.Any(card => card.VisualId == "hand-forest") && view.Match.Viewer.LandPlaysUsed == 1,
                "The later authored hand-card choice still places that Forest while retaining the spent normal land play");
            scenario.SourceActionRead = (game, prompt, key) => {
                var current = scenario.State; var card = current.Viewer!.Zones.SelectMany(zone => zone.Cards).Single(card => card.Key == key);
                sourceRead = (game, prompt, key);
                return new(current.Id, current.Decision!.Id, card.Key, card.VisualId, card.VisualId == "own-grave"
                    ? [new("grave-flashback", "cast", "Flashback {1}{U}, Pay 3 life: Target player draws two cards.")] : []);
            };
            Publish("zones"); before = scenario.Replies.Count; MoveAway();
        });
        Step(.7, () => MoveHit("zone-actions:0:Graveyard")); Click();
        Wait(() => view.Frame!.Cards.Any(card => card.Card.VisualId == "own-grave" && card.Zone == "Graveyard"
            && Microsoft.Xna.Framework.Vector2.Distance(card.Display.Center, card.Rest.Center) < 2
            && Microsoft.Xna.Framework.Vector2.Distance(card.Display.Size, card.Rest.Size) < 2), 3, () => MoveCard("own-grave")); Click();
        Wait(() => view.Frame!.Cards.Any(card => card.Focused && card.Card.VisualId == "own-grave" && card.Zone == "Graveyard")
            && view.Hits.Any(hit => hit.Id == "read-card-action"), 3, () => {
            var current = view.Match!.Viewer!.Zone("Graveyard").Cards.Single(card => card.VisualId == "own-grave");
            Check(scenario.Replies.Count == before && view.ZoneName == "Graveyard" && view.Overlay.Length == 0
                && sourceRead == (view.Match.Id, view.Match.Decision!.Id, current.Key)
                && ReaderActionText() == "Flashback {1}{U}, Pay 3 life: Target player draws two cards."
                && !view.Hits.Any(hit => hit.Id is "zone-use:own-grave" or "inspect-zone-use"),
                "A source-aware graveyard card opens its actual live reader and current-key cast action without an inspector detour or a game reply");
            Capture("10-source-aware-grave-reader");
        });
        Step(.2, () => {
            Check(view.Error.Length == 0, "The complete hand and zone action journey completes without client errors");
            File.WriteAllText(Path.Combine(view.Profile, "gesture-events.json"), JsonSerializer.Serialize(gestures, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            File.WriteAllText(Path.Combine(view.Profile, "hand-placement-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            view.Exit();
        });
    }
    private string ReaderActionText()
    {
        var bounds = view.Hits.Single(hit => hit.Id == "read-card-action").Bounds;
        // Wrapping preserves every character, including boundary whitespace.
        return string.Concat(view.Frame!.Observe().Text.Where(text => bounds.Contains(new Point(text.Bounds.X, text.Bounds.Y)))
            .Select(text => text.Value));
    }
    private bool ReplySettled() => scenario.Replies.Count == before + 1 && !view.Busy && view.Match?.Revision == scenario.State.Revision;
    private Card Forest() => view.Match!.Viewer!.Zone("Hand").Cards.Single(c => c.VisualId == "hand-forest");
    private void Expect(string ability)
    {
        before = scenario.Replies.Count; expectedGame = view.Match!.Id; expectedDecision = view.Match.Decision!.Id;
        expectedCard = Forest().Key; expectedAbility = ability;
    }
    private void CheckPlacement(string description) => Check(scenario.Replies.Count == before + 1 && scenario.Replies[^1] is PlaceFromHand reply && reply.GameId == expectedGame && reply.DecisionId == expectedDecision
        && reply.Card == expectedCard && reply.AbilityId == expectedAbility, description);
    private void Publish(string mode) { scenario.Present(Example(scenario.State, mode)); view.Accept(scenario.State); }
    private static GameSnapshot Example(GameSnapshot previous, string mode)
    {
        var state = PresentationSmoke.Example();
        string prefix = "scenario-" + (previous.Revision + 1) + ":";
        Card Card(string id, string name, string type, bool selectable = false) => new() { VisualId = id, CombatId = id, Name = name, Type = type, Selectable = selectable };
        var forest = Card("hand-forest", "Forest", "Basic Land — Forest", mode == "normal");
        var source = Card("source-a", "Gaea's Touch", "Enchantment", true) with { PhasedOut = mode == "phased" };
        var sources = new[] { source, Card("source-b", "Second test source", "Enchantment") };
        var players = state.Players.Select(p => p with { LandPlaysUsed = p.Id == 0 ? mode == "normal" ? 0 : 1 : null, LandPlayLimit = p.Id == 0 ? 1 : null,
            Zones = p.Zones.Select(z => z.Name switch {
                "Hand" => z with { Count = p.Id == 0 ? 2 : 7, Cards = p.Id == 0 ? [forest, Card("hand-island", "Island", "Basic Land — Island")] : [] },
                "Battlefield" => z with { Count = p.Id == 0 ? 2 : 0, Cards = (p.Id == 0 ? sources : []).ToImmutableArray() },
                "Command" => z with { Count = p.Id == 0 && mode == "zones" ? 1 : 0, Cards = p.Id == 0 && mode == "zones" ? [Card("own-command", "Commander", "Legendary Creature", true)] : [] },
                "Graveyard" => z with { Count = mode == "zones" && p.Id is 0 or 1 ? 1 : 0, Cards = mode == "zones" && p.Id is 0 or 1
                    ? [Card(p.Id == 0 ? "own-grave" : "other-grave", "Deep Analysis", "Sorcery", true) with { Text = "Draw two cards. Flashback — pay its cost." }] : [] },
                "Exile" => z with { Count = mode == "zones" && p.Id == 0 ? 1 : 0, Cards = mode == "zones" && p.Id == 0 ? [Card("own-exile", "Suspended spell", "Sorcery", true)] : [] },
                _ => z
            }).ToImmutableArray() }).ToArray();
        PriorityAbility Option(string id, string sourceId, string name) => new(id, prefix + sourceId, name, "Put a basic Forest card from your hand onto the battlefield.", false) {
            EligibleHandKeys = [prefix + "hand-forest"], HandPlacementLimit = 1
        };
        return state with { Id = "hand-placement-scenario", Players = players.ToImmutableArray(), Stack = [], Combat = null, Activity = [], ActivePlayerId = 0,
            Decision = new() { Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = true, CanAutoPass = false,
                PriorityAbilities = mode is "zones" or "revoked" ? [] : mode == "multi"
                    ? [Option("placement-a", "source-a", "Gaea's Touch"), Option("placement-b", "source-b", "Second test source")]
                    : [Option("placement-a", "source-a", "Gaea's Touch")] } };
    }
    private bool Reading(string id) => view.DisplayedPose(id) is { Size.X: >= 400 } && view.HoverPreview is { Width: >= 400 };
    private void MoveCard(string id)
    {
        var hit = view.Hits.Last(h => h.Card?.VisualId == id);
        MoveToHit(hit);
    }
    private void MoveHit(string id) => MoveToHit(view.Hits.Single(h => h.Id == id));
    private void MoveToHit(Mana.Table.Interaction.TableHit hit)
    {
        var physical = SmokePointer.Hit(view, hit);
        mouse = new(physical.X, physical.Y, 0, ButtonState.Released, ButtonState.Released,
            ButtonState.Released, ButtonState.Released, ButtonState.Released);
        pointerTarget = (hit.Id, hit.Scope); validatePointer = true;
        stage = "Target " + hit.Id;
    }
    private void MoveAway() => Move(new(1590, 890));
    private void Move(Point logical, ButtonState left = ButtonState.Released)
    {
        var physical = SmokePointer.Screen(view, logical);
        mouse = new(physical.X, physical.Y,
            0, left, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
        pointerTarget = null; validatePointer = true; stage = "Free move " + logical;
    }
    private void Buttons(ButtonState left = ButtonState.Released, ButtonState right = ButtonState.Released, bool validateTarget = true)
    {
        mouse = new(mouse.X, mouse.Y, 0, left, ButtonState.Released, right, ButtonState.Released, ButtonState.Released);
        validatePointer = validateTarget;
    }
    private void Click() { Step(.03, () => Buttons(left: ButtonState.Pressed)); Step(.04, () => Buttons()); }
    private void ReadAndUse()
    {
        Click();
        Wait(() => Reading("hand-forest") && view.Hits.Any(h => h.Id == "read-card-action"), 3, () => {
            Check(Reading("hand-forest") && scenario.Replies.Count == before && view.Hits.Any(h => h.Id == "read-card-action"),
                "Reading the current hand card leaves its engine command behind an explicit action button");
            MoveHit("read-card-action");
        });
        Click();
    }
    private void RightClick() { Step(.03, () => Buttons(right: ButtonState.Pressed)); Step(.04, () => Buttons()); }
    private void Drag() { Step(.03, () => Buttons(left: ButtonState.Pressed)); Step(.08, () => Move(new(800, 580), ButtonState.Pressed)); Step(.18, () => Buttons()); }
    private void Step(double delay, Action action) => steps.Enqueue((delay, null, action));
    private void Wait(Func<bool> ready, double deadline, Action action) => steps.Enqueue((deadline, ready, action));
    private void Check(bool value, string description) { if (!value) { WriteFailure(description); Capture("failure"); throw new InvalidOperationException(description); } checks.Add(description); }
    private void DriverFailure(string description) { WriteFailure(description); throw new InvalidOperationException(description); }
    private void WriteFailure(string description)
    {
        var physical = new Point(mouse.X, mouse.Y);
        var logical = SmokePointer.Table(view, physical);
        var hit = view.HitAt(logical);
        File.WriteAllText(Path.Combine(view.Profile, "failure.json"), JsonSerializer.Serialize(new {
            description, stage, stepNumber, frameNumber, time = view.Now, remainingSteps = steps.Count,
            expectedGame, expectedDecision, expectedCard, expectedAbility, before,
            expectedHit = pointerTarget?.Id, expectedScope = pointerTarget?.Scope, physical, logical,
            actualHit = hit?.Id, actualScope = hit?.Scope, currentScope = view.Scope,
            buttons = new { mouse.LeftButton, mouse.RightButton },
            hovered = view.Hovered?.VisualId, view.HoverPreview, view.Busy, view.Error, view.Overlay, view.ZoneName,
            poses = new[] { "hand-forest", "hand-island" }.Select(id => new { id, pose = view.DisplayedPose(id), paints = view.CardPaintCount(id) }),
            replies = scenario.Replies, match = view.Match, scenario = scenario.State, gestures
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }
    private void Capture(string name) { using var output = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height); }
    public void Frame(Texture2D surface)
    {
        frameNumber++;
        try {
            if (view.Now > 90) throw new TimeoutException("Hand placement journey did not finish");
            if (view.Error.Length > 0) Check(false, view.Error);
            if (steps.TryPeek(out var next) && (next.Ready?.Invoke() == true || view.Now - lastStep >= next.Delay)) {
                steps.Dequeue(); stepNumber++; next.Action(); lastStep = view.Now;
            }
        } catch (Exception exception) { WriteFailure(exception.Message); throw; }
    }
}
