using System.Collections.Immutable;
using System.Globalization;
using Mana.Contracts;

namespace Mana.Client;

public enum ClientDiagnosticOrigin { Direct, Automatic, Quiet, Finite, Publication }
public enum ClientDiagnosticStage
{
    LocalDeclined, Admitted, DispatchStarted, ProviderReturned, ProviderObservationReturned,
    ObservationAccepted, ObservationDiscarded, ProviderRejected, OutcomeUnknown
}
public enum ClientDiagnosticFailure
{
    None, Busy, Closed, RecoveryRequired, MissingState, NotOffered, SourceOfferChanged,
    FiniteRevocationPending, ProviderFailure, ObservationFailure, Superseded
}
public enum ClientDiagnosticDecision
{
    None, Undisclosed, Priority, OpeningHand, Payment, Attack, Block, Targets, Entities,
    Confirmation, Options, Ordering, Abilities, Reveal, Number, Text, Allocation
}
public enum ClientDiagnosticCommand
{
    Unknown, PassPriority, AutoPassPriority, KeepOpeningHand, MulliganOpeningHand, PayMana,
    AutoPayOptionalPayment, CancelPayment, CompleteAttackers, CompleteBlockers, CompleteSelection,
    CancelSelection, AcceptConfirmation, DeclineConfirmation, AcknowledgeDecision, AcknowledgeReveal,
    UndoLastAction, PassTurn, SelectCard, PlayCard, ActivateCard, UseManaSource, SpendPoolMana,
    SelectPlayer, ToggleAttackPlayer, ToggleAttackPermanent, ToggleBlock, ChooseOptions, OrderOptions,
    ChooseNumber, EnterText, AllocateAmounts, SkipAllocation, UseOfferedAction, PlaceFromHand,
    IgnorePriorityAbility, RestorePriorityAbility, PassForNow, GrantFiniteStackYield, RevokeFiniteStackYield, UndoAction
}
public sealed record ClientDiagnosticManaPart(string Symbol, int Count);
public sealed record ClientDiagnosticFacts(bool HasSnapshot, int? ViewerId, long? Revision,
    ClientDiagnosticDecision Decision, int? DecisionOwnerId, ExecutionStep? ExecutionStep,
    string? DecisionSourceName, int? SelectedTargetCount, bool? TargetCompletionOffered,
    bool? ManaPaymentOffered, ImmutableArray<ClientDiagnosticManaPart> RemainingMana,
    bool ManaBillUnknownOrTruncated);

/// <summary>Viewer-local operational evidence. Provider returns and accepted
/// publications never certify that a command applied or its effect resolved.
/// Publication events deliberately carry no command-attempt correlation.</summary>
public sealed record ClientDiagnostic(long? AttemptId, ClientDiagnosticOrigin Origin,
    ClientDiagnosticStage Stage, ClientDiagnosticCommand Command, ClientDiagnosticFailure Failure,
    int? SubmittedNumber, ClientDiagnosticFacts Facts);

// Only an immutable summary and private audience fence survive a dispatch.
// No command, snapshot, action handle, token or source ID is retained here.
internal sealed record ClientDiagnosticAttempt(long Id, ClientDiagnosticOrigin Origin,
    ClientDiagnosticCommand Command, int? SubmittedNumber, string GameId, int ViewerId, long Epoch,
    ClientDiagnosticFacts Initial);

internal sealed class ClientCommandDiagnostics(Action<ClientDiagnostic> emit, Func<long> currentEpoch, Func<bool> enabled)
{
    private long sequence;
    private static readonly ClientDiagnosticFacts Empty = new(false, null, null, ClientDiagnosticDecision.None,
        null, null, null, null, null, null, [], false);

    internal ClientDiagnosticAttempt? Begin(GameSnapshot state, GameCommand command, ClientDiagnosticOrigin origin, long epoch)
    {
        if (!enabled()) return null;
        var attempt = new ClientDiagnosticAttempt(Interlocked.Increment(ref sequence), origin, Kind(command),
            command is ChooseNumber number ? number.Value : null, state.Id, state.ViewerId, epoch, Facts(state, epoch == currentEpoch()));
        Record(attempt, ClientDiagnosticStage.Admitted);
        return attempt;
    }
    internal void Decline(GameSnapshot? state, GameCommand command, ClientDiagnosticFailure failure)
    {
        if (!enabled()) return;
        Emit(new(Interlocked.Increment(ref sequence), ClientDiagnosticOrigin.Direct, ClientDiagnosticStage.LocalDeclined,
            Kind(command), failure, command is ChooseNumber number ? number.Value : null,
            Facts(state, failure != ClientDiagnosticFailure.Closed)));
    }

