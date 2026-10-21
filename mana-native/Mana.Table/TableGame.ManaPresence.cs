using Mana.Contracts;
using Mana.Renderer;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private readonly ManaPoolPresence manaPresence = new();

    private void ObserveMana(GameSnapshot? previous, GameSnapshot? current)
    {
        if (current == null) manaPresence.Clear();
        else manaPresence.Observe(previous, current, now);
    }
}
