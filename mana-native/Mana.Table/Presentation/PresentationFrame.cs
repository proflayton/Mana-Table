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
    public FrameStamp Stamp { get; }
    public BoardView BoardView { get; }
    public bool TurnFollowEnabled { get; }

    public PresentationFrame(DrawingFrame drawing, IReadOnlyList<TableHit> hits,
        TableScene? scene, IEnumerable<PresentedCard> cards, int designHeight, FrameStamp stamp,
        BoardView boardView = default, bool turnFollowEnabled = false)
    {
        Drawing = drawing;
        Hits = hits;
        Scene = scene;
        Cards = Array.AsReadOnly(cards.ToArray());
        DesignHeight = designHeight;
        Stamp = stamp;
        BoardView = boardView; TurnFollowEnabled = turnFollowEnabled;
    }

    public FrameObservation Observe() => FrameObservation.From(this);
}

internal sealed record FrameStamp(string? GameId, long Revision, string? DecisionId, string Scope, double Time);
