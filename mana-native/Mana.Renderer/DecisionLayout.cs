using Mana.Contracts;

namespace Mana.Renderer;

// Shared by table reservations and the game painter. Source context uses the
// existing occurrence; only real stack items retain their stack-area footprint.
public static class DecisionLayout
{
    public static bool UsesSourceWorkspace(Decision? decision) => decision is {
        Task: DecisionTask.OpeningHand or DecisionTask.Confirmation
    } || decision?.LibrarySearch != null;
}
