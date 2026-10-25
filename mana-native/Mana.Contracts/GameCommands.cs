using System.Collections.Immutable;
using System.Globalization;

namespace Mana.Contracts;

// Validation and provider dispatch accept only the declared semantic variants.
// Players never construct provider wire bags.
public abstract record GameCommand
{
    public string GameId { get; init; }
    private protected GameCommand(string gameId) => GameId = gameId;
    public void Validate()
    {
        static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);
        bool valid = Present(GameId) && (this is not DecisionCommand decision || Present(decision.DecisionId)) && this switch {
            SelectCard value => Present(value.Card), PlayCard value => Present(value.Card),
            ActivateCard value => Present(value.Card), UseManaSource value => Present(value.Card),
            ToggleAttackPlayer value => Present(value.Attacker),
            ToggleAttackPermanent value => Present(value.Attacker) && Present(value.DefenderCard),
            ToggleBlock value => Present(value.Attacker) && Present(value.Blocker),
            ChooseOptions value => ValidChoices(value.Choices), OrderOptions value => ValidChoices(value.Choices),
            AllocateAmounts value => !value.Amounts.IsDefault && value.Amounts.All(amount => amount >= 0),
            EnterText value => value.Value != null,
            UseOfferedAction value => Present(value.Card) && Present(value.ActionId),
            PlaceFromHand value => Present(value.Card) && Present(value.AbilityId),
            IgnorePriorityAbility value => Present(value.AbilityId), RestorePriorityAbility value => Present(value.AbilityId),
            PassForNow value => Present(value.YieldToken),
            PassPriority or AutoPassPriority or KeepOpeningHand or MulliganOpeningHand or PayMana or CancelPayment
                or CompleteAttackers or CompleteBlockers or CompleteSelection or CancelSelection or AcceptConfirmation
                or DeclineConfirmation or AcknowledgeDecision or AcknowledgeReveal or UndoLastAction or PassTurn or SelectPlayer
                or ChooseNumber or SkipAllocation => true,
            _ => false
        };
        if (!valid) throw new ArgumentException("Incomplete or invalid game command.");
        static bool ValidChoices(ImmutableArray<int> choices) => !choices.IsDefault
            && choices.All(index => index >= 0) && choices.Distinct().Count() == choices.Length;
    }
}

public abstract record DecisionCommand : GameCommand
{
    public string DecisionId { get; init; }
    private protected DecisionCommand(string gameId, string decisionId) : base(gameId) => DecisionId = decisionId;
}

