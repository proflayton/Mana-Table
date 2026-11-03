using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Reuses NativeSmoke's already-warmed real TCP clients and its original deadline.
// The two human connections are scripted drivers, not human UX acceptance. This
// observes a bounded continuation, not a natural outcome or deck qualification.
internal static class MixedLobbyEngineJourney
{
    internal static async Task RunAsync(ForgeEngine[] engines, Lobby lobby, string profile,
        DateTime deadline, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "mixed-lobby");
        Directory.CreateDirectory(directory);
        var accepted = new Dictionary<(AiDecisionStamp Stamp, string Command), AiDecisionRecord>();
        var humanReplies = new HashSet<(int Viewer, string Decision, long Revision)>();
        var botIds = new HashSet<int>();
        var botCommanders = new Dictionary<int, string[]>();
        var developed = new HashSet<int>();
        GameSnapshot? hostState = null, guestState = null;
        ImmutableArray<AutomatedSeatObservation> bots = [];
        int[] humanIds = [];
        int coherentCohorts = 0, postDepartureCohorts = 0, scriptedReplies = 0, steps = 0;
        bool historyChecked = false, remoteOwnershipChecked = false, hostConceded = false, continued = false;
        long departureBoard = -1;
        HashSet<(AiDecisionStamp Stamp, string Command)> beforeDeparture = [];
        string? failure = null;
        var retainedHumans = lobby.Seats.Where(seat => seat.Index is 0 or 1).OrderBy(seat => seat.Index)
            .Select(seat => seat.ParticipantId).ToArray();

