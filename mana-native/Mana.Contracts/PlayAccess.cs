using System.Collections.Immutable;

namespace Mana.Contracts;

public enum PlayAccessKind { Cast, PlayLand }

// A viewer's retained permission to play a disclosed occurrence. This is
// presentation state, never an action handle or proof of mana affordability.
public sealed record PlayAccessRoute
{
    public string Id { get; }
    public PlayAccessKind Kind { get; }
    public string SourceCardId { get; }
    public string SourceName { get; }
    public string Detail { get; }
    public bool CanStart { get; init; }

    public PlayAccessRoute(string Id, PlayAccessKind Kind, string SourceCardId,
        string SourceName, string Detail, bool CanStart)
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new ArgumentException("Play access requires a route identity.", nameof(Id));
        if (!Enum.IsDefined(Kind)) throw new ArgumentOutOfRangeException(nameof(Kind));
        this.Id = Id;
        this.Kind = Kind;
        this.SourceCardId = SourceCardId ?? "";
        this.SourceName = SourceName ?? "";
        this.Detail = Detail ?? "";
        this.CanStart = CanStart;
    }
}

// PlayerId identifies the physical zone owner, which need not be the viewer.
// The face and any current source key come from that same snapshot's zone.
public sealed record PlayAccess
{
    public int PlayerId { get; }
    public string Zone { get; }
    public string CardId { get; }
    public ImmutableArray<PlayAccessRoute> Routes { get; }

    public PlayAccess(int PlayerId, string Zone, string CardId, ImmutableArray<PlayAccessRoute> Routes)
    {
        if (PlayerId < 0) throw new ArgumentOutOfRangeException(nameof(PlayerId));
        if (string.IsNullOrWhiteSpace(Zone)) throw new ArgumentException("Play access requires its source zone.", nameof(Zone));
        if (string.IsNullOrWhiteSpace(CardId)) throw new ArgumentException("Play access requires a disclosed occurrence.", nameof(CardId));
        if (Routes.IsDefaultOrEmpty || Routes.Any(route => route == null)
            || Routes.Select(route => route.Id).Distinct(StringComparer.Ordinal).Count() != Routes.Length)
            throw new ArgumentException("Play access requires distinct engine routes.", nameof(Routes));
        this.PlayerId = PlayerId;
        this.Zone = Zone;
        this.CardId = CardId;
        this.Routes = Routes;
    }
}
