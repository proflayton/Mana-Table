using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;

namespace Mana.Table;

// A controlled scheduling counterexample, not a reconstruction of the missing
// Native438 callback timestamps. Both cases use the production presence owner.
internal static class PolledPresenceSequenceChecks
{
    private static readonly TableInspectionSubject First = new(TableInspectionKind.BattlefieldCard, "sequence-public-a");
    private static readonly TableInspectionSubject Second = new(TableInspectionKind.BattlefieldCard, "sequence-public-b");
    private static readonly TableInspectionSubject[] Subjects = [First, Second];
    private static readonly double[] Schedule = [.01, .27, 1.24, 1.495, 1.76];

    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        check(PolledMixedLobbyEngineJourney.PresenceHoldSeconds == 1.75,
            "The controlled subject handoff uses the original real fixture's1.75-second hold bound");
        var old = await RunCase(profile, previousSelector: true, check);
        var current = await RunCase(profile, previousSelector: false, check);
        check(!old.TerminalWouldPass && old.Seen.SequenceEqual([First.Id])
                && old.BackendAtTerminal == Second.Id && old.LastReplyStillQueued,
            "The old half-second wall selector can first return and post B at the terminal frame while the owner-visible assertion has seen only A");
        check(current.TerminalWouldPass && current.FirstVisibleA == .27 && current.FirstVisibleB == 1.495,
            "The real fixture subject selector preserves A until a prior owner-visible sample and exposes B at1.495 before the unchanged1.75-second bound");
        check(old.ExchangesPerOwner == current.ExchangesPerOwner
                && old.MaximumExecuting.All(value => value == 1) && current.MaximumExecuting.All(value => value == 1),
            "The real subject handoff adds no presence exchanges and retains production single-flight ownership");
        CheckPostPassivity(check);
    }

    private sealed record Sample(double At, string Desired, string[] PriorSeen, string[] Visible,
        string[] CreditedSeen, int[] PendingCallbacks);
    private sealed record Result(bool TerminalWouldPass, string[] Seen, double? FirstVisibleA, double? FirstVisibleB,
        string? BackendAtTerminal, bool LastReplyStillQueued, int ExchangesPerOwner, int[] MaximumExecuting);

    private static async Task<Result> RunCase(string profile, bool previousSelector, Action<bool, string> check)
    {
        string mode = previousSelector ? "old-wall-selector" : "current-fixture-selector";
        string directory = Path.Combine(profile, "polled-presence-sequence-" + mode);
        Directory.CreateDirectory(directory);
        var samples = new List<Sample>();
        var clock = new Clock();
        using var actor = new Owner(0, clock);
        using var observer = new Owner(1, clock);
        var owners = new[] { actor, observer };
        string initial = JsonSerializer.Serialize(owners.Select(owner => owner.State));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        double? firstA = null, firstB = null;
        TableInspectionSubject? backend = null;
        bool terminalWouldPass = false, lastReplyStillQueued = false;
        string? failure = null;
        try {
            check(owners.All(owner => PublicInspectionFacts.IsCurrent(owner.State)
                    && TablePresenceSession.PublicSubject(owner.State, First) && TablePresenceSession.PublicSubject(owner.State, Second)),
                mode + ": both actual sessions have the same current public context and two disclosed subjects");
            for (int index = 0; index < Schedule.Length; index++) {
                clock.Now = Schedule[index];
                // Match the real fixture order: drain prior callbacks, choose
                // from prior recorded remote samples, then update both owners.
                foreach (var owner in owners) owner.Pump();
                var priorSeen = seen.Order(StringComparer.Ordinal).ToArray();
                var desired = previousSelector ? Subjects[(int)(clock.Now / .5) % Subjects.Length]
                    : PolledMixedLobbyEngineJourney.DesiredPresenceSubject(Subjects, seen);
                foreach (var owner in owners) {
                    owner.Session.Observe(owner.State);
                    owner.Session.Update(clock.Now, owner.Id == 0 ? desired : null, true, true);
                }
                int count = index + 1;
                await Until(() => owners.All(owner => owner.Backend.Requests.Count == count));
                var sent = owners.Select(owner => owner.Backend.Requests.ToArray()[index]).ToArray();
                check(sent[0].Update.Subject == desired && sent[0].Update.ContextId == actor.State.PublicInspection!.Id
                        && sent[1].Update.Subject == null && sent[1].Update.ContextId == null,
                    mode + ": production dispatch preserves actor/observer ownership at frame" + index);
                backend = sent[0].Update.Subject;
                foreach (var owner in owners) owner.Backend.Complete(sent[owner.Id], new(actor.State.Id,
                    actor.State.BoardRevision, count, [new(0, actor.State.PublicInspection!.Id, backend!, 1500)]));
                // Finish and post the real asynchronous work without admitting
                // its callback. A provider return is not an owner-visible cue.
                await Until(() => owners.All(owner => owner.PendingCallbacks == 1));
                var visible = observer.Session.Visible(clock.Now).Select(cue => cue.Subject.Id).ToArray();
                if (clock.Now <= PolledMixedLobbyEngineJourney.PresenceHoldSeconds) foreach (string subject in visible) {
                    seen.Add(subject);
                    if (subject == First.Id) firstA ??= clock.Now;
                    if (subject == Second.Id) firstB ??= clock.Now;
                }
                samples.Add(new(clock.Now, desired.Id, priorSeen, visible,
                    seen.Order(StringComparer.Ordinal).ToArray(), owners.Select(owner => owner.PendingCallbacks).ToArray()));
                if (clock.Now >= PolledMixedLobbyEngineJourney.PresenceHoldSeconds) {
                    // The real fixture awaits history reads here, then asserts
                    // without another owner Pump. Completed tasks do not drain it.
                    await Task.CompletedTask;
                    terminalWouldPass = Subjects.All(subject => seen.Contains(subject.Id));
                    lastReplyStillQueued = observer.PendingCallbacks == 1
                        && observer.Port.CallbackTrace.Count(row => row.Stage.ToString() == "Returned") == index;
                }
            }
            check(initial == JsonSerializer.Serialize(owners.Select(owner => owner.State)),
                mode + ": presence leaves both snapshots, decisions, contexts and rules facts unchanged");
            foreach (var owner in owners) {
                var requests = owner.Backend.Requests.ToArray();
                check(requests.Length == Schedule.Length && requests.Zip(requests.Skip(1))
                        .All(pair => pair.Second.DispatchedAt - pair.First.DispatchedAt >= .25),
                    mode + ": actual dispatches preserve the quarter-second minimum interval for seat" + owner.Id);
                check(owner.Port.CallbackTrace.Count(row => row.Stage.ToString() == "Posted") == Schedule.Length
                        && owner.Port.CallbackTrace.Count(row => row.Stage.ToString() == "Returned") == Schedule.Length - 1,
                    mode + ": queued completion diagnostics never apply the final callback before the terminal sample");
            }
            // Late admission remains diagnostic-only and cannot earn credit.
            var credited = seen.Order(StringComparer.Ordinal).ToArray();
            clock.Now = 1.77;
            foreach (var owner in owners) owner.Pump();
            clock.Now = 3.27;
            check(observer.Session.Visible(clock.Now).IsEmpty && credited.SequenceEqual(seen.Order(StringComparer.Ordinal)),
                mode + ": dispatch-anchored1.5-second leases expire without credit from a post-deadline pump");
            return new(terminalWouldPass, credited, firstA, firstB, backend?.Id, lastReplyStillQueued,
                actor.Backend.Requests.Count, owners.Select(owner => owner.Port.MaximumExecuting).ToArray());
        } catch (Exception error) { failure = error.ToString(); throw; }
        finally {
            try {
                File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new {
                    scope = "Authored exact owner-order regression using production TablePresenceSession, fixture selector and presence diagnostic wrapper. No engine, network, RNG, policy, game commands or claim of actual438 chronology.",
                    mode, passed = failure == null, failure, schedule = Schedule,
                    holdBoundSeconds = PolledMixedLobbyEngineJourney.PresenceHoldSeconds,
                    productionIntervalSeconds = .25, leaseMilliseconds = 1500,
                    callbackTimestampFrequency = PolledLobbyPorts.PresencePort.TimestampFrequency,
                    clocks = "Samples use authored logical seconds; callback trace ticks use Stopwatch and do not reconstruct the actual438 chronology.",
                    terminalWouldPass, seen = seen.Order(StringComparer.Ordinal), firstA, firstB,
                    backendAtTerminal = backend?.Id, lastReplyStillQueued, samples,
                    endpoints = owners.Select(owner => new { owner.Id, owner.Port.Started, owner.Port.MaximumExecuting,
                        pendingCallbacks = owner.PendingCallbacks, trace = owner.Port.CallbackTrace })
                }, new JsonSerializerOptions { WriteIndented = true }));
            } catch when (failure != null) { /* Preserve the original case failure. */ }
        }
    }

    private static void CheckPostPassivity(Action<bool, string> check)
    {
        var port = new PolledLobbyPorts.PresencePort(new Backend(new Clock()));
        var queue = new ConcurrentQueue<Action>();
        int invoked = 0;
        var first = new InvalidOperationException("Controlled owner callback failure");
        port.Post(queue.Enqueue, () => { invoked++; throw first; });
        check(invoked == 0 && queue.Count == 1
                && port.CallbackTrace.Count(row => row.Stage.ToString() == "Posted") == 1,
            "Presence callback diagnostics enqueue exactly once without executing owner work inline");
        Exception? caught = null;
        try { queue.TryDequeue(out var callback); callback!(); } catch (Exception error) { caught = error; }
        check(ReferenceEquals(caught, first) && invoked == 1
                && port.CallbackTrace.Count(row => row.Stage.ToString() == "Applying") == 1
                && port.CallbackTrace.Count(row => row.Stage.ToString() == "Returned") == 1,
            "Diagnostic callback boundaries preserve the original owner exception and exactly one invocation");
        int additional = 0;
        for (int i = 0; i <= port.CallbackCapacity; i++) port.Post(queue.Enqueue, () => additional++);
        check(additional == 0 && queue.Count == port.CallbackCapacity + 1,
            "A full diagnostic ring cannot execute or discard queued owner callbacks");
        while (queue.TryDequeue(out var pending)) pending();
        check(additional == port.CallbackCapacity + 1 && port.CallbackTrace.Length == port.CallbackCapacity
                && port.CallbackDropped > 0,
            "Bounded presence diagnostics drop only trace points while every posted owner callback still executes exactly once");
    }

    private sealed class Clock { internal double Now; }
    private sealed record Request(TablePresenceUpdate Update, double DispatchedAt,
        TaskCompletionSource<TablePresenceResult> Completion);

    private sealed class Backend(Clock clock) : ITablePresence
    {
        internal ConcurrentQueue<Request> Requests { get; } = new();
        public Task<TablePresenceResult> ExchangePresenceAsync(TablePresenceUpdate update, CancellationToken cancellation = default)
        {
            var completion = new TaskCompletionSource<TablePresenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Enqueue(new(update, clock.Now, completion));
            return completion.Task;
        }
        internal void Complete(Request request, TablePresenceFrame frame)
            => request.Completion.SetResult(new TablePresenceResult.Current(frame));
    }

    private sealed class Owner : IDisposable
    {
        internal int Id { get; }
        internal GameSnapshot State { get; }
        internal Backend Backend { get; }
        internal PolledLobbyPorts.PresencePort Port { get; }
        internal TablePresenceSession Session { get; }
        private readonly ConcurrentQueue<Action> callbacks = new();
        internal int PendingCallbacks => callbacks.Count;
        internal Owner(int id, Clock clock)
        {
            Id = id; State = Snapshot(id); Backend = new(clock); Port = new(Backend);
            Session = new(Port, callback => Port.Post(callbacks.Enqueue, callback));
            Session.Observe(State);
        }
        internal void Pump() { while (callbacks.TryDequeue(out var callback)) callback(); }
        public void Dispose() => Session.Close();
    }

    private static GameSnapshot Snapshot(int viewer) => new("presence-sequence-game", "playing", null, null, viewer,
        3, "Main", "MAIN1", 0, Enumerable.Range(0, 4).Select(id => new Player(id, "Seat" + id, 40, id == 0, false,
            ImmutableDictionary<string, int>.Empty, [new("Battlefield", id == 0 ? 2 : 0, id == 0
                ? [new Card { VisualId = First.Id, Name = "Public A" }, new Card { VisualId = Second.Id, Name = "Public B" }] : [], null)])).ToImmutableArray(),
        [], null, viewer == 0 ? new("presence-sequence-priority", new(), new PriorityPayload(new("Pass", true))) : null) {
            Revision = 30, BoardRevision = 10, HumanPlayerIds = [0, 1], PublicInspection = new("presence-sequence-context", 10, 0),
            PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority"
        };

    private static async Task Until(Func<bool> complete)
    {
        var elapsed = Stopwatch.StartNew();
        while (!complete()) {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Controlled presence handshake did not settle.");
            await Task.Yield();
        }
    }
}

