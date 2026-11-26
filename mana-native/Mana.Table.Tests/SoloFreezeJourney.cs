using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.AI;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;
using Mana.Magic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// One actual local player application, its ordinary poll/Auto/presentation owners,
// and three production background bots. The script supplies only the human's
// current, independently admitted choices; every transition remains engine-owned.
internal static class SoloFreezeJourney
{
    private sealed record Receipt(GameCommand Command, AiExecutionReceipt Outcome, string? Error);
    private sealed class PlayerPort(ForgeEngine engine) : IMatchConnection
    {
        internal readonly ConcurrentQueue<Receipt> Receipts = new();
        public bool SupportsPriorityContinuation => engine.SupportsPriorityContinuation;
        public Task<GameSnapshot?> ObserveAsync() => engine.ObserveAsync();
        public Task ConcedeAsync(string game) => engine.ConcedeAsync(game);
        public async Task ExecuteAsync(GameCommand command)
        {
            try { await engine.ExecuteAsync(command); Receipts.Enqueue(new(command, AiExecutionReceipt.Accepted, null)); }
            catch (EngineCommandRejectedException error) { Receipts.Enqueue(new(command, AiExecutionReceipt.Rejected, error.ToString())); throw; }
            catch (Exception error) { Receipts.Enqueue(new(command, AiExecutionReceipt.Unknown, error.ToString())); throw; }
        }
    }

