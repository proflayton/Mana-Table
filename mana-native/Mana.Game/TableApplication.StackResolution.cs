using Mana.Client;
using Mana.Contracts;
using Mana.Magic;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private StackResolutionIntent? stackResolutionIntent;
    private GameSnapshot? stackEligibilitySnapshot;
    private bool cachedStackEligibility;
    private bool ResolvingCapturedStack => stackResolutionIntent?.IsActive == true;
    private bool CanOfferStackResolution
    {
        get {
            if (!ReferenceEquals(stackEligibilitySnapshot, match)) {
                stackEligibilitySnapshot = match;
                cachedStackEligibility = match is { Stack.Length: > 1 } state && StackResolutionIntent.TryArm(state) != null;
            }
            return fixture == null && !session.NeedsRecovery && cachedStackEligibility;
        }
    }

    private void ArmStackResolution()
    {
        if (!CanOfferStackResolution || busy || match == null) return;
        StopQuietPriority(); session.CancelAdvance();
        held = false; priorityAttention.Resume();
        stackResolutionIntent = StackResolutionIntent.TryArm(match);
        error = ""; commandFailure = false;
    }

    private void StopStackResolution()
    {
        if (stackResolutionIntent == null) return;
        stackResolutionIntent.Stop(); stackResolutionIntent = null; session.CancelAdvance();
    }

    private bool UpdateStackResolution(bool interactionBlocked)
    {
        if (!ResolvingCapturedStack) return false;
        if (held || fixture != null || session.NeedsRecovery || commandFailure || match == null
            || !stackResolutionIntent!.CanContinue(match)) { StopStackResolution(); return false; }
        // Readable public beats and inspection pause the local continuation,
        // without stopping the authoritative game clock or retaining old input.
        if (interactionBlocked || !matchPresentation.ReadyForStack(now)) { session.CancelAdvance(); return true; }
        if (!busy && !polling && session.AdvanceStack(stackResolutionIntent, PresentationPacing.ShouldPresent))
            pendingActionLabel = "Resolving the captured stack.";
        return true;
    }
}
