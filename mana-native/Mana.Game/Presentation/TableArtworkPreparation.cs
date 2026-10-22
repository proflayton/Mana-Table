using Mana.Contracts;

namespace Mana.Table.Presentation;

internal readonly record struct ArtworkReadiness(int Total, int Ready, int Unavailable)
{
    internal bool Settled => Ready + Unavailable >= Total;
}

// Presentation owns this handoff, independently of engine command completion.
// The budget starts at the first playable snapshot, not at the start request.
internal sealed class TableArtworkPreparation
{
    internal const double MaximumWaitSeconds = 8;
    private (string Game, int Viewer)? identity;
    private double started = -1;
    private bool completed, openingHandPrepared;
    internal bool Waiting { get; private set; }
    internal bool WaitingForTable { get; private set; }
    internal ArtworkReadiness Progress { get; private set; }

    internal void Update(GameSnapshot? state, Card[] visibleFaces, Func<Card[], ArtworkReadiness> readiness, double now)
    {
        var next = state == null ? ((string Game, int Viewer)?)null : (state.Id, state.ViewerId);
        if (identity != next) {
            identity = next; started = -1; completed = openingHandPrepared = false; Progress = default;
        }
        Waiting = WaitingForTable = false;
        if (state == null) return;
        if (state.Status is "error" or "finished") { completed = true; return; }
        bool hasHand = state.Viewer?.Zone("Hand").Cards.Any(card => !card.FaceDown) == true;
        if (completed) {
            // A pre-deal choice may need the player before an opening hand can
            // exist. Preparing its public cards must not consume that later
            // hand's handoff, or deadlock the choice waiting for a future deal.
            if (openingHandPrepared || !hasHand || state.Turn > 0) return;
            completed = false; started = -1;
        }
        if (state.Status == "starting" || state.Players.Length == 0 || state.Viewer == null) {
            Waiting = WaitingForTable = true;
            return;
        }
        if (started < 0) started = now;
        // Re-evaluate the current seat view while opening. Revoked faces cannot
        // keep loading blocked, and a fresh poll never restarts the deadline.
        Progress = readiness(visibleFaces);
        Waiting = !Progress.Settled && now - started < MaximumWaitSeconds;
        completed = !Waiting;
        if (completed && hasHand) openingHandPrepared = true;
    }
}
