using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;

namespace Mana.Table;

internal static partial class PolledMixedLobbyEngineJourney
{
    private sealed record PresenceCohort(int Actor, PublicInspectionContext Context, string ClockId, bool Thinking);

    // Admission is deliberately separate from the optional exchange. A command
    // ACK and an earlier coherent board cannot identify the next input's clock.
    private sealed class PresencePreparation
    {
        private int[] polls = [];
        internal PresenceCohort? Candidate { get; private set; }
        internal bool Ready { get; private set; }
        internal bool ShouldHoldReply(GameSnapshot? state, bool baselineHeld)
            => baselineHeld || polls.Length > 0 && !Ready && state?.Decision?.Payload is PriorityPayload;
        internal void Synchronize(int[] currentPolls) { polls = [.. currentPolls]; Candidate = null; Ready = false; }
        internal PresenceCohort? Update(PresenceCohort? current, int[] currentPolls)
        {
            if (current == null) return null;
            if (Candidate is { } prior && (prior.Actor != current.Actor || prior.Context != current.Context || prior.ClockId != current.ClockId)) {
                Synchronize(currentPolls); return null;
            }
            if (currentPolls.Length != polls.Length || currentPolls.Where((value, index) => value <= polls[index]).Any()) return null;
            if (Candidate == null) {
                Candidate = current; polls = [.. currentPolls]; return current;
            }
            Ready = current.Thinking;
            return null;
        }
    }

    internal static void CheckPresencePreparation(Action<bool, string> check)
    {
        var preparation = new PresencePreparation();
        var context = new PublicInspectionContext("old-priority", 20, 0);
        var old = new PresenceCohort(0, context, "old-clock", true);
        var next = new PresenceCohort(1, new("fresh-priority", 21, 1), "fresh-clock", false);
        preparation.Synchronize([4, 4]);
        check(preparation.Update(old, [4, 4]) == null && preparation.Candidate == null && !preparation.Ready,
            "A prior command receipt and already accepted old priority clock cannot start the real presence hold before fresh ordinary polls");
        check(preparation.Update(next, [5, 4]) == null && preparation.Candidate == null,
            "One human's fresh priority cannot borrow the other human's older polled cohort");
        check(preparation.Update(next, [5, 5]) == next && !preparation.Ready,
            "Two fresh matching human polls select the actual new clock instead of pinning the old acknowledged input");
        var replaced = next with { Context = new("replacement-priority", 22, 1), ClockId = "replacement-clock" };
        check(preparation.Update(replaced, [6, 6]) == null && preparation.Candidate == null && !preparation.Ready,
            "Replacing an input before the thinking readback retires its clock and resynchronizes instead of holding forever for that old ID");
        check(preparation.Update(replaced, [7, 7]) == replaced && !preparation.Ready
            && preparation.Update(replaced with { Thinking = true }, [8, 7]) == null && !preparation.Ready,
            "The replacement clock is presented once and cannot freeze a baseline before both post-presentation polls arrive");
        preparation.Update(replaced with { Thinking = true }, [8, 8]);
        check(preparation.Ready && preparation.Candidate?.ClockId == "replacement-clock",
            "Both fresh thinking readbacks admit the actual replacement clock without replaying a retired presentation");
        var priority = new GameSnapshot("preparation", "playing", null, null, 0, 1, "Main", "MAIN1", 0,
            [], [], null, new("old-priority", new(), new PriorityPayload(new("Pass", true))));
        var required = priority with { Decision = new("actual-attackers", new(), new AttackPayload(new("No attackers", true))) };
        var blocked = required with { Decision = new("blocked-attackers", new(), new AttackPayload(new("No attackers", false))) };
        preparation.Synchronize([10, 10]);
        check(preparation.ShouldHoldReply(priority, false) && !preparation.ShouldHoldReply(required, false)
            && DecisionCommands.Confirm(required) is CompleteAttackers { DecisionId: "actual-attackers" }
            && DecisionCommands.Confirm(blocked) == null && preparation.ShouldHoldReply(required, true),
            "Synchronizing holds priority while required declarations retain their exact ordinary command permission; disabled replies stay disabled and an established baseline holds all replies");
    }

