using Mana.Client;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private EncounterCheckpointStatus CurrentEncounterCheckpoint => match is not { } state
        ? EncounterCheckpointStatus.NotEncounter : EncounterCheckpoint.For(state, lobby?.PartyProgress);
    private bool EncounterReturnEnabled => CurrentEncounterCheckpoint is EncounterCheckpointStatus.NotEncounter
        or EncounterCheckpointStatus.NotCompleted or EncounterCheckpointStatus.Committed;
    private bool OwnsEncounterSaveRetry => CurrentEncounterCheckpoint == EncounterCheckpointStatus.Failed
        && services.MatchSetup is { SupportsMatchSetup: true }
        && lobby is { Mode: "hosting", PartyProgress.RetryAllowed: true };
    private bool CanRetryEncounterSave => !busy && OwnsEncounterSaveRetry;
    private bool CanLeaveUnverifiedEncounter => !busy && !localMatch
        && match is { Status: "finished", Encounter: not null }
        && CurrentEncounterCheckpoint == EncounterCheckpointStatus.Unknown;

    private string EncounterCompletionText => CurrentEncounterCheckpoint switch {
        EncounterCheckpointStatus.Pending => "Saving this encounter's result…",
        EncounterCheckpointStatus.Failed when OwnsEncounterSaveRetry => "The result could not be saved. Retry saving before returning to your party.",
        EncounterCheckpointStatus.Failed => "The host needs to retry saving this encounter's result.",
        EncounterCheckpointStatus.Committed => $"Encounter saved · {lobby!.PartyProgress!.CompletedEncounters} played · {lobby.PartyProgress.Victories} victories",
        EncounterCheckpointStatus.NotCompleted => "This encounter did not produce a saved result.",
        EncounterCheckpointStatus.Unknown => "Checking saved progress. Stay here, or leave the table.",
        _ => ""
    };

    private void RetryEncounterSave()
    {
        if (!CanRetryEncounterSave || match?.Encounter is not { } identity || lobby is not { } current
            || services.MatchSetup is not { } service) return;
        string gameId = match.Id;
        Work(async () => {
            var receipt = await service.RetryEncounterSaveAsync(current.TableId, identity.EncounterId);
            updates.Enqueue(() => {
                if (lobby?.TableId == current.TableId && match?.Id == gameId && match.Encounter == identity)
                    session.AcceptLobby(receipt);
            });
        }, "Saving the encounter", "Keeping this result before returning to your party.");
    }
}
