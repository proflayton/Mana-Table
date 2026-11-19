using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

public enum LibraryAcquisitionStage { Direct, SelectBundle, DistributeBundle }

/// <summary>A nominal destination bound, not a resolved zone-change receipt.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LibraryAcquisitionDestination
{
    public string Destination { get; }
    public int MaximumCount { get; }

    [JsonConstructor]
    public LibraryAcquisitionDestination(string destination, int maximumCount)
    {
        if (destination is not ("Hand" or "Battlefield") || maximumCount is < 1 or > 2)
            throw new ArgumentException("A supported acquisition destination is Hand or Battlefield with a bound of one or two cards.");
        Destination = destination; MaximumCount = maximumCount;
    }
}

/// <summary>
/// Read-only facts from the actual rules-owned library choice. Current option
/// indices alone authorize selection; destinations are contingent on resolution.
/// Missing facts never imply a free or beneficial acquisition.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LibraryAcquisitionContext
{
    public int ActorPlayerId { get; }
    public int LibraryOwnerId { get; }
    public int RecipientPlayerId { get; }
    public LibraryAcquisitionStage Stage { get; }
    public string CurrentDestination { get; }
    public bool AdditionalCostKnownAbsent { get; }
    public ImmutableArray<LibraryAcquisitionDestination> PlannedDestinations { get; }

    [JsonConstructor]
    public LibraryAcquisitionContext(int actorPlayerId, int libraryOwnerId, int recipientPlayerId,
        LibraryAcquisitionStage stage, string currentDestination, bool additionalCostKnownAbsent,
        ImmutableArray<LibraryAcquisitionDestination> plannedDestinations)
    {
        if (actorPlayerId < 0 || libraryOwnerId != actorPlayerId || recipientPlayerId != actorPlayerId
            || !Enum.IsDefined(stage) || !additionalCostKnownAbsent
            || plannedDestinations.IsDefaultOrEmpty || plannedDestinations.Length > 2
            || plannedDestinations.Any(destination => destination == null)
            || plannedDestinations.Select(destination => destination.Destination).Distinct(StringComparer.Ordinal).Count() != plannedDestinations.Length)
            throw new ArgumentException("A supported acquisition identifies one acting library owner and recipient with known absent additional costs.");
        bool single = plannedDestinations.Length == 1 && plannedDestinations[0].Destination == currentDestination;
        bool valid = stage switch {
            LibraryAcquisitionStage.Direct => single && currentDestination is "Hand" or "Battlefield",
            LibraryAcquisitionStage.SelectBundle => currentDestination == "Library" && plannedDestinations.Length == 2
                && plannedDestinations.All(destination => destination.MaximumCount == 1)
                && plannedDestinations.Any(destination => destination.Destination == "Hand")
                && plannedDestinations.Any(destination => destination.Destination == "Battlefield"),
            LibraryAcquisitionStage.DistributeBundle => single && currentDestination is "Hand" or "Battlefield"
                && plannedDestinations[0].MaximumCount == 1,
            _ => false
        };
        if (!valid) throw new ArgumentException("The acquisition stage must match its supported current and planned destinations.");
        ActorPlayerId = actorPlayerId; LibraryOwnerId = libraryOwnerId; RecipientPlayerId = recipientPlayerId;
        Stage = stage; CurrentDestination = currentDestination; AdditionalCostKnownAbsent = additionalCostKnownAbsent;
        PlannedDestinations = plannedDestinations;
    }
}
