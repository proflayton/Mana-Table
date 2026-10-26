using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

/// <summary>Authored counter-payment checkpoints, never a simulation of Magic rules.</summary>
public static class CounterPaymentScenario
{
    public const string Id = "counter-payment.v1";
    public const string MareId = "counter-payment-mare-a";
    public const string OtherMareId = "counter-payment-mare-b";
    public const string DivinerId = "counter-payment-diviner";
    public const string DrawnId = "counter-payment-drawn-island";
    public const string Action = "counter-payment-draw";
    public const string MareAbility = "{1}{U}, Remove a counter from this creature: Draw a card.";
    public const string DivinerAbility = "{T}, Remove a counter from an artifact, creature, land, or planeswalker you control: Draw a card.";

    public static IReadOnlyList<ExperienceStage> Create() => [
        Stage(0, "Loch Mare · read and activate", "Read Loch Mare's current 1/2 and three -1/-1 counters; start its draw ability."),
        Stage(1, "Loch Mare · pay mana", "Pay the authored {1}{U} cost. Its only removable counter type does not require another card click."),
        Stage(2, "Loch Mare · counter paid", "The authored debit is visible now: two -1/-1 counters and current 2/3. Draw is pending on the stack."),
        Stage(3, "Loch Mare · card drawn", "The authored ability has resolved once. An Island joins your hand; the paid counter and 2/3 remain."),
        Stage(4, "Mixed counters · read and activate", "The Mare is 1/2 with three -1/-1 counters and one stun counter. Start its draw ability."),
        Stage(5, "Mixed counters · pay mana", "Pay {1}{U}; the next authored checkpoint keeps the meaningful counter-type choice explicit."),
        Stage(6, "Mixed counters · choose the debit", "Choose which current counter type to remove from this exact Mare. This payment has not yet been applied."),
        Stage(7, "Mixed counters · chosen debit paid", "The chosen authored counter is gone and the draw is pending. A -1/-1 removal changes current P/T; stun removal does not."),
        Stage(8, "Mixed counters · card drawn", "The authored draw resolved once. Inspect the same Mare to read its settled counters and current P/T."),
        Stage(9, "Soul Diviner · read and activate", "Read Soul Diviner, then activate its draw ability. Two same-name Mares can supply the counter cost."),
        Stage(10, "Soul Diviner · choose a permanent", "Choose either exact highlighted Mare. Soul Diviner is the source, not the permanent losing a counter."),
        Stage(11, "Soul Diviner · selected debit paid", "Only the chosen Mare loses a -1/-1 counter and becomes 2/3. Soul Diviner is tapped; draw remains pending."),
        Stage(12, "Soul Diviner · card drawn", "The authored draw resolves once. The other Mare keeps all three counters and remains 1/2."),
        Stage(13, "480 permanents · activate Soul Diviner", "The same authored interaction over four crowded battlefields. Read the actual source, then start its draw ability."),
        Stage(14, "480 permanents · choose a Mare", "Only the two authored Mares are legal cost donors. Pan or focus the local battlefield; other permanents remain readable."),
        Stage(15, "480 permanents · selected debit paid", "Read the selected donor's current 2/3, its counter debit and the tapped source while draw is pending."),
        Stage(16, "480 permanents · card drawn", "The authored result preserves all 480 permanent identities and adds the one drawn Island to your hand.")
    ];

    private static ExperienceStage Stage(int stage, string name, string description) =>
        new(name, "Authored presentation checkpoint; no rules are simulated. " + description, Snapshot(stage));

    public static bool IsInitial(int stage) => stage is 0 or 4 or 9 or 13;
    public static bool IsPending(int stage) => stage is 2 or 7 or 11 or 15;
    public static string SourceId(int stage) => stage >= 9 ? DivinerId : MareId;

