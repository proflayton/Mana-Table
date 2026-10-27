using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiObservationSupersededRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static AutomatedSeatObservation Opening(string id, long revision) => new(0,
            new GameSnapshot("superseded-game", "playing", null, null, 0, 0, "Pregame", "PREGAME", null,
                [new Player(0, "You", 40, false, false, ImmutableDictionary<string, int>.Empty,
                    [new("Hand", 7, [new Card { Name = "Known own face", VisualId = "own-hand" }], null)])],
                [], null, new(id, new(), new OpeningHandPayload(new("Keep", true), new("Mulligan", true)))) {
                    Revision = revision, DecisionOwnerId = 0
                }, DecisionActionDomain.None, new());
        var policy = new CountingPolicy();
        var provider = new Provider { Observation = Opening("initial", 1) };
        await using (var host = new AiTableHost(provider, _ => policy, 42)) {
            await host.StepAsync();
            provider.ObserveError = new EngineObservationSupersededException();
            for (int index = 0; index < 3; index++) await host.StepAsync();
            check(provider.Observations == 4 && policy.Calls == 1 && provider.Commands.Count == 1
                    && provider.Faults.Count == 0 && host.LastFault == null,
                "Superseded observation batches produce no evaluations, replayed commands, accepted snapshots or table fault");
            check(host.Histories.Single().Decisions.Count(record => record.Outcome == AiDecisionOutcome.Accepted) == 1,
                "Discarding a raced discovery preserves only the preceding command's actual engine receipt");
            provider.ObserveError = null;
            provider.Observation = Opening("new-current", 2);
            await host.StepAsync();
            check(provider.Commands.Select(command => ((DecisionCommand)command).DecisionId)
                    .SequenceEqual(["initial", "new-current"]) && policy.Calls == 2,
                "The next ordinary step answers only the fresh current scope after a superseded observation");
            await host.StepAsync();
            check(provider.Commands.Count == 2,
                "A stable post-race scope retains exactly-once submission instead of replaying its answer");
        }

        foreach (var failure in new Exception[] {
            new EngineCommandRejectedException("The automated input changed during discovery"),
            new IOException("provider connection failed"),
            new InvalidDataException("provider emitted malformed metadata") }) {
            var failing = new Provider { Observation = Opening("not-observed", 1), ObserveError = failure };
            await using var host = new AiTableHost(failing, _ => new CountingPolicy(), 42);
            Exception? received = null;
            try { await host.StepAsync(); } catch (Exception error) { received = error; }
            check(ReferenceEquals(received, failure) && failing.Commands.Count == 0 && host.Histories.IsEmpty,
                "Only the typed superseded-observation signal is recoverable; ordinary command, transport and schema errors remain failures");
        }

        var commandFailure = new Provider { Observation = Opening("current-command", 1),
            ExecuteError = new EngineObservationSupersededException() };
        await using (var host = new AiTableHost(commandFailure, _ => new CountingPolicy(), 42)) {
            await host.StepAsync();
            check(host.LastFault?.ReasonCode == "execution-unknown" && commandFailure.Commands.Count == 1
                    && commandFailure.Faults.Single() == "execution-unknown",
                "A superseded signal during execution never masquerades as a safe observation retry or repeats a possibly accepted command");
        }
    }

    private sealed class CountingPolicy : IAiPolicy
    {
        internal int Calls;
        public AiPolicyDescriptor Descriptor { get; } = new("superseded-observation-test.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            Calls++;
            return Task.FromResult<AiProposal>(new AiProposal.Answer(
                new KeepOpeningHand(request.Frame.State.Id, request.Frame.State.Decision!.Id), "keep-current"));
        }
    }

    private sealed class Provider : IAutomatedSeatProvider
    {
        internal required AutomatedSeatObservation Observation;
        internal Exception? ObserveError, ExecuteError;
        internal int Observations;
        internal List<GameCommand> Commands = [];
        internal List<string> Faults = [];
        public Task EnableAutomatedSeatsAsync() => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync()
        {
            Observations++;
            return ObserveError == null ? Task.FromResult<ImmutableArray<AutomatedSeatObservation>>([Observation])
                : Task.FromException<ImmutableArray<AutomatedSeatObservation>>(ObserveError);
        }
        public Task ExecuteAutomatedAsync(int seat, GameCommand command)
        {
            Commands.Add(command);
            return ExecuteError == null ? Task.CompletedTask : Task.FromException(ExecuteError);
        }
        public Task ReportAutomationFaultAsync(string game, int seat, string reason)
        {
            Faults.Add(reason); return Task.CompletedTask;
        }
    }
}
