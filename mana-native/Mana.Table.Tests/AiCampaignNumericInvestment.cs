using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

namespace Mana.Table;

internal sealed record AiCampaignNumericPaymentInput(string DecisionId, long BoardRevision, ImmutableArray<string> UnpaidMana);
internal sealed record AiCampaignNumericPublicCard(long BoardRevision, int ControllerId, string Zone,
    int? Power, int? Toughness, ImmutableDictionary<string, int> Counters);
internal sealed record AiCampaignNumericInvestmentEvidence(string GameId, string ReplayCaseId, int SeatId,
    string DecisionId, long Revision, long BoardRevision, string Purpose, string SourceCardId, string SourceName,
    int Value, bool? Accepted, string? ReceiptError, bool SameSourceStackObserved, long? SameSourceCastEventId,
    long? SameSourceResolvedEventId, ImmutableArray<AiCampaignNumericPaymentInput> SameSourcePaymentInputs,
    ImmutableArray<AiCampaignNumericPublicCard> PublicCardObservations)
{
    public string EventAudience => "owned-seat filtered activity; stack and card observations come independently from the public spectator";
    public bool PositiveChoiceWithPublicResolution => Value > 0 && Accepted == true && SameSourceResolvedEventId != null;
}

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
    public string EventAudience => "owned-seat filtered activity; stack and card observations come independently from the public spectator";
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

    internal AiCampaignNumericInvestmentEvidence Snapshot() => new(GameId, ReplayCaseId, SeatId, DecisionId,
        Revision, BoardRevision, Purpose, SourceCardId, SourceName, Value, Accepted, ReceiptError, SameSourceStackObserved,
        SameSourceCastEventId, SameSourceResolvedEventId, SameSourcePaymentInputs.ToImmutableArray(), PublicCardObservations.ToImmutableArray());

    internal void ObserveOwned(AutomatedSeatObservation observation)
    {
        if (Accepted != true || observation.State.Id != GameId || observation.SeatId != SeatId || observation.State.ViewerId != SeatId
            || observation.State.BoardRevision < BoardRevision) return;
        foreach (var entry in observation.State.Activity) {
            if (entry.Id <= lastPriorEvent || entry.CardId != SourceCardId) continue;
            if (entry.Kind == "cast") SameSourceCastEventId ??= entry.Id;
            if (entry.Kind == "resolved" && entry.Outcome == "resolved") SameSourceResolvedEventId ??= entry.Id;
        }
        if (observation.State.Decision is not { Payload: PaymentPayload } decision
            || !(decision.Context.Source?.CardId == SourceCardId || Matches(decision.Context.Source?.Card)) || SameSourcePaymentInputs.Count >= 16
            || !paymentStates.Add(decision.Id + ":" + string.Join(",", observation.Semantics.UnpaidMana))) return;
        SameSourcePaymentInputs.Add(new(decision.Id, observation.State.BoardRevision, observation.Semantics.UnpaidMana));
    }

    internal void ObservePublic(GameSnapshot state)
    {
        if (Accepted != true || state.ViewerId != -1 || state.Id != GameId || state.BoardRevision < BoardRevision || string.IsNullOrEmpty(SourceCardId)) return;
        if (state.Stack.Any(item => Matches(item.Card))) SameSourceStackObserved = true;
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
            Activity = [],
            Players = [player with { Zones = [new("Battlefield", 1,
                [card with { Power = 2, Toughness = 2, Counters = ImmutableDictionary<string, int>.Empty.Add("P1P1", 2) }], null)] }] };
        var owned = new AutomatedSeatObservation(0, current with { ViewerId = 0,
            Activity = [new(8, 1, "MAIN1", "cast", 0, "", card.VisualId, card.Name),
                new(9, 1, "MAIN1", "resolved", 0, "", card.VisualId, card.Name) { Outcome = "resolved" }] }, DecisionActionDomain.None, new());
        var evidence = new AiCampaignNumericInvestment(request, "AnnounceX", card.VisualId, 3);
        evidence.ObservePublic(current);
        check(!evidence.SameSourceStackObserved && !evidence.PositiveChoiceWithPublicResolution && evidence.PublicCardObservations.Count == 0,
            "A proposed numeric choice does not establish an accepted investment or public outcome");
        evidence.Receipt(true, null);
        evidence.ObserveOwned(owned with { State = choice });
        evidence.ObserveOwned(owned with { State = owned.State with { Id = "other-game" } });
        evidence.ObserveOwned(owned with { State = owned.State with {
            Activity = [owned.State.Activity[1] with { CardId = "other-source" }] } });
        check(!evidence.SameSourceStackObserved && !evidence.PositiveChoiceWithPublicResolution,
            "Old events, another game and another source cannot certify the numeric choice's public resolution");
        evidence.ObservePublic(current);
        check(evidence.SameSourceStackObserved && evidence.SameSourceResolvedEventId == null,
            "A real public stack/card observation does not invent resolution events omitted by the spectator");
        evidence.ObserveOwned(owned);
        check(evidence.PositiveChoiceWithPublicResolution && evidence.SameSourceStackObserved
                && evidence.SameSourceCastEventId == 8 && evidence.SameSourceResolvedEventId == 9
                && evidence.PublicCardObservations is [{ Power: 2, Toughness: 2 }]
                && evidence.PublicCardObservations[0].Counters["P1P1"] == 2,
            "An accepted positive numeric choice retains attributed exact-source event reports and independent public statistics without inventing X-sized effects");
        var zero = new AiCampaignNumericInvestment(request, "AnnounceX", card.VisualId, 0);
        zero.Receipt(true, null); zero.ObserveOwned(owned); zero.ObservePublic(current);
        var rejected = new AiCampaignNumericInvestment(request, "AnnounceX", card.VisualId, 3);
        rejected.Receipt(false, "rejected"); rejected.ObserveOwned(owned); rejected.ObservePublic(current);
        check(!zero.PositiveChoiceWithPublicResolution && zero.SameSourceResolvedEventId == 9
                && !rejected.PositiveChoiceWithPublicResolution && rejected.PublicCardObservations.Count == 0,
            "A resolved zero choice and a rejected positive choice do not become completed positive investments");
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<AiCampaignNumericInvestmentEvidence>(
            System.Text.Json.JsonSerializer.Serialize(evidence.Snapshot()))!;
        check(snapshot.PositiveChoiceWithPublicResolution && snapshot.Value == 3
                && snapshot.PublicCardObservations is [{ Power: 2, Toughness: 2 }] && snapshot.SameSourceResolvedEventId == 9,
            "Persisted numeric evidence retains immutable receipt/event/public-outcome facts for the release aggregate without inventing X-sized statistics");
    }
}
