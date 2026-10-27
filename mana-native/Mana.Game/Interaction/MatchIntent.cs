using Mana.Contracts;

namespace Mana.Table.Interaction;

internal enum MatchView { Settings, History, Help, Concede, LeaveTable, TableState }
internal enum MatchPage { Choice, Inspect, Zone, Combat, Change, History, PriorityQueue, PriorityText, Placement, PlayerDamage, PlayerCounter, TableState, AllocationRules }

// Input describes a player intent. Rendering cannot retain application callbacks
// or gain authority by choosing a debug control name.
internal abstract record MatchIntent : UiIntent
{
    internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Current;
    internal sealed record OpenView(MatchView View) : MatchIntent;
    internal sealed record OpenHistory : MatchIntent;
    internal sealed record CloseView : MatchIntent;
    internal sealed record CloseZone : MatchIntent;
    internal sealed record CloseInspector : MatchIntent;
    internal sealed record ViewBattlefield : MatchIntent;
    internal sealed record OpenDecision : MatchIntent;
    internal sealed record OpenZone(int PlayerId, string Zone) : MatchIntent;
    internal sealed record OpenBattlefields : MatchIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Playback; }
    internal sealed record OpenStack(int Index = 0) : MatchIntent;
    internal sealed record InspectCard(Card Card) : MatchIntent;
    internal sealed record InspectOccurrence(string Id) : MatchIntent;
    internal sealed record UseCard(Card Card, bool CloseOverlay = false, bool DismissZone = false) : MatchIntent;
    internal sealed record UseStackCard(Card Card, Card Reported, int Index, bool PayingSource) : MatchIntent;
    internal sealed record InspectDecisionSource : MatchIntent;
    internal sealed record UsePlayer(int PlayerId) : MatchIntent;
    internal sealed record ChooseRecipient(int PlayerId) : MatchIntent;
    internal sealed record SetPage(MatchPage PageKind, int Page) : MatchIntent {
        internal override InteractionScopeKind ScopeKind => PageKind == MatchPage.PriorityText ? InteractionScopeKind.Playback : InteractionScopeKind.Current;
    }
    internal sealed record ExpandCardCollection(Mana.Renderer.CardCollectionTarget Target) : MatchIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Playback; }
    internal sealed record FoldCardCollection : MatchIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Playback; }
    internal sealed record ResetCardCollectionView : MatchIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Playback; }
    internal sealed record FlipInspector : MatchIntent;
    internal sealed record FlipReaderText(string Id) : MatchIntent;
    internal sealed record SubmitCommand(GameCommand Command) : MatchIntent;
    internal sealed record ConfirmDecision(bool CloseOverlay = false) : MatchIntent;
    internal sealed record CancelDecision : MatchIntent;
    internal sealed record ConfirmTurnPass : MatchIntent;
    internal sealed record DismissTurnPass : MatchIntent;
    internal sealed record ConfirmSelection : MatchIntent;
    internal sealed record ChooseNone(bool CollapseReader = false) : MatchIntent;
    internal sealed record ClearSelection : MatchIntent;
    internal sealed record ToggleChoiceDisclosure : MatchIntent;
    internal sealed record SelectChoice(int Index) : MatchIntent;
    internal sealed record ChooseAbility(int Index) : MatchIntent;
    internal sealed record ChooseCounterPayment(int Index) : MatchIntent;
    internal sealed record FocusInput(bool Filter) : MatchIntent;
    internal sealed record AdjustNumber(int Delta) : MatchIntent;
    internal sealed record ConfirmValue : MatchIntent;
    internal sealed record OpenAllocationSource : MatchIntent;
    internal sealed record OpenAllocationRecipient(int Index, bool Edit = false) : MatchIntent;
    internal sealed record CloseAllocation : MatchIntent;
    internal sealed record ResetAllocation : MatchIntent;
    internal sealed record ConfirmAllocation : MatchIntent;
    internal sealed record SetAllocation(int Index, int Amount) : MatchIntent;
    internal sealed record AdjustAllocation(int Index, int Delta) : MatchIntent;
    internal sealed record AssignRemaining(int Index) : MatchIntent;
    internal sealed record BeginAllocationInput(int Index) : MatchIntent;
    internal sealed record ApplyAllocationInput(int Index) : MatchIntent;
    internal sealed record SetCombatView(bool Candidates) : MatchIntent;
    internal sealed record SelectCombatant(string Id) : MatchIntent;
    internal sealed record ClearCombatant : MatchIntent;
    internal sealed record RemoveCombatAssignment(string Id, string? Attacker = null) : MatchIntent;
    internal sealed record OpenCombat(bool Candidates = false) : MatchIntent;
    internal sealed record ReviewBlockers(string Attacker) : MatchIntent;
    internal sealed record ReturnFromMatch : MatchIntent;
    internal sealed record ReturnToWorkshop(bool Again = false) : MatchIntent;
    internal sealed record Concede : MatchIntent;
    internal sealed record RetryConnection : MatchIntent;
    internal sealed record LeaveTable : MatchIntent;
    internal sealed record OpenChanges(string? Id = null) : MatchIntent;
    internal sealed record FocusChange(string Id) : MatchIntent;
    internal sealed record ShowResolution(double Started, string Title, string Detail, string Outcome) : MatchIntent {
        internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Playback;
    }
    internal sealed record InspectPendingEffect(string Id) : MatchIntent;
    internal sealed record ReadPendingReference(string StackId, string CardId, string DecisionId, bool InspectSource) : MatchIntent;
    internal sealed record PreviewPendingCard(string StackId, string CardId, string DecisionId = "") : MatchIntent;
    internal sealed record ReadPendingPlayer(string StackId, int PlayerId) : MatchIntent;
    internal sealed record CloseBattlefieldFocus : MatchIntent;
    internal sealed record FocusSurface : MatchIntent;
    internal sealed record SetBattlefieldOwner(int PlayerId) : MatchIntent;
    internal sealed record ScrollBattlefieldTo(int Offset) : MatchIntent;
    internal sealed record ReadFocusedCard(string Game, int Owner, string Identity) : MatchIntent;
    internal sealed record SetStudyFocus(int? PlayerId) : MatchIntent;
    internal sealed record OpenPlayerState(int PlayerId, string StateId) : MatchIntent;
    internal sealed record OpenPlayerDetails(int PlayerId) : MatchIntent;
    internal sealed record OpenManaPool(int PlayerId) : MatchIntent;
    internal sealed record SpendFloatingMana(string Color) : MatchIntent;
    internal sealed record OpenTableState(int Index) : MatchIntent;
    internal sealed record UseHandPlacement(Card Card, string AbilityId) : MatchIntent;
    internal sealed record CloseHandPlacement : MatchIntent;
    internal sealed record UseReaderCard(Card Card) : MatchIntent;
    internal sealed record RetrySourceActions : MatchIntent;
    internal sealed record UseSourceAction(string Id) : MatchIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Source; }
    internal sealed record UseSourcePlacement(string Id) : MatchIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Source; }
    internal sealed record SetAutomatic(bool Enabled) : MatchIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Playback; }
    internal sealed record ToggleHold : MatchIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Playback; }
    internal sealed record TogglePhaseStop(string Key, bool Resume = false) : MatchIntent {
        internal override InteractionScopeKind ScopeKind => Resume ? InteractionScopeKind.PhaseStop : InteractionScopeKind.Playback;
    }
    internal sealed record SetQuietPriority(bool Armed) : MatchIntent {
        internal override InteractionScopeKind ScopeKind => Armed ? InteractionScopeKind.Current : InteractionScopeKind.Playback;
    }
    internal sealed record ToggleReducedMotion : MatchIntent;
    internal sealed record ToggleSounds : MatchIntent;
    internal sealed record CycleTextScale : MatchIntent;
    internal sealed record OpenPriorityPassing : MatchIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Playback; }
    internal sealed record ReadPriorityAbility(string Id) : MatchIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Playback; }
    internal sealed record SetPriorityAbility(string Id, bool Ignored) : MatchIntent {
        internal override InteractionScopeKind ScopeKind => Ignored ? InteractionScopeKind.Current : InteractionScopeKind.Playback;
    }
    internal sealed record OpenDecisionHelp : MatchIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Playback; }
}
