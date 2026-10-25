using Mana.Contracts;
using Mana.Magic;

namespace Mana.Table.Presentation;

// Permissions for reading are prepared by the same decision presentation owner
// that selects the workspace. They never carry an engine command.
internal readonly record struct DecisionReadingPolicy(DecisionDockView? View)
{
    internal bool Allows(DecisionReading permission) => (View?.Reading & permission) == permission;
    internal Card? ChoiceFace(GameSnapshot? state, int slot)
    {
        if (state == null) return null;
        if (View?.Payload is RevealPayload reveal)
            return reveal.Cards.ElementAtOrDefault(slot) is { } disclosed ? CardViews.Disclosed(state, disclosed) : null;
        if (View?.Selection is not { } selection) return null;
        if (!selection.Disclosures.IsEmpty)
            return selection.Disclosures.ElementAtOrDefault(slot) is { } disclosed ? CardViews.Disclosed(state, disclosed) : null;
        return selection.Choices.ElementAtOrDefault(slot) is { } choice ? CardViews.Choice(state, choice) : null;
    }
}
