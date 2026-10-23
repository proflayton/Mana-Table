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
        if (pause == DecisionDockPauseReason.None && !playback.Ready(now)) pause = DecisionDockPauseReason.Presentation;
        return new() {
            Automatic = automatic, Held = held, PauseReason = pause,
            PhaseStopKey = state.ActivePlayerId == state.ViewerId && preferences.Stops.Contains(state.PhaseKey) ? state.PhaseKey : "",
            QuietArmed = QuietPriorityArmed, QuietCanPass = QuietPriorityArmed && DecisionQuietAdvance.CanPass(state, quietYieldToken),
            AutomaticAdvanceInFlight = session.IsAdvancing, CommandBusy = busy, ObservationPending = polling
        };
    }

    private void DrawDecisionDock()
    {
        var state = match!; var d = state.Decision;
        decisionDock = DecisionDockPresentation.From(state, CurrentDecisionControls());
        decisionTextBounds = Rectangle.Empty; decisionText = ""; decisionTextOverflow = false;
        decisionPanelBounds = new(1240, 712, 360, 188);
        if (DrawRecoveryDock(state)) return;
        if (state.Status is "finished" or "error") {
            canvas.Text(state.Result ?? "Game stopped", 1267, 730, Gold, 1.17f, 295, 2, true);
            canvas.Text(state.Error ?? "You can still inspect the final battlefield.", 1267, 767, Muted, .63f, 294, 2);
            if (localMatch || state.Status == "finished") Button("return", IsWorkshopMatch ? "Return to tune" : localMatch ? "Return to setup" : "Return to lobby", 1266, 828, 296, ReturnFromMatch, !busy, true, 47);
            else Button("error-leave", "Leave table", 1266, 828, 296, () => overlay = "leave-table", !busy, true, 47);
            return;
        }
        if (state.Viewer?.Eliminated == true) {
            canvas.Text("Watching the table", 1266, 735, Muted, .88f, 298, 2, true);
            Button("eliminated-leave", "Leave table", 1266, 828, 296, () => overlay = "leave-table", !busy, height: 47);
            return;
        }

        var view = decisionDock;
        bool recipientChoice = !SelectingActionTarget && d?.Intent == DecisionIntent.Selection && StateClarity.Recipients(state).Length > 0;
        bool following = busy || d == null || view.Status == DecisionDockStatus.AutoPassing;
        var abilities = PriorityAbilityOptions();
        // A source/recipient workspace owns its effect. The dock owns the next
        // gesture. Only unfamiliar required prompts retain the engine's full text.
        string instruction = ValuedTargetDecision || recipientChoice ? "" : view.Instruction;
        var layout = PlanDecisionDock(view, instruction, abilities.Length > 0);
        canvas.Gradient(new(1240, Math.Max(55, decisionPanelBounds.Top - 24), 360, 924 - Math.Max(55, decisionPanelBounds.Top - 24)),
            Color.Transparent, new Color(8, 16, 21) * .95f);
        var accent = view.Status is DecisionDockStatus.Waiting or DecisionDockStatus.Resolving or DecisionDockStatus.Submitted ? Muted
            : view.PauseReason == DecisionDockPauseReason.PhaseStop || view.PauseReason == DecisionDockPauseReason.Held ? Gold : PriorityAccent;
        DrawDecisionRole(view, new(1281, layout.StatusY + layout.StatusHeight / 2), accent);
        canvas.Text(view.Headline, 1304, layout.StatusY, accent, .78f, 258, 80, true);
        if (view.Caption.Length > 0) {
            if (view.PauseReason == DecisionDockPauseReason.PhaseStop && d?.Intent == DecisionIntent.Priority) {
                int width = Math.Min(262, canvas.TextWidth(view.Caption, .68f, true) + 37);
                var bounds = new Rectangle(1300, layout.CaptionY - 3, width, layout.CaptionHeight + 6);
                string id = "resume-phase-stop", key = state.PhaseKey;
                PaintControlSurface(id, bounds, new(44, 40, 25), Gold * .45f);
                canvas.Text(view.Caption, 1307, layout.CaptionY, Gold, .68f, width - 30, 80, true);
                canvas.Text("×", bounds.Right - 17, layout.CaptionY, IsControlHovered(id) ? Ink : Gold, .68f, 16, 1, true);
                hits.Add(new(id, ControlScope(id), bounds, () => { preferences.Stops.Remove(key); SavePreferences(); }));
            } else canvas.Text(view.Caption, 1304, layout.CaptionY, Muted, .68f, 258, 80);
        }
        if (instruction.Length > 0) {
            canvas.Text(instruction, 1266, layout.TextY, Ink, .75f, 295, layout.Lines);
            hits.Add(new("instructions", ControlScope("instructions"), decisionTextBounds, () => { overlay = "help"; inspectPage = 0; }));
            if (decisionTextOverflow) Button("decision-read-all", "Read all", 1456, layout.StatusY - 26, 106,
                () => { overlay = "help"; inspectPage = 0; }, height: 23);
        }
        if (abilities.Length > 0) DrawPriorityAbilityControl(abilities);

        bool quietPrimary = !SelectingActionTarget && !recipientChoice && (QuietPriorityArmed || !following && CanOfferQuietPriority);
        if (quietPrimary) {
            // The bounded intent is one explicit action, rather than a second
            // full-size pass button floating above the same decision.
            Button(QuietPriorityArmed ? "quiet-priority-stop" : "quiet-priority-arm", QuietPriorityArmed ? "Take control" : "Pass for now",
                1266, 799, 296, QuietPriorityArmed ? StopQuietPriority : ArmQuietPriority, !busy || QuietPriorityArmed, true, 47);
            if (!busy && d is { Intent: DecisionIntent.Priority, OkEnabled: true })
                PriorityAbilityButton("confirm", "Pass once", new(1459, 852, 103, 27), () => Send(new PassPriority(state.Id, d.Id)));
        } else if (following) {
            // Waiting is a state, not a disabled action button. Its public actor
            // is named above and at their portrait; leave the battlefield quiet.
            if (busy || view.Status == DecisionDockStatus.AutoPassing) {
                var origin = new Vector2(1274, 823);
                for (int i = 0; i < 3; i++) {
                    float alpha = preferences.ReducedMotion ? .55f : .3f + .6f * (.5f + .5f * MathF.Sin((float)now * 3 - i));
                    canvas.Circle(origin + new Vector2(i * 10, 0), 2, accent * alpha);
                }
            }
        } else if (SelectingActionTarget) {
            if (d!.OkEnabled) Button("confirm", d.Ok, 1266, 799, 296, () => Send(DecisionCommands.Confirm(state)), !busy, true, 47);
            if (d.CancelEnabled) Button("cancel", d.Cancel, 1459, d.OkEnabled ? 852 : 799, 103, () => Send(DecisionCommands.Cancel(state)), !busy, height: 32);
        } else if (recipientChoice) {
            // The named source/portrait targets own this required choice.
        } else if (TableChoice) {
            Button("confirm", selections.Count == 0 && d!.Min == 0 ? "Choose none" : $"Confirm ({selections.Count}/{d!.Max})", 1266, 799, 296,
                () => Send(DecisionCommands.Choose(state, selections)), TableChoices.CanConfirm(state, selections), true, 47);
            Button("choicesclear", "Clear", 1459, 852, 103, () => selections.Clear(), selections.Count > 0, height: 27);
        } else if (d?.Kind == "input") {
            Button("confirm", view.ActionLabel, 1266, 799, 296, () => Send(DecisionCommands.Confirm(state)), CombatGuide.CanConfirm(state), true, 47);
            if (d.CancelEnabled) Button("cancel", d.Intent == DecisionIntent.Priority && d.Cancel == "End Turn" ? "Pass turn" : d.Cancel,
                1459, 852, 103, () => Send(DecisionCommands.Cancel(state)), height: 27);
        } else if (d != null) Button("open-decision", "Make a choice", 1266, 799, 296, () => collapsedDecision = false, true, true, 47);
        if (!SelectingActionTarget) {
            Button("auto", automatic ? "Auto" : preferences.TextScale > 1.1f ? "Manual" : "Full control", 1266, 852, 111, () => UseAutomaticControl(!automatic), true, automatic, 27);
            Button("hold", held ? "Held" : "Hold", 1383, 852, 70, ToggleHoldControl, true, held, 27);
        }
        DrawPhaseStrip();
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
