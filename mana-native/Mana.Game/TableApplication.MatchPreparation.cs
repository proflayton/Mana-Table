using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string[] matchReadingPages = [""];

    // Navigation and draft reconciliation happen once after input and accepted
    // observations, before registering the next frame's controls.
    private void PrepareMatchPresentation()
    {
        ReconcileDecisionDraft();
        abilityActionPlan = null;
        if (match is not { } state) return;
        turnProgress = TurnProgressPresentation.From(state, preferences.Stops);
        priorityView = PriorityPresentation.From(state);
        handPage = Math.Clamp(handPage, 0, Math.Max(0, ((state.Viewer?.Zone("Hand").Cards.Length ?? 0) - 1) / 11));
        if (allocationScope != Scope) { allocationScope = Scope; CloseAllocationDetail(); }
        if (state.Decision is not { Task: DecisionTask.Allocation } allocation) CloseAllocationDetailIfOpen();
        else if (allocationDetail >= -1 && (allocationDetail < 0 ? allocationDetailId != allocation.SourceCardId
            : allocationDetail >= allocation.Choices.Length || allocationDetailId != allocation.Choices[allocationDetail].Id)) CloseAllocationDetail();

        var player = state.Players.FirstOrDefault(player => player.Id == zonePlayer);
        if (overlay == "inspect" && inspected == null || overlay == "action" && inspectedAction == null
            || overlay is "player" or "mana" && player == null) overlay = "";
        if (overlay == "ability-auto-pass") {
            var abilities = PriorityAbilityOptions();
            if (abilities.Length == 0) overlay = "";
            else priorityAbilityId = abilities[Math.Max(0, Array.FindIndex(abilities, ability => ability.Id == priorityAbilityId))].Id;
        }
        if (overlay == "hand-placement") {
            var card = placementCard == null ? null : state.Viewer?.Zone("Hand").Cards.FirstOrDefault(card => CardViews.Same(card, placementCard));
            var options = card == null ? [] : HandPlacements.Options(state, card);
            if (Scope != placementScope || card == null || options.Length == 0) { overlay = ""; placementCard = null; }
            else placementPage = Math.Clamp(placementPage, 0, Math.Max(0, (options.Length - 1) / 3));
        }
        historyPage = Math.Clamp(historyPage, 0, Math.Max(0, (state.Notices.Length + state.Activity.Length - 1) / 10));
        string top = state.Stack.FirstOrDefault()?.Id ?? "";
        if (stackQueueTop != top) { stackQueueTop = top; stackQueuePage = 0; }
        stackQueuePage = Math.Clamp(stackQueuePage, 0, Math.Max(0, (state.Stack.Length - 1) / 3));
        if (zoneName == "Stack") {
            zonePage = Math.Clamp(zonePage, 0, Math.Max(0, state.Stack.Length - 1));
            int current = Array.FindIndex(state.Stack.ToArray(), item => item.Id == inspectedStack);
            if (current >= 0) zonePage = current;
        } else if (zoneName is not null and not "Battlefield")
            zonePage = Math.Clamp(zonePage, 0, Math.Max(0, ((player?.Zone(zoneName).Cards.Length ?? 0) - 1) / 7));
        if (overlay == "combat") {
            int total = combatCandidates ? state.Viewer?.Zone("Battlefield").Cards.Count(card => CombatGuide.CanSelect(state, card)) ?? 0 : state.Combat?.Attackers.Length ?? 0;
            combatPage = Math.Clamp(combatPage, 0, Math.Max(0, (total - 1) / (combatCandidates ? 6 : 3)));
        } else if (overlay == "blockers") {
            int count = state.Combat?.Attackers.FirstOrDefault(attack => attack.CardId == reviewedAttacker)?.BlockerIds.Count(id => CombatGuide.Find(state, id) != null) ?? 0;
            combatPage = Math.Clamp(combatPage, 0, Math.Max(0, (count - 1) / 6));
        }
        if (overlay == "player" && player != null) {
            string context = state.Id + ":" + player.Id;
            if (playerCounterContext != context) { playerCounterContext = context; playerCounterPage = 0; }
            var states = StateClarity.ProminentPlayerStates(player);
            if (playerStateFocus.Length > 0) {
                int index = Array.FindIndex(states, item => item.Id == playerStateFocus);
                if (index >= 0) playerCounterPage = index;
                playerStateFocus = "";
            }
            int pages = player.States.Length > 0 ? states.Length : (StateClarity.PlayerCounters(player).Length + 3) / 4;
            playerCounterPage = Math.Clamp(playerCounterPage, 0, Math.Max(0, pages - 1));
            playerDamagePage = Math.Clamp(playerDamagePage, 0, Math.Max(0, (player.CommanderDamage.Length - 1) / 6));
        }
        if (state.Decision is { } decision && !AbilityActionPending) {
            var faces = decision.LibraryCards.Length > 0
                ? decision.LibraryCards.Select(choice => (choice.Label, HasCard: true))
                : decision.Choices.Select(choice => (choice.Label, HasCard: CardViews.Choice(state, choice) != null));
            int count = faces.Count(choice => choice.Label.Contains(choiceFilter, StringComparison.OrdinalIgnoreCase));
            int capacity = decision.Task == DecisionTask.Allocation ? 6 : faces.Any(choice => choice.HasCard) ? 7 : 6;
            choicePage = Math.Clamp(choicePage, 0, Math.Max(0, (count - 1) / capacity));
        }
        PrepareFocusedBattlefield();
        abilityActionPlan = PrepareAbilityActionLayout();
        if (abilityActionPlan != null) choicePage = Math.Clamp(choicePage, 0, abilityActionPlan.Pages.Count - 1);
        PrepareReaderBindings();
        matchReadingPages = PrepareMatchReadingPages();
        inspectPage = Math.Clamp(inspectPage, 0, matchReadingPages.Length - 1);
        changePage = Math.Clamp(changePage, 0, matchReadingPages.Length - 1);
        tableStatePage = Math.Clamp(tableStatePage, 0, matchReadingPages.Length - 1);
        allocationRulesPage = Math.Clamp(allocationRulesPage, 0, matchReadingPages.Length - 1);
        priorityAbilityTextPage = Math.Clamp(priorityAbilityTextPage, 0, matchReadingPages.Length - 1);
    }

    private void CloseAllocationDetailIfOpen() { if (allocationDetail >= -1) CloseAllocationDetail(); }

    private void PrepareReaderBindings()
    {
        var entry = matchPresentation.Cards.Pinned;
        string sourceKey = entry == null ? "" : string.Join("\n", match?.Id, match?.Decision?.Id, entry.Id, entry.Zone,
            VisibleCards().FirstOrDefault(card => SameVisibleCard(card, entry.Card))?.Key);
        if (sourceKey != sourceReaderViewKey) { sourceReaderViewKey = sourceKey; sourceReaderScroll = 0; }
        string battlefieldKey = entry == null ? "" : (match?.Id ?? "") + "\n" + BattlefieldReaderBinding(entry);
        if (battlefieldKey != battlefieldReaderStateBinding) { battlefieldReaderStateBinding = battlefieldKey; battlefieldReaderStateScroll = 0; }
    }

    private string[] PrepareMatchReadingPages()
    {
        if (overlay == "inspect" && inspected is { } original) return InspectorPages(original, InspectorFace(original));
        if (overlay == "action" && inspectedAction is { } action)
            return TextPages(action.Detail + "\n\nRecent table changes\n" + string.Join("\n", action.Changes), 1800);
        if (overlay == "help") {
            string purpose = string.IsNullOrWhiteSpace(match!.Decision?.Message) ? StateClarity.DecisionInstruction(match, busy, pendingActionLabel) : match.Decision.Message;
            return MeasuredHelpPages(purpose + "\n\n" + (match.Combat?.BlockProblem ?? "") + "\n\n" + CardActions.Help);
        }
        if (overlay == "table-state") return TextPages(string.Join("\n\n", match!.States.Select(PublicStatePresentation.Description)), ReadingPageSize(1100));
        if (overlay == "ability-auto-pass" && PriorityAbilityOptions().FirstOrDefault(ability => ability.Id == priorityAbilityId) is { } ability)
            return TextPages(ability.Text, 850);
        if (overlay == "changes" && CurrentChange is { } group) return ChangeReadingPages(group);
        if (overlay.Length == 0 && zoneName == "Stack" && match!.Stack.ElementAtOrDefault(zonePage) is { } item)
            return TextPages(item.Text, ReadingPageSize(1000));
        if (DecisionModal && match?.Decision is { Task: DecisionTask.Allocation } decision && allocationDetail >= -1) {
            var choice = allocationDetail < 0 ? null : decision.Choices.ElementAtOrDefault(allocationDetail);
            var card = choice == null ? CardViews.Source(match, decision) : CardViews.Choice(match, choice);
            return TextPages(card?.Text ?? choice?.Detail ?? "", 550);
        }
        return [""];
    }
}
