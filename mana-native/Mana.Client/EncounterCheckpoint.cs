using Mana.Contracts;

namespace Mana.Client;

public enum EncounterCheckpointStatus { NotEncounter, Unknown, InProgress, NotCompleted, Pending, Failed, Committed }

// A read model of provider evidence, not a save operation or permission to
// advance. A global rules outcome and its durable receipt are separate facts.
public static class EncounterCheckpoint
{
    public static EncounterCheckpointStatus For(GameSnapshot state, PartyProgress? progress)
    {
        if (state.Encounter is not { } identity) return EncounterCheckpointStatus.NotEncounter;
        try {
            EncounterFacts.Validate(state);
            if (progress == null) return EncounterCheckpointStatus.Unknown;
            EncounterFacts.Validate(progress);
            if (identity.PartyId != progress.PartyId || identity.EncounterId != progress.EncounterId)
                return EncounterCheckpointStatus.Unknown;
            if (state.Status == "error") return EncounterCheckpointStatus.NotCompleted;
            if (state.Outcome == null) return state.Status == "finished"
                ? EncounterCheckpointStatus.NotCompleted : EncounterCheckpointStatus.InProgress;
            MatchOutcomeFacts.Validate(state);
            if (progress.SaveState == EncounterSaveState.Pending) return EncounterCheckpointStatus.Pending;
            if (progress.SaveState == EncounterSaveState.Failed) return EncounterCheckpointStatus.Failed;
            if (progress.LastReceipt is not { } receipt || receipt.EncounterId != identity.EncounterId
                || receipt.GameId != state.Id || !SameOutcome(receipt.Outcome, state.Outcome)
                || receipt.Participants.Length != state.Players.Length) return EncounterCheckpointStatus.Unknown;
            foreach (var participant in receipt.Participants) {
                var player = state.Players.FirstOrDefault(player => player.Id == participant.PlayerId);
                if (player == null || player.ParticipantId != participant.ParticipantId || player.TeamId != participant.TeamId)
                    return EncounterCheckpointStatus.Unknown;
            }
            return EncounterCheckpointStatus.Committed;
        } catch (InvalidOperationException) { return EncounterCheckpointStatus.Unknown; }
    }

    private static bool SameOutcome(MatchOutcome left, MatchOutcome right)
        => left.GameId == right.GameId && left.Kind == right.Kind && left.RuleReason == right.RuleReason
            && left.WinningPlayerIds.ToHashSet().SetEquals(right.WinningPlayerIds)
            && left.WinningTeamIds.ToHashSet().SetEquals(right.WinningTeamIds);
}
