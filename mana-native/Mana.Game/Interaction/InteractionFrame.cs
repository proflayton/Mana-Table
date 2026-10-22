using System.Collections;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table.Interaction;

// Draw composes one ordered set of input regions. Update consumes only a
// completed frame; opening the next composition cannot change that snapshot.
internal sealed class InteractionFrame : IReadOnlyList<TableHit>
{
    private readonly List<TableHit> building = [];
    private IReadOnlyList<TableHit> published = Array.AsReadOnly(Array.Empty<TableHit>());

    internal bool IsBuilding { get; private set; }
    internal IReadOnlyList<TableHit> Published => published;
    private IReadOnlyList<TableHit> Active => IsBuilding ? building : published;

    public int Count => Active.Count;
    public TableHit this[int index]
    {
        get => Active[index];
        set { RequireBuilding(); building[index] = value; }
    }

    internal void Begin()
    {
        if (IsBuilding) throw new InvalidOperationException("The interaction frame is already being composed.");
        building.Clear();
        IsBuilding = true;
    }

    internal void Publish()
    {
        RequireBuilding();
        published = Array.AsReadOnly(building.ToArray());
        IsBuilding = false;
    }

    internal void Add(TableHit hit) { RequireBuilding(); building.Add(hit); }
    internal void Insert(int index, TableHit hit) { RequireBuilding(); building.Insert(index, hit); }
    internal void Clear() { RequireBuilding(); building.Clear(); }
    internal void ClearForModal() => Clear();
    internal int FindLastIndex(Predicate<TableHit> match)
    {
        for (int index = Count - 1; index >= 0; index--) if (match(this[index])) return index;
        return -1;
    }
    internal int RemoveAll(Predicate<TableHit> match) { RequireBuilding(); return building.RemoveAll(match); }
    internal int RemoveIntersections(Rectangle bounds) => RemoveAll(hit => hit.Bounds.Intersects(bounds));

    internal TableHit? FindTop(Point point, Predicate<TableHit>? accept = null)
    {
        for (int index = Count - 1; index >= 0; index--) {
            var hit = this[index];
            if (hit.Contains(point) && (accept?.Invoke(hit) ?? true)) return hit;
        }
        return null;
    }

    internal TableHit? FindTop(Predicate<TableHit> accept)
    {
        int index = FindLastIndex(accept);
        return index < 0 ? null : this[index];
    }

    internal bool ExtendCanonical(string id, string scope, Rectangle bounds)
    {
        RequireBuilding();
        int index = building.FindLastIndex(hit => hit.Id == id && hit.Scope == scope);
        if (index < 0) return false;
        var face = building[index];
        building[index] = face with {
            Bounds = Rectangle.Union(face.Bounds, bounds),
            Shape = point => face.Contains(point) || bounds.Contains(point)
        };
        return true;
    }

    internal bool PromoteCanonical(string id, string scope, TableHit? fallback = null)
    {
        RequireBuilding();
        int index = building.FindLastIndex(hit => hit.Id == id && hit.Scope == scope);
        var hit = index < 0 ? fallback : building[index];
        if (hit == null) return false;
        if (hit.Id != id || hit.Scope != scope)
            throw new ArgumentException("The fallback must identify the promoted occurrence.", nameof(fallback));
        building.RemoveAll(candidate => candidate.Id == id && candidate.Scope == scope);
        building.Add(hit);
        return true;
    }

    public IEnumerator<TableHit> GetEnumerator() => Active.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void RequireBuilding()
    {
        if (!IsBuilding) throw new InvalidOperationException("Begin an interaction frame before changing its regions.");
    }
}
