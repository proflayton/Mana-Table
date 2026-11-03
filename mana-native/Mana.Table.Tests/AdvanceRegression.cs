using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;
using Mana.Magic;

namespace Mana.Table;

internal static class AdvanceRegression
{
    internal static async Task RunAsync(string profile)
    {
        var checks = new List<string>();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
        var initial = PresentationSmoke.Example() with { Revision = 10 };
        GameSnapshot Next(int i, string phase = "MAIN1") => initial with {
            Revision = 10 + i, PhaseKey = phase, Decision = initial.Decision! with { Id = "next-" + i }
        };
        bool CanPass(GameSnapshot s) => TurnGuide.CanAutoPass(s, true, false, new HashSet<string>(), false);
        var response = Next(4) with { Decision = Next(4).Decision! with { Payload = ((PriorityPayload)(Next(4).Decision!).Payload) with { CanAutoPass = false } } };
        var wire = new Scripted(initial, Next(1, "COMBAT_BEGIN"), Next(2, "COMBAT_END"), Next(3, "MAIN2"), response);
        var result = await new DecisionAdvance(wire).RunAsync(initial, CanPass, PresentationPacing.ShouldPresent);
        Check(result == response && wire.Replies.Count == 4, "One advance drains empty phases and returns the next playable response");
        Check(wire.Replies.All(r => r is AutoPassPriority) && wire.Replies.OfType<AutoPassPriority>().Select(r => r.DecisionId).Distinct().Count() == 4,
            "Every consumed window uses a distinct, engine-validated AutoPass receipt");

        var complete = new DecisionReply("Confirm", true);
        var cancel = new DecisionReply("Cancel", false);
        foreach (var payload in new DecisionPayload[] { new OpeningHandPayload(complete, cancel), new ConfirmationPayload(complete, new("No", true)),
            new AttackPayload(complete), new BlockPayload(complete),
            new PaymentPayload(complete, cancel), new TargetSelectionPayload(complete, cancel, new(1, 1), []),
            new EntitySelectionPayload(complete, cancel, []) }) {
            var required = Next(1) with { Decision = Next(1).Decision! with { Payload = payload } };
            wire = new(initial, required);
            result = await new DecisionAdvance(wire).RunAsync(initial, _ => true, PresentationPacing.ShouldPresent);
            Check(result == required && wire.Replies.Count == 1, $"Even a permissive client policy cannot skip {payload.GetType().Name}");
        }
        var selection = new ChoiceSelection([new(7, "Offered option", null)], new(1, 1));
        foreach (var payload in new DecisionPayload[] { new OptionsPayload(selection), new OrderingPayload(selection),
            new AbilityChoicePayload(selection), new RevealPayload([]), new NumberPayload(""), new TextPayload(""),
            new AllocationPayload(0, AllocationKind.Generic, "", false, []) }) {
            var required = Next(1) with { Decision = Next(1).Decision! with { Payload = payload } };
            wire = new(initial, required);
            result = await new DecisionAdvance(wire).RunAsync(initial, _ => true, PresentationPacing.ShouldPresent);
            Check(result == required && wire.Replies.Count == 1, $"Advance stops for a required {payload.GetType().Name} prompt");
        }
        var stop = Next(1, "MAIN2"); wire = new(initial, stop);
        result = await new DecisionAdvance(wire).RunAsync(initial,
            s => TurnGuide.CanAutoPass(s, true, false, new HashSet<string> { "MAIN2" }, false), PresentationPacing.ShouldPresent);
        Check(result == stop && wire.Replies.Count == 1, "A saved own-turn phase stop interrupts a batch");

        var checkpoints = new Dictionary<string, GameSnapshot> {
            ["turn"] = Next(1) with { Turn = initial.Turn + 1 },
            ["cast"] = Next(1) with { Stack = [new("Spell", "", false, null) { Id = "spell" }] },
            ["life"] = Next(1) with { Players = initial.Players.Select(p => p.Id == 1 ? p with { Life = 37 } : p).ToImmutableArray() },
            ["draw"] = Next(1) with { Players = initial.Players.Select(p => p.Id == 1 ? p with { Zones = p.Zones.Select(z => z.Name == "Hand" ? z with { Count = 8 } : z).ToImmutableArray() } : p).ToImmutableArray() },
            ["combat"] = Next(1) with { Combat = new([new("attacker", 0, [], [])], [], null) },
            ["notice"] = Next(1) with { Notices = ["A replacement effect occurred."] },
            ["activity"] = Next(1) with { Activity = [new(1, initial.Turn, "MAIN1", "resolved", 1, "Spell resolved", null, null)] }
        };
        foreach (var (name, snapshot) in checkpoints) {
            wire = new(initial, snapshot);
            result = await new DecisionAdvance(wire).RunAsync(initial, CanPass, PresentationPacing.ShouldPresent);
            Check(result == snapshot && wire.Replies.Count == 1, $"A visible {name} is presented before another pass");
        }
        var pacing = new PresentationPacing();
        pacing.Observe(initial, Next(1, "COMBAT_BEGIN"), 10);
        Check(pacing.Ready(10), "Empty phase transitions have no artificial presentation delay");
        pacing.Observe(initial, checkpoints["cast"], 10);
        pacing.Observe(checkpoints["cast"], checkpoints["cast"], 11);
        Check(!pacing.Ready(11.2) && pacing.Ready(11.3), "Visible casts retain their readable beat; duplicate polls do not extend it");

        foreach (bool lateObservation in new[] { false, true }) {
            wire = new(initial, initial, Next(1) with { Decision = null }, initial with { Revision = 9 }, Next(2), Next(2), response);
            if (lateObservation) {
                bool delayed = false;
                wire.BeforeObserve = async () => {
                    if (delayed) return;
                    delayed = true;
                    // A valid late I/O completion can exhaust the unchanged
                    // 350ms batch allowance before this sequence has drained.
                    await Task.Delay(400);
                };
            }
            var observedWatch = System.Diagnostics.Stopwatch.StartNew();
            var observationRunner = new DecisionAdvance(wire);
            var continuation = initial;
            var batches = new List<object>();
            string[] expectedDecisions = [initial.Decision!.Id, Next(2).Decision!.Id];
            string journal = "advance-observation-prefix" + (lateObservation ? "-late" : "");
            for (int batch = 0; batch < 8; batch++) {
                double batchStarted = observedWatch.Elapsed.TotalMilliseconds;
                result = await observationRunner.RunAsync(continuation, CanPass, PresentationPacing.ShouldPresent);
                var replyIds = wire.Replies.OfType<AutoPassPriority>().Select(reply => reply.DecisionId).ToArray();
                bool exactPrefix = wire.Replies.All(reply => reply is AutoPassPriority pass && pass.GameId == initial.Id)
                    && replyIds.Length <= expectedDecisions.Length && replyIds.SequenceEqual(expectedDecisions.Take(replyIds.Length))
                    && replyIds.Distinct(StringComparer.Ordinal).Count() == replyIds.Length;
                var batchEvidence = new {
                    scenario = "duplicate-and-stale-observations", lateObservation, batch = batch + 1,
                    elapsedMilliseconds = observedWatch.Elapsed.TotalMilliseconds,
                    batchMilliseconds = observedWatch.Elapsed.TotalMilliseconds - batchStarted,
                    startedRevision = continuation.Revision, startedDecision = continuation.Decision?.Id,
                    returnedRevision = result?.Revision, returnedDecision = result?.Decision?.Id,
                    finalResponse = result == response, exactPrefix,
                    expectedDecisions,
                    replies = wire.Replies.OfType<DecisionCommand>().Select(reply => new {
                        kind = reply.GetType().Name, reply.GameId, reply.DecisionId }).ToArray(),
                    observations = wire.Observations.ToArray()
                };
                batches.Add(batchEvidence);
                string batchFacts = JsonSerializer.Serialize(batchEvidence, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(profile, journal + "-batch-" + (batch + 1) + ".json"), batchFacts);
                Check(exactPrefix && result != null && result.Id == initial.Id && result.ViewerId == initial.ViewerId
                        && result.Revision >= continuation.Revision,
                    "Every bounded batch retains the exact ordered unique AutoPass prefix through resolving, duplicate and stale observations; " + batchFacts);
                if (lateObservation && batch == 0)
                    Check(result == initial && wire.Replies.Count == 1 && wire.Observations.Count == 1,
                        "A late first observation legitimately yields the current prefix after one pass without replay or a larger batch deadline; " + batchFacts);
                if (result == response) break;
                // Retain the runner's submitted-decision memory and resume only
                // its returned authoritative prefix, never the original input.
                continuation = result!;
            }
            observedWatch.Stop();
            var evidence = new {
                scenario = "duplicate-and-stale-observations", lateObservation,
                elapsedMilliseconds = observedWatch.Elapsed.TotalMilliseconds,
                returnedRevision = result?.Revision, returnedDecision = result?.Decision?.Id,
                finalResponse = result == response, expectedDecisions, batches,
                replies = wire.Replies.OfType<DecisionCommand>().Select(reply => new {
                    kind = reply.GetType().Name, reply.GameId, reply.DecisionId }).ToArray(),
                observations = wire.Observations.ToArray()
            };
            string facts = JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(profile, journal + ".json"), facts);
            Check(result == response, "Bounded continuations reach the next playable response instead of requiring one batch to drain the whole sequence; " + facts);
            Check(wire.Replies.OfType<AutoPassPriority>().Select(reply => reply.DecisionId).SequenceEqual(expectedDecisions),
                "The completed sequence submitted exactly its two authorized AutoPass decisions; " + facts);
        }
        using (var cancellation = new CancellationTokenSource()) {
            wire = new(initial, response); var runner = new DecisionAdvance(wire); cancellation.Cancel();
            await runner.RunAsync(initial, CanPass, PresentationPacing.ShouldPresent, cancellation.Token);
            Check(wire.Replies.Count == 0, "Hold before dispatch submits nothing");
            await runner.RunAsync(initial, CanPass, PresentationPacing.ShouldPresent);
            Check(wire.Replies.Count == 1, "Releasing Hold can resume the same unsubmitted decision");
        }
        using (var cancellation = new CancellationTokenSource()) {
            wire = new(initial, Next(1)); wire.OnReply = () => cancellation.Cancel();
            result = await new DecisionAdvance(wire).RunAsync(initial, CanPass, PresentationPacing.ShouldPresent, cancellation.Token);
            Check(result == Next(1) && wire.Replies.Count == 1, "Hold during a command reconciles that command and prevents the next one");
        }
        wire = new(initial, initial);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await new DecisionAdvance(wire).RunAsync(initial, CanPass, PresentationPacing.ShouldPresent);
        Check(wire.Replies.Count == 1 && watch.Elapsed.TotalSeconds < 2, "Waiting on another seat is bounded and never passes for that seat");
        foreach (var foreign in new[] { Next(1) with { Id = "other-game" }, Next(1) with { ViewerId = initial.ViewerId + 1 } }) {
            wire = new(initial, foreign);
            IOException? rejected = null;
            try { await new DecisionAdvance(wire).RunAsync(initial, CanPass, PresentationPacing.ShouldPresent); }
            catch (IOException error) { rejected = error; }
            Check(rejected != null && wire.Replies.Count == 1 && wire.Observations.Count == 1,
                "An advance rejects a foreign game or viewer receipt after one command without returning it as an accepted lifecycle handoff");
        }

