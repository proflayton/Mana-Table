using Mana.Contracts;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static partial class CooperativeLobbyInteractionChecks
{
    private static void ReadyAdmissionParty(Journey run)
    {
        run.Click("cooperative-tab"); run.Click("host-cooperative");
        run.Until(() => run.View.Lobby != null && !run.View.Busy, "Admission opens the current party lobby");
        run.Click("ready");
        run.Until(() => run.View.Lobby?.CanStart == true && !run.View.Busy && !run.View.ActivityOpen,
            "The displayed party and timing are acknowledged before start");
    }

    private static void CheckEncounterAdmission(string profile, Point viewport, Action<bool, string> check)
    {
        var port = new Port { HoldAdmission = true };
        using var run = new Journey(profile, "admission", viewport, port, check);
        ReadyAdmissionParty(run);
        string consent = run.View.Lobby!.MatchSetupConsent!, tempo = run.View.Lobby.Tempo!.Revision;
        run.Click("start");
        run.Until(() => run.View.Lobby?.StartState?.Phase == EncounterStartPhase.Recording && !run.View.Busy,
            "A start acknowledgement stays in the party lobby while recording is pending");
        string attempt = run.View.Lobby!.StartState!.AttemptId;
        check(run.View.Match == null && !run.View.ActivityOpen && port.StartCalls == 0
                && port.AdmissionCalls == 1 && port.AdmittedConsent == consent && port.AdmittedTempo == tempo
                && run.Has("encounter-start-cancel") && run.Has("unready") && run.Has("leave") && !run.Has("start"),
            "The whole ready/start gesture retains the lobby and reachable cancellation without inventing a game or blocking on disk at " + viewport);
        port.FailAdmission();
        run.Until(() => run.Has("encounter-start-retry"), "A failed admission exposes the scoped retry beside its source party");
        check(run.View.Match == null && run.Text.Contains("could not be recorded") && run.Has("unready"),
            "Recording failure is a start failure with no result overlay or awarded progress");
        run.Click("encounter-start-retry");
        run.Until(() => run.View.Lobby?.StartState?.Phase == EncounterStartPhase.Recording && !run.View.Busy,
            "Retry acknowledges the same recording attempt");
        check(port.AdmissionRetries == 1 && run.View.Lobby!.StartState!.AttemptId == attempt && port.StartCalls == 0,
            "Retry keeps one immutable attempt without claiming gameplay began");
        port.CompleteAdmission();
        run.Until(() => run.View.Match != null, "The ordinary lobby poll enters play after the actual match is published");
        check(port.StartCalls == 1 && run.View.Match!.Encounter?.EncounterId == attempt,
            "Only the actual provider match for the admitted encounter supplies the battlefield");
        run.Save("record-retry-launch", new { port.AdmissionCalls, port.AdmissionRetries, port.StartCalls, attempt });
    }

    private static void CheckCancelledAdmission(string profile, Point viewport, Action<bool, string> check)
    {
        var port = new Port { HoldAdmission = true };
        using var run = new Journey(profile, "admission-cancel", viewport, port, check);
        ReadyAdmissionParty(run); run.Click("start");
        run.Until(() => run.Has("encounter-start-cancel") && !run.View.Busy, "A pending encounter can be cancelled from the original start position");
        string cancelledAttempt = run.View.Lobby!.StartState!.AttemptId;
        run.Click("encounter-start-cancel");
        run.Until(() => run.View.Lobby?.StartState?.Phase == EncounterStartPhase.Cancelled && !run.View.Busy,
            "Cancellation returns an authoritative cancelled attempt to the same party");
        port.CompleteAdmission();
        for (int frame = 0; frame < 50; frame++) run.Tick();
        check(run.View.Match == null && port.StartCalls == 0 && port.AdmissionCancels == 1 && run.Has("start"),
            "A late recording completion cannot open the cancelled encounter or hide the next start at " + viewport);
        run.Click("start");
        run.Until(() => run.Has("encounter-start-cancel") && !run.View.Busy, "The retained party can admit a fresh encounter after cancellation");
        check(run.View.Lobby!.StartState!.AttemptId != cancelledAttempt,
            "A deliberate new start has a new attempt identity while a retry retains the old one");
        run.Click("unready");
        run.Until(() => run.View.Lobby?.StartState?.Phase == EncounterStartPhase.Cancelled && !run.View.Busy && !run.View.ActivityOpen,
            "Choosing Not ready also revokes the pending start before launch");
        run.Save("unready-cancelled-settled", run.ControlEvidence(["ready", "start", "encounter-start-cancel"]));
        check(!run.Has("start") && run.Has("ready") && run.View.Match == null,
            "Revoked Ready does not grant a fresh start permission or create a match");
    }

    private static void CheckUnsupportedAdmission(string profile, Point viewport, Action<bool, string> check)
    {
        var port = new Port { SupportsEncounterStart = false };
        using var run = new Journey(profile, "admission-unsupported", viewport, port, check);
        ReadyAdmissionParty(run);
        check(!run.Has("start") && run.Text.Contains("Update Mana Table on the host") && run.Has("unready")
                && port.AdmissionCalls == 0 && port.StartCalls == 0,
            "An older host never falls through to an unjournaled cooperative start and retains reachable Not ready at " + viewport);
    }

    private sealed partial class Port : ITableEncounterStart
    {
        public bool SupportsEncounterStart { get; init; } = true;
        internal bool HoldAdmission;
        internal bool FailAdmissionOnBegin;
        internal int AdmissionCalls, AdmissionRetries, AdmissionCancels;
        internal string? AdmittedConsent, AdmittedTempo;

        public Task<Lobby> BeginEncounterAsync(string tableId, string matchSetupConsent, string? tempoRevision = null)
        {
            lock (gate) {
                if (!SupportsEncounterStart || lobby?.TableId != tableId || lobby.MatchSetupConsent != matchSetupConsent
                    || lobby.Tempo?.Revision != tempoRevision || !lobby.CanStart || lobby.MatchActive)
                    throw new InvalidOperationException("Review the current party before starting.");
                AdmissionCalls++; AdmittedConsent = matchSetupConsent; AdmittedTempo = tempoRevision;
                lobby = lobby with { StartState = new(Guid.NewGuid().ToString(), EncounterStartPhase.Recording, null, false) };
                Refresh();
                if (FailAdmissionOnBegin) FailAdmission();
                else if (!HoldAdmission) CompleteAdmission();
                return Task.FromResult(lobby);
            }
        }

        public Task<Lobby> RetryEncounterStartAsync(string tableId, string attemptId)
        {
            lock (gate) {
                if (lobby?.TableId != tableId || lobby.StartState is not { Phase: EncounterStartPhase.Failed, RetryAllowed: true } attempt
                    || attempt.AttemptId != attemptId) throw new InvalidOperationException("The start attempt changed.");
                AdmissionRetries++;
                lobby = lobby with { StartState = attempt with { Phase = EncounterStartPhase.Recording, Error = null, RetryAllowed = false } };
                Refresh(); return Task.FromResult(lobby);
            }
        }

        public Task<Lobby> CancelEncounterStartAsync(string tableId, string attemptId)
        {
            lock (gate) {
                if (lobby?.TableId != tableId || lobby.StartState?.AttemptId != attemptId || lobby.MatchActive)
                    throw new InvalidOperationException("The start attempt changed.");
                AdmissionCancels++; CancelPendingAdmission(); Refresh(); return Task.FromResult(lobby);
            }
        }

        private void CancelPendingAdmission()
        {
            if (lobby?.StartState is { Phase: EncounterStartPhase.Recording or EncounterStartPhase.Preparing or EncounterStartPhase.Failed } attempt)
                lobby = lobby with { StartState = attempt with { Phase = EncounterStartPhase.Cancelled, Error = null, RetryAllowed = false } };
        }

        internal void FailAdmission()
        {
            lock (gate) {
                lobby = lobby! with { StartState = lobby.StartState! with { Phase = EncounterStartPhase.Failed,
                    Error = "The encounter could not be recorded. Retry start or choose Not ready.", RetryAllowed = true } };
                Refresh();
            }
        }

        internal void CompleteAdmission()
        {
            lock (gate) {
                if (lobby?.StartState is not { Phase: EncounterStartPhase.Recording } attempt) return;
                lobby = lobby with { StartState = null }; Refresh();
                StartAsync().GetAwaiter().GetResult();
                state = state! with { Encounter = new("party", attempt.AttemptId) };
                lobby = lobby with { StartState = attempt with { Phase = EncounterStartPhase.Starting },
                    PartyProgress = lobby.PartyProgress! with { EncounterId = attempt.AttemptId } }; Refresh();
            }
        }
    }
}
