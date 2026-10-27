using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

namespace Mana.Table;

internal sealed record AiCampaignNumericPaymentInput(string DecisionId, long BoardRevision, ImmutableArray<string> UnpaidMana);
internal sealed record AiCampaignNumericPublicCard(long BoardRevision, int ControllerId, string Zone,
    int? Power, int? Toughness, ImmutableDictionary<string, int> Counters);

// A choice, its receipt and subsequent source observations are separate facts.
// Even an exact source match does not prove that X caused every later effect.
internal sealed class AiCampaignNumericInvestment
{
    private readonly long lastPriorEvent;
    private readonly HashSet<string> paymentStates = [];
    private string lastPublicCard = "";
    public string GameId { get; }
    public string ReplayCaseId { get; }
    public int SeatId { get; }
    public string DecisionId { get; }
    public long Revision { get; }
    public long BoardRevision { get; }
    public string Purpose { get; }
    public string SourceCardId { get; }
    public string SourceName { get; }
    public int Value { get; }
    public bool? Accepted { get; private set; }
    public string? ReceiptError { get; private set; }
    public bool SameSourceStackObserved { get; private set; }
    public long? SameSourceCastEventId { get; private set; }
    public long? SameSourceResolvedEventId { get; private set; }
    public List<AiCampaignNumericPaymentInput> SameSourcePaymentInputs { get; } = [];
    public List<AiCampaignNumericPublicCard> PublicCardObservations { get; } = [];
    public bool PositiveChoiceWithPublicResolution => Value > 0 && Accepted == true && SameSourceResolvedEventId != null;

    internal AiCampaignNumericInvestment(AiDecisionReplayCase request, string purpose, string sourceCardId, int value)
    {
        GameId = request.State.Id; ReplayCaseId = request.Id; SeatId = request.State.ViewerId;
        DecisionId = request.State.Decision!.Id; Revision = request.State.Revision; BoardRevision = request.State.BoardRevision;
        Purpose = purpose; SourceCardId = sourceCardId;
        SourceName = request.State.Decision.Context.Source?.Card?.Name ?? "";
        Value = value; lastPriorEvent = request.State.Activity.Select(entry => entry.Id).DefaultIfEmpty().Max();
    }

    internal void Receipt(bool accepted, string? error) { Accepted = accepted; ReceiptError = error; }

    internal void ObserveOwned(AutomatedSeatObservation observation)
    {
        if (Accepted != true || observation.State.Id != GameId || observation.SeatId != SeatId
            || observation.State.BoardRevision < BoardRevision || observation.State.Decision is not { Payload: PaymentPayload } decision
            || !(decision.Context.Source?.CardId == SourceCardId || Matches(decision.Context.Source?.Card)) || SameSourcePaymentInputs.Count >= 16
            || !paymentStates.Add(decision.Id + ":" + string.Join(",", observation.Semantics.UnpaidMana))) return;
        SameSourcePaymentInputs.Add(new(decision.Id, observation.State.BoardRevision, observation.Semantics.UnpaidMana));
    }

    internal void ObservePublic(GameSnapshot state)
    {
        if (Accepted != true || state.ViewerId != -1 || state.Id != GameId || state.BoardRevision < BoardRevision || string.IsNullOrEmpty(SourceCardId)) return;
        if (state.Stack.Any(item => Matches(item.Card))) SameSourceStackObserved = true;
        foreach (var entry in state.Activity) {
            if (entry.Id <= lastPriorEvent || entry.CardId != SourceCardId) continue;
            if (entry.Kind == "cast") SameSourceCastEventId ??= entry.Id;
            if (entry.Kind == "resolved" && entry.Outcome == "resolved") SameSourceResolvedEventId ??= entry.Id;
        }
        foreach (var player in state.Players)
        foreach (var zone in player.Zones)
        foreach (var card in zone.Cards.Where(Matches)) {
            string facts = zone.Name + ":" + player.Id + ":" + card.Power + ":" + card.Toughness + ":"
                + string.Join(",", card.Counters.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value));
            if (facts == lastPublicCard || PublicCardObservations.Count >= 16) continue;
            lastPublicCard = facts;
            PublicCardObservations.Add(new(state.BoardRevision, player.Id, zone.Name, card.Power, card.Toughness, card.Counters));
        }
    }

    private bool Matches(Card? card) => card != null && (card.VisualId == SourceCardId || card.CombatId == SourceCardId);

    internal static void CheckRegression(Action<bool, string> check)
    {
        var card = new Card { Name = "Numeric fixture", VisualId = "numeric-source", Power = 0, Toughness = 0 };
        var player = new Player(0, "Seat", 40, true, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", 0, [], null)]);
        var choice = new GameSnapshot("numeric-fixture", "playing", null, null, 0, 1, "Main", "MAIN1", 0, [player], [], null,
            new("number", new(), new NumberPayload("", new(0, 10)))) { BoardRevision = 2,
                Activity = [new(7, 1, "MAIN1", "resolved", 0, "", card.VisualId, card.Name) { Outcome = "resolved" }] };
        var request = new AiDecisionReplayCase(1, "numeric-case", "test", choice, DecisionActionDomain.None, new(), new([], []), 1);
        var current = choice with { ViewerId = -1, BoardRevision = 3, Decision = null,
            Stack = [new(card.Name, "", StackActionKind.Spell, card)],
            Activity = [new(8, 1, "MAIN1", "resolved", 0, "", card.VisualId, card.Name) { Outcome = "resolved" }],
            Players = [player with { Zones = [new("Battlefield", 1,
                [card with { Power = 2, Toughness = 2, Counters = ImmutableDictionary<string, int>.Empty.Add("P1P1", 2) }], null)] }] };
        var evidence = new AiCampaignNumericInvestment(request, "AnnounceX", card.VisualId, 3);
        evidence.ObservePublic(current);
        check(!evidence.SameSourceStackObserved && !evidence.PositiveChoiceWithPublicResolution && evidence.PublicCardObservations.Count == 0,
            "A proposed numeric choice does not establish an accepted investment or public outcome");
        evidence.Receipt(true, null);
        evidence.ObservePublic(choice with { ViewerId = -1, Decision = null });
        evidence.ObservePublic(current with { Id = "other-game" });
        evidence.ObservePublic(current with { Stack = [], Players = [player], Activity = [current.Activity[0] with { CardId = "other-source" }] });
        check(!evidence.SameSourceStackObserved && !evidence.PositiveChoiceWithPublicResolution,
            "Old events, another game and another source cannot certify the numeric choice's public resolution");
        evidence.ObservePublic(current);
        check(evidence.PositiveChoiceWithPublicResolution && evidence.SameSourceStackObserved
                && evidence.SameSourceResolvedEventId == 8 && evidence.PublicCardObservations is [{ Power: 2, Toughness: 2 }]
                && evidence.PublicCardObservations[0].Counters["P1P1"] == 2,
            "An accepted positive numeric choice retains separate exact-source stack, resolution and actual statistics without inventing X-sized effects");
        var zero = new AiCampaignNumericInvestment(request, "AnnounceX", card.VisualId, 0);
        zero.Receipt(true, null); zero.ObservePublic(current);
        var rejected = new AiCampaignNumericInvestment(request, "AnnounceX", card.VisualId, 3);
        rejected.Receipt(false, "rejected"); rejected.ObservePublic(current);
        check(!zero.PositiveChoiceWithPublicResolution && zero.SameSourceResolvedEventId == 8
                && !rejected.PositiveChoiceWithPublicResolution && rejected.PublicCardObservations.Count == 0,
            "A resolved zero choice and a rejected positive choice do not become completed positive investments");
    }
}