    // Reuses the ordinary real lobby prefix. Only the scripted human replies
    // pause; ordinary polling, the actual AI host and decision time continue.
    private sealed class PresenceProbe(PlayerOwner[] players, PolledLobbyPorts.BotPort bots, Stopwatch elapsed)
    {
        private sealed record HistoryPoint(long FirstSequence, long LastSequence, long NextSequence);
        private sealed record HoldPoint(int Viewer, long Board, string? Decision, string RulesHash,
            DecisionClock Clock, HistoryPoint History);
        private sealed record Seen(double ElapsedSeconds, int Observer, string Subject, int RemainingMilliseconds);
        private readonly List<Seen> seen = [];
        private readonly HashSet<string> remoteSubjects = new(StringComparer.Ordinal);
        private readonly PresencePreparation preparation = new();
        private string stage = "waiting", clockId = "";
        private int actor = -1;
        private PolledLobbyPorts.PlayerReceiptCounts[] acceptedBefore = [];
        private PublicInspectionContext? context;
        private TableInspectionSubject[] subjects = [];
        private HoldPoint[] before = [], after = [];
        private double heldAt, resumedAt, completedAt;
        private long resumedTicks;
        private int botAcceptedBefore;
        private bool presenceExecutingAtResume;
        private long measurementStartedTicks;
        private int[] exchangesBefore = [];
        internal bool ShouldHoldReply(GameSnapshot? state) => preparation.ShouldHoldReply(state, stage == "holding");
        internal bool Completed => stage == "completed";
        internal bool HoldIntegrityVerified { get; private set; }
        internal bool TrafficVerified { get; private set; }
        internal bool ResumedReceiptVerified { get; private set; }

        internal async Task UpdateAsync(bool ordinaryPrefixReady, DateTime deadline)
        {
            if (stage == "waiting") {
                if (!ordinaryPrefixReady) return;
                Need(players.All(player => player.Inspection.Supported),
                    "The current real host and TCP guest must support public table presence; an older provider is not this gate.");
                preparation.Synchronize(players.Select(player => player.PollCallbacks).ToArray());
                measurementStartedTicks = Stopwatch.GetTimestamp();
                exchangesBefore = players.Select(player => player.InspectionPort.Started).ToArray();
                stage = "synchronizing";
                return;
            }
            if (stage is "synchronizing" or "preparing") {
                // A command's first published board can still be followed by
                // its new priority cohort. Wait for real fresh polls on both
                // audiences before freezing any authority or rules facts.
                var requested = preparation.Update(CurrentCohort(), players.Select(player => player.PollCallbacks).ToArray());
                if (requested != null) {
                    actor = requested.Actor; context = requested.Context; clockId = requested.ClockId;
                    var state = players[actor].Client.State!;
                    Need(state.Tempo is { Settings.Enabled: true } && state.Tempo.Settings.Expiry == TempoExpiry.Warn,
                        "The real presence gate needs an enabled warning clock, without automatic timeout actions.");
                    stage = "preparing";
                    await players[actor].Engine.PresentedAsync(state.Id, clockId).WaitAsync(Remaining(deadline));
                    return;
                }
                if (!preparation.Ready) { stage = preparation.Candidate == null ? "synchronizing" : "preparing"; return; }
                subjects = players[actor].Client.State!.Players.SelectMany(player => player.Zone("Battlefield").Cards)
                    .Where(card => !card.FaceDown && !card.PhasedOut && card.VisualId.Length > 0)
                    .Select(card => new TableInspectionSubject(TableInspectionKind.BattlefieldCard, card.VisualId))
                    .Distinct().Where(subject => players.All(player => TablePresenceSession.PublicSubject(player.Client.State!, subject)))
                    .Take(2).ToArray();
                Need(subjects.Length == 2, "The developed real table must disclose two actual public inspection occurrences to both humans.");
                var histories = await Histories(deadline);
                before = players.Select((player, index) => Point(player.Client.State!, histories[index])).ToArray();
                acceptedBefore = players.Select(player => player.Port.ReceiptCounts).ToArray();
                botAcceptedBefore = bots.CaptureProcessing().Accepted;
                heldAt = elapsed.Elapsed.TotalSeconds;
                stage = "holding";
            }
            if (stage is not ("holding" or "resuming")) return;
            double now = elapsed.Elapsed.TotalSeconds;
            foreach (var player in players) {
                player.Inspection.Observe(player.Client.State);
                var desired = stage == "holding" && player.Index == actor
                    ? subjects[(int)((now - heldAt) / .5) % subjects.Length] : null;
                player.Inspection.Update(now, desired, true,
                    !player.Client.Busy && !player.Client.Polling && !player.Client.IsAdvancing);
                Need(player.InspectionPort.MaximumExecuting <= 1, "A human presence session dispatched overlapping actual provider tasks.");
            }
            foreach (var cue in players[1 - actor].Inspection.Visible(now)) {
                if (cue.PlayerId == context!.PlayerId && cue.ContextId == context.Id
                    && subjects.Contains(cue.Subject) && remoteSubjects.Add(cue.Subject.Id))
                    seen.Add(new(now - heldAt, players[1 - actor].Index, cue.Subject.Id, cue.RemainingMilliseconds));
            }
            if (stage == "holding") {
                CheckHeld();
                if (now - heldAt < 1.75) return;
                var histories = await Histories(deadline);
                after = players.Select((player, index) => Point(player.Client.State!, histories[index])).ToArray();
                CheckHeld();
                Need(remoteSubjects.Count == subjects.Length,
                    "The actual other human must receive both changing public inspection subjects during the bounded priority hold.");
                Need(before.Select(point => point.History).SequenceEqual(after.Select(point => point.History)),
                    "Public presence must not append, remove or move either real human history cursor.");
                Need(after[actor].Clock.RemainingMs < before[actor].Clock.RemainingMs,
                    "The actual held decision clock must continue to run while inspection is shared.");
                HoldIntegrityVerified = true;
                resumedAt = elapsed.Elapsed.TotalSeconds;
                resumedTicks = Stopwatch.GetTimestamp();
                presenceExecutingAtResume = players.Any(player => player.InspectionPort.Executing > 0);
                stage = "resuming";
            }
            if (stage == "resuming" && players[actor].Port.InputAccepted > acceptedBefore[actor].Inputs
                && players.All(player => !player.Client.Busy && player.InputReceiptsReconciled && player.Port.ReceiptCounts.Conserved)
                && players[actor].Client.State?.PublicInspection != context) {
                ResumedReceiptVerified = true;
                completedAt = elapsed.Elapsed.TotalSeconds;
                VerifyTraffic();
                foreach (var player in players) player.Inspection.Suspend();
                stage = "completed";
            }
        }

