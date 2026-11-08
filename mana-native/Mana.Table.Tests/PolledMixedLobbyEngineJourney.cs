using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Mana.AI;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;
using Mana.Magic;

namespace Mana.Table;

// Actual shipping observation owners: ordinary player polls and one background
// bot host share the real host provider. Scripts answer only their own current
// client state. No rich/private bot observation is borrowed by a human driver.
internal static partial class PolledMixedLobbyEngineJourney
{
    internal static async Task RunStandaloneAsync(ForgePaths paths)
    {
        var engines = Enumerable.Range(0, 2).Select(index => new ForgeEngine(paths with {
            Profile = Path.Combine(paths.Profile, "connection-" + index)
        })).ToArray();
        try {
            await Task.WhenAll(engines.Select(engine => engine.InitializeAsync()));
            var decks = new List<DeckSummary>();
            foreach (var engine in engines) decks.Add(await engine.ImportAsync("Polled owner prefix",
                "Deck\n99 Mountain\nCommander\n1 Rograkh, Son of Rohgahh"));
            await RunAsync(engines, decks.ToArray(), paths.Profile, DateTime.UtcNow.AddMinutes(4), (passed, message) => {
                if (!passed) throw new InvalidDataException(message);
            });
        } finally { foreach (var engine in engines) await engine.DisposeAsync(); }
    }

