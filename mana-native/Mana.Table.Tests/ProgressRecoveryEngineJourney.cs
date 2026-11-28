using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.AI;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Observation-loss fault injection around the production process/seat ports.
// Real Forge inputs remain unanswered; recovery never fabricates rules results.
internal static class ProgressRecoveryEngineJourney
{
    internal static async Task RunAsync(ForgePaths paths)
    {
        Directory.CreateDirectory(paths.Profile);
        var checks = new List<string>(); string? failure = null;
        void Check(bool value, string description) { if (!value) throw new InvalidDataException(description); checks.Add(description); }
        try {
            await using var engine = new ForgeEngine(paths); await engine.InitializeAsync();
            Check(engine.SupportsMatchProgressRecovery, "The packaged JVM negotiates match-progress-recovery-v1");
            await engine.EnableAutomatedSeatsAsync();
            var deck = await engine.ImportAsync("Progress recovery control", "Deck\n99 Forest\nCommander\n1 Rhys the Redeemed");
            var options = await engine.PrepareSoloAsync(deck.Id);
            await engine.StartSimulationAsync(options.Opponents.Take(4).Select(option => option.Id).ToImmutableArray());
            var clock = new Clock(); var port = new AutomatedPort(engine);
            await using (var host = new AiTableHost(port, _ => new CasualCommanderPolicy(), 42, operationClock: clock)) {
                for (int attempt = 0; attempt < 100 && port.Target == null; attempt++) { await host.StepAsync(); await Task.Delay(5); }
                Check(port.Target?.State.Decision != null, "A real private Forge input is captured before the observation-loss injection");
                Check(port.Commands.Count == 0, "The masked actual input has not been answered");
                clock.Advance(45); await host.StepAsync();
                Check(port.Repairs == 1 && port.Commands.Count == 0, "The AI host reconciles through the real JVM without answering the missing input");
                for (int attempt = 0; attempt < 100 && port.Commands.Count == 0; attempt++) { await host.StepAsync(); await Task.Delay(5); }
                Check(host.LastFault == null && port.Commands.Count(command => command is DecisionCommand decision
                    && decision.DecisionId == port.Target!.State.Decision!.Id) == 1,
                    "A fresh real owned observation restores exactly one legal answer to the original Forge input");
            }
            await engine.StopSimulationAsync(port.Target!.State.Id);
            var setup = await engine.PrepareSoloAsync(deck.Id);
            await engine.StartSoloAsync(deck.Id, [setup.Opponents.First().Id]);
            await using (var bots = new AiTableHost(engine, _ => new CasualCommanderPolicy(), 43)) {
                GameSnapshot? initial = null;
                var wait = Stopwatch.StartNew();
                while (initial?.Decision == null) {
                    if (wait.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("The real local human input did not appear");
                    await bots.StepAsync(); initial = await engine.ObserveAsync(); await Task.Delay(5);
                }
                var player = new PlayerPort(engine);
                var client = new ClientSession(player, clock: clock);
                client.Accept(initial with { Status = "resolving", Decision = null, DecisionOwnerId = initial.ViewerId });
                clock.Advance(45); client.Pump();
                wait.Restart();
                while (client.Busy || player.Repairs == 0) {
                    if (wait.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("The real player reconciliation did not finish");
                    client.Pump(); await Task.Delay(5);
                }
                Check(!client.NeedsRecovery && client.State?.Decision?.Id == initial.Decision.Id && player.Commands == 0,
                    "The player client restores the actual JVM decision through read/control repair, without a game command");
                var latest = await engine.ObserveAsync();
                Check(latest?.Decision?.Id == initial.Decision.Id, "Player recovery leaves the real Forge input unanswered and inspectable");
                client.Close();
            }
        } catch (Exception error) { failure = error.ToString(); throw; }
        finally { File.WriteAllText(Path.Combine(paths.Profile, "progress-recovery-engine-result.json"), JsonSerializer.Serialize(new {
            passed = failure == null, failure, checks,
            scope = "Real packaged Forge process with controlled observation loss; authoritative inputs are not deleted by this native wrapper. Java tests separately remove publication ownership."
        }, new JsonSerializerOptions { WriteIndented = true })); }
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        internal void Advance(int seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class AutomatedPort(ForgeEngine engine) : IAutomatedSeatProvider, IAutomatedMatchProgressRecovery
    {
        internal AutomatedSeatObservation? Target;
        internal int Repairs;
        internal List<GameCommand> Commands = [];
        public bool SupportsMatchProgressRecovery => engine.SupportsMatchProgressRecovery;
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => engine.EnableAutomatedSeatsAsync(cancellation);
        public async Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default) {
            var rows = await engine.ObserveAutomatedSeatsAsync(cancellation);
            Target ??= rows.FirstOrDefault(row => row.State.Decision != null);
            if (Target == null) return [];
            var owned = rows.First(row => row.SeatId == Target.SeatId);
            return [Repairs > 0 ? owned : owned with { State = owned.State with {
                Status = "resolving", Decision = null, DecisionOwnerId = owned.SeatId
            }, Domain = DecisionActionDomain.None, Semantics = new() }];
        }
        public async Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        { await engine.ExecuteAutomatedAsync(seatId, command, cancellation); Commands.Add(command); }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default)
            => engine.ReportAutomationFaultAsync(gameId, seatId, reasonCode, cancellation);
        public async Task ReconcileAutomatedMatchProgressAsync(string gameId, int seatId, CancellationToken cancellation = default)
        { await engine.ReconcileAutomatedMatchProgressAsync(gameId, seatId, cancellation); Repairs++; }
    }
    private sealed class PlayerPort(ForgeEngine engine) : IMatchConnection, IMatchProgressRecovery
    {
        internal int Repairs, Commands;
        public bool SupportsMatchProgressRecovery => engine.SupportsMatchProgressRecovery;
        public Task<GameSnapshot?> ObserveAsync() => engine.ObserveAsync();
        public Task ExecuteAsync(GameCommand command) { Commands++; return engine.ExecuteAsync(command); }
        public Task ConcedeAsync(string gameId) => engine.ConcedeAsync(gameId);
        public async Task ReconcileMatchProgressAsync(string gameId, CancellationToken cancellation = default)
        { await engine.ReconcileMatchProgressAsync(gameId, cancellation); Repairs++; }
    }
}