    internal static async Task RunAsync(ForgePaths paths, long seed = 261125, bool fast = false)
    {
        Directory.CreateDirectory(paths.Profile);
        await using var engine = new ForgeEngine(paths);
        await engine.InitializeAsync();
        File.WriteAllText(Path.Combine(paths.Profile, "engine-process.json"), JsonSerializer.Serialize(engine.DiagnosticStatus));
        await using var bots = new AiTableHost(engine, _ => new CasualCommanderPolicy(), seed,
            pacing: new TimedAiHostPacing(TimeSpan.FromSeconds(fast ? 0 : 2.4)));
        await bots.StartAsync();
        var deck = await engine.ImportAsync("Solo freeze landfall", LandfallDeck());
        var setup = await engine.PrepareSoloAsync(deck.Id);
        var opponents = new[] { "Blood Rites", "Veloci-Ramp-Tor", "Cinder" }.Select(name =>
            setup.Opponents.FirstOrDefault(o => o.Name.Contains(name, StringComparison.OrdinalIgnoreCase))?.Id)
            .Where(id => id != null).Select(id => id!).ToList();
        foreach (var option in setup.Opponents) if (opponents.Count < 3 && !opponents.Contains(option.Id)) opponents.Add(option.Id);
        File.WriteAllText(Path.Combine(paths.Profile, "setup.json"), JsonSerializer.Serialize(new { deck, opponents, list = LandfallDeck(), seed, fast, cadenceSeconds = fast ? 0 : 2.4 }));
        await engine.StartSoloAsync(deck.Id, opponents.ToImmutableArray());
        GameSnapshot? initial = null;
        var startup = Stopwatch.StartNew();
        while (initial == null) {
            if (startup.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("Solo launch published no initial state");
            initial = await engine.ObserveAsync();
            if (initial == null) await Task.Delay(10);
        }
        new PlayPreferences { Automatic = true, ReducedMotion = true, Sounds = false }.Save(paths.Profile);
        var port = new PlayerPort(engine);
        using var app = new TableApplication(TestTableServices.For(engine) with { Match = port }, paths.Profile, initialState: initial);
        app.Automated = true; app.Initialize();
        var view = app.TestPort;
        var time = Stopwatch.StartNew();
        var receipts = new Queue<Receipt>();
        var events = new Queue<object>();
        var diagnostics = new ConcurrentQueue<ClientDiagnostic>();
        view.Client.DiagnosticRecorded += diagnostics.Enqueue;
        AiSeatSession? human = null;
        AiSubmission? pending = null;
        string facts = "";
        double progressAt = 0;
        long iterations = 0, submitted = 0;
        string? failure = null;
        OwnedDecisionObservation? lastOwned = null;
        object? lastRead = null;
        object? lastProposed = null;
        await using var trace = new StreamWriter(Path.Combine(paths.Profile, "public-progress.jsonl"));
        try {
            while (time.Elapsed < TimeSpan.FromMinutes(12)) {
                var now = time.Elapsed;
                // Device gestures run before the next poll in the shipping
                // update. Give this scripted human the same precedence;
                // accelerated presentation time must not starve it with polls.
                view.Client.Pump();
                while (port.Receipts.TryDequeue(out var receipt)) {
                    receipts.Enqueue(receipt); while (receipts.Count > 64) receipts.Dequeue();
                    if (pending != null && receipt.Command == pending.Command) { human!.Report(pending, receipt.Outcome); pending = null; }
                }
                // The human script deliberately passes when its policy has no
                // play. Confirm the application's ordinary no-land warning
                // through its actual current rendered control.
                if (!view.Busy && view.Hits.FirstOrDefault(hit => hit.Id == "turn-pass-confirm") is { } confirm
                    && view.CurrentHit(confirm)) view.Activate(confirm);
                while (diagnostics.Count > 64) diagnostics.TryDequeue(out _);
                if (bots.LastFault != null) throw new InvalidOperationException("Background AI fault: " + JsonSerializer.Serialize(bots.LastFault));
                if (view.Client.NeedsRecovery || view.Error.Length > 0) throw new InvalidOperationException(view.Error.Length > 0 ? view.Error : "Player client requires recovery");
                if (view.Match is not { } state) throw new InvalidDataException("The local application lost its match");
                if (state.Status == "error") throw new InvalidOperationException(state.Error ?? "Rules failure");
                string next = JsonSerializer.Serialize(new { state.Turn, state.PhaseKey, state.ActivePlayerId,
                    stack = state.Stack.Select(s => s.Id), action = state.Activity.LastOrDefault()?.Id,
                    players = state.Players.Select(p => new { p.Id, p.Life, p.Eliminated, zones = p.Zones.Select(z => new { z.Name, z.Count }) }) });
                if (next != facts) {
                    facts = next; progressAt = now.TotalSeconds;
                    var entry = new { seconds = progressAt, state.Turn, state.PhaseKey, state.ActivePlayerId, state.Status,
                        state.Revision, state.BoardRevision, decision = state.Decision?.Id, state.DecisionOwnerId,
                        stack = state.Stack.Length, battlefield = state.Players.Select(p => new { p.Name, count = p.Zone("Battlefield").Count }) };
                    events.Enqueue(entry); while (events.Count > 64) events.Dequeue();
                    await trace.WriteLineAsync(JsonSerializer.Serialize(entry)); await trace.FlushAsync();
                    File.WriteAllText(Path.Combine(paths.Profile, "checkpoint.json"), JsonSerializer.Serialize(entry));
                }
                if (state.Status == "finished") break;
                if (now.TotalSeconds - progressAt > 45) throw new TimeoutException("No public rules progress for 45 seconds in the actual local application");
                if (pending == null && !view.Busy && !view.Polling && state.Status == "playing" && state.Decision != null
                    && state.Viewer?.Eliminated == false && (state.Decision.Payload is not PriorityPayload
                        || state.ActivePlayerId == state.ViewerId || !view.Client.PriorityContinuationReserved)) {
                    using var readBudget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    var read = await view.Client.ReadCurrentDecisionAsync(engine, readBudget.Token);
                    lastRead = new { kind = read.GetType().Name, ordinary = view.Match,
                        returned = read is ClientSession.CurrentReadResult.Current fresh ? fresh.Observation :
                            read is ClientSession.CurrentReadResult.Retired stale ? stale.Observation : null };
                    if (read is ClientSession.CurrentReadResult.Current { Observation: { } own } current
                        && current.Read.IsCurrent && view.Match is { } currentState
                        && ConnectedPlayerDriver.MatchesOwnedObservation(currentState, own.State)) {
                        lastOwned = own;
                        human ??= new(state.Id, state.ViewerId, new CasualCommanderPolicy(), seed, new(TimeSpan.FromSeconds(30), 4096));
                        human.Observe(new(currentState, domain: own.Domain, semantics: own.Semantics));
                        var evaluated = await human.EvaluateAsync();
                        if (evaluated.Status is AiSeatStatus.Unavailable or AiSeatStatus.Invalid or AiSeatStatus.BudgetExceeded or AiSeatStatus.NeedsRecovery)
                            throw new InvalidOperationException("Human script cannot answer the current actual input: " + JsonSerializer.Serialize(evaluated));
                        if (human.TakeCommand() is { } ticket) {
                            lastProposed = new { ticket.Stamp, kind = ticket.Command.GetType().Name, ticket.Command, ticket.ReasonCode };
                            if (ticket.Command is UseOfferedAction offered) {
                                var card = CardViews.FindActionSource(currentState, offered.Card)?.Card
                                    ?? throw new InvalidDataException("The human script's current source disappeared");
                                view.Client.SourceActions.Observe(new(currentState.Id, currentState.Decision!.Id,
                                    currentState.Revision, currentState.ViewerId, card.Key, CardViews.Identity(card)));
                                while (view.Client.SourceActions.View.Status == SourceActionStatus.Loading) {
                                    view.Client.Pump(); await Task.Delay(2, readBudget.Token);
                                }
                            }
                            if (current.Read.IsCurrent && view.SubmitCurrent(ticket.Command)) { pending = ticket; submitted++; }
                            else { human.Dispose(); human = null; }
                        }
                    }
                }
                app.Update(new GameTime(now * (fast ? 20 : 1), TimeSpan.FromMilliseconds(10)), new MouseState(), new KeyboardState(), new Point(1344, 768));
                app.ReadPresentation(); iterations++;
                await Task.Delay(5);
            }
            if (view.Match?.Status != "finished") throw new TimeoutException("The local application reached its 12-minute investigation limit");
        } catch (Exception error) {
            failure = error.ToString();
            try {
                var observations = await engine.ObserveAutomatedSeatsAsync().WaitAsync(TimeSpan.FromSeconds(5));
                File.WriteAllText(Path.Combine(paths.Profile, "stalled-bot-observations.json"), JsonSerializer.Serialize(observations));
                var owners = (System.Collections.IDictionary)typeof(AiTableHost).GetField("priorityOwners", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(bots)!;
                var values = owners.Values.Cast<object>().Select(owner => owner.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                    .ToDictionary(field => field.Name, field => field.GetValue(owner)?.ToString())).ToArray();
                File.WriteAllText(Path.Combine(paths.Profile, "stalled-bot-priority-owners.json"), JsonSerializer.Serialize(values));
            } catch (Exception diagnosticError) { File.WriteAllText(Path.Combine(paths.Profile, "bot-capture-error.txt"), diagnosticError.ToString()); }
            try {
                string jstack = Path.Combine(Environment.GetEnvironmentVariable("JAVA_HOME") ?? "", "bin", "jstack.exe");
                if (engine.DiagnosticStatus is { } status && File.Exists(jstack)) {
                    var start = new ProcessStartInfo(jstack) { UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true };
                    start.ArgumentList.Add(status.ProcessId.ToString());
                    using var dump = Process.Start(start)!;
                    var output = dump.StandardOutput.ReadToEndAsync();
                    await dump.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    File.WriteAllText(Path.Combine(paths.Profile, "rules-threads.txt"), await output);
                }
            } catch (Exception diagnosticError) { File.WriteAllText(Path.Combine(paths.Profile, "thread-capture-error.txt"), diagnosticError.ToString()); }
            throw;
        }
        finally {
            File.WriteAllText(Path.Combine(paths.Profile, "result.json"), JsonSerializer.Serialize(new {
                failure, elapsedSeconds = time.Elapsed.TotalSeconds, iterations, submitted, state = view.Match,
                view.Busy, view.Polling, view.Error, view.Client.RecoveryState, pending, lastOwned, lastRead, lastProposed,
                humanView = human?.View, sourceView = view.Client.SourceActions.View,
                receipts = receipts.ToArray(), events = events.ToArray(), diagnostics = diagnostics.ToArray(),
                botHistories = bots.Histories, botFault = bots.LastFault, priority = bots.PriorityContinuations
            }, new JsonSerializerOptions { WriteIndented = true }));
            human?.Dispose();
        }
    }

    private static string LandfallDeck()
    {
        string[] spells = ["Icetill Explorer", "Elven Chorus", "Mossborn Hydra", "Scute Swarm", "Springheart Nantuko",
            "Tireless Tracker", "Tireless Provisioner", "World Shaper", "Realms Uncharted", "Heroic Intervention",
            "Ramunap Excavator", "Crucible of Worlds", "Exploration", "Azusa, Lost but Seeking", "Wayward Swordtooth",
            "Dryad of the Ilysian Grove", "Ancient Greenwarden", "Splendid Reclamation", "Aftermath Analyst",
            "Roaring Earth", "Avenger of Zendikar", "Sakura-Tribe Elder", "Cultivate", "Kodama's Reach", "Rampant Growth",
            "Nature's Lore", "Three Visits", "Farseek", "Harrow", "Roiling Regrowth", "Crop Rotation", "Explore", "Growth Spiral",
            "Oracle of Mul Daya", "Courser of Kruphix", "Lotus Cobra", "Llanowar Elves", "Elvish Mystic", "Birds of Paradise",
            "Sol Ring", "Arcane Signet", "Talisman of Curiosity", "Simic Signet", "Mind Stone", "Beast Within", "Return to Nature",
            "Reclamation Sage", "Eternal Witness", "Harmonize", "Shamanic Revelation", "Rishkar's Expertise", "Return of the Wildspeaker",
            "Cultivator Colossus", "Rampaging Baloths", "Greensleeves, Maro-Sorcerer", "Sporemound", "Zendikar's Roil", "Titania, Protector of Argoth",
            "Bala Ged Recovery", "Rejuvenating Springs"];
        return "Deck\n25 Forest\n10 Island\n1 Command Tower\n1 Simic Growth Chamber\n1 Evolving Wilds\n1 Terramorphic Expanse\n"
            + string.Join('\n', spells.Select(s => "1 " + s)) + "\nCommander\n1 Aesi, Tyrant of Gyre Strait";
    }
}
