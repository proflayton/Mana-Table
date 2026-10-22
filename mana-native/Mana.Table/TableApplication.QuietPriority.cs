using Mana.Client;
using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string quietYieldGame = "", quietYieldToken = "";
    private int quietYieldTurn;
    private int? quietYieldActivePlayer;
    private long quietYieldActivity;
    private bool QuietPriorityArmed => quietYieldToken.Length > 0;
    private bool CanOfferQuietPriority => !ExperienceLabOpen && fixture == null && !session.NeedsRecovery && !held
        && match is { } state && !(state.ActivePlayerId == state.ViewerId && preferences.Stops.Contains(state.PhaseKey))
        && DecisionQuietAdvance.CanPass(state, state.Decision?.QuietYieldToken ?? "");
    private bool QuietPriorityControlVisible => !OverlayOpen && readingId == null && match != null
        && (QuietPriorityArmed && DecisionQuietAdvance.CanWait(match, quietYieldGame, quietYieldTurn, quietYieldActivePlayer, quietYieldToken)
            || CanOfferQuietPriority);
    private int QuietPriorityControlTop => decisionPanelBounds.Top - 66;

    private void ArmQuietPriority()
    {
        // Observation does not change this local intent. The worker waits for
        // its result and rechecks this exact token before submitting any pass.
        if (!CanOfferQuietPriority || busy || match is not { } state) return;
        session.CancelAdvance(); priority.Reset();
        quietYieldGame = state.Id; quietYieldTurn = state.Turn; quietYieldActivePlayer = state.ActivePlayerId;
        quietYieldToken = state.Decision!.QuietYieldToken;
        quietYieldActivity = state.Activity.Select(entry => entry.Id).DefaultIfEmpty().Max();
        error = ""; commandFailure = false;
    }

    private void StopQuietPriority()
    {
        if (!QuietPriorityArmed) return;
        quietYieldGame = quietYieldToken = "";
        quietYieldActivity = 0; session.CancelAdvance(); priority.Reset();
    }

    // True reserves the connection for this explicit policy, including while
    // another seat owns priority. Inspection pauses it without granting a new token.
    private bool UpdateQuietPriority(bool interactionBlocked)
    {
        if (!QuietPriorityArmed) return false;
        if (held || ExperienceLabOpen || fixture != null || session.NeedsRecovery || commandFailure
            || match is not { } state || state.ActivePlayerId == state.ViewerId && preferences.Stops.Contains(state.PhaseKey)
            || !DecisionQuietAdvance.CanWait(state, quietYieldGame, quietYieldTurn, quietYieldActivePlayer, quietYieldToken)
            || DecisionQuietAdvance.HasInterruption(state, quietYieldActivity)) {
            StopQuietPriority(); return false;
        }
        if (interactionBlocked) { session.CancelAdvance(); return true; }
        if (busy || polling || !playback.Ready(now)) return true;
        if (DecisionQuietAdvance.CanPass(state, quietYieldToken)) {
            // Capture the baseline, never replace it with a subsequently issued token.
            string token = quietYieldToken, game = quietYieldGame;
            int turn = quietYieldTurn; int? active = quietYieldActivePlayer;
            long activity = quietYieldActivity; var stops = preferences.Stops.ToHashSet();
            bool Continue(GameSnapshot next) => !(next.ActivePlayerId == next.ViewerId && stops.Contains(next.PhaseKey))
                && DecisionQuietAdvance.CanWait(next, game, turn, active, token)
                && !DecisionQuietAdvance.HasInterruption(next, activity);
            if (session.AdvanceQuietly(token, Continue, Mana.Magic.PresentationPacing.ShouldPresent))
                pendingActionLabel = "Passing quiet priority windows.";
        }
        return true;
    }

    private void DrawQuietPriorityControl()
    {
        if (!QuietPriorityControlVisible) return;
        int top = QuietPriorityControlTop;
        if (QuietPriorityArmed) {
            canvas.Text("Passing quiet windows", 1266, top + 1, Blue, .54f, 296, 1, true);
            PriorityAbilityButton("quiet-priority-stop", "Take control", new Rectangle(1266, top + 26, 296, 28), StopQuietPriority, active: true);
        } else {
            // Keep the control and its pointer state steady during routine
            // observations of the same decision; only commands disable it.
            PriorityAbilityButton("quiet-priority-arm", "Pass for now", new Rectangle(1266, top, 296, 30), ArmQuietPriority, !busy);
            canvas.Text("Stops on the next action", 1266, top + 35, Muted, .49f, 296, 1);
        }
    }
}
