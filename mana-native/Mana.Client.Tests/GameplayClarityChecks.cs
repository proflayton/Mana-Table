using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;

internal static class GameplayClarityChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var players = new[] {
            new Player(10, "You", 40, false, false, ImmutableDictionary<string, int>.Empty, []),
            new Player(20, "Alex", 40, true, false, ImmutableDictionary<string, int>.Empty, []),
            new Player(30, "Alex", 40, false, false, ImmutableDictionary<string, int>.Empty, [])
        };
        var state = new GameSnapshot("clarity", "playing", null, null, 10, 3, "Main phase", "MAIN1", 10, players.ToImmutableArray(), [], null, null) {
            PriorityPlayerId = 20, DecisionOwnerId = 30, ActionClass = "choice"
        };
        check(StateClarity.WaitingTitle(state).Contains("Alex · Seat 3") && state.ActivePlayerId == 10 && state.PriorityPlayerId == 20,
            "A private chooser is identified independently of active turn and priority, including duplicate player names");
        check(StateClarity.WaitingTitle(state with { DecisionOwnerId = null }).Contains("Alex · Seat 2"),
            "Waiting for a priority response uses the explicit priority seat");
        check(StateClarity.WaitingTitle(state with { ActionClass = "simultaneous" }) == "Players are making their choices",
            "Simultaneous choices do not guess one acting player");
        check(StateClarity.DecisionInstruction(state, true, "Submitted selection.").Contains("Submitted selection.")
            && !StateClarity.DecisionInstruction(state, true).Contains("resolved", StringComparison.OrdinalIgnoreCase),
            "Submitted intent is acknowledged without inventing a resolved outcome");
        var source = new Card { Name = "Toothy, Imaginary Friend", Type = "Creature", Text = "This printed text must not generate target permissions." };
        var choice = state with { DecisionOwnerId = 10, Decision = new() { Id = "choose-1", Kind = "input", Intent = DecisionIntent.Selection,
            Message = "Target player may search for Pir, then shuffle.", PlayerChoices = [10, 30], SourceCard = source, SourceControllerId = 20 } };
        check(StateClarity.Purpose(choice) == choice.Decision.Message && StateClarity.Source(choice).Contains("Seat 2"),
            "Required-choice purpose and source controller come from the engine rather than printed Oracle text");
        check(StateClarity.Recipients(choice).Select(p => p.Id).SequenceEqual([10, 30]),
            "Named recipients retain legal self and other-player choices without adding another seat");
        check(StateClarity.ChooseRecipient(choice, 10) is SelectPlayer { GameId: "clarity", DecisionId: "choose-1", Player: 10 },
            "The explicit You recipient sends the same scoped player-selection action as a portrait");
        check(StateClarity.ChooseRecipient(choice, 20) == null && StateClarity.ChooseRecipient(choice with { Decision = null }, 10) == null,
            "No player command is constructed for an unprojected recipient or retired prompt");
        var refreshed = choice with { Decision = choice.Decision with { Id = "choose-2", PlayerChoices = [30] } };
        check(StateClarity.ChooseRecipient(refreshed, 10) == null && StateClarity.ChooseRecipient(refreshed, 30)?.DecisionId == "choose-2",
            "Recipient refresh revokes old eligibility and uses the new exact prompt scope");
        check(StateClarity.Recipients(state).Length == 0 && StateClarity.Purpose(state) != choice.Decision.Message,
            "A public chooser ID does not expose the other seat's private prompt or recipients");
        var stack = new StackItem("Same source", "Public trigger", true, source) { Id = "stack-2", ControllerId = 30 };
        check(StateClarity.StackController(state, stack).Contains("Seat 3") && StateClarity.StackController(state, stack with { ControllerId = 20 }).Contains("Seat 2"),
            "Identically named stack sources retain their separate controlling seats");
        var counters = players[0] with { Counters = ImmutableDictionary<string, int>.Empty.Add("Energy", 3).Add("Poison", 9).Add("Experience", 2).Add("Gone", 0) };
        check(StateClarity.PlayerCounters(counters).Select(c => c.Key).SequenceEqual(["Poison", "Energy", "Experience"])
            && StateClarity.CounterSummary(counters).Contains("Poison 9"),
            "Public player resources retain their exact types/totals and suppress zero values");
        check(StateClarity.CounterSummary(counters with { Counters = ImmutableDictionary<string, int>.Empty }) == "",
            "Removing a public counter removes its persistent summary rather than retaining a stale value");
        var speed = new PlayerState("speed", "Speed", "progress", 1, 4, "The engine's public progress explanation.");
        var generic = counters with { States = [new("counter:energy", "Energy", "counter", 3, null, "Public energy."), speed,
            new("monarch", "Monarch", "status", null, null, "This player's end-step ability."),
            new("future", "Momentum", "progress", 2, 6, "A future engine-projected mechanic.")] };
        check(StateClarity.ProminentPlayerStates(generic).Take(3).Select(s => s.Kind).SequenceEqual(["progress", "counter", "status"])
            && StateClarity.PlayerStates(generic).Length == 4,
            "Persistent player state keeps a progress track, counter and public role prominent without mechanic-specific IDs");
        check(StateClarity.StateCaption(speed) == "Speed 1/4" && !StateClarity.StateAtMaximum(speed)
            && StateClarity.StateCaption(speed with { Value = 4 }) == "Speed 4/4 MAX",
            "Engine progress retains its exact current value, ceiling and maximum-state label");
        check(StateClarity.StateCaption(generic.States[2]) == "Monarch" && StateClarity.StateCaption(generic.States[3]) == "Momentum 2/6"
            && !StateClarity.StateAtMaximum(new("counter:limited", "Counter", "counter", 4, 4, "")),
            "Statuses and unseen future progress render generically while counter limits are never called max progress");
        check(StateClarity.PlayerStates(counters).Select(s => s.Label).SequenceEqual(["Poison", "Energy", "Experience"])
            && StateClarity.PlayerStates(counters with { Counters = ImmutableDictionary<string, int>.Empty }).Length == 0,
            "Older engine counters remain an honest positive-value fallback without inventing progress or roles");
        var anonymous = new Card { Name = "Face-down card", FaceDown = true, Type = "Creature", Power = 3, Toughness = 3,
            Damage = 1, CombatKeywords = ["flying"], Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 1) };
        check(CardPresentation.HasCombatStats(anonymous) && CardPresentation.HasAbility(anonymous, "flying")
            && CardPresentation.StateLabels(anonymous).Contains("Flying") && anonymous.Text == "" && anonymous.ArtName == "" && anonymous.OtherFace == null,
            "Authoritative anonymous public combat state is readable while identity/text/art/alternate faces remain absent");
        check(!CardPresentation.HasAbility(anonymous with { Type = "" }, "flying") && CardPresentation.Abilities(anonymous with { Type = "" }).Length == 0,
            "A hidden-zone object without public characteristics does not receive inferred keyword presentation");
        var parsed = JsonSerializer.Deserialize<Player>("{\"id\":10,\"name\":\"You\",\"life\":40,\"priority\":false,\"eliminated\":false,\"mana\":{},\"zones\":[],\"counters\":{\"Poison\":9}}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        check(parsed?.Counters["Poison"] == 9, "Public resource JSON survives the native snapshot contract");
        check(StateClarity.PendingActionLabel(choice, new SelectPlayer("clarity", "choose-1", Player: 30)).Contains("Seat 3"),
            "Immediate target feedback identifies the exact submitted recipient");
    }
}