        private PresenceCohort? CurrentCohort()
        {
            int currentActor = Array.FindIndex(players, player => player.Client.State?.Decision?.Payload is PriorityPayload);
            if (currentActor < 0) return null;
            var first = players[currentActor].Client.State;
            if (first is not { Decision.Payload: PriorityPayload, PublicInspection: { } current }
                || current.PlayerId != first.ViewerId || current.BoardRevision != first.BoardRevision) return null;
            if (players.Any(player => player.Client.State is not { } state || state.Id != first.Id
                || state.BoardRevision != first.BoardRevision || state.PublicInspection != current)) return null;
            var clocks = players.Select(player => player.Client.State!.Tempo?.Clocks.SingleOrDefault(clock => clock.PlayerId == first.ViewerId)).ToArray();
            if (clocks.Any(clock => clock == null || clock.Id.Length == 0 || clock.Id != clocks[0]?.Id)) return null;
            return new(currentActor, current, clocks[0]!.Id, clocks.All(clock => clock!.Phase == TempoPhase.Thinking));
        }

        private void CheckHeld()
        {
            var botProcessing = bots.CaptureProcessing();
            Need(botProcessing.Accepted == botAcceptedBefore && botProcessing.Executing == 0,
                "Public presence must not admit an AI action while the real human holds ordinary priority.");
            for (int index = 0; index < players.Length; index++) {
                var state = players[index].Client.State!;
                var receipts = players[index].Port.ReceiptCounts;
                Need(receipts.Conserved && receipts.Inputs == acceptedBefore[index].Inputs && receipts.Controls == acceptedBefore[index].Controls
                    && state.PublicInspection == context && RulesHash(state) == before[index].RulesHash,
                    "Presence changed a held human command count, rules board, decision, stack, activity or execution facts.");
                var clock = Clock(state);
                Need(clock.Id == before[index].Clock.Id && clock.Id == clockId
                    && clock.Phase == before[index].Clock.Phase && clock.LimitMs == before[index].Clock.LimitMs
                    && clock.Strikes == before[index].Clock.Strikes && clock.RemainingMs <= before[index].Clock.RemainingMs,
                    "Presence replaced, refilled or altered the original real human decision clock.");
            }
        }