    internal async Task ExecuteAsync(IMatchConnection connection, GameCommand command, ClientDiagnosticAttempt? attempt)
    {
        if (attempt == null) { await connection.ExecuteAsync(command); return; }
        Record(attempt, ClientDiagnosticStage.DispatchStarted);
        try { await connection.ExecuteAsync(command); }
        catch (EngineCommandRejectedException) { Record(attempt, ClientDiagnosticStage.ProviderRejected); throw; }
        catch { Record(attempt, ClientDiagnosticStage.OutcomeUnknown, ClientDiagnosticFailure.ProviderFailure); throw; }
        Record(attempt, ClientDiagnosticStage.ProviderReturned);
    }
    internal async Task<GameSnapshot?> ObserveAsync(IMatchConnection connection, ClientDiagnosticAttempt? attempt)
    {
        try {
            var state = await connection.ObserveAsync();
            if (attempt != null) Record(attempt, ClientDiagnosticStage.ProviderObservationReturned,
                attempt.Epoch == currentEpoch() ? ClientDiagnosticFailure.None : ClientDiagnosticFailure.Superseded,
                Facts(state, attempt.Epoch == currentEpoch() && state?.Id == attempt.GameId && state.ViewerId == attempt.ViewerId));
            return state;
        } catch {
            // Even EngineCommandRejectedException here is an unavailable
            // observation, not a rejection of the already-returned dispatch.
            if (attempt != null) Record(attempt, ClientDiagnosticStage.OutcomeUnknown, ClientDiagnosticFailure.ObservationFailure);
            throw;
        }
    }
    internal void Publication(GameSnapshot? previous, GameSnapshot? state, bool accepted, bool sameAudience, bool commandObservation)
    {
        if (!enabled()) return;
        if (accepted ? !MateriallyChanged(previous, state) : !commandObservation) return;
        Emit(new(null, ClientDiagnosticOrigin.Publication,
            accepted ? ClientDiagnosticStage.ObservationAccepted : ClientDiagnosticStage.ObservationDiscarded,
            ClientDiagnosticCommand.Unknown, ClientDiagnosticFailure.None, null, Facts(state, accepted && sameAudience)));
    }

    private static bool MateriallyChanged(GameSnapshot? previous, GameSnapshot? state)
    {
        if (previous == null || state == null) return !ReferenceEquals(previous, state);
        if (previous.Id != state.Id || previous.ViewerId != state.ViewerId || previous.Revision != state.Revision
            || previous.Status != state.Status || previous.Decision?.Id != state.Decision?.Id
            || previous.Decision?.ExecutionId != state.Decision?.ExecutionId) return true;
        var before = Facts(previous, true); var after = Facts(state, true);
        return before with { RemainingMana = [] } != after with { RemainingMana = [] }
            || !before.RemainingMana.SequenceEqual(after.RemainingMana);
    }

    private void Record(ClientDiagnosticAttempt attempt, ClientDiagnosticStage stage,
        ClientDiagnosticFailure failure = ClientDiagnosticFailure.None, ClientDiagnosticFacts? facts = null)
        => Emit(new(attempt.Id, attempt.Origin, stage, attempt.Command, failure, attempt.SubmittedNumber, facts ?? attempt.Initial));
    private void Emit(ClientDiagnostic diagnostic) { try { emit(diagnostic); } catch { /* Diagnostics never own play. */ } }

