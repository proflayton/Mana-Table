using Mana.Contracts;

namespace Mana.Magic;

/// <summary>Dispatches each engine-authorized automatic pass once, without a presentation delay.</summary>
public sealed class AutoPriority
{
    private string? submitted;
    public void Reset() { submitted = null; }
    public DecisionReply? Update(GameSnapshot? state, bool automatic, bool held, IReadOnlySet<string> stops,
        bool interactionBlocked, bool transportBusy)
    {
        if (state == null || transportBusy || !TurnGuide.CanAutoPass(state, automatic, held, stops, interactionBlocked)) return null;
        string current = state.Id + ":" + state.Decision!.Id;
        if (submitted == current) return null;
        submitted = current;
        return new(state.Id, state.Decision.Id, ReplyAction.AutoPass);
    }
}