public sealed record PassPriority(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record AutoPassPriority(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record KeepOpeningHand(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record MulliganOpeningHand(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record PayMana(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record CancelPayment(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record CompleteAttackers(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record CompleteBlockers(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record CompleteSelection(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record CancelSelection(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record AcceptConfirmation(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record DeclineConfirmation(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record AcknowledgeDecision(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record AcknowledgeReveal(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record UndoLastAction(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record PassTurn(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record SelectCard(string GameId, string DecisionId, string Card) : DecisionCommand(GameId, DecisionId);
public sealed record PlayCard(string GameId, string DecisionId, string Card) : DecisionCommand(GameId, DecisionId);
public sealed record ActivateCard(string GameId, string DecisionId, string Card) : DecisionCommand(GameId, DecisionId);
public sealed record UseManaSource(string GameId, string DecisionId, string Card) : DecisionCommand(GameId, DecisionId);
public sealed record SelectPlayer(string GameId, string DecisionId, int Player) : DecisionCommand(GameId, DecisionId);
public sealed record ToggleAttackPlayer(string GameId, string DecisionId, string Attacker, int Player) : DecisionCommand(GameId, DecisionId);
public sealed record ToggleAttackPermanent(string GameId, string DecisionId, string Attacker, string DefenderCard) : DecisionCommand(GameId, DecisionId);
public sealed record ToggleBlock(string GameId, string DecisionId, string Attacker, string Blocker) : DecisionCommand(GameId, DecisionId);
public sealed record ChooseOptions(string GameId, string DecisionId, ImmutableArray<int> Choices) : DecisionCommand(GameId, DecisionId);
public sealed record OrderOptions(string GameId, string DecisionId, ImmutableArray<int> Choices) : DecisionCommand(GameId, DecisionId);
public sealed record ChooseNumber(string GameId, string DecisionId, int Value) : DecisionCommand(GameId, DecisionId);
public sealed record EnterText(string GameId, string DecisionId, string Value) : DecisionCommand(GameId, DecisionId);
public sealed record AllocateAmounts(string GameId, string DecisionId, ImmutableArray<int> Amounts) : DecisionCommand(GameId, DecisionId);
public sealed record SkipAllocation(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record UseOfferedAction(string GameId, string DecisionId, string Card, string ActionId) : DecisionCommand(GameId, DecisionId);
public sealed record PlaceFromHand(string GameId, string DecisionId, string Card, string AbilityId) : DecisionCommand(GameId, DecisionId);
public sealed record IgnorePriorityAbility(string GameId, string DecisionId, string AbilityId) : DecisionCommand(GameId, DecisionId);
public sealed record RestorePriorityAbility(string GameId, string AbilityId) : GameCommand(GameId);
public sealed record PassForNow(string GameId, string DecisionId, string YieldToken) : DecisionCommand(GameId, DecisionId);

/// <summary>Checks only the provider's published permissions and constraints, never card rules or prose.</summary>
public static class GameCommandPermissions
{
    public static bool Allows(GameSnapshot state, GameCommand command)
    {
        try { command.Validate(); } catch (ArgumentException) { return false; }
        if (state.Id != command.GameId || state.Status is "finished" or "error") return false;
        if (command is RestorePriorityAbility restore)
            return state.IgnoredPriorityAbilities.Any(ability => ability.Id == restore.AbilityId);
        if (command is not DecisionCommand scoped || state.Decision is not { } decision || scoped.DecisionId != decision.Id) return false;
        bool priority = decision is { Task: DecisionTask.Priority };
        bool general = decision.Task == DecisionTask.Confirmation;
        Card? Card(string key) => CardViews.Located(state).Select(item => item.Card)
            .Concat(state.Players.SelectMany(player => player.Zones).Where(zone => zone.TopCard != null).Select(zone => zone.TopCard!))
            .FirstOrDefault(card => card.Key == key && key.Length > 0);
        Card? CurrentSource(string key) => CardViews.FindZoneActionSource(state, key)?.Card;
        bool Selectable(string key) => Card(key) is { Selectable: true };
        bool Located(string key, params string[] zones) => CardViews.Located(state).Any(item => item.Card.Key == key && zones.Contains(item.Zone));
        return command switch {
            PassPriority => priority && decision.OkEnabled,
            AutoPassPriority => state.Status == "playing" && priority && decision.OkEnabled && decision.CanAutoPass,
            KeepOpeningHand => decision.Task == DecisionTask.OpeningHand && decision.OkEnabled,
            MulliganOpeningHand => decision.Task == DecisionTask.OpeningHand && decision.CancelEnabled,
            PayMana => decision.Task == DecisionTask.Payment && decision.OkEnabled,
            CancelPayment => decision.Task == DecisionTask.Payment && decision.CancelEnabled,
            CompleteAttackers => decision.Task == DecisionTask.Attack && decision.OkEnabled,
            CompleteBlockers => decision.Task == DecisionTask.Block && decision.OkEnabled && string.IsNullOrEmpty(state.Combat?.BlockProblem),
            CompleteSelection => decision is { Task: DecisionTask.TargetSelection or DecisionTask.EntitySelection, OkEnabled: true },
            CancelSelection => decision.Task is DecisionTask.TargetSelection or DecisionTask.EntitySelection && decision.CancelEnabled,
            AcceptConfirmation => general && decision.OkEnabled && decision.CancelEnabled,
            DeclineConfirmation => general && decision.CancelEnabled,
            AcknowledgeDecision => general && decision.OkEnabled && !decision.CancelEnabled,
            AcknowledgeReveal => decision.Task is DecisionTask.Reveal,
            UndoLastAction => priority && decision.CancelEnabled && decision.PriorityAlternative == PriorityAlternative.UndoLastAction,
            PassTurn => priority && decision.CancelEnabled && decision.PriorityAlternative == PriorityAlternative.PassTurn,
            SelectCard value => decision.Task is DecisionTask.TargetSelection or DecisionTask.EntitySelection && Selectable(value.Card),
            PlayCard value => priority && Selectable(value.Card) && Located(value.Card, "Hand", "Command"),
            ActivateCard value => priority && Selectable(value.Card) && Located(value.Card, "Battlefield"),
            UseManaSource value => decision.Task == DecisionTask.Payment && CurrentSource(value.Card) is { Selectable: true },
            SelectPlayer value => decision.Task is DecisionTask.TargetSelection or DecisionTask.EntitySelection && decision.PlayerChoices.Contains(value.Player),
            ToggleAttackPlayer value => decision.Task == DecisionTask.Attack && Card(value.Attacker) is { CombatId: var attacker }
                && state.Combat?.AttackOptions.Any(option => option.CardId == attacker
                    && option.Defenders.Any(defender => defender.Kind == "player" && defender.PlayerId == value.Player)) == true,
            ToggleAttackPermanent value => decision.Task == DecisionTask.Attack && Card(value.Attacker) is { CombatId: var attacker }
                && Card(value.DefenderCard) is { CombatId: var defender }
                && state.Combat?.AttackOptions.Any(option => option.CardId == attacker
                    && option.Defenders.Any(target => target.Kind != "player" && target.Id == defender)) == true,
            ToggleBlock value => decision.Task == DecisionTask.Block && Card(value.Attacker) is { CombatId: var attacker }
                && Card(value.Blocker) is { CombatId: var blocker }
                && state.Combat?.Attackers.Any(attack => attack.CardId == attacker && attack.EligibleBlockerIds.Contains(blocker)) == true,
            ChooseOptions value => decision.Task is DecisionTask.Options or DecisionTask.AbilityChoice && Choices(decision, value.Choices),
            OrderOptions value => decision.Task == DecisionTask.Ordering && Choices(decision, value.Choices),
            ChooseNumber value => decision.Task == DecisionTask.Number
                && value.Value >= decision.Min && value.Value <= decision.Max,
            EnterText => decision.Task == DecisionTask.Text,
            AllocateAmounts value => Allocation(decision, value.Amounts),
            SkipAllocation => decision.Task is DecisionTask.Allocation && decision.MaySkip,
            UseOfferedAction value => decision.Task is DecisionTask.Priority or DecisionTask.Payment
                && CurrentSource(value.Card) is { FaceDown: false },
            PlaceFromHand value => state.Status == "playing" && priority
                && state.Viewer is { } viewer && viewer.Zone("Hand").Cards.Any(card => card.Key == value.Card && !card.FaceDown)
                && decision.PriorityAbilities.Any(ability => ability.Id == value.AbilityId && ability.SourceCardKey.Length > 0 && ability.HandPlacementLimit > 0
                    && ability.EligibleHandKeys.Contains(value.Card)
                    && viewer.Zone("Battlefield").Cards.Any(source => source.Key == ability.SourceCardKey
                        && !source.FaceDown && !source.PhasedOut)),
            IgnorePriorityAbility value => priority && decision.PriorityAbilities.Any(ability => ability.Id == value.AbilityId),
            PassForNow value => state.Status == "playing" && priority && decision.OkEnabled && state.Turn > 0 && state.Stack.Length == 0
                && state.ActivePlayerId is { } active && active != state.ViewerId && state.Viewer?.Eliminated != true
                && state.ActionClass is "" or "priority" && (state.PriorityPlayerId == null || state.PriorityPlayerId == state.ViewerId)
                && (state.DecisionOwnerId == null || state.DecisionOwnerId == state.ViewerId)
                && value.YieldToken.Length > 0 && decision.QuietYieldToken == value.YieldToken,
            _ => false
        };
    }

    private static bool Choices(Decision decision, ImmutableArray<int> choices) => !choices.IsDefault
        && choices.Length >= decision.Min && choices.Length <= decision.Max && choices.Distinct().Count() == choices.Length
        && choices.All(index => decision.Choices.Any(choice => choice.Index == index));

    private static bool Allocation(Decision decision, ImmutableArray<int> amounts)
    {
        if (decision.Task is not (DecisionTask.Allocation) || amounts.IsDefault || amounts.Length != decision.Choices.Length
            || amounts.Sum(value => (long)value) != decision.Amount) return false;
        for (int index = 0; index < amounts.Length; index++) {
            var choice = decision.Choices[index];
            int minimum = Math.Max(0, choice.Minimum ?? (decision.AtLeastOne ? 1 : 0));
            int maximum = Math.Max(0, Math.Min(decision.Amount, choice.Maximum
                ?? (index < decision.Limits.Length ? decision.Limits[index] : decision.Amount)));
            if (amounts[index] < minimum || amounts[index] > maximum) return false;
        }
        return decision.AllocationKind != "combat" || !decision.Choices.Where((choice, index) => choice.IsDefender && amounts[index] > 0).Any()
            || decision.Choices.Where((choice, index) => !choice.IsDefender && choice.Lethal is { } lethal && amounts[index] < lethal).Any() == false;
    }
}

/// <summary>Translates a generic gesture using the current semantic task, not a provider input class or label.</summary>
public static class DecisionCommands
{
    public static DecisionCommand? Confirm(GameSnapshot state)
    {
        if (state.Decision is not { } decision) return null;
        DecisionCommand command = decision switch {
            { Task: DecisionTask.OpeningHand } => new KeepOpeningHand(state.Id, decision.Id),
            { Task: DecisionTask.Priority } => new PassPriority(state.Id, decision.Id),
            { Task: DecisionTask.Payment } => new PayMana(state.Id, decision.Id),
            { Task: DecisionTask.Attack } => new CompleteAttackers(state.Id, decision.Id),
            { Task: DecisionTask.Block } => new CompleteBlockers(state.Id, decision.Id),
            { Task: DecisionTask.TargetSelection or DecisionTask.EntitySelection } => new CompleteSelection(state.Id, decision.Id),
            { CancelEnabled: true } => new AcceptConfirmation(state.Id, decision.Id),
            _ => new AcknowledgeDecision(state.Id, decision.Id)
        };
        return GameCommandPermissions.Allows(state, command) ? command : null;
    }

    public static DecisionCommand? Cancel(GameSnapshot state)
    {
        if (state.Decision is not { } decision) return null;
        DecisionCommand command = decision switch {
            { Task: DecisionTask.OpeningHand } => new MulliganOpeningHand(state.Id, decision.Id),
            { Task: DecisionTask.Priority, PriorityAlternative: PriorityAlternative.UndoLastAction } => new UndoLastAction(state.Id, decision.Id),
            { Task: DecisionTask.Priority, PriorityAlternative: PriorityAlternative.PassTurn } => new PassTurn(state.Id, decision.Id),
            { Task: DecisionTask.Payment } => new CancelPayment(state.Id, decision.Id),
            { Task: DecisionTask.TargetSelection or DecisionTask.EntitySelection } => new CancelSelection(state.Id, decision.Id),
            _ => new DeclineConfirmation(state.Id, decision.Id)
        };
        return GameCommandPermissions.Allows(state, command) ? command : null;
    }

    public static DecisionCommand? Card(GameSnapshot state, string key)
    {
        if (state.Decision is not { } decision) return null;
        DecisionCommand command = decision.Task switch {
            DecisionTask.Priority when CardViews.Located(state).Any(item => item.Card.Key == key && item.Zone == "Battlefield")
                => new ActivateCard(state.Id, decision.Id, key),
            DecisionTask.Priority => new PlayCard(state.Id, decision.Id, key),
            DecisionTask.Payment => new UseManaSource(state.Id, decision.Id, key),
            _ => new SelectCard(state.Id, decision.Id, key)
        };
        return GameCommandPermissions.Allows(state, command) ? command : null;
    }

    public static DecisionCommand? Choose(GameSnapshot state, IEnumerable<int> indexes)
    {
        if (state.Decision is not { } decision) return null;
        var choices = indexes.ToImmutableArray();
        DecisionCommand command = decision.Task is DecisionTask.Reveal && choices.IsEmpty ? new AcknowledgeReveal(state.Id, decision.Id)
            : decision.Ordered ? new OrderOptions(state.Id, decision.Id, choices) : new ChooseOptions(state.Id, decision.Id, choices);
        return GameCommandPermissions.Allows(state, command) ? command : null;
    }

    public static DecisionCommand? Value(GameSnapshot state, string value)
    {
        if (state.Decision is not { } decision) return null;
        DecisionCommand command;
        if (decision.Task == DecisionTask.Number) {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) return null;
            command = new ChooseNumber(state.Id, decision.Id, number);
        } else command = new EnterText(state.Id, decision.Id, value);
        return GameCommandPermissions.Allows(state, command) ? command : null;
    }
}
