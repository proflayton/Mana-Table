using Mana.Renderer;
using Mana.Table.Interaction;

namespace Mana.Table.Presentation;

/// <summary>The drawing and interaction observations published for one completed composition.</summary>
internal sealed class PresentationFrame
{
    public DrawingFrame Drawing { get; }
    public IReadOnlyList<TableHit> Hits { get; }
    public IReadOnlyList<PresentedCard> Cards { get; }
    public TableScene? Scene { get; }
    public int DesignHeight { get; }

    public PresentationFrame(DrawingFrame drawing, IReadOnlyList<TableHit> hits,
        TableScene? scene, IEnumerable<PresentedCard> cards, int designHeight)
    {
        Drawing = drawing;
        Hits = hits;
        Scene = scene;
        Cards = Array.AsReadOnly(cards.ToArray());
        DesignHeight = designHeight;
    }
}
