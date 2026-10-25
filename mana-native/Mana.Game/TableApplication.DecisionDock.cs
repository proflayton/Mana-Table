using Mana.Client;
using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private DecisionDockView? decisionDock;

    // One local gate owns both automatic advancement and its explanation. A
    // background observation is deliberately absent: it does not change intent.
    private DecisionDockPauseReason AutomaticInteractionPause()
    {
        if (ActivityOpen) return DecisionDockPauseReason.Presentation;
        if (session.NeedsRecovery) return DecisionDockPauseReason.Recovery;
        if (commandFailure && error.Length > 0) return DecisionDockPauseReason.CommandFailure;
        if (readingId != null || HoveredPendingReference() != null) return DecisionDockPauseReason.Inspection;
        if (OverlayOpen) return DecisionDockPauseReason.Overlay;
        if (chatFocus) return DecisionDockPauseReason.Chat;
        if (AbilityActionPending || dragging || pressed != null || selectedCombat != null) return DecisionDockPauseReason.Interaction;
        return DecisionDockPauseReason.None;
    }

    private DecisionDockControlFacts CurrentDecisionControls()
    {
        var state = match!;
        var pause = AutomaticInteractionPause();
        if (pause == DecisionDockPauseReason.None && ExperienceLabOpen) pause = DecisionDockPauseReason.Preview;
        if (pause == DecisionDockPauseReason.None && !matchPresentation.Ready(now)) pause = DecisionDockPauseReason.Presentation;
        return new() {
            Automatic = automatic, Held = held, PauseReason = pause,
            PhaseStopKey = state.ActivePlayerId == state.ViewerId && preferences.Stops.Contains(state.PhaseKey) ? state.PhaseKey : "",
            QuietArmed = QuietPriorityArmed, QuietCanPass = QuietPriorityArmed && QuietPriorityPolicy.CanPass(state, quietYieldToken)
        };
    }

    private void DrawDecisionDock()
    {
        var state = match!; var d = state.Decision;
        decisionDock = decisions.Presentation ?? throw new InvalidOperationException("Decision presentation was not prepared.");
        decisionTextBounds = Rectangle.Empty; decisionText = ""; decisionTextOverflow = false;
        decisionPanelBounds = new(1240, 712, 360, 188);
        if (DrawRecoveryDock(state)) return;
        if (state.Status is "finished" or "error") {
            canvas.Text(state.Result ?? "Game stopped", 1267, 730, Gold, 1.17f, 295, 2, true);
            canvas.Text(state.Error ?? "You can still inspect the final battlefield.", 1267, 767, Muted, .63f, 294, 2);
            if (localMatch || state.Status == "finished") Button("return", IsWorkshopMatch ? "Return to tune" : localMatch ? "Return to setup" : "Return to lobby", 1266, 828, 296, new MatchIntent.ReturnFromMatch(), !busy, true, 47);
            else Button("error-leave", "Leave table", 1266, 828, 296, new MatchIntent.OpenView(MatchView.LeaveTable), !busy, true, 47);
            return;
        }
        if (state.Viewer?.Eliminated == true) {
            canvas.Text("Watching the table", 1266, 735, Muted, .88f, 298, 2, true);
            Button("eliminated-leave", "Leave table", 1266, 828, 296, new MatchIntent.OpenView(MatchView.LeaveTable), !busy, height: 47);
            return;
        }

        DrawPortraitTurnControls();
        if (!SelectingActionTarget && DrawOptionalDecision(state)) return;
        if (d?.Payload is TargetSelectionPayload { Search: not null }) {
            // Legal recipient gestures live on the actual library piles. The
            // engine still receives its current target-player selection.
            decisionPanelBounds = Rectangle.Empty;
            return;
        }
        if (!SelectingActionTarget && decisionDock.Surface == DecisionDockSurface.None) {
            decisionPanelBounds = Rectangle.Empty;
            var ignoredAbilities = PriorityAbilityOptions().Where(ability => ability.IgnoredUntilNextTurn).ToArray();
            if (ignoredAbilities.Length > 0) DrawPriorityAbilityControl(ignoredAbilities);
            return;
        }
        if (!SelectingActionTarget && decisionDock.Surface == DecisionDockSurface.Portrait) {
            DrawPortraitPriority(decisionDock);
            var availableAbilities = PriorityAbilityOptions();
            if (availableAbilities.Length > 0) DrawPriorityAbilityControl(availableAbilities);
            return;
        }

        // Another seat's required choice does not revoke our pass intent. Keep
        // its cancellation beside our portrait while that choice owns the dock.
        if (QuietPriorityArmed && !SelectingActionTarget) DrawPortraitPriority(decisionDock);

        var view = decisionDock;
        bool recipientChoice = !SelectingActionTarget && d?.Payload is EntitySelectionPayload && StateClarity.Recipients(state).Length > 0;
        bool following = busy || d == null;
        var abilities = PriorityAbilityOptions();
        // A source/recipient workspace owns its effect. The dock owns the next
        // gesture. Only unfamiliar required prompts retain the engine's full text.
        string instruction = ValuedTargetDecision || recipientChoice ? "" : view.Instruction;
        var layout = PlanDecisionDock(view, instruction, abilities.Length > 0);
        canvas.Gradient(new(1240, Math.Max(55, decisionPanelBounds.Top - 24), 360, 924 - Math.Max(55, decisionPanelBounds.Top - 24)),
            Color.Transparent, new Color(8, 16, 21) * .95f);
        var accent = view.Status is DecisionDockStatus.Waiting or DecisionDockStatus.Resolving ? Muted
            : view.PauseReason == DecisionDockPauseReason.PhaseStop || view.PauseReason == DecisionDockPauseReason.Held ? Gold : PriorityAccent;
        DrawDecisionRole(view, new(1281, layout.StatusY + layout.StatusHeight / 2), accent);
        canvas.Text(view.Headline, 1304, layout.StatusY, accent, .78f, 258, 80, true);
        if (view.Caption.Length > 0) canvas.Text(view.Caption, 1304, layout.CaptionY, Muted, .68f, 258, 80);
        if (instruction.Length > 0) {
            canvas.Text(instruction, 1266, layout.TextY, Ink, .75f, 295, layout.Lines);
            hits.Add(new("instructions", ScopeFor(new MatchIntent.OpenDecisionHelp()), decisionTextBounds, new MatchIntent.OpenDecisionHelp()));
            if (decisionTextOverflow) Button("decision-read-all", "Read all", 1456, layout.StatusY - 26, 106,
                new MatchIntent.OpenView(MatchView.Help), height: 23);
        }
        if (abilities.Length > 0) DrawPriorityAbilityControl(abilities);

        // Preserve the prompt while its answer is reconciled. Busy state removes
        // actions without adding a competing status panel.
        if (following) return;
        switch (view.Payload) {
            case TargetSelectionPayload target:
                DrawDecisionReplies(target.Complete, target.Cancel); break;
            case EntitySelectionPayload entities when !recipientChoice:
                DrawDecisionReplies(entities.Complete, entities.Cancel, showDisabledComplete: true); break;
            case EntitySelectionPayload: break;
            case OptionsPayload or OrderingPayload when view.RequiredSurface == DecisionRequiredSurface.Table:
                var range = view.Selection!.Range;
                Button("confirm", decisions.Selections.Count == 0 && range.Min == 0 ? "Choose none" : $"Confirm ({decisions.Selections.Count}/{range.Max})", 1266, 799, 296,
                    new MatchIntent.ConfirmSelection(), TableChoices.CanConfirm(state, decisions.Selections), true, 47);
                Button("choicesclear", "Clear", 1459, 852, 103, new MatchIntent.ClearSelection(), decisions.Selections.Count > 0, height: 27); break;
            case PaymentPayload payment: DrawDecisionReplies(payment.Pay, payment.Cancel, showDisabledComplete: true); break;
            case AttackPayload attack: DrawDecisionReplies(attack.Complete, null, showDisabledComplete: true); break;
            case BlockPayload block: DrawDecisionReplies(block.Complete, null, showDisabledComplete: true); break;
            case OptionsPayload or OrderingPayload or RevealPayload or NumberPayload or TextPayload or AllocationPayload:
                Button("open-decision", "Make a choice", 1266, 799, 296, new MatchIntent.OpenDecision(), true, true, 47); break;
            case null or PriorityPayload or OpeningHandPayload or ConfirmationPayload or AbilityChoicePayload: break;
            default: throw new InvalidOperationException("No decision painter owns " + view.Payload.GetType().Name);
        }
    }

    private void DrawDecisionReplies(DecisionReply complete, DecisionReply? cancel, bool showDisabledComplete = false)
    {
        bool showComplete = complete.Enabled || showDisabledComplete;
        if (showComplete) Button("confirm", complete.Label, 1266, 799, 296, new MatchIntent.ConfirmDecision(), !busy && complete.Enabled && CombatGuide.CanConfirm(match!), true, 47);
        if (cancel?.Enabled == true) Button("cancel", cancel.Label, 1459, showComplete ? 852 : 799, 103, new MatchIntent.CancelDecision(), !busy, height: 32);
    }

    private void DrawDecisionRole(DecisionDockView view, Vector2 center, Color accent)
    {
        if (view.ActorId is { } actor && actor != match!.ViewerId) {
            var player = match.Players.FirstOrDefault(player => player.Id == actor);
            var commander = player?.Zone("Command").Cards.FirstOrDefault(card => !card.FaceDown && card.GamePieceType != "effect");
            canvas.Circle(center, 17, accent * .8f);
            canvas.Circle(center, 15, new(25, 38, 41));
            if (commander != null && resources.Card(commander) is { } art) canvas.Portrait(art, center, 14, Color.White);
            else if (seatPortraits.TryGetValue(actor, out var name) && resources.Card(new Card { Name = name, ArtName = name }) is { } portrait)
                canvas.Portrait(portrait, center, 14, Color.White);
            return;
        }
        if (view.PauseReason is DecisionDockPauseReason.PhaseStop or DecisionDockPauseReason.Held) {
            canvas.Rounded(new((int)center.X - 6, (int)center.Y - 8, 4, 16), accent, 1);
            canvas.Rounded(new((int)center.X + 2, (int)center.Y - 8, 4, 16), accent, 1);
        } else if (view.Status == DecisionDockStatus.Target) {
            canvas.Ring(center, 8, accent, 1.5f);
            canvas.Line(center - new Vector2(12, 0), center + new Vector2(12, 0), accent, 1);
            canvas.Line(center - new Vector2(0, 12), center + new Vector2(0, 12), accent, 1);
        } else if (view.Status is DecisionDockStatus.Priority or DecisionDockStatus.AutoPassing) PaintPriorityToken(center, 10, 1);
        else canvas.Ring(center, 9, accent, 2);
    }
}
