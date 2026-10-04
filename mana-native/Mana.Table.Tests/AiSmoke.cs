using Mana.Magic;
using Mana.Client;
using System.Text.Json;
using Mana.Contracts;
using Mana.Forge;
using Mana.Renderer;

namespace Mana.Table;

internal static class AiSmoke
{
    public static async Task RunAsync(ForgePaths paths)
    {
        Directory.CreateDirectory(paths.Profile);
        await using ICardEngine engine = new ForgeEngine(paths);
        var advance = new DecisionAdvance(engine);
        var results = new List<string>();
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            results.Add(name); File.AppendAllText(Path.Combine(paths.Profile, "progress.log"), name + "\n");
        }
        await engine.InitializeAsync();
        var presets = await engine.PresetsAsync();
        var precon = await engine.ImportPresetAsync(presets[0].Id);
        var setup = await engine.PrepareSoloAsync(precon.Id);
        Check(setup.Opponents.Length >= 6, "Four precons and two starter AI decks available");
        var preconOpponents = setup.Opponents.Where(o => o.Name != precon.Name).Take(3).Select(o => o.Id).ToArray();
        // Opening another deck must not change which deck StartSolo uses.
        var testDeck = await engine.ImportAsync("Native AI benchmark", "Deck\n99 Mountain\nCommander\n1 Rograkh, Son of Rohgahh");
        bool rejectedCount = false;
        try { await engine.StartSoloAsync(precon.Id, preconOpponents.Take(2).ToArray()); }
        catch (ArgumentException) { rejectedCount = true; }
        Check(rejectedCount, "Four-seat table rejects fewer than three AI opponents");
        await engine.StartSoloAsync(precon.Id, preconOpponents);
        var opening = await WaitForDecision(engine);
        Check(opening.Players.Length == 4 && opening.Players.All(p => p.Life == 40), "Four-player precon table starts at 40 life");
        Check(opening.Viewer!.Zone("Command").Cards.All(c => c.Name != "Rograkh, Son of Rohgahh") && opening.Viewer.Zone("Command").Count > 0,
            "Solo start uses the selected deck even after another import");
        Check(opening.Players.All(p => p.Zone("Command").Count > 0), "All four precon commanders are present");
        Check(opening.Players.Where(p => p.Id != opening.ViewerId).All(p => p.Zone("Hand").Cards.Length == 0), "Opening AI hands stay hidden");
        var oldReply = new DecisionReply(opening.Id, opening.Decision!.Id, ReplyAction.Confirm);
        await engine.ConcedeAsync(opening.Id);
        Check((await engine.ObserveAsync()) is { Status: "finished", Result: "Defeat" }, "Conceding ends the solo table");

        var benchmark = await engine.PrepareSoloAsync(testDeck.Id);
        string green = benchmark.Opponents.Single(o => o.Id == "green").Id;
        string red = benchmark.Opponents.Single(o => o.Id == "red").Id;
        await engine.StartSoloAsync(testDeck.Id, [green, red, green]);
        var state = await WaitForDecision(engine);
        Check(state.Id != opening.Id, "A second solo game starts in the same engine process");
        bool rejectedSession = false;
        try { await engine.ReplyAsync(oldReply); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("no longer active")) { rejectedSession = true; }
        Check(rejectedSession, "Previous game's decision cannot affect the rematch");

