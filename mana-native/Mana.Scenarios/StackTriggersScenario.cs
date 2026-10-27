using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

// Declared public checkpoints. They establish the presentation relationship,
// not trigger creation, Pir's replacement rules, or a game-engine shortcut.
public static class StackTriggersScenario
{
    public const string Id = "draw-triggers.v1";
    public const string Skulker = "burst-skulker", Toothy = "burst-toothy", Pir = "burst-pir";
    public static IReadOnlyList<ExperienceStage> Create() => [
        new("Before the draw", "Chasm Skulker and Toothy retain their current counters and stats. Pir is a replacement effect, not another trigger pile.", Snapshot(0)),
        new("71 interleaved triggers", "Two physical sources have interleaved stack entries. The piles count pending work; NEXT identifies the actual top entry. Hover reads its exact ability, and clicking opens its current member references.", Snapshot(1)),
        new("One real trigger settled", "The authored Skulker result has two more counters; the pending workload fell by only one entry. The count is not a forecast of counters.", Snapshot(2)),
        new("The other source settled", "The authored Toothy result is separate from Skulker. Actual stack positions still alternate between the two sources.", Snapshot(3)),
        new("Another player's response", "An opposing Stifle is a new actual stack occurrence. Any captured Resolve these intent must retire before this new response can be passed.", Snapshot(4)),
        new("A required choice", "A current required engine choice takes the foreground. Old pile inspection cannot answer it or keep a captured stack continuation running.", Snapshot(5)),
        new("Checkpoint acknowledged", "This authored continuation ends here with remaining entries still inspectable. It does not simulate their later rules resolution.", Snapshot(6))
    ];

    public static GameSnapshot Snapshot(int stage)
    {
        Card Creature(string id, string name, int counters, string text, int basePower = 1) => new() {
            Key = id, VisualId = id, CombatId = id, Name = name, ArtName = name, Type = "Creature",
            Text = text, Power = basePower + counters, Toughness = basePower + counters,
            Counters = counters == 0 ? ImmutableDictionary<string, int>.Empty : ImmutableDictionary<string, int>.Empty.Add("+1/+1", counters)
        };
        var skulker = Creature(Skulker, "Chasm Skulker", stage >= 2 ? 30 : 28,
            "Whenever you draw a card, put a +1/+1 counter on this creature.\nWhen this creature dies, create X 1/1 blue Squid creature tokens with islandwalk, where X is the number of +1/+1 counters on this creature.");
        var toothy = Creature(Toothy, "Toothy, Imaginary Friend", stage >= 3 ? 26 : 24,
            "Partner with Pir, Imaginative Rascal\nWhenever you draw a card, put a +1/+1 counter on Toothy, Imaginary Friend.\nWhen Toothy leaves the battlefield, draw a card for each +1/+1 counter on it.");
        var pir = Creature(Pir, "Pir, Imaginative Rascal", 0,
            "Partner with Toothy, Imaginary Friend\nIf one or more counters would be put on a permanent your team controls, that many plus one of each of those kinds of counters are put on that permanent instead.");
        var hand = new[] { ("burst-miscast", "Miscast", "{U}", "Counter target instant or sorcery spell unless its controller pays {3}."),
            ("burst-bear", "Grizzly Bears", "{1}{G}", ""), ("burst-island", "Island", "", "{T}: Add {U}.") }
            .Select(card => new Card { Key = card.Item1, VisualId = card.Item1, CombatId = card.Item1, Name = card.Item2,
                ArtName = card.Item2, ManaCost = card.Item3, Type = card.Item2 == "Island" ? "Basic Land — Island" : "Instant", Text = card.Item4 }).ToImmutableArray();
        var players = Enumerable.Range(0, 4).Select(player => new Player(player, player == 0 ? "You" : "Opponent " + player, 40, player == 0, false,
            ImmutableDictionary<string, int>.Empty,
            [new("Battlefield", player == 0 ? 3 : 0, player == 0 ? [skulker, toothy, pir] : [], null),
                new("Hand", player == 0 ? hand.Length : 7, player == 0 ? hand : [], null), new("Library", 43, [], null),
                new("Command", 0, [], null), new("Graveyard", 0, [], null), new("Exile", 0, [], null)])).ToImmutableArray();
        var triggers = Enumerable.Range(0, 71).Select(index => {
            var source = index % 2 == 0 ? skulker : toothy;
            return new StackItem(source.Name, "Put a +1/+1 counter on " + source.Name + ".", StackActionKind.TriggeredAbility, source) {
                Id = "draw-trigger-" + index, ControllerId = 0
            };
        }).Skip(stage is 0 or 1 ? 0 : stage == 2 ? 1 : 2).ToImmutableArray();
        if (stage == 0) triggers = [];
        if (stage == 4) {
            var stifle = new Card { VisualId = "burst-stifle", CombatId = "burst-stifle", Name = "Stifle", ArtName = "Stifle",
                ManaCost = "{U}", Type = "Instant", Text = "Counter target activated or triggered ability." };
            triggers = triggers.Insert(0, new(stifle.Name, stifle.Text, StackActionKind.Spell, stifle) {
                Id = "burst-opponent-response", ControllerId = 1, Targets = [new("stack", "draw-trigger-2", "Chasm Skulker trigger", 0)] });
        }
        Decision decision = stage == 5
            ? new("", new("A required choice interrupts the stack continuation.", Source: new(Toothy, toothy with { Key = "" }, 0, "Battlefield")),
                new ConfirmationPayload(new("Continue", true)))
            : new("", new(), new PriorityPayload(new("Pass priority", stage is >= 1 and <= 4)));
        var state = new GameSnapshot("authored-draw-triggers", "playing", null, null, 0, 9, "Main phase", "MAIN1", 0,
            players, triggers, null, stage == 6 ? null : decision) {
                StackInterruptionVersion = stage >= 4 ? 2 : 1, ActionClass = stage == 6 ? "resolving" : ""
            };
        if (stage >= 2) state = state with { Activity = [new(1, 9, "MAIN1", "counters", 0, "Chasm Skulker received two counters.", Skulker, skulker.Name) {
            Cause = new(Skulker, skulker.Name, "Put a +1/+1 counter on Chasm Skulker.", 0, "triggered", "authored-trigger-0"),
            Change = new("counter", "+1/+1", 28, 30) }] };
        if (stage >= 3) state = state with { Activity = state.Activity.Add(new(2, 9, "MAIN1", "counters", 0, "Toothy received two counters.", Toothy, toothy.Name) {
            Cause = new(Toothy, toothy.Name, "Put a +1/+1 counter on Toothy, Imaginary Friend.", 0, "triggered", "authored-trigger-1"),
            Change = new("counter", "+1/+1", 24, 26) }) };
        return state;
    }
}
