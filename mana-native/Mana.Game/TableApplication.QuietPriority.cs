using Mana.Client;
using Mana.Contracts;
using Mana.Table.Presentation;
using System.Collections.Immutable;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string quietYieldGame = "", quietYieldToken = "";
    private int quietYieldTurn;
    private int? quietYieldActivePlayer;
    private long quietYieldActivity;
    private MatchPresentationLifecycle.QuietStartReading? quietStartReading;
    private readonly PriorityAttention priorityAttention = new();
    private bool QuietPriorityArmed => quietYieldToken.Length > 0;
    private bool CanOfferQuietPriority => !ExperienceLabOpen && fixture == null && !session.NeedsRecovery
        && match is { } state && !(state.ActivePlayerId == state.ViewerId && preferences.Stops.Contains(state.PhaseKey))
        && state.Decision?.Payload is PriorityPayload priority && QuietPriorityPolicy.CanPass(state, priority.QuietYieldToken);

    private void ArmQuietPriority(bool confirmedTurnPass = false)
    {
        // Observation does not change this local intent. The worker waits for
        // its result and rechecks this exact token before submitting any pass.
        if (!CanOfferQuietPriority || busy || match is not { } state) return;
        var command = new PassForNow(state.Id, state.Decision!.Id, ((PriorityPayload)state.Decision.Payload).QuietYieldToken);
        if (!confirmedTurnPass && NeedsFirstLandConfirmation(state, command)) {
            turnPassRequest = new(command, state.ViewerId, state.Turn, state.PhaseKey);
            session.CancelAdvance(); return;
        }
        // Confirmation precedes saved-mode changes. This gesture starts only
        // this current empty-window permission; enabling Auto by itself does not.
        UseAutomaticControl(true);
        CaptureQuietPriority(state);
        quietStartReading = matchPresentation.CaptureQuietStartReading(now);
        error = ""; commandFailure = false;
    }

    private void CaptureQuietPriority(GameSnapshot state)
    {
        quietStartReading = null;
        quietYieldGame = state.Id; quietYieldTurn = state.Turn; quietYieldActivePlayer = state.ActivePlayerId;
        quietYieldToken = ((PriorityPayload)state.Decision!.Payload).QuietYieldToken;
        quietYieldActivity = state.Activity.Select(entry => entry.Id).DefaultIfEmpty().Max();
    }

    private void ArmAutomaticQuietPriority(bool interactionBlocked)
    {
        if (QuietPriorityArmed || ResolvingCapturedStack || !automatic || held || interactionBlocked
            || priorityAttention.NeedsResponse || busy || polling || commandFailure || !matchPresentation.Ready(now)
            || !CanOfferQuietPriority || match is not { } state || state.ActivePlayerId == state.ViewerId) return;
        // Ordinary Auto still uses CanAutoPass. This separate scoped token is
        // consent only for unchanged empty opponent windows, even with an instant.
        CaptureQuietPriority(state);
    }

    private void TakePriorityControl()
    {
        session.ClearAllOptionalPaymentPolicies();
        StopQuietPriority(); StopStackResolution(); StopGroupBlocks(); session.CancelAdvance();
        held = true; priorityAttention.Interrupt();
    }

    private void StopQuietPriority()
    {
        quietStartReading = null;
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
            // A replacement token must not silently start a new Auto batch.
            if (match is { } current && current.Id == quietYieldGame && current.Turn == quietYieldTurn
                && current.ActivePlayerId == quietYieldActivePlayer) priorityAttention.Interrupt();
            StopQuietPriority(); return false;
        }
        if (interactionBlocked) { session.CancelAdvance(); return true; }
        bool ready = quietStartReading is { } reading
            ? matchPresentation.ReadyForQuietStart(now, reading) : matchPresentation.Ready(now);
        if (session.SupportsPriorityContinuation) {
            if (ready || session.PriorityContinuationReserved)
                session.EnsurePriorityContinuation(PriorityContinuationMode.Quiet,
                    preferences.Stops.Order(StringComparer.Ordinal).ToImmutableArray(), quietYieldToken);
            return true;
        }
        if (busy || polling || !ready) return true;
        if (QuietPriorityPolicy.CanPass(state, quietYieldToken)) {
            // Capture the baseline, never replace it with a subsequently issued token.
            string token = quietYieldToken, game = quietYieldGame;
            int turn = quietYieldTurn; int? active = quietYieldActivePlayer;
            long activity = quietYieldActivity; var stops = preferences.Stops.ToHashSet();
            bool Continue(GameSnapshot next) => !(next.ActivePlayerId == next.ViewerId && stops.Contains(next.PhaseKey))
                && QuietPriorityPolicy.CanWait(next, game, turn, active, token)
                && !QuietPriorityPolicy.HasInterruption(next, activity);
            if (session.AdvanceQuietly(token, Continue, Mana.Magic.PresentationPacing.ShouldPresent)) {
                // Consume this startup-only allowance when the serialized
                // advance is admitted. Later batches retain ordinary pacing.
                quietStartReading = null;
                pendingActionLabel = "Passing quiet priority windows.";
            }
        }
        return true;
    }

}
