using System.Collections.Concurrent;
using Mana.Client;
using Mana.Contracts;

// Enumerate only legal, deliberately held schedules. The oracle observes real
// session publications and I/O; it does not reproduce the recovery reducer.
internal static class RecoveryScheduleRegression
{
    private enum ProviderEnd { Rejected, Unknown, ReadbackFailed, Returned }
    private enum Transition { Newer, Stale, Reset, ViewerRoundTrip, Close }
    private enum Boundary { DispatchHeld, CompletionQueued }
    private enum Diagnostics { Off, Recording, Throwing }
    private sealed record Schedule(ProviderEnd End, Transition Change, Boundary At)
    {
        public override string ToString() => $"{End}/{Change}/{At}";
    }
    private sealed record Transcript(string Commands, int Reads, string Publications, string Failures,
        string State, bool Closed, int Completions, int Recoveries, string Recovery);
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var schedules = new List<Schedule>();
        foreach (var end in Enum.GetValues<ProviderEnd>()) {
            schedules.Add(new(end, Transition.Newer, Boundary.DispatchHeld));
            foreach (var change in new[] { Transition.Reset, Transition.ViewerRoundTrip, Transition.Close })
                schedules.Add(new(end, change, Boundary.CompletionQueued));
        }
        foreach (var end in new[] { ProviderEnd.ReadbackFailed, ProviderEnd.Returned })
            schedules.Add(new(end, Transition.Stale, Boundary.DispatchHeld));
        foreach (var end in new[] { ProviderEnd.Unknown, ProviderEnd.Returned })
            foreach (var change in new[] { Transition.Reset, Transition.ViewerRoundTrip })
                schedules.Add(new(end, change, Boundary.DispatchHeld));
        check(schedules.Count == 22 && schedules.Distinct().Count() == 22,
            "The recovery schedule suite retains its fixed22 distinct legal orderings");
        foreach (var schedule in schedules) {
            var baseline = await Run(schedule, Diagnostics.Off, check);
            foreach (var diagnostics in new[] { Diagnostics.Recording, Diagnostics.Throwing })
                check(await Run(schedule, diagnostics, check) == baseline,
                    $"{schedule}: diagnostics {diagnostics} cannot alter the gameplay transcript");
        }
    }

    private static async Task<Transcript> Run(Schedule schedule, Diagnostics mode, Action<bool, string> check)
    {
        void Expect(bool value, string message) => check(value, $"Recovery schedule {schedule}, diagnostics {mode}: {message}");
        var initial = State(); var returned = Next(initial, 11, "returned");
        var port = new Port(initial); var session = new ClientSession(port);
        var publications = new List<string>(); var failures = new List<ClientOperationFailure>();
        var diagnostics = new ConcurrentQueue<ClientDiagnostic>();
        int completions = 0, recoveries = 0;
        session.Changed += (_, state) => publications.Add(Describe(state));
        session.Failed += failures.Add;
        session.Completed += () => completions++;
        session.Recovered += () => recoveries++;
        if (mode == Diagnostics.Throwing) session.DiagnosticRecorded += _ => throw new IOException("Diagnostic sink unavailable");
        if (mode != Diagnostics.Off) session.DiagnosticRecorded += diagnostics.Enqueue;
        session.Accept(initial);
        var original = new PassPriority(initial.Id, initial.Decision!.Id);
        Exception cause = schedule.End == ProviderEnd.Rejected
            ? new EngineCommandRejectedException("The offered action was rejected") : new EngineObservationSupersededException();
        try {
            Expect(session.Submit(original), "one current gesture enters the held dispatch");
            await port.Dispatch.Started.Task.WaitAsync(HangGuard);
            Expect(port.Commands.Count == 1 && port.Reads == 0, "dispatch is genuinely held before any readback");
            GameSnapshot expected = initial;
            if (schedule.At == Boundary.DispatchHeld) expected = Change();
            if (schedule.End is ProviderEnd.Rejected or ProviderEnd.Unknown) port.Dispatch.Reply.SetException(cause);
            else {
                port.Dispatch.Reply.SetResult();
                await port.Read.Started.Task.WaitAsync(HangGuard);
                if (schedule.End == ProviderEnd.ReadbackFailed) port.Read.Reply.SetException(cause);
                else port.Read.Reply.SetResult(returned);
            }
            if (schedule.At == Boundary.CompletionQueued) {
                // There was no earlier posted work. Provider completion has now
                // reached the owner queue, but none of its callbacks have run.
                await session.WaitForQueuedWorkAsync().WaitAsync(HangGuard);
                Expect(failures.Count == 0 && publications.Count == 1,
                    "the completion remains queued before the explicit retirement");
                expected = Change();
            }
            int publicationsBeforeCompletion = publications.Count;
            await Settle(session);
            bool retired = schedule.Change is Transition.Reset or Transition.ViewerRoundTrip or Transition.Close;
            bool returnedAccepted = schedule.End == ProviderEnd.Returned && schedule.Change == Transition.Stale;
            if (returnedAccepted) expected = returned;
            Expect(ReferenceEquals(session.State, expected)
                    && publications.Count == publicationsBeforeCompletion + (returnedAccepted ? 1 : 0),
                "only a current admitted readback can publish; late or older work cannot replace the current frame");
            int originalReads = schedule.End is ProviderEnd.ReadbackFailed or ProviderEnd.Returned ? 1 : 0;
            Expect(port.Commands.Count == 1 && port.Reads == originalReads,
                "finishing the original operation performs no replay or undisclosed extra observation");
            if (retired || schedule.End == ProviderEnd.Returned) {
                Expect(failures.Count == 0 && session.LastFailure == null && session.RecoveryOrigin == null
                        && !session.NeedsRecovery && !session.UncertainCommand,
                    "retired completions and successful current reads do not invent a failure episode");
            } else {
                var phase = schedule.End == ProviderEnd.Rejected ? ClientFailurePhase.DispatchRejected
                    : schedule.End == ProviderEnd.Unknown ? ClientFailurePhase.DispatchUnknown : ClientFailurePhase.ReadbackUnknown;
                Expect(failures.Count == 1 && failures[0].Phase == phase && ReferenceEquals(failures[0].Cause, cause)
                        && failures[0].Context is { Epoch: 1, GameId: "recovery-schedule", ViewerId: 0,
                            Revision: 10, BoardRevision: 20, DecisionId: "initial" },
                    "failure retains the actual phase, original cause and dispatch-admission context despite intervening state");
                Expect(session.NeedsRecovery == (schedule.End != ProviderEnd.Rejected)
                        && session.UncertainCommand == (schedule.End != ProviderEnd.Rejected)
                        && (schedule.End == ProviderEnd.Rejected ? session.RecoveryOrigin == null
                            : ReferenceEquals(session.RecoveryOrigin, failures[0])),
                    "definite rejection and uncertain completion retain distinct recovery authority");
            }
            Expect(!session.Submit(original) && port.Commands.Count == 1,
                "the retired decision or uncertain old gesture cannot be submitted through stale authority");

            if (!session.Closed) {
                if (schedule.End == ProviderEnd.ReadbackFailed && schedule.Change == Transition.Newer) {
                    var origin = session.RecoveryOrigin;
                    port.ArmRead(); session.Poll(false, _ => { });
                    await port.Read.Started.Task.WaitAsync(HangGuard);
                    var later = new IOException("A later reconciliation observation failed");
                    port.Read.Reply.SetException(later); await Settle(session);
                    Expect(failures.Count == 2 && ReferenceEquals(session.RecoveryOrigin, origin)
                            && ReferenceEquals(session.LastFailure, failures[1]) && ReferenceEquals(failures[1].Cause, later)
                            && failures[1].Phase == ClientFailurePhase.Observation
                            && failures[1].Context.Revision == expected.Revision && session.UncertainCommand
                            && port.Commands.Count == 1,
                        "a newer failed observation updates diagnostics without erasing the first uncertain cause or replaying it");
                }
                var reconciled = Next(expected, expected.Revision + 1, "reconciled");
                port.ArmRead(); session.Poll(false, _ => { });
                await port.Read.Started.Task.WaitAsync(HangGuard);
                port.Read.Reply.SetResult(reconciled); await Settle(session);
                Expect(ReferenceEquals(session.State, reconciled) && !session.NeedsRecovery
                        && !session.UncertainCommand && session.RecoveryOrigin == null && port.Commands.Count == 1,
                    "an ordinary fresh observation reconciles without resending the earlier gesture");
                var deliberate = new PassPriority(reconciled.Id, reconciled.Decision!.Id);
                port.ArmDispatch(); port.ArmRead();
                Expect(session.Submit(deliberate), "a deliberately new current gesture remains available after reconciliation");
                await port.Dispatch.Started.Task.WaitAsync(HangGuard); port.Dispatch.Reply.SetResult();
                await port.Read.Started.Task.WaitAsync(HangGuard);
                var final = Next(reconciled, reconciled.Revision + 1, "after-deliberate");
                port.Read.Reply.SetResult(final); await Settle(session);
                Expect(port.Commands.ToArray().SequenceEqual([original, deliberate]) && ReferenceEquals(session.State, final)
                        && !session.NeedsRecovery && !session.UncertainCommand,
                    "the complete journey sends exactly the old gesture and one explicit fresh gesture");
            } else {
                int closedCallbacks = 0;
                session.Post(() => closedCallbacks++); session.Pump();
                Expect(completions == 0 && recoveries == 0 && closedCallbacks == 0 && session.Closed,
                    "the closed owner discards an explicitly queued callback without requiring the retired worker to finish or Busy to clear");
            }

            var settled = Capture();
            for (int i = 0; i < 3; i++) session.Pump();
            Expect(Capture() == settled, session.Closed
                ? "closed owner pumps remain inert without certifying worker completion"
                : "settled owner pumps add no I/O, publication, recovery or failure");
            Expect(mode == Diagnostics.Off ? diagnostics.IsEmpty : !diagnostics.IsEmpty,
                "disabled evidence stays absent and a throwing subscriber cannot suppress the subsequent recorder");
            return settled;

            GameSnapshot Change()
            {
                switch (schedule.Change) {
                    case Transition.Newer:
                        var newer = Next(initial, 30, "external-newer");
                        Expect(session.Accept(newer), "the newer same-audience observation is accepted while dispatch is held");
                        return newer;
                    case Transition.Stale:
                        Expect(!session.Accept(Next(initial, 9, "stale")) && ReferenceEquals(session.State, initial),
                            "an older external observation cannot change operation ownership");
                        return initial;
                    case Transition.Reset:
                        session.Reset();
                        var replacement = Next(initial with { Id = "replacement-game" }, 1, "replacement");
                        Expect(session.State == null && session.Accept(replacement),
                            "reset retires the old game and binds a different explicit game");
                        return replacement;
                    case Transition.ViewerRoundTrip:
                        var away = Next(initial with { ViewerId = 1 }, 12, "away");
                        var back = Next(initial, 13, "back");
                        Expect(session.Accept(away) && session.Accept(back),
                            "an explicit viewer round trip retires the earlier operation epoch");
                        return back;
                    default:
                        session.Close(); return initial;
                }
            }
            Transcript Capture() => new(string.Join(";", port.Commands.Select(command => command.GameId + ":" + ((PassPriority)command).DecisionId)),
                port.Reads, string.Join(";", publications), string.Join(";", failures.Select(failure =>
                    $"{failure.Phase}:{failure.Context.Epoch}:{failure.Context.GameId}:{failure.Context.ViewerId}:{failure.Context.Revision}:{failure.Context.BoardRevision}:{failure.Context.DecisionId}")),
                Describe(session.State), session.Closed, completions, recoveries, session.RecoveryState.ToString());
        } finally { port.ReleaseOutstanding(); session.Close(); }
    }

    private static async Task Settle(ClientSession session)
    {
        using var deadline = new CancellationTokenSource(HangGuard);
        do {
            deadline.Token.ThrowIfCancellationRequested();
            session.Pump();
            if (session.Closed || !session.Busy && !session.Polling) return;
            await session.WaitForQueuedWorkAsync(deadline.Token);
        } while (true);
    }
    private static GameSnapshot State() => new("recovery-schedule", "playing", null, null, 0, 1, "Main", "MAIN1", 0,
        [], [], null, new("initial", new(), new PriorityPayload(new("Pass", true)))) { Revision = 10, BoardRevision = 20 };
    private static GameSnapshot Next(GameSnapshot state, long revision, string decision)
        => state with { Revision = revision, BoardRevision = revision + 10, Decision = state.Decision! with { Id = decision } };
    private static string Describe(GameSnapshot? state)
        => state == null ? "none" : $"{state.Id}:{state.ViewerId}:{state.Revision}:{state.BoardRevision}:{state.Decision?.Id}";
    private sealed class DispatchGate
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ReadGate
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<GameSnapshot?> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Port(GameSnapshot fallback) : IMatchConnection
    {
        internal readonly ConcurrentQueue<GameCommand> Commands = new();
        internal DispatchGate Dispatch = new();
        internal ReadGate Read = new();
        private int reads;
        internal int Reads => Volatile.Read(ref reads);
        internal void ArmDispatch() => Dispatch = new();
        internal void ArmRead() => Read = new();
        public Task ExecuteAsync(GameCommand command)
        {
            Commands.Enqueue(command); var gate = Dispatch; gate.Started.TrySetResult(); return gate.Reply.Task;
        }
        public Task<GameSnapshot?> ObserveAsync()
        {
            Interlocked.Increment(ref reads); var gate = Read; gate.Started.TrySetResult(); return gate.Reply.Task;
        }
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("Recovery must not concede the match");
        internal void ReleaseOutstanding() { Dispatch.Reply.TrySetResult(); Read.Reply.TrySetResult(fallback); }
    }
}
