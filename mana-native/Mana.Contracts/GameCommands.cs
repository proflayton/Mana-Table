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
            GrantFiniteStackYield value => FiniteStackYieldFacts.Id(value.GrantId) && FiniteStackYieldFacts.Members(value.ExecutionIds)
                && value.StackActionVersion >= 0 && value.ResolutionChoiceVersion >= 0,
            RevokeFiniteStackYield value => FiniteStackYieldFacts.Id(value.GrantId),
            UndoAction value => UndoFacts.Identifier(value.OfferId) && UndoFacts.Identifier(value.ActionId) && value.BarrierVersion >= 0,
            SelectCard value => Present(value.Card), PlayCard value => Present(value.Card),
            ActivateCard value => Present(value.Card), UseManaSource value => Present(value.Card),
            SpendPoolMana value => value.Color is "W" or "U" or "B" or "R" or "G" or "C",
            AutoPayOptionalPayment value => FiniteStackYieldFacts.Id(value.OfferId),
            ToggleAttackPlayer value => Present(value.Attacker),
            ToggleAttackPermanent value => Present(value.Attacker) && Present(value.DefenderCard),
            ToggleBlock value => Present(value.Attacker) && Present(value.Blocker),
            ChooseOptions value => ValidChoices(value.Choices), OrderOptions value => ValidChoices(value.Choices),
            AllocateAmounts value => !value.Amounts.IsDefault && value.Amounts.All(amount => amount >= 0),
            EnterText value => value.Value != null,
            UseOfferedAction value => Present(value.Card) && Present(value.ActionId) && (value.ManaOutput == null || value.ManaOutput.IsValid()),
            PlaceFromHand value => Present(value.Card) && Present(value.AbilityId),
            IgnorePriorityAbility value => Present(value.AbilityId), RestorePriorityAbility value => Present(value.AbilityId),
            PassForNow value => Present(value.YieldToken),
            PassPriority value => value.StackInterruptionVersion is null or >= 0,
            AutoPassPriority or KeepOpeningHand or MulliganOpeningHand or PayMana or CancelPayment
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

