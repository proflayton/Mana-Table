using Mana.Renderer;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record ReaderDragOrigin(string Scope, string HitId, Pose Reader, Pose HandAnchor);
    private ReaderDragOrigin? readerDragOrigin;
    private bool readerDragExited;

    private void BeginReaderDrag(Hit? source)
    {
        ClearReaderDrag();
        if (source is not { Zone: "Hand" } || source.Scope != Scope
            || cardPresentation.Pinned is not { Focused: true, Zone: "Hand" } entry
            || source.Id != entry.Hit.Id || source.Scope != entry.Hit.Scope) return;
        // Capture what was actually published, not a reader pose that may still
        // be animating. The original hand anchor remains an inspection hit too.
        readerDragOrigin = new(source.Scope, source.Id, entry.Display, entry.Rest);
    }

    private void ObserveReaderDrag(Point point)
    {
        if (readerDragOrigin is { } origin && ReaderDragMatches(origin)
            && !origin.Reader.Contains(point) && !origin.HandAnchor.Contains(point)) readerDragExited = true;
    }

    private bool ReaderDragMatches(ReaderDragOrigin origin)
        => origin.Scope == Scope && dragSource?.Scope == origin.Scope && dragSource.Id == origin.HitId;

    // Being inside the generic battlefield rectangle is insufficient for a
    // reader that started there. An explicit drag must first leave its reader
    // and original hand faces; an ordinary hand drag keeps its existing policy.
    private bool ReaderDragCanCommit => readerDragOrigin is not { } origin
        || ReaderDragMatches(origin) && readerDragExited;

    private void ClearReaderDrag() { readerDragOrigin = null; readerDragExited = false; }
}
