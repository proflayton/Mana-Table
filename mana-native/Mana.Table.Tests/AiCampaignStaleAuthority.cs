using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Table;

internal sealed record AiCampaignStaleProbe(string ProbeId, int SeatId, string GameId, string CommandType,
    string PriorDecisionId, long PriorRevision, long PriorOfferVersion,
    string CurrentDecisionId, long CurrentRevision, long CurrentOfferVersion, bool ExplicitlyRejected, string? Error);

// One release-only challenge per seat. This never chooses an answer for the
// current decision: it submits an already consumed payload under a fresh RPC.
internal sealed class AiCampaignStaleAuthority
{
    private readonly Dictionary<(string Game, int Seat), (AutomatedSeatObservation Observation, DecisionCommand Command)> accepted = [];
    private readonly HashSet<(string Game, int Seat)> probed = [];

    internal void Remember(AutomatedSeatObservation observation, GameCommand command)
    {
        if (command is DecisionCommand decision && command.GameId == observation.State.Id
            && decision.DecisionId == observation.State.Decision?.Id)
            accepted[(command.GameId, observation.SeatId)] = (observation, decision);
    }

    internal async Task ObserveAsync(ImmutableArray<AutomatedSeatObservation> observations,
        Func<int, GameSnapshot, GameSnapshot, DecisionCommand, string, Task> sendFresh,
        Action<AiCampaignStaleProbe, DecisionCommand> record)
    {
        foreach (var current in observations) {
            var key = (current.State.Id, current.SeatId);
            if (probed.Contains(key) || current.State.Status != "playing" || current.State.ViewerId != current.SeatId
                || current.State.Decision == null || current.State.Viewer?.Eliminated != false
                || !accepted.TryGetValue(key, out var prior) || prior.Command.DecisionId == current.State.Decision.Id) continue;
            probed.Add(key);
            string probeId = Guid.NewGuid().ToString("N");
            AiCampaignStaleProbe Result(bool rejected, string? error) => new(probeId, current.SeatId, current.State.Id,
                prior.Command.GetType().Name, prior.Command.DecisionId, prior.Observation.State.Revision, prior.Observation.Domain.Version,
                current.State.Decision.Id, current.State.Revision, current.Domain.Version, rejected, error);
            try { await sendFresh(current.SeatId, prior.Observation.State, current.State, prior.Command, probeId); }
            catch (EngineCommandRejectedException error) { record(Result(true, error.Message), prior.Command); continue; }
            catch (Exception error) { record(Result(false, error.GetType().Name + ": " + error.Message), prior.Command); throw; }
            record(Result(false, "The provider accepted a command from a consumed decision."), prior.Command);
            throw new InvalidDataException("The authoritative provider accepted a stale automated command.");
        }
    }

    internal static async Task CheckRegressionAsync(Action<bool, string> check)
    {
        var player = new Player(0, "Seat", 40, true, false, ImmutableDictionary<string, int>.Empty, []);
        var old = new GameSnapshot("stale-probe", "playing", null, null, 0, 1, "Pregame", "PREGAME", null, [player], [], null,
            new("old", new(), new OpeningHandPayload(new("Keep", true), new("Mulligan", true)))) { Revision = 2 };
        var current = old with { Revision = 5, Decision = new("new", new(), new PriorityPayload(new("Pass", true))) };
        var before = new AutomatedSeatObservation(0, old, DecisionActionDomain.None with { Version = 3 }, new());
        var after = before with { State = current, Domain = DecisionActionDomain.None with { Version = 7 } };
        var command = new KeepOpeningHand(old.Id, old.Decision!.Id);
        var probe = new AiCampaignStaleAuthority(); probe.Remember(before, command);
        int sends = 0; var results = new List<AiCampaignStaleProbe>();
        Task Reject(int seat, GameSnapshot prior, GameSnapshot next, DecisionCommand payload, string id) {
            sends++;
            if (seat != 0 || prior != old || next != current || payload != command || !Guid.TryParseExact(id, "N", out _))
                throw new InvalidDataException("The stale probe replaced the historical payload or its current scope.");
            return Task.FromException(new EngineCommandRejectedException("explicit stale rejection"));
        }
        await probe.ObserveAsync([before], Reject, (result, _) => results.Add(result));
        await probe.ObserveAsync([after], Reject, (result, _) => results.Add(result));
        await probe.ObserveAsync([after], Reject, (result, _) => results.Add(result));
        check(sends == 1 && results is [{ ExplicitlyRejected: true, PriorDecisionId: "old", CurrentDecisionId: "new", PriorOfferVersion: 3, CurrentOfferVersion: 7 }],
            "A release stale probe waits for a distinct current decision and checks its exact historical payload only once per seat");
        var invalid = new AiCampaignStaleAuthority(); invalid.Remember(before, command);
        bool failed = false;
        try { await invalid.ObserveAsync([after], (_, _, _, _, _) => Task.CompletedTask, (result, _) => results.Add(result)); }
        catch (InvalidDataException) { failed = true; }
        check(failed && results.Count == 2 && !results[^1].ExplicitlyRejected && results[0].ProbeId != results[1].ProbeId,
            "Accepting a stale command fails the evaluation; fresh probe identities cannot turn an old successful receipt into rejection evidence");
    }
}
