using System.Runtime.CompilerServices;
using Mana.Contracts;

[assembly: InternalsVisibleTo("Mana.Table.Tests")]

namespace Mana.Forge;

// A provider connection must observe a published task before translating a typed
// command. This cache belongs to one engine process; its immutable frame cannot
// leak into a replacement process or survive leaving its match.
internal sealed class ForgeCommandScope
{
    private GameSnapshot? observed;
    internal void Observe(GameSnapshot? state) => Volatile.Write(ref observed, state);
    internal void Clear() => Volatile.Write(ref observed, null);
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
            case PassPriority: Action("passPriority"); break;
            case KeepOpeningHand or PayMana or CompleteAttackers or CompleteBlockers
                or CompleteSelection or AcceptConfirmation or AcknowledgeDecision: Action("ok"); break;
            case MulliganOpeningHand or CancelPayment or CancelSelection or DeclineConfirmation:
                Action("cancel"); break;
            case UndoLastAction: Action("priorityUndo"); break;
            case PassTurn: Action("priorityPassTurn"); break;
            case AutoPassPriority: Action("passIfNoResponse"); break;
            case SelectCard value: Card(value.Card); break;
            case PlayCard value: Card(value.Card); break;
            case ActivateCard value: Card(value.Card); break;
            case UseManaSource value: Card(value.Card); break;
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
                Action("useSourceAction"); args["key"] = value.Card; args["actionId"] = value.ActionId; break;
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

    private static string Task(Decision decision) => decision switch {
        { Purpose: DecisionPurpose.OpeningHand } => "openingHand",
        { Intent: DecisionIntent.Priority } => "priority",
        { Intent: DecisionIntent.Mana } => "mana",
        { Intent: DecisionIntent.Attack } => "attack",
        { Intent: DecisionIntent.Block } => "block",
        { Intent: DecisionIntent.Selection } => "selection",
        { Kind: "text", Numeric: true } => "number",
        { Kind: "choice" or "reveal" or "number" or "text" or "allocate" } => decision.Kind,
        { Kind: "input", Intent: DecisionIntent.General } => "confirmation",
        _ => throw new EngineCommandRejectedException("The observed decision has no supported semantic task.")
    };
}
