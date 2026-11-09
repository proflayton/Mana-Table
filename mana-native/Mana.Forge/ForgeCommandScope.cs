using System.Runtime.CompilerServices;
using System.Text.Json;
using Mana.Contracts;

[assembly: InternalsVisibleTo("Mana.Table.Tests")]

namespace Mana.Forge;

// A provider connection must observe a published task before translating a typed
// command. This cache belongs to one engine process; its immutable frame cannot
// leak into a replacement process or survive leaving its match.
internal sealed class ForgeCommandScope
{
    private readonly object observationGate = new();
    private GameSnapshot? observed;
    private long audienceGeneration;
    private long commandAdmission;
    internal readonly record struct ObservationLease(long Audience, long Admission, GameSnapshot? Anchor);
    internal ObservationLease CaptureRead()
    {
        lock (observationGate) return new(audienceGeneration, commandAdmission, observed);
    }
    internal void EnsureCurrentRead(ObservationLease read, bool? nullReply = null)
    {
        lock (observationGate) RequireCurrentRead(read, null, nullReply);
    }
    private void RequireCurrentRead(ObservationLease read, long? revision, bool? nullReply)
    {
        if (read.Audience != audienceGeneration)
            throw Superseded(read, ObservationRetirementReason.AudienceChanged, revision, nullReply);
        if (read.Admission != commandAdmission)
            throw Superseded(read, ObservationRetirementReason.CommandAdmitted, revision, nullReply);
    }
    private EngineObservationSupersededException Superseded(ObservationLease read, ObservationRetirementReason reason,
        long? revision, bool? nullReply) => new(new(reason, read.Audience, audienceGeneration,
            read.Admission, commandAdmission, revision, observed?.Revision, nullReply));
    internal void AdmitRead(ObservationLease read, GameSnapshot? state)
    {
        lock (observationGate) {
            RequireCurrentRead(read, state?.Revision, state == null);
            if (state == null && !ReferenceEquals(observed, read.Anchor))
                throw Superseded(read, ObservationRetirementReason.PublicationChanged, null, true);
            if (!Observe(state, read.Audience))
                throw Superseded(read, ObservationRetirementReason.RevisionBehind, state?.Revision, state == null);
        }
    }
    internal long AudienceGeneration { get { lock (observationGate) return audienceGeneration; } }
    internal string ObservedGameId => Volatile.Read(ref observed)?.Id ?? "";
    internal GameSnapshot? ObservedState => Volatile.Read(ref observed);
    internal bool Observe(GameSnapshot? state, long? expectedAudience = null)
    {
        lock (observationGate) {
            if (expectedAudience is { } expected && expected != audienceGeneration) return false;
            // Response continuations can finish out of order. The client's
            // replica rejects older revisions; command authority must not move
            // backward either and revive a prompt it has already replaced.
            if (state != null && observed is { } previous && state.Id == previous.Id
                && state.Revision < previous.Revision) return false;
            UndoFacts.ValidateProgress(observed, state);
            if (state == null || observed == null || state.Id != observed.Id || state.ViewerId != observed.ViewerId
                || UndoFacts.RestoredSince(observed, state)) audienceGeneration++;
            Volatile.Write(ref observed, state);
            return true;
        }
    }
    internal GameSnapshot? ObserveRaw(JsonElement raw, bool certifiedUndo = false)
    {
        try {
            var state = ForgeSnapshotDecoder.Decode(raw, certifiedUndo: certifiedUndo);
            Observe(state);
            return state;
        } catch {
            Clear();
            throw;
        }
    }
    internal bool Clear(long? expectedAudience = null) => Observe(null, expectedAudience);
    internal bool ClearIfCurrent(ObservationLease read)
    {
        lock (observationGate) {
            RequireCurrentRead(read, null, null);
            return ReferenceEquals(observed, read.Anchor) && Clear(read.Audience);
        }
    }
    internal bool CanReadSource(string gameId, string decisionId, string key)
    {
        var state = Volatile.Read(ref observed);
        return state is { Status: "playing", Decision: { Payload: PriorityPayload or PaymentPayload } decision }
            && state.Id == gameId && decision.Id == decisionId
            && (state.DecisionOwnerId == null || state.DecisionOwnerId == state.ViewerId)
            && (decision.Payload is not PriorityPayload || state.PriorityPlayerId == null || state.PriorityPlayerId == state.ViewerId)
            && CardViews.CanReadActionSource(state, key);
    }
    internal Dictionary<string, object?> Encode(GameCommand command)
    {
        lock (observationGate) {
            var args = EncodeCurrent(command);
            // Validation and protocol translation must both finish before a
            // command retires reads that began under its preceding authority.
            commandAdmission++;
            return args;
        }
    }
    private Dictionary<string, object?> EncodeCurrent(GameCommand command)
    {
        command.Validate();
        var state = Volatile.Read(ref observed);
        if (state == null || !GameCommandPermissions.Allows(state, command))
            throw new EngineCommandRejectedException("That command does not answer the currently observed decision. Observe the table again.");
        var args = new Dictionary<string, object?> { ["sessionId"] = command.GameId,
            ["promptId"] = command is DecisionCommand decision ? decision.DecisionId : "" };
        if (command is DecisionCommand) args["expectedTask"] = Task(state.Decision!);
        void Action(string name) => args["action"] = name;
        void Card(string key) { Action("card"); args["key"] = key; }
        switch (command) {
            case UndoAction value:
                Action("undoCertifiedAction"); args["offerId"] = value.OfferId; args["actionId"] = value.ActionId;
                args["undoBarrierVersion"] = value.BarrierVersion; break;
            case GrantPriorityContinuation value:
                Action("grantPriorityContinuation"); args["sequence"] = value.Sequence; args["grantId"] = value.GrantId;
                args["mode"] = value.Mode.ToString(); args["phaseStops"] = value.PhaseStops;
                args["quietYieldToken"] = value.QuietYieldToken; break;
            case RevokePriorityContinuation value:
                Action("revokePriorityContinuation"); args["throughSequence"] = value.ThroughSequence; break;
            case AcknowledgePriorityObservation value:
                Action("acknowledgePriorityObservation"); args["boardRevision"] = value.BoardRevision;
                args["publicActivitySequence"] = value.PublicActivitySequence; break;
            case GrantFiniteStackYield value:
                Action("grantFiniteStackYield"); args["grantId"] = value.GrantId; args["executionIds"] = value.ExecutionIds;
                args["stackActionVersion"] = value.StackActionVersion; args["resolutionChoiceVersion"] = value.ResolutionChoiceVersion;
                break;
            case RevokeFiniteStackYield value:
                Action("revokeFiniteStackYield"); args["grantId"] = value.GrantId; break;
            case PassPriority value:
                Action("passPriority");
                if (value.StackInterruptionVersion is { } version) args["stackInterruptionVersion"] = version;
                break;
            case KeepOpeningHand or PayMana or CompleteAttackers or CompleteBlockers
                or CompleteSelection or AcceptConfirmation or AcknowledgeDecision: Action("ok"); break;
            case AutoPayOptionalPayment value:
                Action("autoPayOptionalPayment"); args["offerId"] = value.OfferId; break;
            case MulliganOpeningHand or CancelPayment or CancelSelection or DeclineConfirmation:
                Action("cancel"); break;
            case PassTurn: Action("priorityPassTurn"); break;
            case AutoPassPriority: Action("passIfNoResponse"); break;
            case SelectCard value: Card(value.Card); break;
            case PlayCard value: Card(value.Card); break;
            case ActivateCard value: Card(value.Card); break;
            case UseManaSource value: Card(value.Card); break;
            case SpendPoolMana value: Action("spendPoolMana"); args["manaColor"] = value.Color; break;
            case SelectPlayer value: Action("player"); args["playerId"] = value.Player; break;
            case ToggleAttackPlayer value:
                Action("attack"); args["attackerKey"] = value.Attacker; args["defenderPlayerId"] = value.Player; break;
            case ToggleAttackPermanent value:
                Action("attack"); args["attackerKey"] = value.Attacker; args["defenderKey"] = value.DefenderCard; break;
            case ToggleBlock value:
                Action("block"); args["attackerKey"] = value.Attacker; args["blockerKey"] = value.Blocker; break;
            case ChooseOptions value: args["choices"] = value.Choices; break;
            case OrderOptions value: args["choices"] = value.Choices; break;
            case AcknowledgeReveal: args["choices"] = Array.Empty<int>(); break;
            case ChooseNumber value: args["value"] = value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture); break;
            case EnterText value: args["value"] = value.Value; break;
            case AllocateAmounts value: args["values"] = value.Amounts; break;
            case SkipAllocation: Action("skip"); break;
            case UseOfferedAction value:
                Action("useSourceAction"); args["key"] = value.Card; args["actionId"] = value.ActionId;
                if (value.ManaOutput != null) args["manaOutput"] = value.ManaOutput;
                break;
            case PlaceFromHand value:
                Action("useHandPlacement"); args["handKey"] = value.Card; args["abilityId"] = value.AbilityId; break;
            case IgnorePriorityAbility value:
                Action("setAbilityAutoPass"); args["abilityId"] = value.AbilityId; args["ignored"] = true; break;
            case RestorePriorityAbility value:
                Action("setAbilityAutoPass"); args["abilityId"] = value.AbilityId; args["ignored"] = false; break;
            case PassForNow value:
                Action("passUntilPublicAction"); args["yieldToken"] = value.YieldToken; break;
            default: throw new NotSupportedException("This game command has no Forge protocol mapping.");
        }
        return args;
    }

    private static string Task(Decision decision) => decision.Payload switch {
        OpeningHandPayload => "openingHand", PriorityPayload => "priority",
        PaymentPayload => "mana", AttackPayload => "attack", BlockPayload => "block",
        TargetSelectionPayload or EntitySelectionPayload => "selection",
        AbilityChoicePayload or OptionsPayload or OrderingPayload => "choice",
        RevealPayload => "reveal", NumberPayload => "number", TextPayload => "text",
        AllocationPayload => "allocate", ConfirmationPayload => "confirmation",
        _ => throw new EngineCommandRejectedException("The observed decision has no supported semantic task.")
    };
}