        await engines[2].LeaveAsync();
        await engines[3].LeaveAsync();
        await WaitAsync(async () => (await engines[0].LobbyAsync()).Seats
            .Count(seat => seat.Index is 2 or 3 && seat.Type == "OPEN") == 2, deadline);
        lobby = await engines[0].SetSeatAsync(lobby.TableId, 2, "AI", "green");
        lobby = await engines[0].SetSeatAsync(lobby.TableId, 3, "AI", "red");
        var configuredBots = lobby.Seats.Where(seat => seat.Type == "AI").ToArray();
        check(configuredBots.Length == 2 && configuredBots.Select(seat => seat.OpponentId).Distinct().Count() == 2
            && lobby.Seats.Count(seat => seat.Type == "LOCAL") == 1 && lobby.Seats.Count(seat => seat.Type == "REMOTE") == 1
            && retainedHumans.Length == 2 && retainedHumans.All(id => id.Length > 0) && retainedHumans.Distinct().Count() == 2
            && lobby.Seats.Where(seat => seat.Index is 0 or 1).OrderBy(seat => seat.Index)
                .Select(seat => seat.ParticipantId).SequenceEqual(retainedHumans),
            "Mixed lobby retains the original host/TCP guest and assigns two distinct existing AI decks to the vacated seats");
        await engines[0].SetReadyAsync(true);
        await engines[1].SetReadyAsync(true);
        await WaitAsync(async () => (await engines[0].LobbyAsync()).CanStart, deadline);
        await engines[0].StartAsync();
        await using var ai = new AiTableHost(engines[0], _ => new CasualCommanderPolicy(), 261102,
            new(TimeSpan.FromSeconds(3), 2048));
        try {
            while (DateTime.UtcNow < deadline && !continued) {
                hostState = await engines[0].ObserveAsync();
                guestState = await engines[1].ObserveAsync();
                bots = await engines[0].ObserveAutomatedSeatsAsync();
                foreach (var state in new[] { hostState, guestState }.OfType<GameSnapshot>().Concat(bots.Select(bot => bot.State))) {
                    if (state.Status == "error") throw new InvalidOperationException(state.Error ?? "The mixed table stopped with a rules error.");
                    if (state.Status == "finished") throw new InvalidOperationException("The mixed table ended before its bounded continuation was observed.");
                }
                if (hostState is { Status: "playing", Players.Length: 4 } && guestState is { Status: "playing", Players.Length: 4 }
                    && hostState.Id == guestState.Id && bots.Length == 2) {
                    humanIds = [hostState.ViewerId, guestState.ViewerId];
                    if (humanIds.Distinct().Count() != 2 || bots.Select(bot => bot.SeatId).Distinct().Count() != 2
                        || bots.Any(bot => bot.State.Id != hostState.Id || bot.SeatId != bot.State.ViewerId
                            || humanIds.Contains(bot.SeatId))
                        || !hostState.Players.Select(player => player.Id).ToHashSet()
                            .SetEquals(humanIds.Concat(bots.Select(bot => bot.SeatId))))
                        throw new InvalidOperationException("The mixed table must expose four actual player identities and exactly two disjoint host-owned bot audiences.");
                    foreach (var bot in bots) {
                        botIds.Add(bot.SeatId);
                        var commanders = bot.State.Viewer!.Zone("Command").Cards.Select(card => card.Name).Order(StringComparer.Ordinal).ToArray();
                        if (commanders.Length > 0) botCommanders.TryAdd(bot.SeatId, commanders);
                    }
                    if (botIds.Count != 2) throw new InvalidOperationException("The mixed table changed its bot audience identities.");
                    if (!AiCampaignVisibility.PrivateWindowsAreDisclosed(hostState)
                        || !AiCampaignVisibility.PrivateWindowsAreDisclosed(guestState)
                        || bots.Any(bot => !AiCampaignVisibility.PrivateWindowsAreDisclosed(bot.State, bot.Semantics, bot.Domain)))
                        throw new InvalidOperationException("A mixed-lobby observation exposed an undeclared private hand or library window.");
                    foreach (var bot in bots)
                        if (bot.State.Viewer!.Zone("Battlefield").Cards.Any(card => card.Type.Contains("Land", StringComparison.Ordinal)))
                            developed.Add(bot.SeatId);

                    // Only compare coherent board cohorts. Sequential pipe/TCP
                    // observations can legitimately straddle an engine transition.
                    if (guestState.BoardRevision == hostState.BoardRevision
                        && bots.All(bot => bot.State.BoardRevision == hostState.BoardRevision)) {
                        if (!AiWholeSeatEngineCheck.ComparePublicProjection(hostState, guestState, directory)
                            || bots.Any(bot => !AiWholeSeatEngineCheck.ComparePublicProjection(hostState, bot.State, directory)))
                            throw new InvalidOperationException("A coherent mixed-lobby public projection disagreed across TCP and bot audiences.");
                        coherentCohorts++;
                        if (hostConceded && hostState.BoardRevision > departureBoard) postDepartureCohorts++;
                    }
                    if (!remoteOwnershipChecked) {
                        try {
                            if (!(await engines[1].ObserveAutomatedSeatsAsync()).IsEmpty)
                                throw new InvalidOperationException("The TCP guest received the host's private bot observations.");
                        } catch (EngineCommandRejectedException) { /* Explicit ownership denial is also safe. */ }
                        remoteOwnershipChecked = true;
                    }
                    if (!historyChecked && engines[0].SupportsAutomatedHistory) {
                        foreach (int bot in botIds) {
                            var history = await engines[0].AutomatedHistoryAsync(hostState.Id, bot, 0, 16);
                            if (history.GameId != hostState.Id || history.ViewerId != bot)
                                throw new InvalidOperationException("The host read a different automated history audience.");
                            if (engines[1].SupportsAutomatedHistory)
                                await RequireHistoryDenialAsync(engines[1], hostState.Id, bot);
                        }
                        foreach (int human in humanIds) await RequireHistoryDenialAsync(engines[0], hostState.Id, human);
                        historyChecked = true;
                    }
                    bool bothBotsActed = botIds.All(id => accepted.Values.Any(record => record.Stamp.SeatId == id
                        && record.Command is nameof(PlayCard) or nameof(UseOfferedAction)));
                    if (!hostConceded && developed.Count == 2 && bothBotsActed && coherentCohorts > 0 && remoteOwnershipChecked) {
                        beforeDeparture = accepted.Keys.ToHashSet();
                        await engines[0].ConcedeAsync(hostState.Id);
                        hostState = await MatchDepartureObservation.WaitAsync(engines[0], hostState,
                            MatchDepartureObservation.Remaining(deadline));
                        check(hostState.Status == "playing" && hostState.Viewer?.Eliminated == true && hostState.Decision == null,
                            "The real mixed-table host concedes without closing the TCP table or claiming a match outcome");
                        departureBoard = hostState.BoardRevision;
                        hostConceded = true;
                        continue;
                    }
                    continued = hostConceded && postDepartureCohorts > 0 && guestState.Viewer?.Eliminated == false
                        && accepted.Any(pair => !beforeDeparture.Contains(pair.Key)
                            && pair.Value.Stamp.BoardRevision >= departureBoard
                            && pair.Value.Command is nameof(PlayCard) or nameof(UseOfferedAction));
                    if (continued) break;
                }
                for (int index = 0; index < 2; index++) {
                    var state = index == 0 ? hostState : guestState;
                    if (state is not { Status: "playing", Viewer.Eliminated: false, Decision: { } decision }
                        || humanReplies.Contains((state.ViewerId, decision.Id, state.Revision))) continue;
                    var reply = ScriptedHumanReply(state);
                    if (reply == null) throw new InvalidOperationException("The mixed-lobby scripted human driver cannot answer " + decision.Payload.GetType().Name);
                    try {
                        await engines[index].ExecuteAsync(reply);
                        humanReplies.Add((state.ViewerId, decision.Id, state.Revision));
                        scriptedReplies++;
                    } catch (EngineCommandRejectedException error) when (error.Message.Contains("choice has changed")
                        || error.Message == "That command does not answer the currently observed decision. Observe the table again.") { }
                }
                await ai.StepAsync();
                steps++;
                foreach (var record in ai.Histories.SelectMany(history => history.Decisions)
                    .Where(record => record.Outcome == AiDecisionOutcome.Accepted)) accepted[(record.Stamp, record.Command)] = record;
                if (ai.LastFault != null) throw new InvalidOperationException("Mixed-lobby AI host fault: " + JsonSerializer.Serialize(ai.LastFault));
                await Task.Delay(1);
            }
            check(botIds.Count == 2 && humanIds.Length == 2 && remoteOwnershipChecked && developed.Count == 2 && scriptedReplies > 0
                && botCommanders.Count == 2 && botCommanders.Values.Select(names => string.Join("\n", names)).Distinct().Count() == 2,
                "Two real TCP human connections and two independent host-owned policies develop one shared four-player match with isolated private windows");
            check(!engines[0].SupportsAutomatedHistory || historyChecked,
                "Available automated history admits the two host-owned bots and denies foreign human and remote bot audiences");
            check(coherentCohorts > 0 && hostConceded && continued && ai.LastFault == null,
                "After the host concedes, accepted bot actions and a later coherent public board continue for the connected guest within the original smoke deadline");
        } catch (Exception error) { failure = error.ToString(); throw; }
        finally {
            // These snapshots and policy receipts contain private hands. Keep
            // them separate from the summary and label their authority honestly.
            await File.WriteAllTextAsync(Path.Combine(directory, "private-final-observations.json"),
                JsonSerializer.Serialize(new { hostState, guestState, bots, histories = ai.Histories }));
            await File.WriteAllTextAsync(Path.Combine(directory, "prefix-results.json"), JsonSerializer.Serialize(new {
                formatVersion = 1, scenario = "real-tcp-two-scripted-humans-two-ai",
                evidence = "bounded-integration-prefix", humanUxAcceptance = false, naturalOutcomeClaimed = false,
                deckQualificationClaimed = false, failure, steps, scriptedReplies, humanIds, botIds, botCommanders, developed,
                coherentCohorts, postDepartureCohorts, historySupported = engines[0].SupportsAutomatedHistory,
                historyChecked, remoteOwnershipChecked, hostConceded, continued, departureBoard,
                acceptedCommands = accepted.Values.Select(record => new { record.Stamp, record.Task, record.Command }),
                stopReason = continued ? "observed-host-departure-and-ai-progress" : "required-continuation-not-observed"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static DecisionCommand? ScriptedHumanReply(GameSnapshot state)
    {
        var decision = state.Decision!;
        if (decision.Payload is PriorityPayload && state.ActivePlayerId == state.ViewerId && state.PhaseKey is "MAIN1" or "MAIN2") {
            var land = state.Viewer!.Zone("Hand").Cards.FirstOrDefault(card => card.Name == "Mountain" && card.Selectable);
            if (land != null && DecisionCommands.Card(state, land.Key) is { } play) return play;
        }
        if (DecisionCommands.Confirm(state) is { } confirm) return confirm;
        var selection = decision.Payload switch {
            OptionsPayload options => options.Selection,
            AbilityChoicePayload ability => ability.Selection,
            OrderingPayload ordering => ordering.Selection, _ => null };
        if (selection != null) return DecisionCommands.Choose(state, selection.Choices
            .Take(decision.Payload is OrderingPayload ? selection.Choices.Length : selection.Range.Min).Select(choice => choice.Index));
        if (decision.Payload is RevealPayload) return DecisionCommands.Choose(state, []);
        if (decision.Payload is NumberPayload number) return DecisionCommands.Value(state,
            number.Range?.Min.ToString(CultureInfo.InvariantCulture) ?? number.Initial);
        if (decision.Payload is TextPayload text) return DecisionCommands.Value(state, text.Initial);
        if (decision.Payload is TargetSelectionPayload or EntitySelectionPayload) {
            var players = decision.Payload is TargetSelectionPayload targets ? targets.Players : ((EntitySelectionPayload)decision.Payload).Players;
            foreach (int player in players) {
                if (decision.Payload is TargetSelectionPayload { Progress: { } progress } && progress.SelectedPlayers.Contains(player)) continue;
                var select = new SelectPlayer(state.Id, decision.Id, player);
                if (GameCommandPermissions.Allows(state, select)) return select;
            }
            foreach (var card in CardViews.Located(state).Select(item => item.Card).Where(card => card.Selectable && !card.Highlighted))
                if (DecisionCommands.Card(state, card.Key) is { } select) return select;
        }
        return DecisionCommands.Cancel(state);
    }

    private static async Task RequireHistoryDenialAsync(ForgeEngine engine, string gameId, int seatId)
    {
        try { await engine.AutomatedHistoryAsync(gameId, seatId, 0, 16); }
        catch (EngineCommandRejectedException) { return; }
        throw new InvalidOperationException("An unowned automated history audience was readable.");
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, DateTime deadline)
    {
        while (DateTime.UtcNow < deadline) {
            if (await condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("The mixed lobby did not advance within NativeSmoke's original deadline.");
    }
}