        wire = new(initial, Next(1), Next(2), response);
        var session = new ClientSession(wire); session.Accept(initial);
        var presented = new List<GameSnapshot?>(); session.Changed += (_, after) => presented.Add(after);
        Exception? failure = null; session.Failed += e => failure = e;
        Check(session.Advance(CanPass, PresentationPacing.ShouldPresent), "Shared client starts an advance through the normal seat connection");
        await Pump(session);
        Check(failure == null && presented.Count == 1 && session.State == response,
            "Renderer receives only the actionable snapshot, without intermediate priority or resolving states");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wire = new(initial, response) { BeforeObserve = async () => { entered.TrySetResult(); await release.Task; } };
        session = new(wire); session.Accept(initial); session.Advance(CanPass, PresentationPacing.ShouldPresent);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); session.Reset(); release.SetResult(); await Pump(session);
        Check(session.State == null, "Reset rejects an in-flight advance from the retired match");
        File.WriteAllText(Path.Combine(profile, "advance-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task Pump(ClientSession session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (session.Busy && DateTime.UtcNow < deadline) { await Task.Delay(5); session.Pump(); }
        if (session.Busy) throw new TimeoutException("Client session failed to finish an advance");
    }
    private sealed class Scripted(GameSnapshot initial, params GameSnapshot[] observations) : IMatchConnection
    {
        private readonly Queue<GameSnapshot> snapshots = new(observations);
        private GameSnapshot state = initial;
        public readonly List<GameCommand> Replies = [];
        public readonly List<object> Observations = [];
        public Action? OnReply;
        public Func<Task>? BeforeObserve;
        public async Task<GameSnapshot?> ObserveAsync() {
            if (BeforeObserve != null) await BeforeObserve();
            if (snapshots.TryDequeue(out var next)) state = next;
            Observations.Add(new { state.Revision, decisionId = state.Decision?.Id });
            return state;
        }
        public Task ExecuteAsync(GameCommand reply) { Replies.Add(reply); OnReply?.Invoke(); return Task.CompletedTask; }
        public Task ConcedeAsync(string id) => throw new NotSupportedException();
    }
}
