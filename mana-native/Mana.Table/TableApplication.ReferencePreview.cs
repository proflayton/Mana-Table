using Mana.Contracts;
using Mana.Renderer;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    // The caller owns current visibility, position and hover lifetime. This is
    // only drawing: the canonical occurrence and all of its input stay in place.
    private void PaintReferencePreview(Card card, Pose pose)
        => PaintInteractionCard(card, pose, selected: false, legal: false);
}
