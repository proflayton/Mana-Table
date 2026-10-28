using Mana.Client;
using Mana.Contracts;

namespace Mana.Table.Presentation;

// Local consent to skip quiet windows, never authority to answer a decision.
// Observe before input: acknowledging one displayed response cannot acknowledge
// a cast or trigger which arrives while that response is in flight.
internal sealed class PriorityAttention
{
    private string? game, token, required;
    private int viewer, turn;
    private int? active;
    private long activity;
    private long? interruption;
    internal bool NeedsResponse { get; private set; }

    internal void Observe(GameSnapshot? state)
    {
        if (state == null || game != state.Id || viewer != state.ViewerId) {
            game = state?.Id; viewer = state?.ViewerId ?? 0;
            turn = state?.Turn ?? 0; active = state?.ActivePlayerId;
            activity = LatestActivity(state); interruption = state?.StackInterruptionVersion;
            token = null; required = null;
            NeedsResponse = state?.Stack.Length > 0;
        }
        if (state == null) return;
        if (turn != state.Turn || active != state.ActivePlayerId) {
            turn = state.Turn; active = state.ActivePlayerId;
            token = null; required = null; NeedsResponse = false;
        }
        // The provider classifies routine history; a turn boundary independently
        // retires the old wait. Preserve real actions arriving in that same poll.
        if (QuietPriorityPolicy.HasInterruption(state, activity)
            || interruption is { } previous && state.StackInterruptionVersion is { } current && previous != current)
            NeedsResponse = true;
        var choice = state.Decision is { Payload: not PriorityPayload } decision ? decision.Id : null;
        if (choice != null && choice != required) NeedsResponse = true;
        required = choice;
        if (state.Decision?.Payload is PriorityPayload { QuietYieldToken.Length: > 0 } priority) {
            if (token != null && token != priority.QuietYieldToken) NeedsResponse = true;
            token = priority.QuietYieldToken;
        }
        activity = Math.Max(activity, LatestActivity(state));
        interruption = state.StackInterruptionVersion;
    }

    internal void Acknowledge() => NeedsResponse = false;
    internal void Interrupt() => NeedsResponse = true;
    private static long LatestActivity(GameSnapshot? state) => state?.Activity.Select(entry => entry.Id).DefaultIfEmpty().Max() ?? 0;
}
