using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Mana.AI;
using Mana.Contracts;

namespace Mana.Table;

internal sealed record AiCampaignPaymentCancellation(string GameId, int SeatId, int Turn, string DecisionId,
    PaymentDecision? Provenance, ImmutableArray<string> UnpaidMana, bool CancellationOffered)
{
    public bool? Accepted { get; init; }
    public bool OptionalEffectDecline => CancellationOffered && Provenance is { Mandatory: false } payment
        && payment.PayerId == SeatId && !string.IsNullOrWhiteSpace(payment.SourceCardId)
        && payment.Purpose is PaymentPurpose.TriggeredAbilityCost or PaymentPurpose.EffectCost;

    internal static AiCampaignPaymentCancellation Capture(GameSnapshot state, DecisionSemantics semantics)
        => new(state.Id, state.ViewerId, state.Turn, state.Decision?.Id ?? "", semantics.Payment, semantics.UnpaidMana,
            state.Decision?.Payload is PaymentPayload { Cancel.Enabled: true });

    internal static void CheckRegression(Action<bool, string> check)
    {
        var state = new GameSnapshot("payment-evidence", "playing", null, null, 0, 3, "Main", "MAIN1", 0, [], [], null,
            new("payment", new(), new PaymentPayload(new("Pay", false), new("Cancel", true))));
        var optional = new DecisionSemantics { Payment = new(PaymentPurpose.TriggeredAbilityCost, 0, "current-source", false), UnpaidMana = ["{1}"] };
        check(Capture(state, optional).OptionalEffectDecline
                && Capture(state, optional with { Payment = optional.Payment! with { Purpose = PaymentPurpose.EffectCost } }).OptionalEffectDecline,
            "Explicit optional triggered/effect cost declines are distinguished from abandoned spell or activation payments");
        check(!Capture(state, optional with { Payment = optional.Payment! with { Purpose = PaymentPurpose.SpellCost } }).OptionalEffectDecline
                && !Capture(state, optional with { Payment = optional.Payment! with { Purpose = PaymentPurpose.ActivatedAbilityCost } }).OptionalEffectDecline
                && !Capture(state, optional with { Payment = null }).OptionalEffectDecline
                && !Capture(state, optional with { Payment = optional.Payment! with { Mandatory = true } }).OptionalEffectDecline
                && !Capture(state, optional with { Payment = optional.Payment! with { PayerId = 1 } }).OptionalEffectDecline
                && !Capture(state, optional with { Payment = optional.Payment! with { SourceCardId = "" } }).OptionalEffectDecline,
            "Missing provenance, foreign payers and mandatory or initiating costs cannot be reclassified as optional effect declines");
        check(!Capture(state with { Decision = new("no-cancel", new(), new PaymentPayload(new("Pay", true), new("Cancel", false))) }, optional).OptionalEffectDecline
                && Capture(state, optional).Accepted == null,
            "Payment evidence preserves the current cancel authority and does not invent an accepted command receipt");
    }
}

internal sealed record AiCampaignEvidence(ImmutableArray<string> OfferedEffects, ImmutableArray<string> SelectedEffects,
    ImmutableArray<string> BoardWitnesses, ImmutableArray<string> DecisionWitnesses, ImmutableArray<string> ResolvedSources,
    ImmutableArray<AiTask> Tasks, int Accepted, ImmutableDictionary<string, int> Commands,
    ImmutableDictionary<string, int> TargetRecipients, ImmutableArray<string> UnmodeledOperationSources,
    [property: JsonPropertyName("findings")] ImmutableArray<AiCampaignFinding> Findings,
    [property: JsonPropertyName("numericInvestments")] ImmutableArray<AiCampaignNumericInvestmentEvidence> NumericInvestments,
    ImmutableArray<AiCampaignStaleProbe> StaleAuthorityProbes,
    ImmutableArray<AiCampaignPaymentCancellation> PaymentCancellations,
    [property: JsonPropertyName("privateReplayCases")] ImmutableArray<string> PrivateReplayCases,
    [property: JsonPropertyName("diagnosticErrors")] ImmutableArray<string> DiagnosticErrors,
    [property: JsonPropertyName("missingBoardWitnesses")] ImmutableArray<string> MissingBoardWitnesses,
    [property: JsonPropertyName("interpretation")] string Interpretation);

internal sealed record AiCampaignSeatOutcome(int SeatId, string Name, bool Eliminated, int Life,
    int LibraryCount, bool Developed);
internal sealed record AiCampaignSeatScope(int SeatId, int ViewerId, string? DecisionId,
    long Revision, long BoardRevision, long OfferVersion);

internal sealed record AiCampaignOutcomeProof(string? GameId, bool CapturedBeforeCleanup, string Status,
    string? Result, string? Error, AiHostFault? HostFault, bool VerifiedNaturalOutcome,
    ImmutableArray<AiCampaignSeatOutcome> Seats, ImmutableArray<AiCampaignSeatScope> SeatScopes,
    string PublicSnapshotFile, string Scope)
{
    internal static AiCampaignOutcomeProof Capture(GameSnapshot? state, string stopReason, AiHostFault? fault,
        IEnumerable<int> developed, ImmutableArray<AutomatedSeatObservation> observations)
    {
        var developedSeats = developed.ToHashSet();
        return new(state?.Id, state != null, state?.Status ?? "not-started", state?.Result, state?.Error, fault,
            stopReason == "natural-outcome" && state is { Status: "finished", Error: null }
                && !string.IsNullOrWhiteSpace(state.Result) && fault == null,
            state?.Players.Select(player => new AiCampaignSeatOutcome(player.Id, player.Name, player.Eliminated,
                player.Life, player.Zone("Library").Count, developedSeats.Contains(player.Id))).ToImmutableArray() ?? [],
            observations.Select(item => new AiCampaignSeatScope(item.SeatId, item.State.ViewerId,
                item.State.Decision?.Id, item.State.Revision, item.State.BoardRevision, item.Domain.Version)).ToImmutableArray(),
            "pre-cleanup-public-state.json",
            "Captured before the runner's only StopSimulation call. No concede or seat substitution is issued by this driver. "
                + "The engine reports the outcome; structured individual loss reasons are not projected and are not inferred from prose.");
    }
}
