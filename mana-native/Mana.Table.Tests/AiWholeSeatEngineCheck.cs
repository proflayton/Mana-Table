using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Four distinct legal Commander decks, four complete policies and no scripted
// seat answers. Spectator JSONL is public; per-seat decision evidence is separate.
internal static class AiWholeSeatEngineCheck
{
    private const int TraceFormatVersion = 1;
    private const long PolicySeed = 261026;
    internal static async Task RunStandaloneAsync(ForgePaths paths, bool complete = false)
    {
        Directory.CreateDirectory(paths.Profile);
        await using var engine = new ForgeEngine(paths);
        await engine.InitializeAsync();
        await RunAsync(engine, paths.Profile, (passed, detail) => {
            if (!passed) throw new InvalidOperationException(detail);
            File.AppendAllText(Path.Combine(paths.Profile, "whole-seat-ai-progress.log"), detail + "\n");
        }, complete);
    }

    internal static async Task RunAsync(ForgeEngine engine, string profile, Action<bool, string> check, bool complete = false)
    {
        await engine.EnableAutomatedSeatsAsync();
        var rhys = await Save("AI canonical token economy", "Deck\n99 Forest\nCommander\n1 Rhys the Redeemed");
        var partners = await Save("AI canonical partner counters", "Deck\n49 Forest\n49 Island\nCommander\n1 Toothy, Imaginary Friend\n1 Pir, Imaginative Rascal");
        ImmutableArray<string> decks = ["green", "red", "deck:" + rhys, "deck:" + partners];
        await using var host = new AiTableHost(engine, _ => new CasualCommanderPolicy(), PolicySeed,
            new(TimeSpan.FromSeconds(3), 2048));
        GameSnapshot? state = await engine.StartSimulationAsync(decks);
        ImmutableArray<AutomatedSeatObservation> lastBotObservations = [];
        var elapsed = Stopwatch.StartNew();
        var observedTasks = new HashSet<AiTask>();
        var seenBots = new HashSet<int>();
        var developed = new HashSet<int>();
        var commanders = new HashSet<int>();
        var accepted = new Dictionary<(AiDecisionStamp Stamp, string Command), AiDecisionRecord>();
        bool privateIsolation = true, spellSeen = false, tokenSeen = false, combatSeen = false, counterSeen = false, completed = false;
        string? failure = null;
        int steps = 0, lastTurn = 0, comparedCheckpoints = 0, divergentCheckpoints = 0;
        int? rhysSeat = null;
        string? lastPublicFacts = null;
        await using var trace = new StreamWriter(Path.Combine(profile, "whole-seat-ai-public.jsonl"), false);
        try {
            // Specific draw/counter rules are mandatory controlled goldens. A
            // stochastic opponent can legally remove Toothy before its next draw.
            while (complete || !(lastTurn >= 20 && tokenSeen && combatSeen && developed.Count == 4 && commanders.Count >= 3)) {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(complete ? 120 : 60) || complete && lastTurn > 160)
                    throw new TimeoutException(complete ? "The four-policy game did not finish within two minutes and 160 turns." : "The four-policy canonical game did not reach paid token creation and combat.");
                state = await engine.ObserveSimulationAsync();
                if (state?.Status == "error") throw new InvalidOperationException(state.Error ?? "The simulation stopped with a rules error.");
                if (state?.Status == "finished") {
                    completed = true;
                    lastTurn = state.Turn;
                    await WriteCheckpoint(state);
                    if (complete || tokenSeen && combatSeen && developed.Count == 4 && commanders.Count >= 3
                        && !string.IsNullOrWhiteSpace(state.Result)) break;
                    throw new InvalidOperationException("The simulation ended before its required continuations.");
                }
                var bots = await engine.ObserveAutomatedSeatsAsync(); lastBotObservations = bots;
                foreach (var bot in bots) {
                    seenBots.Add(bot.SeatId);
                    bool isolated = bot.State.ViewerId == bot.SeatId
                        && AiCampaignVisibility.PrivateWindowsAreDisclosed(bot.State, bot.Semantics, bot.Domain);
                    if (!isolated) RecordPrivacyContext(profile, bot);
                    privateIsolation &= isolated;
                    if (bot.State.Decision != null) observedTasks.Add(AiTasks.From(bot.State.Decision.Payload));
                    if (state is { Status: "playing" } && bot.State.Id == state.Id && bot.State.BoardRevision == state.BoardRevision) {
                        if (!ComparePublicProjection(state, bot.State, profile)) divergentCheckpoints++;
                        comparedCheckpoints++;
                    }
                }
                if (state != null) {
                    lastTurn = state.Turn;
                    privateIsolation &= state.ViewerId == -1 && state.Decision == null
                        && state.Players.All(player => player.Zone("Hand").Cards.IsEmpty && player.Zone("Library").Cards.IsEmpty)
                        && CardViews.InZones(state).All(item => item.Card.Key.Length == 0);
                    foreach (var player in state.Players) {
                        if (player.Zone("Command").Cards.Any(card => card.Name == "Rhys the Redeemed")) rhysSeat = player.Id;
                        if (player.Zone("Battlefield").Cards.Any(card => card.Type.Contains("Land", StringComparison.Ordinal))) developed.Add(player.Id);
                        if (player.Zone("Battlefield").Cards.Any(card => card.Type.Contains("Legendary", StringComparison.Ordinal) && card.Power != null)) commanders.Add(player.Id);
                        tokenSeen |= player.Id == rhysSeat && player.Zone("Battlefield").Cards.Any(card => card.GamePieceType == "token");
                        counterSeen |= player.Zone("Battlefield").Cards.Any(card => card.Name == "Toothy, Imaginary Friend" && card.Counters.Values.Any(value => value > 0));
                    }
                    spellSeen |= state.Stack.Any(item => item.Kind == StackActionKind.Spell);
                    combatSeen |= state.Combat?.Attackers.Length > 0;
                    string publicFacts = ReplayFacts(state);
                    if (publicFacts != lastPublicFacts) {
                        lastPublicFacts = publicFacts;
                        await WriteCheckpoint(state);
                    }
                }
                await host.StepAsync();
                foreach (var record in host.Histories.SelectMany(history => history.Decisions)
                        .Where(record => record.Outcome == AiDecisionOutcome.Accepted)) accepted[(record.Stamp, record.Command)] = record;
                if (host.LastFault != null) throw new InvalidOperationException("Whole-seat host fault: " + JsonSerializer.Serialize(host.LastFault));
                steps++;
                await Task.Delay(1);
            }
            check(decks.Distinct().Count() == 4 && seenBots.Count == 4 && privateIsolation,
                "Four distinct Commander decks run through independent whole-seat policies with public-only spectator state and no shared private hands");
            check(comparedCheckpoints > 0, "Same-revision spectator and seat projections agree on public life, mana, zone counts and battlefield state; agreement alone does not establish rules correctness");
            check(divergentCheckpoints == 0, "Every compared public projection matches at its exact board revision; see the preserved first mismatch artifact if this fails");
            check(developed.Count == 4 && (complete || commanders.Count >= 3 && (lastTurn >= 20 || completed)),
                "Every bot develops its battlefield and multiple command-zone commanders resolve through five rounds or a verified natural game outcome");
            check(spellSeen && (complete || tokenSeen) && accepted.Values.Any(record => record.Command == nameof(UseOfferedAction))
                    && accepted.Values.Any(record => record.Task == AiTask.Payment
                        && record.Command is nameof(SpendPoolMana) or nameof(PayMana) or nameof(UseOfferedAction)),
                "The policies cast and fund real actions; authoritative battlefield state establishes a commander-created token");
            check(combatSeen && accepted.Values.Any(record => record.Command is nameof(ToggleAttackPlayer) or nameof(ToggleAttackPermanent))
                    && accepted.Values.Any(record => record.Command == nameof(CompleteAttackers)),
                "Whole-seat policies declare and complete real multiplayer combat without a Forge strategy controller or scripted seat responses");
            check(observedTasks.Contains(AiTask.OpeningHand) && observedTasks.Contains(AiTask.Priority)
                    && observedTasks.Contains(AiTask.Payment) && observedTasks.Contains(AiTask.Attack),
                "The real all-AI continuation traverses opening, priority, payment and combat decisions through one host and action boundary");
            if (complete) check(completed && state?.Status == "finished" && !string.IsNullOrWhiteSpace(state.Result),
                "Four complete policies reach a natural rules-engine game outcome without scripted seat answers or a forced concession");
        } catch (Exception error) { failure = error.ToString(); throw; }
        finally {
            File.WriteAllText(Path.Combine(profile, "whole-seat-ai-private-final-observations.json"),
                JsonSerializer.Serialize(lastBotObservations, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(profile, "whole-seat-ai.json"), JsonSerializer.Serialize(new {
                formatVersion = TraceFormatVersion, policySeed = PolicySeed, engineRandomnessControlled = false,
                failure, elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, steps, lastTurn, decks, seenBots, developed, commanders,
                complete, completed, result = state?.Result, tokenSeen, combatSeen, counterSeen, privateIsolation, comparedCheckpoints, divergentCheckpoints, observedTasks, accepted = accepted.Values, histories = host.Histories
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (state != null) await engine.StopSimulationAsync(state.Id);
        }

        async Task<string> Save(string name, string list)
        {
            var deck = await engine.ImportAsync(name, list);
            var details = await engine.OpenDeckAsync(deck.Id);
            await engine.SaveDeckAsync(deck.Id, details.Revision);
            return deck.Id;
        }
        async Task WriteCheckpoint(GameSnapshot observation)
        {
            await trace.WriteLineAsync(JsonSerializer.Serialize(new {
                formatVersion = TraceFormatVersion, step = steps, elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, observation
            }));
            await trace.FlushAsync();
        }
    }

    internal static string ReplayFacts(GameSnapshot state) => JsonSerializer.Serialize(new {
        state.Turn, state.PhaseKey, state.ActivePlayerId, state.Status, state.Result, state.States,
        players = state.Players.Select(player => new { player.Id, player.Life, player.Eliminated, player.Mana, player.Counters,
            player.States, player.CommanderDamage, player.LandPlaysUsed, player.LandPlayLimit, player.MaxHandSize,
            zones = player.Zones.Select(zone => new { zone.Name, zone.Count, zone.Cards, zone.TopCard }) }),
        state.Stack, state.Combat
    });

    private static void RecordPrivacyContext(string profile, AutomatedSeatObservation observation)
    {
        string path = Path.Combine(profile, "whole-seat-ai-privacy-context.json");
        if (File.Exists(path)) return;
        File.WriteAllText(Path.Combine(profile, "whole-seat-ai-privacy-original-private.json"), JsonSerializer.Serialize(observation));
        var state = observation.State;
        var disclosures = state.Decision?.Payload switch {
            OptionsPayload options => options.Selection.Disclosures.Length,
            OrderingPayload ordering => ordering.Selection.Disclosures.Length,
            RevealPayload reveal => reveal.Cards.Length, _ => 0 };
        File.WriteAllText(path, JsonSerializer.Serialize(new { observation.SeatId, state.ViewerId, state.Turn, state.PhaseKey,
            state.Revision, state.BoardRevision, task = state.Decision == null ? "none" : AiTasks.From(state.Decision.Payload).ToString(),
            observation.Semantics.Intent, disclosures, exposed = state.Players.SelectMany(player => player.Zones
                .Where(zone => zone.Name is "Library" or "Hand" && !zone.Cards.IsEmpty).Select(zone => new {
                    player.Id, zone.Name, zone.Count, visible = zone.Cards.Length, opaque = zone.Cards.Count(card => card.FaceDown),
                    identifiers = zone.Cards.Count(card => card.VisualId.Length > 0), actionable = zone.Cards.Count(card => card.Key.Length > 0) }))
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static bool ComparePublicProjection(GameSnapshot spectator, GameSnapshot seat, string profile)
    {
        string PublicFacts(GameSnapshot state) => JsonSerializer.Serialize(state.Players.OrderBy(player => player.Id).Select(player => new {
            player.Id, player.Life, player.Eliminated,
            mana = player.Mana.OrderBy(entry => entry.Key).Select(entry => new { entry.Key, entry.Value }),
            zones = player.Zones.OrderBy(zone => zone.Name).Select(zone => new { zone.Name, zone.Count }),
            battlefield = player.Zone("Battlefield").Cards.OrderBy(card => card.CombatId.Length > 0 ? card.CombatId : card.VisualId)
                .Select(card => new { id = card.CombatId.Length > 0 ? card.CombatId : card.VisualId, card.Power, card.Toughness,
                    card.Damage, card.Tapped, card.PhasedOut, counters = card.Counters.OrderBy(entry => entry.Key).Select(entry => new { entry.Key, entry.Value }) })
        }));
        string expected = PublicFacts(spectator), actual = PublicFacts(seat);
        if (expected != actual) {
            string path = Path.Combine(profile, "whole-seat-ai-projection-mismatch.json");
            if (!File.Exists(path)) File.WriteAllText(path, JsonSerializer.Serialize(new {
                    spectator.BoardRevision, seat.ViewerId, expected = JsonSerializer.Deserialize<JsonElement>(expected), actual = JsonSerializer.Deserialize<JsonElement>(actual)
                }, new JsonSerializerOptions { WriteIndented = true }));
            return false;
        }
        return true;
    }
}
