using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Magic;

public sealed record TurnPhaseMarker(string Key, string Label, bool Current, bool Stop, string Hint);
public sealed record TurnProgressView(int? ActivePlayerId, bool IsOwnTurn, string PhaseLabel, ImmutableArray<TurnPhaseMarker> Markers)
{
    public string PhaseKey { get; init; } = "";
}

// Progress groups describe the current turn. Their saved stops still belong to
// the exact TurnGuide keys; displaying a group never broadens passing policy.
public static class TurnProgressPresentation
{
    public static TurnProgressView From(GameSnapshot state, IReadOnlySet<string> stops)
    {
        bool ownTurn = state.ActivePlayerId == state.ViewerId;
        string current = state.PhaseKey switch {
            "UNTAP" => "UPKEEP",
            "COMBAT_BEGIN" or "COMBAT_DECLARE_ATTACKERS" or "COMBAT_DECLARE_BLOCKERS"
                or "COMBAT_FIRST_STRIKE_DAMAGE" or "COMBAT_DAMAGE" or "COMBAT_END" => "COMBAT_BEGIN",
            "CLEANUP" => "END_OF_TURN",
            "UPKEEP" or "DRAW" or "MAIN1" or "MAIN2" or "END_OF_TURN" => state.PhaseKey,
            _ => ""
        };
        string phase = state.PhaseKey switch {
            "UNTAP" => "Untap step", "UPKEEP" => "Upkeep", "DRAW" => "Draw step",
            "MAIN1" => "Main phase", "MAIN2" => "Second main phase",
            "COMBAT_BEGIN" => "Beginning of combat", "COMBAT_DECLARE_ATTACKERS" => "Declare attackers",
            "COMBAT_DECLARE_BLOCKERS" => "Declare blockers", "COMBAT_FIRST_STRIKE_DAMAGE" => "First-strike damage",
            "COMBAT_DAMAGE" => "Combat damage", "COMBAT_END" => "End of combat",
            "END_OF_TURN" => "End step", "CLEANUP" => "Cleanup",
            _ => state.Phase
        };
        var markers = TurnGuide.Stops.Select(stop => {
            bool saved = stops.Contains(stop.Key);
            string hint = stop.Key switch {
                "UPKEEP" => saved ? "Remove my upkeep stop" : "Stop on my upkeep",
                "DRAW" => saved ? "Remove my draw-step stop" : "Stop on my draw step",
                "MAIN1" => saved ? "Remove my first-main-phase stop" : "Stop on my first main phase",
                "COMBAT_BEGIN" => saved ? "Remove my beginning-of-combat stop" : "Stop at the beginning of my combat",
                "MAIN2" => saved ? "Remove my second-main-phase stop" : "Stop on my second main phase",
                "END_OF_TURN" => saved ? "Remove my end-step stop" : "Stop on my end step",
                _ => ""
            };
            return new TurnPhaseMarker(stop.Key, stop.Key == "END_OF_TURN" ? "End step" : stop.Label,
                ownTurn && stop.Key == current, saved, hint);
        }).ToImmutableArray();
        return new(state.ActivePlayerId, ownTurn, phase, markers) { PhaseKey = current };
    }
}
