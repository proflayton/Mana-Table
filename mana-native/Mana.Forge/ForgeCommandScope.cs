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
    internal string ObservedGameId => Volatile.Read(ref observed)?.Id ?? "";
    internal GameSnapshot? ObservedState => Volatile.Read(ref observed);
    internal void Observe(GameSnapshot? state)
    {
        lock (observationGate) {
            // Response continuations can finish out of order. The client's
            // replica rejects older revisions; command authority must not move
            // backward either and revive a prompt it has already replaced.
            if (state != null && observed is { } previous && state.Id == previous.Id
                && state.Revision < previous.Revision) return;
            Volatile.Write(ref observed, state);
        }
    }
    internal GameSnapshot? ObserveRaw(JsonElement raw)
    {
        try {
            var state = ForgeSnapshotDecoder.Decode(raw);
            Observe(state);
            return state;
        } catch {
            Clear();
            throw;
        }
    }
    internal void Clear() => Observe(null);
    internal bool CanReadSource(string gameId, string decisionId, string key)
    {
        var state = Volatile.Read(ref observed);
        return state is { Status: "playing", Decision: { Payload: PriorityPayload or PaymentPayload } decision }
            && state.Id == gameId && decision.Id == decisionId
            && (state.DecisionOwnerId == null || state.DecisionOwnerId == state.ViewerId)
            && (decision.Payload is not PriorityPayload || state.PriorityPlayerId == null || state.PriorityPlayerId == state.ViewerId)
            && CardViews.FindActionSource(state, key)?.Card is { FaceDown: false };
    }
    internal Dictionary<string, object?> Encode(GameCommand command)
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
