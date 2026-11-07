using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mana.Contracts;

namespace Mana.Table;

internal static partial class PolledMixedLobbyEngineJourney
{
    // A real combat input from the ordinary prefix, not an authored decision or
    // extra rules command. Only scripted replies pause; polling and clocks run.
    private sealed class ChoiceProbe(PlayerOwner[] players, PolledLobbyPorts.BotPort bots, Stopwatch elapsed)
    {
        private sealed record HoldPoint(int Viewer, long Board, string? Decision, string RulesHash, DecisionClock Clock,
            long FirstSequence, long LastSequence, long NextSequence);
        private string stage = "waiting";
        private int actor = -1;
        private PublicChoiceContext? context;
        private TableChoicePreview? aim;
        private string clockId = "";
        private int[] polls = [], exchangesBefore = [];
        private PolledLobbyPorts.PlayerReceiptCounts[] acceptedBefore = [];
        private HoldPoint[] before = [], after = [];
        private int botAcceptedBefore;
        private double heldAt, resumedAt, completedAt;
        private long holdTicks, clearingTicks, resumedTicks;
        internal bool Completed => stage == "completed";
        internal bool SawSource { get; private set; }
        internal bool SawTarget { get; private set; }
        internal bool SawClear { get; private set; }
        internal bool HoldIntegrityVerified { get; private set; }
        internal bool TrafficVerified { get; private set; }
        internal bool ResumedReceiptVerified { get; private set; }

        internal bool ShouldHoldReply(GameSnapshot? state)
            => !Completed && stage != "resuming" && state != null && Pick(state) != null;