        private async Task<HistoryPoint[]> Histories(DateTime deadline)
        {
            var values = await Task.WhenAll(players.Select(player => player.Engine.HistoryAsync(player.Client.State!.Id, 0, 1)))
                .WaitAsync(Remaining(deadline));
            for (int index = 0; index < values.Length; index++) Need(values[index].GameId == players[index].Client.State!.Id
                && values[index].ViewerId == players[index].Client.State!.ViewerId, "The presence probe received a foreign history audience.");
            return values.Select(value => new HistoryPoint(value.FirstSequence, value.LastSequence, value.NextSequence)).ToArray();
        }

        private DecisionClock Clock(GameSnapshot state) => state.Tempo?.Clocks.SingleOrDefault(clock => clock.PlayerId == players[actor].Client.State!.ViewerId)
            ?? throw new InvalidDataException("The real held human priority clock was not projected.");

        private HoldPoint Point(GameSnapshot state, HistoryPoint history)
            => new(state.ViewerId, state.BoardRevision, state.Decision?.Id, RulesHash(state), Clock(state), history);

        private static string RulesHash(GameSnapshot state)
        {
            // Audience Revision may refresh normally. Timer elapsed is checked
            // separately; no state delta is allowed to masquerade as a cue.
            var text = JsonSerializer.Serialize(new {
                publicRules = ForgePublicProjectionFacts.Capture(state), state.PublicInspection,
                decision = state.Decision == null ? null : new { state.Decision.Id,
                    payload = JsonSerializer.SerializeToElement(state.Decision.Payload, state.Decision.Payload.GetType()) },
                state.PriorityPlayerId, state.DecisionOwnerId, state.ActionClass, state.Activity, state.Executions,
                state.StackInterruptionVersion, state.StackActionVersion, state.ResolutionChoiceVersion,
                tempoSettings = state.Tempo?.Settings, timedOutPlayers = state.Tempo?.TimedOutPlayers
            });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }

        private void VerifyTraffic()
        {
            // The inclusive first sample allows one request at either end of
            // the measured interval. Actual inter-dispatch gaps are retained,
            // without mistaking worker scheduling jitter for a faster clock.
            int maximum = 1 + (int)Math.Ceiling((completedAt - heldAt) * 4);
            foreach (var player in players) {
                var calls = player.InspectionPort.Exchanges.Where(call => call.StartedTicks >= measurementStartedTicks).ToArray();
                Need(player.InspectionPort.MaximumExecuting <= 1 && player.InspectionPort.Started - exchangesBefore[player.Index] <= maximum,
                    "Real presence exceeded its single-flight four-Hz interval budget.");
                Need(calls.Length > 0 && calls.All(call => call.Outcome is "Current" or "Superseded")
                    && (player.Index == actor || calls.All(call => call.Subject == null)),
                    "An observer published tentative inspection or a current provider failed presence exchange.");
            }
            TrafficVerified = true;
        }

        internal async Task CloseAsync(TimeSpan budget)
        {
            foreach (var player in players) player.Inspection.Close();
            // Do not cancel a rules transport to retire optional attention.
            long started = Stopwatch.GetTimestamp();
            while (players.Any(player => player.InspectionPort.Executing > 0)) {
                if (Stopwatch.GetElapsedTime(started) >= budget)
                    throw new TimeoutException("The real presence provider did not settle within the existing cleanup budget.");
                await Task.Delay(1);
            }
        }

        internal object Evidence => new {
            stage, actorIndex = actor, context, subjects, heldAt, resumedAt, completedAt,
            holdSeconds = resumedAt > 0 ? resumedAt - heldAt : 0,
            HoldIntegrityVerified, TrafficVerified, ResumedReceiptVerified, presenceExecutingAtResume,
            botAcceptedBefore,
            measurementStartedTicks, exchangesBefore,
            observations = seen.ToArray(), before, after, acceptedBefore,
            scope = "Actual TablePresenceSession and ForgeEngine host/TCP ports. Contract byte totals exclude transport envelopes; latencies are observations, not a zero head-of-line guarantee.",
            endpoints = players.Select(player => new { player.Index,
                player.InspectionPort.Started, player.InspectionPort.MaximumExecuting, executing = player.InspectionPort.Executing,
                exchanges = player.InspectionPort.Exchanges.Where(call => call.StartedTicks >= measurementStartedTicks).ToArray(),
                resumedCommands = player.Port.CommandLatencies.Where(command => resumedTicks > 0
                    && command.StartedTicks >= resumedTicks).ToArray()
            })
        };
        private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        private static TimeSpan Remaining(DateTime deadline) => TimeSpan.FromMilliseconds(Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds));
    }
}
