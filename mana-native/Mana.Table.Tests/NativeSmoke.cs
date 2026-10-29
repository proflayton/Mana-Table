using System.Text.Json;
using Mana.Contracts;
using Mana.Client;
using Mana.Magic;
using Mana.Forge;

namespace Mana.Table;

internal static class NativeSmoke
{
    public static async Task RunAsync(ForgePaths paths)
    {
        Directory.CreateDirectory(paths.Profile);
        var engines = Enumerable.Range(0, 4).Select(i => new ForgeEngine(paths with { Profile = Path.Combine(paths.Profile, "seat-" + i) })).ToArray();
        var results = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); results.Add(name); File.AppendAllText(Path.Combine(paths.Profile, "progress.log"), name + "\n"); }
        try {
            await Task.WhenAll(engines.Select(e => e.InitializeAsync()));
            var decks = new List<DeckSummary>();
            for (int i = 0; i < engines.Length; i++) {
                var deck = await engines[i].ImportAsync("Native adapter smoke " + i, "Deck\n99 Mountain\nCommander\n1 Rograkh, Son of Rohgahh");
                Check(!string.IsNullOrEmpty(deck.Id), "Imported deck for seat " + i); decks.Add(deck);
                Check((await engines[i].DecksAsync()).Any(d => d.Id == deck.Id), "Saved deck listed for seat " + i);
            }
            Check((await engines[0].PresetsAsync()).Length >= 4, "Bundled precons available");
            var lobby = await engines[0].HostAsync(4, false);
            var endpoint = lobby.Addresses.First(a => a.Url.Contains(':')).Url;
            var port = endpoint.Split(':').Last();
            for (int i = 1; i < engines.Length; i++) await engines[i].JoinAsync("127.0.0.1:" + port);
            await Wait(async () => (await engines[0].LobbyAsync()).Seats.Count(s => s.Type != "OPEN") == 4);
            for (int i = 0; i < engines.Length; i++) {
                await engines[i].SelectDeckAsync(new(lobby.TableId, decks[i].Id, decks[i].LibraryToken));
                await engines[i].SetReadyAsync(true);
            }
            await Wait(async () => (await engines[0].LobbyAsync()).CanStart);
            await engines[0].StartAsync();
            var previous = new Dictionary<int, string>();
            DecisionCommand? staleReply = null;
            int staleSeat = 0;
            var deadline = DateTime.UtcNow.AddMinutes(4);
            GameSnapshot? fixture = null;
            var seen = new HashSet<int>();
            bool guestUndo = false;
            while (DateTime.UtcNow < deadline && (seen.Count < 4 || !guestUndo)) {
                for (int i = 0; i < engines.Length; i++) {
                    var state = await engines[i].ObserveAsync();
                    await File.WriteAllTextAsync(Path.Combine(paths.Profile, $"latest-seat-{i}.json"), JsonSerializer.Serialize(state));
                    if (state?.Players.Length != 4) continue;
                    if (state.Status == "error") throw new InvalidOperationException(state.Error);
                    foreach (var player in state.Players.Where(p => p.Id != state.ViewerId))
                        if (player.Zone("Hand").Cards.Length != 0) throw new InvalidOperationException("Hidden hand leaked");
                    var d = state.Decision;
                    if (state.Viewer!.Zone("Battlefield").Cards.Any(c => c.Name == "Rograkh, Son of Rohgahh")) seen.Add(i);
                    fixture = state;
                    if (d == null || previous.GetValueOrDefault(i) == d.Id) continue;
                    if (i == 1 && !guestUndo && d.Payload is PriorityPayload
                            && state.ActivePlayerId == state.ViewerId && state.PhaseKey is "MAIN1" or "MAIN2") {
                        var source = state.Viewer.Zone("Battlefield").Cards.FirstOrDefault(card => card.Name == "Mountain" && !card.Tapped);
                        if (source != null) {
                            Check(state.Players.All(player => player.Zone("Battlefield").Cards.All(card => card.Name == "Mountain")),
                                "The TCP guest reversal runs on a quiet basic-land battlefield before commanders introduce unrelated traits");
                            var actions = await engines[i].SourceActionsAsync(state.Id, d.Id, source.Key);
                            var mana = actions.Actions.Single(action => action.Kind == "activate");
                            await engines[i].ExecuteAsync(new UseOfferedAction(state.Id, d.Id, source.Key, mana.Id));
                            GameSnapshot? tapped = null;
                            while (DateTime.UtcNow < deadline && UndoFacts.Command(tapped ?? state) == null) {
                                tapped = await engines[i].ObserveAsync();
                                if (tapped?.Status == "error") throw new InvalidOperationException(tapped.Error);
                                if (UndoFacts.Command(tapped ?? state) == null) await Task.Delay(10);
                            }
                            var undo = tapped == null ? null : UndoFacts.Command(tapped);
                            Check(undo != null && tapped!.Undo!.Offer!.SourceCardId == source.VisualId
                                && tapped.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == source.VisualId).Tapped
                                && tapped.Viewer.Mana.GetValueOrDefault("R") == state.Viewer.Mana.GetValueOrDefault("R") + 1,
                                "A real TCP guest receives certified Undo for its own manual Mountain activation");
                            var hostTapped = await engines[0].ObserveAsync();
                            Check(hostTapped?.Id == state.Id && hostTapped.Undo?.Offer == null
                                && hostTapped.Players.Single(player => player.Id == state.ViewerId).Zone("Battlefield").Cards
                                    .Single(card => card.VisualId == source.VisualId).Tapped,
                                "The original host observes the guest's tapped Mountain without borrowing the guest's Undo permission");
                            await engines[i].ExecuteAsync(undo!);
                            GameSnapshot? restored = null, hostRestored = null;
                            while (DateTime.UtcNow < deadline) {
                                restored = await engines[i].ObserveAsync(); hostRestored = await engines[0].ObserveAsync();
                                if (restored?.Undo?.RestorationVersion > tapped!.Undo!.RestorationVersion
                                    && hostRestored?.Undo?.RestorationVersion == restored.Undo.RestorationVersion) break;
                                await Task.Delay(10);
                            }
                            Check(restored?.Undo is { Offer: null, LastRestoration: { } marker } && marker.ActionId == undo!.ActionId
                                && restored.BoardRevision > tapped!.BoardRevision && hostRestored?.BoardRevision == restored.BoardRevision
                                && !restored.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == source.VisualId).Tapped
                                && !hostRestored.Players.Single(player => player.Id == state.ViewerId).Zone("Battlefield").Cards
                                    .Single(card => card.VisualId == source.VisualId).Tapped
                                && restored.Viewer.Mana.OrderBy(pair => pair.Key).SequenceEqual(state.Viewer.Mana.OrderBy(pair => pair.Key))
                                && hostRestored.Players.Single(player => player.Id == state.ViewerId).Mana.OrderBy(pair => pair.Key)
                                    .SequenceEqual(restored.Viewer.Mana.OrderBy(pair => pair.Key)),
                                "Guest reversal advances one shared board/restoration version and restores exact source/resources in both TCP projections");
                            bool replayRejected = false;
                            try { await engines[i].ExecuteAsync(undo!); }
                            catch (EngineCommandRejectedException) { replayRejected = true; }
                            Check(replayRejected, "A consumed TCP guest Undo cannot replay through its original native connection");
                            guestUndo = true; continue;
                        }
                        if (state.Viewer.Zone("Hand").Cards.FirstOrDefault(card => card.Selectable && card.Name == "Mountain") is { } land) {
                            await engines[i].ExecuteAsync(new PlayCard(state.Id, d.Id, land.Key)); previous[i] = d.Id; continue;
                        }
                    }
                    DecisionCommand? reply = null;
                    if (DecisionDockPresentation.From(state, new()).Selection is { } selection) reply = DecisionCommands.Choose(state, selection.Choices.Take(Math.Max(selection.Range.Min, Math.Min(1, selection.Range.Max))).Select(c => c.Index));
                    else if (d.Payload is RevealPayload) reply = new AcknowledgeReveal(state.Id, d.Id);
                    else if (d.Payload is PriorityPayload or OpeningHandPayload or PaymentPayload or AttackPayload or BlockPayload or TargetSelectionPayload or EntitySelectionPayload or ConfirmationPayload) {
                        var commander = state.Viewer.Zone("Command").Cards.FirstOrDefault(c => c.Selectable);
                        if (d.Payload is PriorityPayload && commander != null && guestUndo) reply = new PlayCard(state.Id, d.Id, Card: commander.Key);
                        else if (DecisionCommands.Confirm(state) != null) reply = DecisionCommands.Confirm(state);
                        else if (StateClarity.Recipients(state) is { Length: > 0 } recipients) reply = new SelectPlayer(state.Id, d.Id, Player: recipients[0].Id);
                        else if (d.Payload is TargetSelectionPayload or EntitySelectionPayload && state.Viewer.Zone("Hand").Cards.FirstOrDefault(c => c.Selectable && !c.Highlighted) is { } card) reply = new SelectCard(state.Id, d.Id, Card: card.Key);
                    }
                    if (reply != null) {
                        try { await engines[i].ExecuteAsync(reply); previous[i] = d.Id; if (staleReply == null) { staleReply = reply; staleSeat = i; } }
                        catch (InvalidOperationException ex) when (ex.Message.Contains("choice has changed")) { }
                    }
                }
                await Task.Delay(25);
            }
            Check(seen.Count == 4, "All four native adapters cast their commander through typed decisions");
            Check(guestUndo, "The existing four-client real-engine match exercises certified guest Undo before lifecycle teardown");
            Check(fixture!.Players.All(p => p.Life == 40), "Four players at 40 life");
            Check(fixture.Players.All(p => p.CommanderDamage.Length == 4), "Each player sees all four commander-damage totals");
            bool rejected = false;
            try { await engines[staleSeat].ExecuteAsync(staleReply!); }
            catch (EngineCommandRejectedException ex) when (ex.Message.Contains("choice has changed")
                || ex.Message == "That command does not answer the currently observed decision. Observe the table again.") { rejected = true; }
            Check(rejected, "Stale decision rejected through the typed adapter");
            await File.WriteAllTextAsync(Path.Combine(paths.Profile, "table-fixture.json"), JsonSerializer.Serialize(fixture, new JsonSerializerOptions { WriteIndented = true }));
            for (int i = 3; i > 0; i--) {
                var state = await engines[i].ObserveAsync();
                await engines[i].ConcedeAsync(state!.Id);
                await MatchDepartureObservation.WaitAsync(engines[i], state, MatchDepartureObservation.Remaining(deadline));
            }
            await Wait(async () => (await engines[0].ObserveAsync())?.Status == "finished");
            Check((await engines[0].ObserveAsync())?.Result == "Victory", "Last remaining player wins");
            await engines[0].ReturnToLobbyAsync();
            Check(!(await engines[0].LobbyAsync()).MatchActive, "Return to lobby after game");
            await File.WriteAllTextAsync(Path.Combine(paths.Profile, "smoke-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        } finally { foreach (var engine in engines) await engine.DisposeAsync(); }
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(80); }
        throw new TimeoutException("Native multiplayer did not reach the expected state");
    }
}