        var deadline = DateTime.UtcNow.AddMinutes(5);
        int landTurn = -1, decisions = 0, automaticPasses = 0;
        var priority = new AutoPriority();
        var noStops = new HashSet<string>();
        bool commanderCast = false, commanderSeen = false, battleSeen = false;
        int humanAttackStage = 0; string? humanAttacker = null;
        var developed = new HashSet<int>();
        string? previous = null;
        DecisionReply? stale = null;
        while (DateTime.UtcNow < deadline) {
            state = await engine.ObserveAsync() ?? throw new InvalidOperationException("Solo game disappeared");
            await File.WriteAllTextAsync(Path.Combine(paths.Profile, "latest.json"), JsonSerializer.Serialize(state));
            if (state.Status is "error" or "finished") throw new InvalidOperationException(state.Error ?? "Game finished before benchmark completed");
            if (state.Players.Length != 4) { await Task.Delay(25); continue; }
            foreach (var ai in state.Players.Where(p => p.Id != state.ViewerId)) {
                if (ai.Zone("Hand").Cards.Length != 0) throw new InvalidOperationException("AI hand leaked");
                if (ai.Zone("Battlefield").Cards.Any(c => c.Type.Contains("Land")) && ai.Zone("Battlefield").Cards.Any(c => !c.Type.Contains("Land"))) developed.Add(ai.Id);
            }
            commanderSeen |= state.Viewer!.Zone("Battlefield").Cards.Any(c => c.Name == "Rograkh, Son of Rohgahh");
            battleSeen |= state.ActivePlayerId != state.ViewerId && state.Combat?.Attackers.Length > 0;
            if (developed.Count == 3 && commanderSeen && battleSeen && humanAttackStage == 3 && state.Decision != null) break;
            var d = state.Decision;
            if (d == null || d.Id == previous) { await Task.Delay(25); continue; }
            DecisionReply? reply = null;
            bool playingLand = false, playingCommander = false;
            bool assigningHuman = false, recallingHuman = false;
            if (d.Kind == "choice") reply = new(state.Id, d.Id, ReplyAction.Choose, Choices: d.Choices.Take(Math.Max(d.Min, Math.Min(1, d.Max))).Select(c => c.Index).ToArray());
            else if (d.Kind == "reveal") reply = new(state.Id, d.Id, ReplyAction.Choose, Choices: []);
            else if (d.Kind == "input") {
                var commander = state.Viewer.Zone("Command").Cards.FirstOrDefault(c => c.Selectable);
                var land = state.Viewer.Zone("Hand").Cards.FirstOrDefault(c => c.Selectable && c.Type.Contains("Land"));
                if (!commanderCast && d.Intent == DecisionIntent.Priority && commander != null) { reply = new(state.Id, d.Id, ReplyAction.SelectCard, Card: commander.Key); playingCommander = true; }
                else if (d.Intent == DecisionIntent.Priority && state.ActivePlayerId == state.ViewerId && state.PhaseKey == "MAIN1" && landTurn != state.Turn && land != null) { reply = new(state.Id, d.Id, ReplyAction.SelectCard, Card: land.Key); playingLand = true; }
                else if (d.Intent == DecisionIntent.Attack && humanAttackStage == 0 && state.Combat?.AttackOptions.FirstOrDefault(a => a.Defenders.Any(t => t.Kind == "player")) is { } option) {
                    var attacker = CombatGuide.Find(state, option.CardId)!; var defender = option.Defenders.First(t => t.Kind == "player");
                    humanAttacker = option.CardId; assigningHuman = true;
                    reply = new(state.Id, d.Id, ReplyAction.AssignAttack, Attacker: attacker.Key, Player: defender.PlayerId);
                } else if (d.Intent == DecisionIntent.Attack && humanAttackStage == 1) {
                    Check(state.Combat!.Attackers.Any(a => a.CardId == humanAttacker), "Human attack assignment appears in the real Forge projection");
                    reply = CombatGuide.Remove(state, humanAttacker!) ?? throw new InvalidOperationException("The assigned attacker cannot be recalled"); recallingHuman = true;
                } else if (d.Intent == DecisionIntent.Attack && humanAttackStage == 2) {
                    Check(state.Combat!.Attackers.All(a => a.CardId != humanAttacker), "Renderer recall command removes the attack through Forge with fresh handles");
                    humanAttackStage = 3; reply = new(state.Id, d.Id, ReplyAction.Confirm);
                } else if (priority.Update(state, true, false, noStops, false, false) != null) {
                    await advance.RunAsync(state, s => TurnGuide.CanAutoPass(s, true, false, noStops, false), PresentationPacing.ShouldPresent);
                    previous = d.Id; automaticPasses++; continue;
                }
                else if (d.OkEnabled) reply = new(state.Id, d.Id, ReplyAction.Confirm);
                else if (d.PlayerChoices.Length > 0) reply = new(state.Id, d.Id, ReplyAction.SelectPlayer, Player: d.PlayerChoices.Contains(state.ViewerId) ? state.ViewerId : d.PlayerChoices[0]);
                else if (d.Intent == DecisionIntent.Selection && state.Viewer.Zone("Hand").Cards.FirstOrDefault(c => c.Selectable && !c.Highlighted) is { } card) reply = new(state.Id, d.Id, ReplyAction.SelectCard, Card: card.Key);
            }
            if (reply == null) throw new InvalidOperationException("Unhandled benchmark decision: " + JsonSerializer.Serialize(d));
            await engine.ReplyAsync(reply);
            previous = d.Id; stale ??= reply; decisions++;
            if (playingLand) landTurn = state.Turn;
            if (playingCommander) commanderCast = true;
            if (assigningHuman) humanAttackStage = 1;
            if (recallingHuman) humanAttackStage = 2;
        }
        Check(commanderSeen, "Human casts a commander using the normal decision controls");
        Check(developed.Count == 3, "All three AI opponents play lands and cast nonland permanents");
        Check(battleSeen, "AI declares attackers through real combat");
        Check(automaticPasses > 0, "Batched Auto traverses real Forge priority windows during the four-player game");
        Check(humanAttackStage == 3, "Human attacker assignment and recall both complete before confirming combat");
        var commanderOwners = state!.Players.Select(p => p.Id).Order().ToArray();
        Check(state.Players.All(p => p.CommanderDamage.Select(c => c.OwnerId).Order().SequenceEqual(commanderOwners)),
            "Commander damage includes every owner's commander, including your own under another player's control");
        bool rejectedDecision = false;
        try { await engine.ReplyAsync(stale!); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("choice has changed")) { rejectedDecision = true; }
        Check(rejectedDecision, "Stale decisions are rejected in a local game");
        await File.WriteAllTextAsync(Path.Combine(paths.Profile, "table-fixture.json"), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        await engine.ConcedeAsync(state.Id);
        Check((await engine.ObserveAsync())?.Status == "finished", "AI game stops cleanly after several rounds and combat");
        var lobby = await engine.HostAsync(4, false);
        Check(lobby.Mode == "hosting" && !lobby.MatchActive, "Can switch from solo play to a friends lobby");
        await engine.LeaveAsync();
        await File.WriteAllTextAsync(Path.Combine(paths.Profile, "smoke-results.json"), JsonSerializer.Serialize(new { results, decisions, automaticPasses, turn = state.Turn }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static async Task<GameSnapshot> WaitForDecision(ICardEngine engine)
    {
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline) {
            var state = await engine.ObserveAsync();
            if (state?.Status == "error") throw new InvalidOperationException(state.Error);
            if (state?.Decision != null) return state;
            await Task.Delay(40);
        }
        throw new TimeoutException("AI table did not present its first decision");
    }
}
