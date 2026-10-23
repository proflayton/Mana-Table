using Mana.Client;
using Mana.Contracts;

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
        && QuietPriorityPolicy.CanPass(state, state.Decision?.QuietYieldToken ?? "");

    private void ArmQuietPriority()
    {
        // Observation does not change this local intent. The worker waits for
        // its result and rechecks this exact token before submitting any pass.
        if (!CanOfferQuietPriority || busy || match is not { } state) return;
        session.CancelAdvance();
        quietYieldGame = state.Id; quietYieldTurn = state.Turn; quietYieldActivePlayer = state.ActivePlayerId;
        quietYieldToken = state.Decision!.QuietYieldToken;
        quietYieldActivity = state.Activity.Select(entry => entry.Id).DefaultIfEmpty().Max();
        error = ""; commandFailure = false;
    }

    private void StopQuietPriority()
    {
        if (!QuietPriorityArmed) return;
        quietYieldGame = quietYieldToken = "";
        quietYieldActivity = 0; session.CancelAdvance();
    }

    // True reserves the connection for this explicit policy, including while
    // another seat owns priority. Inspection pauses it without granting a new token.
    private bool UpdateQuietPriority(bool interactionBlocked)
    {
        if (!QuietPriorityArmed) return false;
        if (held || ExperienceLabOpen || fixture != null || session.NeedsRecovery || commandFailure
            || match is not { } state || state.ActivePlayerId == state.ViewerId && preferences.Stops.Contains(state.PhaseKey)
            || !QuietPriorityPolicy.CanWait(state, quietYieldGame, quietYieldTurn, quietYieldActivePlayer, quietYieldToken)
            || QuietPriorityPolicy.HasInterruption(state, quietYieldActivity)) {
            StopQuietPriority(); return false;
        }
        if (interactionBlocked) { session.CancelAdvance(); return true; }
        if (busy || polling || !playback.Ready(now)) return true;
        if (QuietPriorityPolicy.CanPass(state, quietYieldToken)) {
            // Capture the baseline, never replace it with a subsequently issued token.
            string token = quietYieldToken, game = quietYieldGame;
            int turn = quietYieldTurn; int? active = quietYieldActivePlayer;
            long activity = quietYieldActivity; var stops = preferences.Stops.ToHashSet();
            bool Continue(GameSnapshot next) => !(next.ActivePlayerId == next.ViewerId && stops.Contains(next.PhaseKey))
                && QuietPriorityPolicy.CanWait(next, game, turn, active, token)
                && !QuietPriorityPolicy.HasInterruption(next, activity);
            if (session.AdvanceQuietly(token, Continue, Mana.Magic.PresentationPacing.ShouldPresent))
                pendingActionLabel = "Passing quiet priority windows.";
        }
        return true;
    }

}
