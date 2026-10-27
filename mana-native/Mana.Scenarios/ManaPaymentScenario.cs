using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

// Authored interaction checkpoints, not a payment/rules simulator. Each accepted
// gesture advances to a declared state; real-engine tests own rules correctness.
public static class ManaPaymentScenario
{
    public const string Id = "mana-payment.v1";
    public const string Spell = "mana-faerie", Ring = "mana-0-ring", Dual = "mana-1-dual", Plains = "mana-2-plains";
    public static IReadOnlyList<ExperienceStage> Create() => [
        new("Read a cost and its sources", "Hover or lift Faerie Vandal. The dual land is one source; Sol Ring produces two. Reading spends nothing.", Snapshot(0)),
        new("Pay on the same card", "The authored cast has a current {1}{U} bill. Use Sol Ring's current source action.", Snapshot(1)),
        new("One blue remains", "Sol Ring turned; one generic unit was paid and one colorless remains floating. Breeding Pool can supply the unpaid blue.", Snapshot(2)),
        new("Paid; response window", "The dual land turned, the blue bill was paid, and the same Faerie is on the stack. One colorless is still floating.", Snapshot(3)),
        new("The card arrived", "The authored result keeps both paid sources turned and the unused colorless beside your portrait.", Snapshot(4)),
        new("An exact colorless bill", "A separate authored {1}{C} payment: spend the current floating colorless pip, then use Plains for the generic unit.", Snapshot(5)),
        new("Generic remains", "The actual colorless pip was spent. Only the one generic unit remains in this authored bill.", Snapshot(6)),
        new("Uncertain sources remain reachable", "A read-only unknown dependency is not proof of inability to cast. Read its current action and choose sources manually.", Snapshot(7))
    ];

    public static GameSnapshot Snapshot(int stage)
    {
        Card Card(string id, string name, string type, bool tapped = false) => new() {
            Key = id, VisualId = id, CombatId = id, Name = name, ArtName = name, Type = type, Tapped = tapped,
            Selectable = !tapped && stage is 1 or 2 or 5 or 6
        };
        var ring = Card(Ring, "Sol Ring", "Artifact", stage >= 2 && stage < 7) with { Text = "{T}: Add {C}{C}." };
        var dual = Card(Dual, "Breeding Pool", "Land — Forest Island", stage >= 3 && stage < 7) with { Text = "{T}: Add {G} or {U}." };
        var plains = Card(Plains, "Plains", "Basic Land — Plains");
        var forest = Card("mana-forest", "Forest", "Basic Land — Forest", true);
        var spell = Card(Spell, "Faerie Vandal", "Creature — Faerie Rogue") with { ManaCost = "{1}{U}", Power = 1, Toughness = 2,
            Text = "Flash\nFlying\nWhenever you draw your second card each turn, put a +1/+1 counter on Faerie Vandal.", Selectable = stage is 0 or 7 };
        var other = Card("mana-colorless-spell", "Spatial Contortion", "Instant") with { ManaCost = "{1}{C}", Text = "Target creature gets +3/-3 until end of turn.", Selectable = false };
        var bf = new[] { ring, dual, plains, forest }.Concat(stage == 4 ? [spell with { Selectable = false }] : []).ToImmutableArray();
        var hand = stage is 0 or 7 ? ImmutableArray.Create(spell, other) : ImmutableArray.Create(other);
        var pool = stage is >= 2 and <= 5 ? ImmutableDictionary<string, int>.Empty.Add("C", 1) : ImmutableDictionary<string, int>.Empty;
        var players = Enumerable.Range(0, 4).Select(index => new Player(index, index == 0 ? "You" : "Opponent " + index, 40, index == 0, false,
            index == 0 ? pool : ImmutableDictionary<string, int>.Empty,
            [new("Hand", index == 0 ? hand.Length : 6, index == 0 ? hand : [], null),
                new("Battlefield", index == 0 ? bf.Length : 0, index == 0 ? bf : [], null),
                new("Command", 0, [], null), new("Library", 80, [], null), new("Graveyard", 0, [], null), new("Exile", 0, [], null)])).ToImmutableArray();
        var source = stage is 5 or 6 ? other : spell;
        var bill = stage switch { 1 => ImmutableArray.Create("{1}", "{U}"), 2 => ["{U}"], 5 => ["{1}", "{C}"], 6 => ["{1}"], _ => [] };
        Decision decision = bill.IsEmpty
            ? new("", new(Message: "Read cards and their current sources."), new PriorityPayload(new("Pass", true)))
            : new("", new(Source: new(source.VisualId, source, 0, "Hand")),
                new PaymentPayload(new("Auto", true), new("Cancel", true)) {
                    UnpaidManaParts = bill.Select(symbol => new ManaBillPart(symbol, 1)).ToImmutableArray(),
                    FundingContext = Funding(bf, pool, stage == 7), UsablePoolManaColors = stage == 5 ? ["C"] : [] });
        return new("authored-mana-payment", "playing", null, null, 0, 9, "Main phase", "MAIN1", 0, players,
            stage == 3 ? [new("Faerie Vandal", spell.Text, StackActionKind.Spell, spell) { Id = "mana-faerie-stack", ControllerId = 0 }] : [], null, decision);
    }

    public static ManaFundingContext Funding(ImmutableArray<Card> cards, ImmutableDictionary<string, int> pool, bool uncertain = false)
    {
        ManaFundingRoute Route(params (string Color, int Amount)[] output) => new([new(ActionCostKind.Tap, 1, true, "")],
            new(SemanticKnowledge.Known, [new(output.ToImmutableDictionary(part => part.Color, part => part.Amount))]), SemanticKnowledge.Known);
        var sources = cards.Where(card => !card.Tapped && card.VisualId is Ring or Dual or Plains).Select(card => new ManaFundingSource(card.VisualId,
            card.VisualId switch { Ring => [Route(("C", 2))], Plains => [Route(("W", 1))], _ => [
                new([new(ActionCostKind.Tap, 1, true, "")], new(SemanticKnowledge.Known,
                    [new(ImmutableDictionary<string, int>.Empty.Add("U", 1)), new(ImmutableDictionary<string, int>.Empty.Add("G", 1))]),
                    uncertain ? SemanticKnowledge.Unknown : SemanticKnowledge.Known)] })).ToImmutableArray();
        return new(SemanticKnowledge.Unknown, pool, sources);
    }
}