        internal async Task UpdateAsync(DateTime deadline)
        {
            if (Completed) return;
            if (stage == "waiting") {
                actor = Array.FindIndex(players, player => player.Client.State is { } state && Pick(state) != null);
                if (actor < 0) return;
                var state = players[actor].Client.State!;
                Need(players.All(player => player.InspectionPort.SupportsTableChoices), "Both current real presence adapters must negotiate tentative choices.");
                context = state.PublicChoice; aim = Pick(state);
                polls = players.Select(player => player.PollCallbacks).ToArray(); stage = "synchronizing";
                return;
            }
            if (stage is "synchronizing" or "preparing") {
                if (players[actor].Client.State?.PublicChoice != context) { stage = "waiting"; return; }
                if (!CoherentFresh()) return;
                var state = players[actor].Client.State!;
                var clock = Clock(state);
                if (stage == "synchronizing") {
                    Need(state.Tempo is { Settings.Enabled: true } && state.Tempo.Settings.Expiry == TempoExpiry.Warn,
                        "Tentative choices use the real warning decision clock without automatic actions.");
                    clockId = clock.Id;
                    await players[actor].Engine.PresentedAsync(state.Id, clockId).WaitAsync(Remaining(deadline));
                    polls = players.Select(player => player.PollCallbacks).ToArray(); stage = "preparing"; return;
                }
                if (players.Any(player => Clock(player.Client.State!).Id != clockId)) { stage = "waiting"; return; }
                if (players.Any(player => Clock(player.Client.State!).Phase != TempoPhase.Thinking)) return;
                if (bots.CaptureProcessing().Executing != 0 || players.Any(player => player.Client.Busy)) return;
                before = await Points(deadline);
                acceptedBefore = players.Select(player => player.Port.ReceiptCounts).ToArray();
                exchangesBefore = players.Select(player => player.InspectionPort.Started).ToArray();
                botAcceptedBefore = bots.CaptureProcessing().Accepted;
                heldAt = elapsed.Elapsed.TotalSeconds; holdTicks = Stopwatch.GetTimestamp(); stage = "source";
            }
            if (stage is "source" or "aim" or "clearing") {
                double now = elapsed.Elapsed.TotalSeconds;
                foreach (var player in players) {
                    player.Inspection.Observe(player.Client.State);
                    TableChoicePreview? choice = player.Index != actor || stage == "clearing" ? null
                        : stage == "source" ? aim! with { Target = null } : aim;
                    player.Inspection.Update(now, null, true,
                        !player.Client.Busy && !player.Client.Polling && !player.Client.IsAdvancing, choice);
                }
                CheckHeld();
                var observer = players[1 - actor];
                var visible = observer.Inspection.VisibleChoices(now);
                if (stage == "source" && visible.Any(cue => cue.ContextId == context!.Id && cue.Preview == aim! with { Target = null })) {
                    SawSource = true; stage = "aim";
                } else if (stage == "aim" && visible.Any(cue => cue.ContextId == context!.Id && cue.Preview == aim)) {
                    SawTarget = true; stage = "clearing"; clearingTicks = Stopwatch.GetTimestamp();
                } else if (stage == "clearing" && visible.IsEmpty && now - heldAt >= 1) {
                    var clear = players[actor].InspectionPort.Exchanges.Where(call => call.StartedTicks >= clearingTicks
                        && call.ChoiceContextId == context!.Id && call.Choice == null && call.Outcome == "Current")
                        .OrderBy(call => call.StartedTicks).FirstOrDefault();
                    if (clear == null || !observer.InspectionPort.Exchanges.Any(call => call.StartedTicks > clear.StartedTicks
                        && call.Response is { } frame && frame.BoardRevision == context!.BoardRevision && frame.Choices.IsEmpty)) return;
                    after = await Points(deadline); CheckHeld();
                    Need(before.Select(point => (point.FirstSequence, point.LastSequence, point.NextSequence))
                        .SequenceEqual(after.Select(point => (point.FirstSequence, point.LastSequence, point.NextSequence))),
                        "Tentative combat choices cannot change either actual human history cursor.");
                    Need(after[actor].Clock.RemainingMs < before[actor].Clock.RemainingMs,
                        "The real presented attack clock must run while its human considers a public aim.");
                    SawClear = true; HoldIntegrityVerified = true;
                    resumedAt = elapsed.Elapsed.TotalSeconds; resumedTicks = Stopwatch.GetTimestamp(); stage = "resuming";
                }
            }
            if (stage == "resuming" && players[actor].Port.InputAccepted > acceptedBefore[actor].Inputs
                && players[actor].Client.State?.PublicChoice != context
                && players.All(player => !player.Client.Busy && player.InputReceiptsReconciled && player.Port.ReceiptCounts.Conserved)) {
                completedAt = elapsed.Elapsed.TotalSeconds; ResumedReceiptVerified = true;
                int maximum = 1 + (int)Math.Ceiling((resumedAt - heldAt) * 4);
                foreach (var player in players) {
                    var calls = player.InspectionPort.Exchanges.Where(call => call.StartedTicks >= holdTicks).ToArray();
                    Need(player.InspectionPort.MaximumExecuting <= 1 && calls.Length <= maximum && calls.Length > 0
                        && player.InspectionPort.Started - exchangesBefore[player.Index] <= maximum
                        && calls.All(call => call.Outcome is "Current" or "Superseded")
                        && (player.Index == actor || calls.All(call => call.Choice == null)),
                        "Tentative combat sharing exceeded its shared single-flight four-Hz transport budget or impersonated the actor.");
                    player.Inspection.Suspend();
                }
                TrafficVerified = true; stage = "completed";
            }
        }

        private bool CoherentFresh()
        {
            if (context == null || !players.All(player => player.PollCallbacks > polls[player.Index]
                && player.Client.State is { } state && state.Id == players[actor].Client.State?.Id
                && state.PublicChoice == context && state.BoardRevision == context.BoardRevision && PublicChoiceFacts.IsCurrent(state))) return false;
            var clocks = players.Select(player => player.Client.State!.Tempo?.Clocks.SingleOrDefault(clock => clock.PlayerId == context.PlayerId)).ToArray();
            return clocks.All(clock => clock is { Id.Length: > 0 } && clock.Id == clocks[0]?.Id);
        }

