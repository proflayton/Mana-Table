using Mana.Contracts;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private bool EncounterStartAvailable(Lobby state) => state.EncounterStartSupported
        && services.EncounterStart is { SupportsEncounterStart: true };

    private void BeginEncounter()
    {
        if (busy || match != null || lobby is not { Mode: "hosting", MatchActive: false, CanStart: true } current
            || !IsCooperative(current) || !EncounterStartAvailable(current)
            || string.IsNullOrWhiteSpace(current.MatchSetupConsent)) return;
        // Capture the displayed rules and tempo before any asynchronous work.
        // Admission is a lobby fact; only a real match publication enters play.
        string tableId = current.TableId, consent = current.MatchSetupConsent;
        string? timing = current.Tempo?.Revision;
        Work(async () => {
            var admitted = await services.EncounterStart!.BeginEncounterAsync(tableId, consent, timing);
            updates.Enqueue(() => { if (session.AcceptLobby(admitted)) setupModal = ""; });
        }, "Preparing encounter", "Gathering the ready party.", foreground: false, changesMatch: true);
    }

    private void RetryEncounterStart(string tableId, string attemptId)
    {
        if (busy || match != null || lobby is not { Mode: "hosting", MatchActive: false } current
            || current.TableId != tableId || !EncounterStartAvailable(current)
            || current.StartState is not { Phase: EncounterStartPhase.Failed, RetryAllowed: true } attempt
            || attempt.AttemptId != attemptId) return;
        Work(async () => {
            var retried = await services.EncounterStart!.RetryEncounterStartAsync(tableId, attemptId);
            updates.Enqueue(() => session.AcceptLobby(retried));
        }, "Retrying encounter start", "Recording the same encounter before play begins.", foreground: false, changesMatch: true);
    }

    private void CancelEncounterStart(string tableId, string attemptId)
    {
        if (busy || match != null || lobby is not { Mode: "hosting", MatchActive: false } current
            || current.TableId != tableId || !EncounterStartAvailable(current)
            || current.StartState is not { } attempt || attempt.AttemptId != attemptId
            || attempt.Phase is not (EncounterStartPhase.Preparing or EncounterStartPhase.Recording or EncounterStartPhase.Failed)) return;
        Work(async () => {
            var cancelled = await services.EncounterStart!.CancelEncounterStartAsync(tableId, attemptId);
            updates.Enqueue(() => session.AcceptLobby(cancelled));
        }, "Cancelling encounter start", "Keeping the party at this table.", foreground: false, changesMatch: true);
    }

    private void DrawEncounterStart(Lobby state, bool ready, string? problem)
    {
        var attempt = state.StartState;
        bool host = state.Mode == "hosting", supported = EncounterStartAvailable(state);
        if (host && attempt is { Phase: EncounterStartPhase.Preparing or EncounterStartPhase.Recording })
            Button("encounter-start-cancel", "Cancel start", 1170, 883, 340,
                new ShellIntent.CancelEncounterStart(state.TableId, attempt.AttemptId), !busy && supported, false, 52);
        else if (host && attempt is { Phase: EncounterStartPhase.Failed })
            Button(attempt.RetryAllowed ? "encounter-start-retry" : "encounter-start-cancel",
                attempt.RetryAllowed ? "Retry start" : "Cancel start", 1170, 883, 340,
                attempt.RetryAllowed ? new ShellIntent.RetryEncounterStart(state.TableId, attempt.AttemptId)
                    : new ShellIntent.CancelEncounterStart(state.TableId, attempt.AttemptId), !busy && supported, true, 52, prominent: true);
        else if (host)
            Button("start", attempt?.Phase == EncounterStartPhase.Starting ? "Starting encounter" : "Start encounter", 1170, 883, 340,
                new ShellIntent.StartFriends(), !busy && ready && DeckConfirmed && state.CanStart && supported, true, 52, prominent: true);
        else canvas.Text(attempt?.Phase switch {
            EncounterStartPhase.Failed => "Waiting for host to retry",
            EncounterStartPhase.Preparing or EncounterStartPhase.Recording => "Preparing encounter",
            EncounterStartPhase.Starting => "Starting encounter",
            _ => "Waiting for the host"
        }, 1170, 898, Muted, .85f, 340, 2);

        string? status = !supported ? "Update Mana Table on the host before starting this encounter."
            : attempt?.Phase switch {
                EncounterStartPhase.Preparing => "Preparing the encounter. You can still choose Not ready.",
                EncounterStartPhase.Recording => "Recording the encounter before play begins.",
                EncounterStartPhase.Starting => "The party is ready. Starting the encounter.",
                EncounterStartPhase.Failed => attempt.Error ?? "The encounter could not be recorded. Retry start or choose Not ready.",
                EncounterStartPhase.Cancelled => string.IsNullOrWhiteSpace(problem) ? "Start cancelled. Ready when you are." : problem,
                _ => problem
            };
        if (status != null) canvas.Text(status, 444, 889, attempt?.Phase == EncounterStartPhase.Failed ? Gold : Muted, .69f, 694, 2);
    }
}
