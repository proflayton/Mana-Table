using Mana.Client;
using Mana.Contracts;
using Mana.Magic;
using System.Collections.Immutable;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string PriorityContinuationCaption => session.PriorityContinuationWithdrawing ? "Taking control…"
        : session.PriorityContinuationPending ? "Starting Auto…"
        : match?.PriorityContinuation is { Status: PriorityContinuationStatus.Held } grant ? grant.ReasonCode switch {
            "observation-overflow" => "Auto paused · Review the table",
            "awaiting-observation" or "awaiting-observation-registration" => "Auto · Waiting for the table",
            _ => ""
        } : "";

    // The server owns routine priority once this one policy is acknowledged.
    // Presentation pauses delivery credit; an actual player interaction withdraws
    // consent through the same serialized session lane as Take control.
    private bool UpdateStandingPriority(bool interactionBlocked)
    {
        if (!session.SupportsPriorityContinuation) return session.PriorityContinuationReserved;
        if (!automatic || held || interactionBlocked || ExperienceLabOpen || fixture != null
            || session.NeedsRecovery || commandFailure || match is not { Status: "playing" or "resolving" } state
            || UndoFacts.CanUndo(state)) {
            session.RevokePriorityContinuation();
            return session.PriorityContinuationReserved;
        }
        if (state.Status == "resolving") return session.PriorityContinuationReserved;
        if (!matchPresentation.Ready(now) && !session.PriorityContinuationReserved) return true;
        return session.EnsurePriorityContinuation(PriorityContinuationMode.NoResponse,
            preferences.Stops.Order(StringComparer.Ordinal).ToImmutableArray());
    }
}