        private static TableChoicePreview? Pick(GameSnapshot state)
        {
            if (state.Decision?.Payload is not AttackPayload || state.PublicChoice is not { Kind: PublicChoiceKind.Attack } context
                || context.PlayerId != state.ViewerId || !PublicChoiceFacts.IsCurrent(state)) return null;
            foreach (var option in state.Combat?.AttackOptions ?? []) {
                var source = state.Viewer!.Zone("Battlefield").Cards.FirstOrDefault(card => card.CombatId == option.CardId && !card.FaceDown && !card.PhasedOut);
                if (source == null) continue;
                foreach (var defender in option.Defenders) {
                    TableChoiceTarget? target = defender.Kind == "player"
                        ? new(TableChoiceTargetKind.Player, defender.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        : state.Players.SelectMany(player => player.Zone("Battlefield").Cards).FirstOrDefault(card => card.CombatId == defender.Id) is { } card
                            ? new(TableChoiceTargetKind.BattlefieldCard, card.VisualId) : null;
                    var preview = new TableChoicePreview(new(TableInspectionKind.BattlefieldCard, source.VisualId), target);
                    if (target != null && PublicChoiceFacts.AllowsPreview(state, preview)) return preview;
                }
            }
            return null;
        }

        private void CheckHeld()
        {
            var processing = bots.CaptureProcessing();
            Need(processing.Executing == 0 && processing.Accepted == botAcceptedBefore,
                "A bot command was admitted while the actual human was holding combat consideration.");
            for (int index = 0; index < players.Length; index++) {
                var state = players[index].Client.State!; var clock = Clock(state); var prior = before[index].Clock;
                var receipts = players[index].Port.ReceiptCounts;
                Need(receipts.Conserved && receipts.Inputs == acceptedBefore[index].Inputs && receipts.Controls == acceptedBefore[index].Controls
                    && state.PublicChoice == context && RulesHash(state) == before[index].RulesHash,
                    "Tentative combat presence changed a human receipt count, rules board, combat, decision or public activity.");
                Need(clock.Id == clockId && clock.Id == prior.Id && clock.Phase == prior.Phase && clock.LimitMs == prior.LimitMs
                    && clock.Strikes == prior.Strikes && clock.RemainingMs <= prior.RemainingMs,
                    "Tentative combat presence replaced, refilled or altered the actual original declaration clock.");
            }
        }
        private async Task<HoldPoint[]> Points(DateTime deadline)
        {
            var histories = await Task.WhenAll(players.Select(player => player.Engine.HistoryAsync(player.Client.State!.Id, 0, 1)))
                .WaitAsync(Remaining(deadline));
            return players.Select((player, index) => {
                var state = player.Client.State!; var history = histories[index];
                Need(history.GameId == state.Id && history.ViewerId == state.ViewerId, "Tentative combat received a foreign history audience.");
                return new HoldPoint(state.ViewerId, state.BoardRevision, state.Decision?.Id, RulesHash(state), Clock(state),
                    history.FirstSequence, history.LastSequence, history.NextSequence);
            }).ToArray();
        }
        private DecisionClock Clock(GameSnapshot state) => state.Tempo?.Clocks.SingleOrDefault(clock => clock.PlayerId == context!.PlayerId)
            ?? throw new InvalidDataException("The real combat choice clock was not projected.");
        private static string RulesHash(GameSnapshot state)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
                publicRules = ForgePublicProjectionFacts.Capture(state), state.PublicChoice, state.PublicInspection,
                decision = state.Decision == null ? null : new { state.Decision.Id, payload = JsonSerializer.SerializeToElement(state.Decision.Payload, state.Decision.Payload.GetType()) },
                state.PriorityPlayerId, state.DecisionOwnerId, state.ActionClass, state.Activity, state.Executions,
                state.StackInterruptionVersion, state.StackActionVersion, state.ResolutionChoiceVersion,
                tempoSettings = state.Tempo?.Settings, timedOutPlayers = state.Tempo?.TimedOutPlayers
            }))));
        internal object Evidence => new {
            stage, actorIndex = actor, context, aim, heldAt, resumedAt, completedAt, holdTicks, clearingTicks, exchangesBefore,
            SawSource, SawTarget, SawClear, HoldIntegrityVerified, TrafficVerified, ResumedReceiptVerified, before, after, acceptedBefore,
            scope = "Real actor source-only, legal combat aim and clear through actual provider/TCP decoded wire. No tentative rules assignment, hidden source, gameplay command or clock mutation. Times are measured observations, not a separate transport-lane guarantee.",
            endpoints = players.Select(player => new { player.Index, player.InspectionPort.MaximumExecuting,
                exchanges = player.InspectionPort.Exchanges.Where(call => call.StartedTicks >= holdTicks && (resumedTicks == 0 || call.StartedTicks < resumedTicks)).ToArray(),
                resumedCommands = player.Port.CommandLatencies.Where(command => resumedTicks > 0 && command.StartedTicks >= resumedTicks).ToArray()
            })
        };
        private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        private static TimeSpan Remaining(DateTime deadline) => TimeSpan.FromMilliseconds(Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds));
    }
}
