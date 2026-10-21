using Mana.Contracts;
using Mana.Renderer;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly ManaPoolPresence manaPresence = new();

    private void ObserveMana(GameSnapshot? previous, GameSnapshot? current)
    {
        if (current == null) manaPresence.Clear();
        else manaPresence.Observe(previous, current, now);
    }
}