public sealed record PassPriority(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId)
{
    // A finite stack continuation additionally guards against an intervening
    // action or required choice, including one absent from the latest snapshot.
    public long? StackInterruptionVersion { get; init; }
}
public sealed record AutoPassPriority(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record KeepOpeningHand(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record MulliganOpeningHand(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record PayMana(string GameId, string DecisionId) : DecisionCommand(GameId, DecisionId);
public sealed record AutoPayOptionalPayment(string GameId, string DecisionId, string OfferId) : DecisionCommand(GameId, DecisionId);
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
public sealed record SpendPoolMana(string GameId, string DecisionId, string Color) : DecisionCommand(GameId, DecisionId);
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
public sealed record UseOfferedAction(string GameId, string DecisionId, string Card, string ActionId) : DecisionCommand(GameId, DecisionId)
{
    public ManaOutputChoice? ManaOutput { get; init; }
}
public sealed record PlaceFromHand(string GameId, string DecisionId, string Card, string AbilityId) : DecisionCommand(GameId, DecisionId);
public sealed record IgnorePriorityAbility(string GameId, string DecisionId, string AbilityId) : DecisionCommand(GameId, DecisionId);
public sealed record RestorePriorityAbility(string GameId, string AbilityId) : GameCommand(GameId);
public sealed record PassForNow(string GameId, string DecisionId, string YieldToken) : DecisionCommand(GameId, DecisionId);

/// <summary>Checks only published offers and constraints, never printed rules or prose.</summary>
public static class GameCommandPermissions
{
    public static bool Allows(GameSnapshot state, GameCommand command)
    {
        try { command.Validate(); } catch (ArgumentException) { return false; }
        if (state.Id != command.GameId) return false;
        if (command is RevokeFiniteStackYield revoke)
            return state.FiniteStackYield is { } yield && yield.GrantId == revoke.GrantId && yield.Status != FiniteStackYieldStatus.Idle;
        if (state.Status is "finished" or "error") return false;
        if (command is RestorePriorityAbility restore)
            return state.IgnoredPriorityAbilities.Any(ability => ability.Id == restore.AbilityId);
        if (command is not DecisionCommand scoped || state.Decision is not { } decision || scoped.DecisionId != decision.Id) return false;
        var payload = decision.Payload;
        if (state.DecisionOwnerId is { } owner && owner != state.ViewerId
            || payload is PriorityPayload && state.PriorityPlayerId is { } responder && responder != state.ViewerId) return false;
        Card? Card(string key) => CardViews.Located(state).Select(item => item.Card)
            .Concat(state.Players.SelectMany(player => player.Zones).Where(zone => zone.TopCard != null).Select(zone => zone.TopCard!))
            .FirstOrDefault(card => card.Key == key && key.Length > 0);
        Card? CurrentSource(string key) => CardViews.FindActionSource(state, key)?.Card;
        bool Selectable(string key) => Card(key) is { Selectable: true };
        bool Located(string key, params string[] zones) => CardViews.Located(state).Any(item => item.Card.Key == key && zones.Contains(item.Zone));
        return command switch {
            GrantFiniteStackYield grant => payload is PriorityPayload { Pass.Enabled: true } && FiniteStackYieldFacts.CanGrant(state, grant),
            PassPriority value => payload is PriorityPayload { Pass.Enabled: true }
                && (value.StackInterruptionVersion == null || state.Status == "playing" && state.Stack.Length > 0
                    && value.StackInterruptionVersion == state.StackInterruptionVersion),
            AutoPassPriority => state.Status == "playing" && payload is PriorityPayload { Pass.Enabled: true, CanAutoPass: true },
            KeepOpeningHand => payload is OpeningHandPayload { Keep.Enabled: true },
            MulliganOpeningHand => payload is OpeningHandPayload { Mulligan.Enabled: true },
            PayMana => payload is PaymentPayload { Pay.Enabled: true },
            AutoPayOptionalPayment value => payload is PaymentPayload { Cancel.Enabled: true, OptionalDecline: { Mandatory: false } offer,
                OptionalPaymentSupport: { Availability: OptionalManaPaymentAvailability.Payable, CanAutoPay: true } }
                && offer.OfferId == value.OfferId && offer.PayerId == state.ViewerId,
            CancelPayment => payload is PaymentPayload { Cancel.Enabled: true },
            CompleteAttackers => payload is AttackPayload { Complete.Enabled: true },
            CompleteBlockers => payload is BlockPayload { Complete.Enabled: true } && string.IsNullOrEmpty(state.Combat?.BlockProblem),
            CompleteSelection => payload is TargetSelectionPayload { Complete.Enabled: true } or EntitySelectionPayload { Complete.Enabled: true },
            CancelSelection => payload is TargetSelectionPayload { Cancel.Enabled: true } or EntitySelectionPayload { Cancel.Enabled: true },
            AcceptConfirmation => payload is ConfirmationPayload { Accept.Enabled: true, Decline: not null },
            DeclineConfirmation => payload is ConfirmationPayload { Decline.Enabled: true },
            AcknowledgeDecision => payload is ConfirmationPayload { Accept.Enabled: true, Decline: null },
            AcknowledgeReveal => payload is RevealPayload,
            UndoLastAction => false,
            UndoAction value => UndoFacts.CanUndo(state) && state.Undo?.Offer is { } undo
                && value.OfferId == undo.Id && value.ActionId == undo.ActionId && value.BarrierVersion == undo.BarrierVersion,
            PassTurn => payload is PriorityPayload { Alternative: { Kind: PriorityAlternative.PassTurn, Reply.Enabled: true } },
            SelectCard value => payload is TargetSelectionPayload or EntitySelectionPayload && Selectable(value.Card),
            PlayCard value => payload is PriorityPayload && Selectable(value.Card) && Located(value.Card, "Hand", "Command"),
            ActivateCard value => payload is PriorityPayload && Selectable(value.Card) && Located(value.Card, "Battlefield"),
            UseManaSource value => payload is PaymentPayload && CurrentSource(value.Card) is { Selectable: true },
            SpendPoolMana value => payload is PaymentPayload payment
                && (payment.PayerId is { } payer ? state.Players.FirstOrDefault(player => player.Id == payer) : state.Viewer)
                    ?.Mana.GetValueOrDefault(value.Color) > 0
                && (payment.UsablePoolManaColors == null || payment.UsablePoolManaColors.Value.Contains(value.Color)),
            SelectPlayer value => payload switch { TargetSelectionPayload target => target.Players.Contains(value.Player),
                EntitySelectionPayload entity => entity.Players.Contains(value.Player), _ => false },
            ToggleAttackPlayer value => payload is AttackPayload && Card(value.Attacker) is { CombatId: var attacker }
                && state.Combat?.AttackOptions.Any(option => option.CardId == attacker
                    && option.Defenders.Any(defender => defender.Kind == "player" && defender.PlayerId == value.Player)) == true,
            ToggleAttackPermanent value => payload is AttackPayload && Card(value.Attacker) is { CombatId: var attacker }
                && Card(value.DefenderCard) is { CombatId: var defender }
                && state.Combat?.AttackOptions.Any(option => option.CardId == attacker
                    && option.Defenders.Any(target => target.Kind != "player" && target.Id == defender)) == true,
            ToggleBlock value => payload is BlockPayload && Card(value.Attacker) is { CombatId: var attacker }
                && Card(value.Blocker) is { CombatId: var blocker }
                && state.Combat?.Attackers.Any(attack => attack.CardId == attacker && attack.EligibleBlockerIds.Contains(blocker)) == true,
            ChooseOptions value => payload switch { OptionsPayload options => Choices(options.Selection, value.Choices)
                    && (options.HandReduction == null || HandReductionFacts.IsCurrent(state)),
                AbilityChoicePayload ability => Choices(ability.Selection, value.Choices), _ => false },
            OrderOptions value => payload is OrderingPayload order && Choices(order.Selection, value.Choices),
            ChooseNumber value => payload is NumberPayload number && (number.Range is not { } range || value.Value >= range.Min && value.Value <= range.Max),
            EnterText => payload is TextPayload,
            AllocateAmounts value => payload is AllocationPayload allocation && AllocationConstraints.Check(allocation, value.Amounts) == null,
            SkipAllocation => payload is AllocationPayload { MaySkip: true },
            // An opaque route can belong to a face-down source its controller
            // may inspect. The current exact offer, not its printed face, owns
            // that authority; the source session/provider validates membership.
            UseOfferedAction value => payload is PriorityPayload or PaymentPayload && CurrentSource(value.Card) is { } offeredSource
                && (!offeredSource.FaceDown || CardViews.FindActionSource(state, value.Card) is { } opaque
                    && opaque.PlayerId == state.ViewerId && opaque.Zone is "Battlefield" or "Exile" or "Stack" or "Sideboard"),
            PlaceFromHand value => state.Status == "playing" && payload is PriorityPayload priority
                && state.Viewer is { } viewer && viewer.Zone("Hand").Cards.Any(card => card.Key == value.Card && !card.FaceDown)
                && priority.Abilities.Any(ability => ability.Id == value.AbilityId && ability.SourceCardKey.Length > 0 && ability.HandPlacementLimit > 0
                    && ability.EligibleHandKeys.Contains(value.Card)
                    && viewer.Zone("Battlefield").Cards.Any(source => source.Key == ability.SourceCardKey && !source.FaceDown && !source.PhasedOut)),
            IgnorePriorityAbility value => payload is PriorityPayload priority && priority.Abilities.Any(ability => ability.Id == value.AbilityId),
            PassForNow value => state.Status == "playing" && payload is PriorityPayload { Pass.Enabled: true } priority && state.Turn > 0 && state.Stack.Length == 0
                && state.ActivePlayerId != null && state.Viewer?.Eliminated != true
                && state.ActionClass is "" or "priority" && (state.PriorityPlayerId == null || state.PriorityPlayerId == state.ViewerId)
                && (state.DecisionOwnerId == null || state.DecisionOwnerId == state.ViewerId)
                && value.YieldToken.Length > 0 && priority.QuietYieldToken == value.YieldToken,
            _ => false
        };
    }
    private static bool Choices(ChoiceSelection selection, ImmutableArray<int> choices) => !choices.IsDefault
        && choices.Length >= selection.Range.Min && choices.Length <= selection.Range.Max && choices.Distinct().Count() == choices.Length
        && choices.All(index => selection.Choices.Any(choice => choice.Index == index));
}

/// <summary>Maps a gesture to the exact current payload. Unknown tasks never become acknowledgments.</summary>
public static class DecisionCommands
{
    public static DecisionCommand? Confirm(GameSnapshot state)
    {
        if (state.Decision is not { } decision) return null;
        DecisionCommand? command = decision.Payload switch {
            OpeningHandPayload => new KeepOpeningHand(state.Id, decision.Id),
            PriorityPayload => new PassPriority(state.Id, decision.Id),
            PaymentPayload => new PayMana(state.Id, decision.Id),
            AttackPayload => new CompleteAttackers(state.Id, decision.Id),
            BlockPayload => new CompleteBlockers(state.Id, decision.Id),
            TargetSelectionPayload or EntitySelectionPayload => new CompleteSelection(state.Id, decision.Id),
            ConfirmationPayload { Decline: not null } => new AcceptConfirmation(state.Id, decision.Id),
            ConfirmationPayload { Decline: null } => new AcknowledgeDecision(state.Id, decision.Id),
            _ => null
        };
        return command != null && GameCommandPermissions.Allows(state, command) ? command : null;
    }
    public static DecisionCommand? Cancel(GameSnapshot state)
    {
        if (state.Decision is not { } decision) return null;
        DecisionCommand? command = decision.Payload switch {
            OpeningHandPayload => new MulliganOpeningHand(state.Id, decision.Id),
            PriorityPayload { Alternative.Kind: PriorityAlternative.PassTurn } => new PassTurn(state.Id, decision.Id),
            PaymentPayload => new CancelPayment(state.Id, decision.Id),
            TargetSelectionPayload or EntitySelectionPayload => new CancelSelection(state.Id, decision.Id),
            ConfirmationPayload { Decline: not null } => new DeclineConfirmation(state.Id, decision.Id),
            _ => null
        };
        return command != null && GameCommandPermissions.Allows(state, command) ? command : null;
    }
    public static DecisionCommand? Card(GameSnapshot state, string key)
    {
        if (state.Decision is not { } decision) return null;
        DecisionCommand? command = decision.Payload switch {
            PriorityPayload when CardViews.Located(state).Any(item => item.Card.Key == key && item.Zone == "Battlefield")
                => new ActivateCard(state.Id, decision.Id, key),
            PriorityPayload => new PlayCard(state.Id, decision.Id, key),
            PaymentPayload => new UseManaSource(state.Id, decision.Id, key),
            TargetSelectionPayload or EntitySelectionPayload => new SelectCard(state.Id, decision.Id, key),
            _ => null
        };
        return command != null && GameCommandPermissions.Allows(state, command) ? command : null;
    }
    public static DecisionCommand? Choose(GameSnapshot state, IEnumerable<int> indexes)
    {
        if (state.Decision is not { } decision) return null;
        var choices = indexes.ToImmutableArray();
        DecisionCommand? command = decision.Payload switch {
            RevealPayload when choices.IsEmpty => new AcknowledgeReveal(state.Id, decision.Id),
            OrderingPayload => new OrderOptions(state.Id, decision.Id, choices),
            OptionsPayload or AbilityChoicePayload => new ChooseOptions(state.Id, decision.Id, choices),
            _ => null
        };
        return command != null && GameCommandPermissions.Allows(state, command) ? command : null;
    }
    public static DecisionCommand? Value(GameSnapshot state, string value)
    {
        if (state.Decision is not { } decision) return null;
        DecisionCommand? command = decision.Payload switch {
            NumberPayload when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) => new ChooseNumber(state.Id, decision.Id, number),
            TextPayload => new EnterText(state.Id, decision.Id, value),
            _ => null
        };
        return command != null && GameCommandPermissions.Allows(state, command) ? command : null;
    }
}