    private static ClientDiagnosticFacts Facts(GameSnapshot? state, bool sameAudience)
    {
        if (state == null) return Empty;
        try {
            bool disclosed = sameAudience && (state.DecisionOwnerId == null || state.DecisionOwnerId == state.ViewerId);
            var decision = disclosed ? state.Decision : null;
            var target = decision?.Payload as TargetSelectionPayload;
            var payment = decision?.Payload as PaymentPayload;
            string? sourceName = decision?.Context.Source?.Card is { FaceDown: false } source
                && source.Name is { Length: > 0 and <= 160 } name && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl) ? name : null;
            var parts = ImmutableArray.CreateBuilder<ClientDiagnosticManaPart>();
            bool unknown = payment != null;
            if (payment != null && !payment.UnpaidManaParts.IsDefaultOrEmpty) {
                unknown = payment.UnpaidManaParts.Length > 32;
                foreach (var part in payment.UnpaidManaParts.Take(32)) {
                    if (part != null && part.Count > 0 && ManaSymbol(part.Symbol)) parts.Add(new(part.Symbol, part.Count));
                    else unknown = true;
                }
            } else if (payment != null && !payment.UnpaidMana.IsDefaultOrEmpty) {
                unknown = payment.UnpaidMana.Length > 32;
                foreach (var symbol in payment.UnpaidMana.Take(32)) {
                    if (ManaSymbol(symbol)) parts.Add(new(symbol, 1)); else unknown = true;
                }
            }
            return new(true, state.ViewerId, state.Revision,
                !disclosed ? ClientDiagnosticDecision.Undisclosed : DecisionKind(decision?.Payload),
                state.DecisionOwnerId, decision?.ExecutionStep, sourceName, target?.Progress?.SelectedCount,
                target?.Complete.Enabled, payment?.Pay.Enabled, parts.ToImmutable(), unknown);
        } catch { return new(true, state.ViewerId, state.Revision, ClientDiagnosticDecision.Undisclosed,
            null, null, null, null, null, null, [], true); }
    }
    internal static bool ManaSymbol(string? symbol)
    {
        if (symbol is "{W}" or "{U}" or "{B}" or "{R}" or "{G}" or "{C}" or "{S}" or "{X}"
            or "{W/U}" or "{W/B}" or "{U/B}" or "{U/R}" or "{B/R}" or "{B/G}" or "{R/G}" or "{R/W}" or "{G/W}" or "{G/U}"
            or "{2/W}" or "{2/U}" or "{2/B}" or "{2/R}" or "{2/G}"
            or "{W/P}" or "{U/P}" or "{B/P}" or "{R/P}" or "{G/P}") return true;
        return symbol is { Length: >= 3 and <= 12 } && symbol[0] == '{' && symbol[^1] == '}'
            && int.TryParse(symbol.AsSpan(1, symbol.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            && value >= 0 && value.ToString(CultureInfo.InvariantCulture).AsSpan().SequenceEqual(symbol.AsSpan(1, symbol.Length - 2));
    }
    private static ClientDiagnosticDecision DecisionKind(DecisionPayload? payload) => payload switch {
        null => ClientDiagnosticDecision.None, PriorityPayload => ClientDiagnosticDecision.Priority,
        OpeningHandPayload => ClientDiagnosticDecision.OpeningHand, PaymentPayload => ClientDiagnosticDecision.Payment,
        AttackPayload => ClientDiagnosticDecision.Attack, BlockPayload => ClientDiagnosticDecision.Block,
        TargetSelectionPayload => ClientDiagnosticDecision.Targets, EntitySelectionPayload => ClientDiagnosticDecision.Entities,
        ConfirmationPayload => ClientDiagnosticDecision.Confirmation, OptionsPayload => ClientDiagnosticDecision.Options,
        OrderingPayload => ClientDiagnosticDecision.Ordering, AbilityChoicePayload => ClientDiagnosticDecision.Abilities,
        RevealPayload => ClientDiagnosticDecision.Reveal, NumberPayload => ClientDiagnosticDecision.Number,
        TextPayload => ClientDiagnosticDecision.Text, AllocationPayload => ClientDiagnosticDecision.Allocation,
        _ => ClientDiagnosticDecision.Undisclosed
    };
    private static ClientDiagnosticCommand Kind(GameCommand command)
        => Enum.TryParse<ClientDiagnosticCommand>(command.GetType().Name, out var kind) && Enum.IsDefined(kind)
            ? kind : ClientDiagnosticCommand.Unknown;
}
