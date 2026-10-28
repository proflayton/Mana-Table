using Mana.Contracts;
using Mana.Magic;
using Mana.Table.Interaction;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void HandleMatchIntent(MatchIntent intent)
    {
        switch (intent) {
            case MatchIntent.OpenHistory: overlay = "history"; historyPage = 0; noticeUntil = 0; break;
            case MatchIntent.OpenView open:
                overlay = open.View switch { MatchView.Settings => "settings", MatchView.History => "history", MatchView.Help => "help",
                    MatchView.Concede => "concede", MatchView.LeaveTable => "leave-table", _ => "table-state" };
                if (open.View == MatchView.History) historyPage = 0;
                if (open.View == MatchView.Help) inspectPage = 0;
                break;
            case MatchIntent.CloseView: overlay = ""; textFocus = false; break;
            case MatchIntent.CloseInspector: overlay = ""; inspected = null; break;
            case MatchIntent.CloseZone: zoneName = null; break;
            case MatchIntent.ViewBattlefield: decisions.Collapsed = true; textFocus = false; CloseAllocationDetail(); break;
            case MatchIntent.OpenDecision: decisions.Collapsed = false; break;
            case MatchIntent.OpenZone open: OpenZone(open.PlayerId, open.Zone); break;
            case MatchIntent.OpenBattlefields when match != null: OpenZone(match.ViewerId, "Battlefield"); break;
            case MatchIntent.OpenStack open: OpenStack(open.Index); break;
            case MatchIntent.OpenStackWorkload open: OpenStackWorkload(open.Key); break;
            case MatchIntent.CloseStackWorkload: expandedStackWorkload = ""; break;
            case MatchIntent.InspectCard read: Inspect(read.Card); break;
            case MatchIntent.InspectOccurrence read:
                if (matchPresentation.Cards.Cards.LastOrDefault(card => card.Id == read.Id) is { } occurrence)
                    ToggleCardReader(occurrence.Hit);
                break;
            case MatchIntent.UseCard use:
                if (use.CloseOverlay) overlay = "";
                if (use.DismissZone) zoneName = null;
                CardClick(use.Card); break;
            case MatchIntent.UseStackCard use:
                if (use.PayingSource) Inspect(use.Card);
                else if (IsCardActionable(use.Reported)) CardClick(use.Reported);
                else if (match?.Stack.Length > use.Index) OpenStack(use.Index);
                else Inspect(use.Card);
                break;
            case MatchIntent.InspectDecisionSource when match?.Decision?.Context.Source?.Card is { } source: Inspect(source); break;
            case MatchIntent.UsePlayer use: PlayerClick(use.PlayerId); break;
            case MatchIntent.ChooseRecipient choice when match != null && StateClarity.ChooseRecipient(match, choice.PlayerId) is { } reply: Send(reply); break;
            case MatchIntent.SetPage page: SetMatchPage(page.PageKind, page.Page); break;
            case MatchIntent.ExpandCardCollection expand: ExpandCardCollection(expand.Target); break;
            case MatchIntent.FoldCardCollection: CollapseCardReading(); FoldCardCollection(); break;
            case MatchIntent.ResetCardCollectionView: ResetCardCollectionView(); break;
            case MatchIntent.FlipInspector: inspectBack = !inspectBack; inspectPage = 0; break;
            case MatchIntent.FlipReaderText flip: FlipReaderText(flip.Id); break;
            case MatchIntent.SubmitCommand { Command: DecisionCommand command }: Send(command); break;
            case MatchIntent.SubmitCommand { Command: RestorePriorityAbility command }: session.SubmitSetting(command); break;
            case MatchIntent.ConfirmDecision confirm when match?.Decision != null:
                if (confirm.CloseOverlay) overlay = "";
                Send(DecisionCommands.Confirm(match)); break;
            case MatchIntent.CancelDecision when match?.Decision != null: Send(DecisionCommands.Cancel(match)); break;
            case MatchIntent.ConfirmTurnPass: ConfirmTurnPass(); break;
            case MatchIntent.DismissTurnPass: turnPassRequest = null; break;
            case MatchIntent.ConfirmSelection when match?.Decision is { } decision:
                textFocus = false; Send(DecisionCommands.Choose(match, decision.Payload is RevealPayload ? [] : decisions.Selections)); break;
            case MatchIntent.ChooseNone none when match?.Decision != null:
                if (none.CollapseReader) CollapseCardReading();
                Send(DecisionCommands.Choose(match, [])); break;
            case MatchIntent.ClearSelection: decisions.ClearSelection(); break;
            case MatchIntent.ToggleChoiceDisclosure when decisions.Choice is { } choice:
                CollapseCardReading(); choice.ShowAllCards = !choice.ShowAllCards; choice.Page = 0; break;
            case MatchIntent.SelectChoice choice when match?.Decision is { } decision: ToggleChoice(decision, choice.Index); break;
            case MatchIntent.ChooseCounterPayment choice when match?.Decision is { Payload: OptionsPayload { CounterPayment: { } payment } } decision
                && payment.Options.Any(option => option.Index == choice.Index):
                Send(new ChooseOptions(match.Id, decision.Id, [choice.Index])); break;
            case MatchIntent.ChooseManaColor choice when match?.Decision is { Payload: OptionsPayload { ManaPaymentContext: not null } options } decision
                && options.Selection.Choices.Any(option => option.Index == choice.Index):
                Send(new ChooseOptions(match.Id, decision.Id, [choice.Index])); break;
            case MatchIntent.ChooseAbility choice when match?.Decision is { } decision && decision.Payload is AbilityChoicePayload:
                SubmitAbilityChoice(match, decision, choice.Index); break;
            case MatchIntent.FocusInput input: textFocus = true; decisions.Filtering = input.Filter; break;
            case MatchIntent.AdjustNumber step when decisions.Payload is NumberPayload number && int.TryParse(decisions.Input, out int value):
                decisions.Input = Math.Clamp((long)value + step.Delta, number.Range?.Min ?? int.MinValue, number.Range?.Max ?? int.MaxValue).ToString(); break;
            case MatchIntent.ConfirmValue when match?.Decision != null:
                textFocus = false; Send(DecisionCommands.Value(match, decisions.Input)); break;
            case MatchIntent.OpenAllocationSource: OpenAllocationSource(); break;
            case MatchIntent.OpenAllocationRecipient open: OpenAllocationRecipient(open.Index, open.Edit); break;
            case MatchIntent.CloseAllocation: CloseAllocationDetail(); break;
            case MatchIntent.ResetAllocation when match != null: decisions.ResetAllocation(match); break;
            case MatchIntent.ConfirmAllocation when match != null && decisions.AllocationReply(match) is { } reply: Send(reply); break;
            case MatchIntent.SetAllocation set when match != null: decisions.SetAllocation(match, set.Index, set.Amount); break;
            case MatchIntent.AdjustAllocation adjust when match != null: decisions.AdjustAllocation(match, adjust.Index, adjust.Delta); break;
            case MatchIntent.AssignRemaining fill when match != null: decisions.AssignRemaining(match, fill.Index); break;
            case MatchIntent.BeginAllocationInput edit when edit.Index >= 0 && edit.Index < decisions.Allocations.Count:
                OpenAllocationRecipient(edit.Index, true); break;
            case MatchIntent.ApplyAllocationInput edit when match != null && int.TryParse(decisions.Input, out int amount)
                && decisions.SetAllocation(match, edit.Index, amount): CloseAllocationDetail(); break;
            case MatchIntent.SetCombatView view: combatCandidates = view.Candidates; combatPage = 0; break;
            case MatchIntent.SelectCombatant choice when match != null && CombatGuide.Find(match, choice.Id) is { } card && CombatGuide.CanSelect(match, card):
                selectedCombat = choice.Id; overlay = ""; break;
            case MatchIntent.ClearCombatant: selectedCombat = null; break;
            case MatchIntent.RemoveCombatAssignment remove: RemoveAssignment(remove.Id, remove.Attacker); break;
            case MatchIntent.OpenCombat open: OpenCombat(open.Candidates); break;
            case MatchIntent.ReviewBlockers review: reviewedAttacker = review.Attacker; combatPage = 0; overlay = "blockers"; break;
            case MatchIntent.ReturnFromMatch: ReturnFromMatch(); break;
            case MatchIntent.ReturnToWorkshop back: ReturnToWorkshop(back.Again); break;
            case MatchIntent.RetryConnection: nextPoll = 0; break;
            case MatchIntent.LeaveTable: LeaveMatchTable(); break;
            case MatchIntent.Concede when match is { } current:
                overlay = "";
                Work(async () => { await matchConnection.ConcedeAsync(current.Id); var next = await matchConnection.ObserveAsync(); updates.Enqueue(() => Accept(next)); },
                    "Leaving this game", "Updating the result for your seat.", true); break;
            case MatchIntent.OpenChanges open: OpenChanges(open.Id); break;
            case MatchIntent.FocusChange focus when matchPresentation.Changes.FirstOrDefault(group => group.Id == focus.Id) is { } group: FocusAffectedChange(group); break;
            case MatchIntent.ShowResolution read:
                if (matchPresentation.LastResolution is { } recent && recent.Started == read.Started && recent.Title == read.Title
                    && recent.Detail == read.Detail && recent.Outcome == read.Outcome) { inspectedAction = recent; inspectPage = 0; overlay = "action"; }
                break;
            case MatchIntent.InspectPendingEffect effect: InspectPendingEffect(effect.Id); break;
            case MatchIntent.ReadPendingReference read: ReadPendingReference(new(read.StackId, read.CardId, read.DecisionId), read.InspectSource); break;
            case MatchIntent.PreviewPendingCard: break;
            case MatchIntent.ReadPendingPlayer read when match?.Stack.Any(item => item.Id == read.StackId
                && item.Targets.Any(target => target.Kind == "player" && target.PlayerId == read.PlayerId)) == true: PlayerClick(read.PlayerId); break;
            case MatchIntent.CloseBattlefieldFocus: CollapseCardReading(); zoneName = null; break;
            case MatchIntent.FocusSurface: break;
            case MatchIntent.SetBattlefieldOwner owner: CollapseCardReading(); zonePlayer = owner.PlayerId; break;
            case MatchIntent.ScrollBattlefieldTo scroll: CollapseCardReading(); battlefieldFocusScroll = Math.Clamp(scroll.Offset, 0, battlefieldFocusMaximum); break;
            case MatchIntent.ReadFocusedCard read: ReadFocusedBattlefieldCard(read.Game, read.Owner, read.Identity); break;
            case MatchIntent.SetStudyFocus focus: SetStudyFocus(focus.PlayerId); break;
            case MatchIntent.OpenPlayerState state: OpenPlayerState(state.PlayerId, state.StateId); break;
            case MatchIntent.OpenPlayerDetails player: zonePlayer = player.PlayerId; overlay = "player"; break;
            case MatchIntent.OpenManaPool player: OpenManaPool(player.PlayerId); break;
            case MatchIntent.SpendFloatingMana mana when match is { Decision: { Payload: PaymentPayload payment } decision } state
                && payment.UsablePoolManaColors?.Contains(mana.Color) == true
                && state.Players.FirstOrDefault(player => player.Id == (payment.PayerId ?? state.ViewerId))?.Mana.GetValueOrDefault(mana.Color) > 0:
                Send(new SpendPoolMana(state.Id, decision.Id, mana.Color)); break;
            case MatchIntent.OpenTableState state: tableStatePage = Math.Max(0, state.Index); overlay = "table-state"; break;
            case MatchIntent.UseHandPlacement use: UseHandPlacement(use.Card, use.AbilityId, Scope); break;
            case MatchIntent.CloseHandPlacement: overlay = ""; placementCard = null; break;
            case MatchIntent.UseReaderCard use:
                if (VisibleCards().FirstOrDefault(card => SameVisibleCard(card, use.Card)) is not { } currentCard || !IsCardActionable(currentCard)) break;
                if (matchPresentation.Cards.Pinned?.Zone != "Battlefield") { CollapseCardReading(); if (BattlefieldFocused) zoneName = null; }
                CardClick(currentCard); break;
            case MatchIntent.RetrySourceActions: RetryReaderActions(); break;
            case MatchIntent.UseSourceAction use when sourceActions.View.Actions.FirstOrDefault(action => action.Id == use.Id) is { } action: UseReaderAction(action); break;
            case MatchIntent.UseSourcePlacement use when ReaderActionSource() is { } sourceCard && match != null
                && HandPlacements.Options(match, sourceCard).FirstOrDefault(ability => ability.Id == use.Id) is { } placement: UseReaderPlacement(placement); break;
            case MatchIntent.SetAutomatic automatic: UseAutomaticControl(automatic.Enabled); break;
            case MatchIntent.ToggleHold: ToggleHoldControl(); break;
            case MatchIntent.TogglePriorityAlerts: priorityNotification.ToggleMute(now); break;
            case MatchIntent.TogglePhaseStop stop:
                if (stop.Resume) preferences.Stops.Remove(stop.Key);
                else if (!preferences.Stops.Remove(stop.Key)) preferences.Stops.Add(stop.Key);
                SavePreferences(); break;
            case MatchIntent.SetQuietPriority setting: if (setting.Armed) ArmQuietPriority(); else StopQuietPriority(); break;
            case MatchIntent.SetStackResolution setting: if (setting.Armed) ArmStackResolution(); else StopStackResolution(); break;
            case MatchIntent.ToggleReducedMotion: preferences = preferences with { ReducedMotion = !preferences.ReducedMotion }; SavePreferences(); break;
            case MatchIntent.ToggleSounds when overlay == "settings":
                preferences = preferences with { Sounds = !preferences.Sounds }; ApplyAudioMix(); SaveAudioPreferences(); break;
            case MatchIntent.SetMasterVolume setting when overlay == "settings": SetMasterVolume(setting.Volume); FinishAudioAdjustment(); break;
            case MatchIntent.AdjustMasterVolume setting when overlay == "settings": SetMasterVolume(preferences.Audio.Master.Volume + setting.PercentagePoints / 100f); FinishAudioAdjustment(); break;
            case MatchIntent.CycleTextScale:
                preferences = preferences with { TextScale = preferences.TextScale < 1.1f ? 1.15f : preferences.TextScale < 1.25f ? 1.3f : 1 }; SavePreferences(); break;
            case MatchIntent.OpenPriorityPassing when PriorityAbilityOptions().FirstOrDefault() is { } ability:
                overlay = "ability-auto-pass"; priorityAbilityId = ability.Id; priorityAbilityTextPage = 0; break;
            case MatchIntent.ReadPriorityAbility read: priorityAbilityId = read.Id; priorityAbilityTextPage = 0; break;
            case MatchIntent.SetPriorityAbility setting when PriorityAbilityOptions().FirstOrDefault(ability => ability.Id == setting.Id) is { } ability:
                SetAbilityAutoPass(ability, setting.Ignored); break;
            case MatchIntent.OpenDecisionHelp: overlay = "help"; inspectPage = 0; break;
        }
    }

    private void SetMatchPage(MatchPage page, int value)
    {
        switch (page) {
            case MatchPage.Choice: decisions.Page = Math.Max(0, value); break;
            case MatchPage.Inspect: inspectPage = Math.Max(0, value); break;
            case MatchPage.Zone: zonePage = Math.Max(0, value); break;
            case MatchPage.Combat: combatPage = Math.Max(0, value); break;
            case MatchPage.Change: changePage = Math.Max(0, value); break;
            case MatchPage.History: historyPage = Math.Max(0, value); break;
            case MatchPage.PriorityQueue: stackQueuePage = Math.Max(0, value); break;
            case MatchPage.PriorityText: priorityAbilityTextPage = Math.Max(0, value); break;
            case MatchPage.Placement: placementPage = Math.Max(0, value); break;
            case MatchPage.PlayerDamage: playerDamagePage = Math.Max(0, value); break;
            case MatchPage.PlayerCounter: playerCounterPage = Math.Max(0, value); break;
            case MatchPage.TableState: tableStatePage = Math.Max(0, value); break;
            case MatchPage.AllocationRules: if (decisions.Allocation?.Inspection is { } inspection) inspection.RulesPage = Math.Max(0, value); break;
        }
    }
}