    internal static async Task RunAsync(ForgeEngine[] engines, DeckSummary[] decks, string profile,
        DateTime deadline, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "polled-mixed-lobby"); Directory.CreateDirectory(directory);
        var started = Stopwatch.StartNew();
        var limit = DateTime.UtcNow.AddSeconds(45);
        if (deadline < limit) limit = deadline;
        var checks = new List<string>();
        void Require(bool condition, string message) {
            check(condition, message); checks.Add(message);
        }
        var botPort = new PolledLobbyPorts.BotPort(engines[0]);
        var ai = new AiTableHost(botPort, _ => new CasualCommanderPolicy(), 261104,
            new(TimeSpan.FromSeconds(3), 2048), diagnostics: botPort);
        var players = engines.Take(2).Select((engine, index) => new PlayerOwner(engine, index)).ToArray();
        var presence = new PresenceProbe(players, botPort, started);
        var choices = new ChoiceProbe(players, botPort, started);
        var cohorts = new Dictionary<(string Game, long Board), (string Facts,
            ForgePublicProjectionFacts.ExecutionCorrelation Correlations, HashSet<int> Viewers)>();
        int coherent = 0, steps = 0, guestReadsAtHold = 0, botReadsAtHold = 0, hostCommandsAtHold = 0;
        bool captured = false, released = false, completed = false;
        PolledLobbyPorts.BotPort.ProcessingSnapshot? completedBotProcessing = null;
        Exception? failure = null;
        var cleanupErrors = new List<string>();
        try {
            // Configure background ownership before the first lobby operation,
            // as shipping startup does. NativeSmoke retires its prior fixture.
            await ai.StartAsync();
            var lobby = await engines[0].HostAsync(4, false);
            string port = lobby.Addresses.First(address => address.Url.Contains(':')).Url.Split(':').Last();
            await engines[1].JoinAsync("127.0.0.1:" + port);
            lobby = await engines[0].SetSeatAsync(lobby.TableId, 2, "AI", "green");
            lobby = await engines[0].SetSeatAsync(lobby.TableId, 3, "AI", "red");
            lobby = await engines[0].ConfigureTempoAsync(lobby.TableId, TempoPreset.Standard, TempoExpiry.Warn);
            foreach (int index in new[] { 0, 1 }) {
                await engines[index].SelectDeckAsync(new(lobby.TableId, decks[index].Id, decks[index].LibraryToken));
                await engines[index].SetReadyAsync(lobby.TableId, true, lobby.Tempo!.Revision);
            }
            while (!(lobby = await engines[0].LobbyAsync()).CanStart) { Guard(); await Task.Delay(5); }
            Require(lobby.Seats.Count(seat => seat.Type == "AI") == 2
                && lobby.Seats.Where(seat => seat.Type == "AI").Select(seat => seat.OpponentId).Distinct().Count() == 2,
                "The polled prefix starts one actual host/TCP guest and two distinct configured AI decks");
            foreach (int index in new[] { 0, 1 }) players[index].Client.BindLobby(await engines[index].LobbyAsync());
            await engines[0].StartAsync();

            while (!completed) {
                Guard(); steps++;
                foreach (var player in players) player.Client.Pump();
                foreach (var player in players) player.RequireHealthy();
                if (ai.LastFault != null) throw new InvalidDataException("The background host faulted: " + JsonSerializer.Serialize(ai.LastFault));
                if (players[0].Port.Captured.Task.IsCompletedSuccessfully && !captured) {
                    captured = true; guestReadsAtHold = players[1].PollCallbacks; botReadsAtHold = botPort.Reads;
                    hostCommandsAtHold = players[0].Port.InputAcceptedAtCapture;
                }
                if (captured && !released && players[0].Port.InputAccepted > hostCommandsAtHold && !players[0].Client.Busy
                    && players[1].PollCallbacks > guestReadsAtHold && botPort.Reads > botReadsAtHold) {
                    players[0].Port.Release.TrySetResult(); released = true;
                }
                foreach (var player in players) {
                    if (player.Client.State is { Status: "playing", Players.Length: 4 } state) {
                        var key = (state.Id, state.BoardRevision);
                        string facts = ForgePublicProjectionFacts.Capture(state);
                        var correlation = ForgePublicProjectionFacts.CaptureExecutionCorrelations(state);
                        if (!correlation.Valid) throw new InvalidDataException("A polled audience published invalid execution references.");
                        if (cohorts.TryGetValue(key, out var prior)) {
                            if (prior.Facts != facts) throw new InvalidDataException("The polled TCP audiences disagreed on a shared public board.");
                            var merged = ForgePublicProjectionFacts.MergeExecutionCorrelations(prior.Correlations, correlation)
                                ?? throw new InvalidDataException("The polled audiences contradicted a shared stack correlation.");
                            prior.Viewers.Add(state.ViewerId); cohorts[key] = (facts, merged, prior.Viewers);
                        } else cohorts.Add(key, (facts, correlation, [state.ViewerId]));
                    }
                    player.Poll();
                }
                foreach (var key in cohorts.Keys.OrderByDescending(key => key.Board).Skip(64).ToArray()) cohorts.Remove(key);

                bool ordinaryComplete = false;
                PolledLobbyPorts.BotPort.ProcessingSnapshot? qualifyingBotProcessing = null;
                var host = players[0].Client.State; var guest = players[1].Client.State;
                if (host is { Players.Length: 4 } && guest is { Players.Length: 4 } && host.Id == guest.Id
                    && host.ViewerId != guest.ViewerId) {
                    var humanIds = players.Select(player => player.Client.State!.ViewerId).ToHashSet();
                    var bots = host.Players.Where(player => !humanIds.Contains(player.Id)).ToArray();
                    bool humansDeveloped = players.All(player => player.Client.State!.Viewer!.Zone("Battlefield").Cards.Any(card => card.Type.Contains("Land", StringComparison.Ordinal))
                        && player.Client.State.Viewer.Zone("Battlefield").Cards.Any(card => card.Name == "Rograkh, Son of Rohgahh")
                        && player.Port.InputAccepted >= 3);
                    var botProcessing = botPort.CaptureProcessing();
                    bool botsDeveloped = bots.Length == 2 && bots.All(bot => bot.Zone("Battlefield").Cards.Any(card => card.Type.Contains("Land", StringComparison.Ordinal))
                        && botProcessing.ProcessedNonPassBySeat.GetValueOrDefault(bot.Id) > 0);
                    bool sameBoard = host.BoardRevision == guest.BoardRevision
                        && cohorts.TryGetValue((host.Id, host.BoardRevision), out var shared) && shared.Viewers.Count == 2;
                    if (sameBoard && humansDeveloped && botsDeveloped) coherent++;
                    ordinaryComplete = released && !players[0].Client.Polling && humansDeveloped && botsDeveloped && coherent > 0
                        && players.All(player => !player.Client.Busy && player.InputReceiptsReconciled && player.Port.ReceiptCounts.Conserved)
                        && botProcessing.Executing == 0
                        && botProcessing.ProcessedAccepted == botProcessing.Accepted && botPort.Faults.IsEmpty
                        && players.Any(player => player.Client.State?.Decision?.Payload is PriorityPayload);
                    if (ordinaryComplete) qualifyingBotProcessing = botProcessing;
                }
                await choices.UpdateAsync(limit);
                await presence.UpdateAsync(ordinaryComplete && choices.Completed, limit);
                completed = ordinaryComplete && choices.Completed && presence.Completed;
                if (completed) completedBotProcessing = qualifyingBotProcessing;
                if (!completed) foreach (var player in players)
                    if (!choices.ShouldHoldReply(player.Client.State) && !presence.ShouldHoldReply(player.Client.State)) player.Step();
                await Task.Delay(1);
            }
            Require(players.All(player => player.Client.ObservationDelivery == ObservationDeliveryMode.Polled
                    && player.PollCallbacks > 0 && player.Port.Reads > 0 && player.InputReceiptsReconciled && player.Port.ReceiptCounts.Conserved),
                "Both real player endpoints use default ordinary lobby/match polling and reconcile every locally admitted command's provider receipt");
            Require(captured && released && players[0].HeldPublications == 0
                    && players[1].PollCallbacks > guestReadsAtHold && botPort.Reads > botReadsAtHold,
                "A held real host poll overlaps guest polling and the background AI observer, then cannot overwrite its later command readback");
            Require(coherent > 0 && players.All(player => player.Publications > 0 && player.Port.Failures == 0)
                    && ai.LastFault == null && botPort.Faults.IsEmpty
                    && ai.Histories.All(history => history.Decisions.All(record => record.Outcome != AiDecisionOutcome.ExecutionUnknown)),
                "Both polled humans and both background bots develop the actual table with monotonic private-safe audiences and coherent public state, without unknown command receipts");
            Require(presence.Completed,
                "The real polled humans exchange changing public-card inspection through their actual host/TCP presence ports within the unchanged prefix deadline");
            Require(presence.HoldIntegrityVerified,
                "Presence leaves the held rules board, decision, stack, activity and history unchanged while its original decision clock continues without refill");
            Require(presence.TrafficVerified && presence.ResumedReceiptVerified,
                "Single-flight bounded presence traffic is followed by an ordinary reconciled human command, with actual observed transport latencies retained");
            Require(choices.Completed && choices.SawSource && choices.SawTarget && choices.SawClear,
                "The real declaring human shares a source-only consideration, actual legal aim and explicit clearing with the other TCP audience");
            Require(choices.HoldIntegrityVerified,
                "Tentative combat presence preserves both real histories and rules boards while the exact presented attack clock continues without refill");
            Require(choices.TrafficVerified && choices.ResumedReceiptVerified,
                "The new choice capability remains single-flight within the existing four-Hz presence budget and resumes a reconciled ordinary combat declaration");
        } catch (Exception error) { failure = error; }
        finally {
            // Preserve the failed/current reader and command context before
            // closing its owners withdraws their read grants and live history.
            var stoppedPlayers = players.Select(player => new { state = player.Client.State,
                source = player.Client.SourceActions.View, receipts = player.Receipts.ToArray() }).ToArray();
            var stoppedHistories = ai.Histories;
            var stoppedBotProcessing = botPort.CaptureProcessing();
            var reportedBotProcessing = completedBotProcessing ?? stoppedBotProcessing;
            players[0].Port.Release.TrySetResult();
            try {
                var settle = DateTime.UtcNow.AddSeconds(3); if (deadline < settle) settle = deadline;
                while (players.Any(player => player.Client.Busy || player.Client.Polling) && DateTime.UtcNow < settle) {
                    foreach (var player in players) player.Client.Pump();
                    await Task.Delay(1);
                }
                if (players.Any(player => player.Client.Busy || player.Client.Polling))
                    throw new TimeoutException("Polled player owners did not settle before teardown.");
            } catch (Exception error) { cleanupErrors.Add(error.ToString()); }
            try { await presence.CloseAsync(CleanupBudget()); }
            catch (Exception error) { cleanupErrors.Add(error.ToString()); }
            foreach (var player in players) player.Client.Close();
            try { await ai.DisposeAsync().AsTask().WaitAsync(CleanupBudget()); }
            catch (Exception error) { cleanupErrors.Add(error.ToString()); }
            foreach (var engine in engines.Take(2).Reverse()) {
                try { await engine.LeaveAsync().WaitAsync(CleanupBudget()); }
                catch (Exception error) { cleanupErrors.Add(error.ToString()); }
            }
            if (failure == null && cleanupErrors.Count > 0) failure = new IOException("Polled prefix cleanup failed: " + string.Join("\n", cleanupErrors));
            await File.WriteAllTextAsync(Path.Combine(directory, "private-final-observations.json"), JsonSerializer.Serialize(new {
                audience = "host-private", players = stoppedPlayers, histories = stoppedHistories,
                completedBotProcessing, preCleanupBotProcessing = stoppedBotProcessing, faults = botPort.Faults.ToArray()
            }));
            await File.WriteAllTextAsync(Path.Combine(directory, "prefix-results.json"), JsonSerializer.Serialize(new {
                formatVersion = 4, scope = "Real default-polled host/TCP guest, tentative combat choices, public inspection and background AI prefix; no human UX, natural outcome or deck qualification claim.",
                passed = failure == null && completed, failure = failure?.ToString(), cleanupErrors, elapsedSeconds = started.Elapsed.TotalSeconds,
                steps, checks, coherentPublicCheckpoints = coherent, captured, released, guestReadsAtHold, botReadsAtHold, hostCommandsAtHold,
                backgroundReads = botPort.Reads, backgroundAcceptedCommands = reportedBotProcessing.Accepted,
                recordedBotReceipts = reportedBotProcessing.ProcessedAccepted,
                botReceiptAccounting = "Cumulative provider ACK receipts confirmed at the next real serialized host Observe entry; retained histories remain bounded diagnostics. Successful accounting is frozen at qualification before cleanup.",
                botProcessingAtQualification = completedBotProcessing, preCleanupBotProcessing = stoppedBotProcessing,
                botNonPassAcceptances = reportedBotProcessing.ProcessedNonPassBySeat.OrderBy(pair => pair.Key)
                    .Select(pair => new { seat = pair.Key, accepted = pair.Value }),
                presence = presence.Evidence,
                choices = choices.Evidence,
                players = players.Select(player => new { player.Index, player.ClientAdmissions, providerAcceptances = player.Port.Accepted,
                    scriptedInputAcceptances = player.Port.InputAccepted, receiptCounts = player.Port.ReceiptCounts,
                    ordinaryReads = player.Port.Reads, player.PollCallbacks, player.Publications, player.HeldPublications,
                    player.Port.Failures, commandLatencies = player.Port.CommandLatencies.ToArray(),
                    viewer = player.Client.State?.ViewerId, board = player.Client.State?.BoardRevision })
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        void Guard() { if (DateTime.UtcNow >= limit) throw new TimeoutException("The polled mixed-owner prefix exceeded its bounded portion of NativeSmoke's original deadline."); }
        TimeSpan CleanupBudget() => TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(5000, (deadline - DateTime.UtcNow).TotalMilliseconds)));
    }

    private sealed class PlayerOwner
    {
        internal readonly int Index;
        internal readonly PolledLobbyPorts.ReadPort Port;
        internal readonly ClientSession Client;
        internal readonly ForgeEngine Engine;
        internal readonly PolledLobbyPorts.PresencePort InspectionPort;
        internal readonly TablePresenceSession Inspection;
        internal readonly List<object> Receipts = [];
        internal int ClientAdmissions, PollCallbacks, Publications, HeldPublications;
        internal bool InputReceiptsReconciled => ClientAdmissions == Port.InputAccepted;
        private readonly ConcurrentQueue<Exception> failures = new();
        private GameSnapshot? prior;
        private (string Game, long Revision, string Decision)? handed;
        private long nextPoll;
        private bool armed;
        internal PlayerOwner(ForgeEngine engine, int index)
        {
            Index = index; Engine = engine; Port = new(engine); Client = new(Port, engine, sourceActions: engine);
            InspectionPort = new(engine); Inspection = new(InspectionPort, callback => InspectionPort.Post(Client.Post, callback));
            Client.Failed += failures.Enqueue;
            Client.Changed += (_, state) => {
                if (state == null) { if (prior != null) throw new InvalidDataException("A polled player lost its bound match."); return; }
                if (prior != null && (state.Id != prior.Id || state.ViewerId != prior.ViewerId
                    || state.Revision < prior.Revision || state.BoardRevision < prior.BoardRevision))
                    throw new InvalidDataException("A polled player changed its audience or rolled back an accepted publication.");
                if (state.Status is "error" or "finished") throw new InvalidDataException(state.Error ?? "The short polled prefix ended prematurely.");
                if (!AiCampaignVisibility.PrivateWindowsAreDisclosed(state)) throw new InvalidDataException("A polled human received an unauthorized private window.");
                prior = state; Publications++;
                if (ReferenceEquals(state, Port.Held)) HeldPublications++;
            };
        }
        internal void RequireHealthy()
        {
            if (failures.TryPeek(out var error)) ExceptionDispatchInfo.Capture(error).Throw();
            if (Client.UncertainCommand || Client.NeedsRecovery) throw new InvalidDataException("A healthy polled player entered command uncertainty or recovery.");
        }
        internal void Poll()
        {
            long now = Environment.TickCount64;
            if (Client.Busy || Client.Polling) return;
            if (Index == 0 && !armed && Client.State?.Decision?.Payload is PriorityPayload
                && Client.State.ActivePlayerId == Client.State.ViewerId && Client.State.PhaseKey == "MAIN1") {
                if (!Port.TryArmOrdinaryPoll(Client)) return;
                armed = true;
                nextPoll = now;
            }
            if (now < nextPoll) return;
            nextPoll = now + (Client.State?.Decision == null ? 50 : 120);
            Client.Poll(true, _ => PollCallbacks++);
        }
        internal void Step()
        {
            if (Client.Busy || Client.State is not { Status: "playing", Decision: { } decision, Viewer: { } viewer } state) return;
            if (handed == (state.Id, state.Revision, decision.Id)) return;
            // The gate holds only a genuine ordinary poll, before this owner's
            // first main-phase command starts its normal serialized receipt lane.
            if (Index == 0 && armed && !Port.Captured.Task.IsCompleted) return;
            GameCommand? command = null;
            if (decision.Payload is PriorityPayload) {
                var source = viewer.Zone("Hand").Cards.FirstOrDefault(card => card.Selectable && !card.FaceDown
                    && card.Type.Contains("Land", StringComparison.Ordinal));
                string kind = "play";
                if (source == null) { source = viewer.Zone("Command").Cards.FirstOrDefault(card => card.Selectable && !card.FaceDown); kind = "cast"; }
                if (source != null) {
                    var context = new SourceActionContext(state.Id, decision.Id, state.Revision, state.ViewerId, source.Key, CardViews.Identity(source));
                    Client.SourceActions.Observe(context);
                    if (Client.SourceActions.View.Status is SourceActionStatus.Failed or SourceActionStatus.Unsupported)
                        throw new InvalidDataException("The current ordinary source reader failed: " + Client.SourceActions.View.Error);
                    if (Client.SourceActions.View.Status != SourceActionStatus.Ready) return;
                    var action = Client.SourceActions.View.Actions.FirstOrDefault(action => action.Kind == kind)
                        ?? throw new InvalidDataException("The ordinary source reader omitted its current " + kind + " action.");
                    command = Client.SourceActions.TryCreateCommand(context, action.Id);
                }
            }
            if (command == null) {
                var presentation = DecisionDockPresentation.From(state, new());
                if (presentation.Selection is { } selection)
                    command = DecisionCommands.Choose(state, selection.Choices.Take(selection.Range.Min).Select(choice => choice.Index));
                else command = DecisionCommands.Confirm(state);
                if (command == null && StateClarity.Recipients(state) is { Length: > 0 } recipients)
                    command = StateClarity.ChooseRecipient(state, recipients.Any(player => player.Id == state.ViewerId)
                        ? state.ViewerId : recipients[0].Id);
            }
            if (command == null) throw new InvalidDataException("The bounded ordinary player script has no current reply for " + decision.Payload.GetType().Name);
            if (!Client.Submit(command)) throw new InvalidDataException("The ordinary client declined its current scripted command.");
            handed = (state.Id, state.Revision, decision.Id);
            ClientAdmissions++;
            Receipts.Add(new { state.Revision, state.BoardRevision, decision = decision.Id, command = command.GetType().Name });
        }
    }

}
