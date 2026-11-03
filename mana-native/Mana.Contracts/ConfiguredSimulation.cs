using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

// A host-private qualification plan and its actual rules-player bindings. These
// facts neither enroll a live party nor grant any automated action authority.
public sealed record ConfiguredSimulationSetup(
    [property: JsonRequired] MatchSetupSpecification Specification,
    [property: JsonRequired] ImmutableArray<string> Decks);
public sealed record ConfiguredSimulationParticipant([property: JsonRequired] int Index,
    [property: JsonRequired] int PlayerId);
public sealed record ConfiguredSimulationStart(ConfiguredSimulationSetup Setup,
    ImmutableArray<ConfiguredSimulationParticipant> Participants, GameSnapshot State);

public static class ConfiguredSimulationFacts
{
    private static readonly MatchSetupCapabilities Capabilities = new(
        [MatchRulesets.FreeForAllCommander, MatchRulesets.CooperativeCommander], 4, 2, 2);

    public static void ValidateSetup(ConfiguredSimulationSetup setup)
    {
        if (setup == null || setup.Specification == null || setup.Decks.IsDefault || setup.Decks.Length != 4
            || setup.Decks.Any(deck => string.IsNullOrWhiteSpace(deck) || deck.Length > 200)
            || setup.Specification.Seats.IsDefault || setup.Specification.Seats.Length != 4) throw Invalid();
        MatchSetupFacts.Validate(setup.Specification, Capabilities);
        if (setup.Specification.RulesetId == MatchRulesets.CooperativeCommander
            && (setup.Specification.Seats.Count(seat => seat.Role == MatchSeatRole.Party) != 2
                || setup.Specification.Seats.Count(seat => seat.Role == MatchSeatRole.Opposition) != 2)) throw Invalid();
    }

    public static void ValidateReceipt(ConfiguredSimulationStart start, ConfiguredSimulationSetup expected)
    {
        ValidateSetup(expected);
        ValidateBindings(start);
        if (start.Setup.Specification.RulesetId != expected.Specification.RulesetId
            || !start.Setup.Specification.Seats.SequenceEqual(expected.Specification.Seats)
            || !start.Setup.Decks.SequenceEqual(expected.Decks, StringComparer.Ordinal)) throw Invalid();
        _ = IsReady(start, start.State);
    }

    // An actual starting frame may precede the roster. It cannot supply guessed
    // players, teams or policy authority. Every later frame must keep the map.
    public static bool IsReady(ConfiguredSimulationStart start, GameSnapshot state)
    {
        ValidateBindings(start);
        ValidateFrameIdentity(start, state);
        ValidatePublic(state);
        if (state.Status == "starting") {
            if (!state.Players.IsEmpty) ValidateRoster(start, state);
            return false;
        }
        ValidateRoster(start, state);
        return true;
    }

    public static void ValidateState(ConfiguredSimulationStart start, GameSnapshot state)
    {
        if (!IsReady(start, state)) throw Invalid();
    }

    // A bot receives its own authorized observation, never a public frame with
    // a changed viewer ID. Ally private information remains a separate privacy gate.
    public static void ValidateOwnedState(ConfiguredSimulationStart start, GameSnapshot state)
    {
        ValidateBindings(start);
        ValidateFrameIdentity(start, state);
        ValidateRoster(start, state);
        if (!start.Participants.Any(participant => participant.PlayerId == state.ViewerId)) throw Invalid();
    }

    private static void ValidateBindings(ConfiguredSimulationStart start)
    {
        if (start == null || start.State == null) throw Invalid();
        ValidateSetup(start.Setup);
        if (start.Participants.IsDefault || start.Participants.Length != 4
            || start.Participants.Any(participant => participant == null || participant.PlayerId < 0)
            || !start.Participants.Select(participant => participant.Index).Order().SequenceEqual([0, 1, 2, 3])
            || start.Participants.Select(participant => participant.PlayerId).Distinct().Count() != 4) throw Invalid();
    }

    private static void ValidateFrameIdentity(ConfiguredSimulationStart start, GameSnapshot state)
    {
        if (state == null || string.IsNullOrWhiteSpace(start.State.Id) || state.Id != start.State.Id
            || state.Status is not ("starting" or "playing" or "resolving" or "finished")
            || state.Error != null || state.Encounter != null || state.Players.IsDefault) throw Invalid();
        MatchOutcomeFacts.Validate(state);
    }

    private static void ValidateRoster(ConfiguredSimulationStart start, GameSnapshot state)
    {
        if (state.Players.Length != 4 || state.Players.Any(player => player == null)
            || state.Players.Select(player => player.Id).Distinct().Count() != 4) throw Invalid();
        foreach (var participant in start.Participants) {
            var player = state.Players.SingleOrDefault(player => player.Id == participant.PlayerId);
            if (player == null || player.TeamId != start.Setup.Specification.Seats[participant.Index].TeamId) throw Invalid();
        }
    }

    private static void ValidatePublic(GameSnapshot state)
    {
        if (state.ViewerId != -1 || state.Decision != null || !state.ActionSources.IsEmpty || !state.PlayAccesses.IsEmpty
            || !state.Activity.IsEmpty || !state.Notices.IsEmpty
            || state.Players.Any(player => player == null || player.Zones.IsDefault
                || player.Zones.Any(zone => zone.Name is "Hand" or "Library" && (!zone.Cards.IsEmpty || zone.TopCard != null)))
            || state.Players.SelectMany(player => player.Zones).SelectMany(zone => zone.Cards)
                .Concat(state.Stack.Select(item => item.Card).OfType<Card>())
                .Any(card => card.Key.Length > 0 || card.Selectable || card.Highlighted)) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("Invalid configured simulation facts.");
}