    public static GameSnapshot Snapshot(int stage, string donorId = MareId, int counterIndex = 12)
    {
        if (stage is < 0 or > 16 || donorId is not (MareId or OtherMareId) || counterIndex is not (7 or 12))
            throw new ArgumentOutOfRangeException(nameof(stage));
        bool mixed = stage is >= 4 and <= 8, distributed = stage >= 9, crowded = stage >= 13;
        int step = stage - (crowded ? 13 : distributed ? 9 : mixed ? 4 : 0);
        bool choosingType = mixed && step == 2, choosingDonor = distributed && step == 1;
        bool payingMana = !distributed && step == 1;
        bool pending = IsPending(stage), resolved = mixed ? step == 4 : step == 3, paid = pending || resolved;
        if (!distributed) donorId = MareId;
        string removedType = mixed && counterIndex == 12 ? "Stun" : "-1/-1";
        Card Mare(string id) {
            int minus = paid && id == donorId && removedType == "-1/-1" ? 2 : 3;
            var counters = ImmutableDictionary<string, int>.Empty.Add("-1/-1", minus);
            if (mixed && !(paid && removedType == "Stun")) counters = counters.Add("Stun", 1);
            return new() {
                VisualId = id, CombatId = id, Name = "Loch Mare", Type = "Creature — Horse Serpent", ManaCost = "{1}{U}",
                Text = "This creature enters with three -1/-1 counters on it.\n" + MareAbility
                    + "\n{2}{U}, Remove two counters from this creature: Tap target creature. Put a stun counter on it.",
                Power = minus == 3 ? 1 : 2, Toughness = minus == 3 ? 2 : 3, Counters = counters,
                Selectable = choosingDonor || IsInitial(stage) && !distributed && id == MareId
            };
        }
        var mare = Mare(MareId); var other = Mare(OtherMareId);
        var diviner = new Card {
            VisualId = DivinerId, CombatId = DivinerId, Name = "Soul Diviner", Type = "Creature — Zombie Wizard", ManaCost = "{U}{B}",
            Text = DivinerAbility, Power = 2, Toughness = 3, Selectable = IsInitial(stage) && distributed, Tapped = paid
        };
        var source = distributed ? diviner : mare;
        var donor = donorId == OtherMareId ? other : mare;
        var basis = crowded ? ClutteredChangesScenario.Initial() : FourPlayerTableScenario.Create()[0].Snapshot;
        var hand = FourPlayerTableScenario.Create()[0].Snapshot.Viewer!.Zone("Hand").Cards.Take(3)
            .Select(card => card with { Selectable = false }).ToImmutableArray();
        if (resolved) hand = hand.Add(new Card { VisualId = DrawnId, CombatId = DrawnId, Name = "Island", Type = "Basic Land — Island" });
        var players = basis.Players.Select(player => player with {
            Priority = pending && player.Id == 0,
            Mana = player.Id == 0 && !distributed && !paid ? ImmutableDictionary<string, int>.Empty.Add("U", 2) : ImmutableDictionary<string, int>.Empty,
            Zones = player.Zones.Select(zone => {
                var cleared = zone.Cards.Select(card => card with { Selectable = false }).ToImmutableArray();
                if (player.Id != 0) return zone with { Cards = cleared };
                if (zone.Name == "Hand") return zone with { Count = hand.Length, Cards = hand };
                if (zone.Name == "Library") return zone with { Count = resolved ? 71 : 72 };
                if (zone.Name != "Battlefield") return zone with { Cards = cleared };
                ImmutableArray<Card> field = crowded ? [diviner, mare, other, .. cleared.Skip(3)]
                    : distributed ? [diviner, mare, other] : [mare];
                return zone with { Count = field.Length, Cards = field };
            }).ToImmutableArray()
        }).ToImmutableArray();
        var context = new DecisionContext(Source: new(source.VisualId, source with { Key = "", Selectable = false }, 0, "Battlefield"));
        DecisionPayload payload = payingMana ? new PaymentPayload(new("Pay", true), new("Cancel", true))
            : choosingDonor ? new EntitySelectionPayload(new("OK", false), new("Cancel", true), [], CounterPayment: new(1, 1))
            : choosingType ? new OptionsPayload(new([new(7, "-1/-1", null), new(12, "Stun", null)], new(1, 1)),
                new(1, new(donor.VisualId, donor with { Key = "", Selectable = false }, 0, "Battlefield"),
                    [new(7, "M1M1", "-1/-1", 3), new(12, "STUN", "Stun", 1)]))
            : new PriorityPayload(new("Continue", pending));
        string message = payingMana ? "Pay {1}{U}." : choosingDonor ? "Select a permanent to remove 1 counter from."
            : choosingType ? "Select a counter type to remove." : pending ? "Draw a card is pending." : resolved ? "One card drawn." : "You have priority.";
        string ability = distributed ? DivinerAbility : MareAbility;
        int activityBase = crowded ? 30 : distributed ? 20 : mixed ? 10 : 0;
        var cause = new PublicEffectCause(source.VisualId, source.Name, ability, 0, "ability", "counter-payment-effect:" + activityBase);
        ActivityEntry[] activity = paid ? [
            new(activityBase + 1, 8, "MAIN1", "counters", 0, "Removed a " + removedType + " counter.", donor.VisualId, donor.Name) {
                Change = new("counters", removedType, removedType == "Stun" ? 1 : 3, removedType == "Stun" ? 0 : 2), Cause = cause
            },
            new(activityBase + 2, 8, "MAIN1", "ability", 0, "Activated " + source.Name + ".", source.VisualId, source.Name) { Detail = ability, Cause = cause },
            .. resolved ? new ActivityEntry[] {
                new(activityBase + 3, 8, "MAIN1", "draw", 0, "Drew a card.", DrawnId, "Island"),
                new(activityBase + 4, 8, "MAIN1", "resolved", 0, source.Name + "'s draw ability resolved.", source.VisualId, source.Name) {
                    Outcome = "resolved", Detail = "Draw a card.", Cause = cause
                }
            } : []
        ] : [];
        return basis with {
            Id = Id, Turn = 8, Phase = "Main phase", PhaseKey = "MAIN1", ActivePlayerId = 0, DecisionOwnerId = 0,
            PriorityPlayerId = payload is PriorityPayload ? 0 : null,
            ActionClass = payingMana ? "mana" : choosingDonor ? "selection" : choosingType ? "choice" : "priority",
            Players = players, Activity = activity.ToImmutableArray(),
            Stack = pending ? [new(source.Name, "Draw a card.", true, source) { Id = cause.Id, ControllerId = 0 }] : [],
            Decision = new("", context with { Message = message }, payload)
        };
    }
}
