using System.Text.Json;
using Mana.Contracts;
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
            var deckIds = new List<string>();
            for (int i = 0; i < engines.Length; i++) {
                var deck = await engines[i].ImportAsync("Native adapter smoke " + i, "Deck\n99 Mountain\nCommander\n1 Rograkh, Son of Rohgahh");
                Check(!string.IsNullOrEmpty(deck.Id), "Imported deck for seat " + i); deckIds.Add(deck.Id);
                Check((await engines[i].DecksAsync()).Any(d => d.Id == deck.Id), "Saved deck listed for seat " + i);
            }
            Check((await engines[0].PresetsAsync()).Count >= 4, "Bundled precons available");
            var lobby = await engines[0].HostAsync(4, false);
            var endpoint = lobby.Addresses.First(a => a.Url.Contains(':')).Url;
            var port = endpoint.Split(':').Last();
            for (int i = 1; i < engines.Length; i++) await engines[i].JoinAsync("127.0.0.1:" + port);
            await Wait(async () => (await engines[0].LobbyAsync()).Seats.Count(s => s.Type != "OPEN") == 4);
            for (int i = 0; i < engines.Length; i++) { await engines[i].SelectDeckAsync(deckIds[i]); await engines[i].SetReadyAsync(true); }
            await Wait(async () => (await engines[0].LobbyAsync()).CanStart);
            await engines[0].StartAsync();
            var previous = new Dictionary<int, string>();
            DecisionReply? staleReply = null;
            int staleSeat = 0;
            var deadline = DateTime.UtcNow.AddMinutes(4);
            GameSnapshot? fixture = null;
            var seen = new HashSet<int>();
            while (DateTime.UtcNow < deadline && seen.Count < 4) {
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
                    DecisionReply? reply = null;
                    if (d.Kind == "choice") reply = new(state.Id, d.Id, ReplyAction.Choose, Choices: d.Choices.Take(Math.Max(d.Min, Math.Min(1, d.Max))).Select(c => c.Index).ToArray());
                    else if (d.Kind == "reveal") reply = new(state.Id, d.Id, ReplyAction.Choose, Choices: []);
                    else if (d.Kind == "input") {
                        var commander = state.Viewer.Zone("Command").Cards.FirstOrDefault(c => c.Selectable);
                        if (d.Intent == DecisionIntent.Priority && commander != null) reply = new(state.Id, d.Id, ReplyAction.SelectCard, Card: commander.Key);
                        else if (d.OkEnabled) reply = new(state.Id, d.Id, ReplyAction.Confirm);
                        else if (d.PlayerChoices.Length > 0) reply = new(state.Id, d.Id, ReplyAction.SelectPlayer, Player: d.PlayerChoices[0]);
                        else if (d.Intent == DecisionIntent.Selection && state.Viewer.Zone("Hand").Cards.FirstOrDefault(c => c.Selectable && !c.Highlighted) is { } card) reply = new(state.Id, d.Id, ReplyAction.SelectCard, Card: card.Key);
                    }
                    if (reply != null) {
                        try { await engines[i].ReplyAsync(reply); previous[i] = d.Id; if (staleReply == null) { staleReply = reply; staleSeat = i; } }
                        catch (InvalidOperationException ex) when (ex.Message.Contains("choice has changed")) { }
                    }
                }
                await Task.Delay(25);
            }
            Check(seen.Count == 4, "All four native adapters cast their commander through typed decisions");
            Check(fixture!.Players.All(p => p.Life == 40), "Four players at 40 life");
            Check(fixture.Players.All(p => p.CommanderDamage.Length == 4), "Each player sees all four commander-damage totals");
            bool rejected = false;
            try { await engines[staleSeat].ReplyAsync(staleReply!); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("choice has changed")) { rejected = true; }
            Check(rejected, "Stale decision rejected through the typed adapter");
            await File.WriteAllTextAsync(Path.Combine(paths.Profile, "table-fixture.json"), JsonSerializer.Serialize(fixture, new JsonSerializerOptions { WriteIndented = true }));
            for (int i = 3; i > 0; i--) {
                var state = await engines[i].ObserveAsync();
                await engines[i].ConcedeAsync(state!.Id);
                await Task.Delay(300);
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
