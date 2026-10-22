using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;
using Mana.Magic;
using Mana.Conformance;
using System.Text.Json.Nodes;

int checks = 0;
void Check(bool value, string description) { if (!value) throw new Exception(description); checks++; Console.WriteLine(description); }
var replica = new SessionReplica<GameSnapshot>(s => s.Id, s => s.Revision);
var state = new GameSnapshot("game", "playing", null, null, 0, 1, "Main phase", "MAIN1", 0, [], [], null, new() { Id = "decision", Intent = DecisionIntent.Priority }) { Revision = 8 };
Check(replica.Accept(state, 0), "First snapshot initializes a replica");
Check(!replica.Accept(state with { Revision = 7 }, 0), "Out-of-order snapshots cannot roll back state");
long oldEpoch = replica.Epoch; replica.BeginCommand();
Check(!replica.Accept(state with { Revision = 100 }, oldEpoch), "A poll started before a command cannot overwrite its outcome");
Check(replica.Accept(state with { Id = "rematch", Revision = 0 }, replica.Epoch), "New games may restart revision numbering");
Check(!replica.Accept(state with { Revision = 200 }, replica.Epoch), "A retired game's updates cannot resurrect it");
var first = new Card { Name = "Forest", VisualId = "forest-1", CombatId = "forest-1", Key = "decision:1", Selectable = true };
var second = first with { VisualId = "forest-2", CombatId = "forest-2", Key = "decision:2" };
Check(!CardViews.Same(first, second), "Identically named cards retain separate identities");
Check(!CardViews.Same(new() { Name = "Forest" }, new() { Name = "Forest" }), "Anonymous cards are never matched by name");
Check(CardViews.Same(first, first with { Key = "later:1" }), "A new decision handle preserves a visible occurrence");
state = state with { Players = [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", 2, [first, second], null), new("Hand", 0, [], null)])],
    Decision = new() { Id = "decision", Intent = DecisionIntent.Attack }, Combat = new([], [new("forest-1", [new("player", "1", 1, "Opponent")])], null) };
var interaction = new InteractionController();
Check(interaction.CardClick(state, first).Effect == InteractionEffect.Select, "Click selects a projected attack source");
var click = interaction.PlayerClick(state, 1).Reply;
var drag = InteractionController.Assign(state, "forest-1", 1);
Check(click == drag && drag is ToggleAttackPlayer { Attacker: "decision:1" }, "Click and drag construct the identical scoped command");
Check(InteractionController.Assign(state, "forest-1", 2) == null, "Unprojected defenders never produce commands");
Check(interaction.CardClick(state with { Players = [] }, first).Effect == InteractionEffect.None, "Revoked cards cannot be selected through an old reference");
var malformed = new ToggleAttackPermanent("game", "decision", Attacker: "card", DefenderCard: " ");
try { malformed.Validate(); throw new Exception("Invalid reply was accepted"); } catch (ArgumentException) { Check(true, "A permanent attack requires its own defender handle"); }
var sourceReply = new UseOfferedAction("game", "decision", Card: "current-source-key", ActionId: "opaque-action");
sourceReply.Validate();
foreach (var incomplete in new[] { sourceReply with { GameId = "" }, sourceReply with { DecisionId = "" },
    sourceReply with { Card = " " }, sourceReply with { ActionId = "" } }) {
    bool rejected = false;
    try { incomplete.Validate(); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "A source action requires the current game, decision, source key and opaque action handle");
}

var scenario = new Scenario("oracle-check", "test", new(), [new(0, "pass", new())], [], Players: 1);
var expected = new JsonObject { ["library"] = new JsonArray("occurrence-1", "occurrence-2"), ["life"] = 40 };
await using var reference = new Oracle("reference", expected);
await using var identical = new Oracle("same", (JsonObject)expected.DeepClone());
Check((await ConformanceRunner.RunAsync(scenario, reference, identical)).Passed, "Conformance compares initial state and each semantic decision boundary");
await using var wrongLibrary = new Oracle("wrong-library", new() { ["library"] = new JsonArray("occurrence-2", "occurrence-1"), ["life"] = 40 });
var report = await ConformanceRunner.RunAsync(scenario, reference, wrongLibrary);
Check(!report.Passed && report.Checkpoints[0].Differences.Any(d => d.Path.StartsWith("$.rules.library")), "Hidden library order divergence fails conformance before another action");
await using var wrongSeat = new Oracle("wrong-seat", expected, true);
Check(!(await ConformanceRunner.RunAsync(scenario, reference, wrongSeat)).Passed, "A private-view mismatch fails even when rules state agrees");
Check(ConformanceRunner.CompareJson(JsonNode.Parse("{\"x\":null}"), JsonNode.Parse("{}")).Length == 1, "Missing fields differ from explicit null values");
var passState = state with { Decision = new() { Id = "pass", Kind = "input", Intent = DecisionIntent.Priority, CanAutoPass = true, OkEnabled = true } };
var tableChoice = state with { Decision = new() { Id = "choose", Kind = "choice", Min = 1, Max = 2,
    Choices = [new(7, first.Name, first with { VisualId = "", Key = "", CombatId = "" }) { CardId = first.VisualId }, new(9, second.Name, second) { CardId = second.VisualId }] } };
Check(TableChoices.IsDirect(tableChoice), "Existing cards are chosen on the table without matching names");
Check(interaction.CardClick(tableChoice, second) is { Effect: InteractionEffect.SelectChoice, ChoiceIndex: 9, Reply: null }, "Clicking the second identical card stages its authoritative index without committing");
Check(!TableChoices.CanConfirm(tableChoice, []) && !TableChoices.CanConfirm(tableChoice, [7, 7]) && TableChoices.CanConfirm(tableChoice, [9, 7]), "Choice confirmation preserves count, uniqueness, and selection order");
Check(!TableChoices.IsDirect(tableChoice with { Players = [] }), "A disappeared table occurrence falls back to the chooser");
Check(!TableChoices.IsDirect(tableChoice with { Decision = tableChoice.Decision! with { Choices = [new(0, first.Name, first)] } }), "A face with no explicit occurrence link cannot select an identically named card");
var bulkCards = Enumerable.Range(0, 50).Select(i => first with { VisualId = "bulk-" + i, CombatId = "bulk-" + i, Key = "bulk:" + i }).ToArray();
var bulkChoice = tableChoice with { Players = [tableChoice.Viewer! with { Zones = [new("Battlefield", bulkCards.Length, bulkCards.ToImmutableArray(), null)] }],
    Decision = tableChoice.Decision! with { Ordered = true, Choices = bulkCards.Select((card, i) => new Choice(i, card.Name, card) { CardId = card.VisualId }).ToImmutableArray() } };
Check(!TableChoices.IsDirect(bulkChoice), "Ordering fifty visible occurrences uses the numbered gallery without removing cards from the battlefield");
Check(TableChoices.IsDirect(bulkChoice with { Decision = bulkChoice.Decision! with { Ordered = false } }), "An unordered bulk selection retains its direct battlefield targets");
var tempo = new PresentationPacing();
tempo.Observe(null, passState, 0);
Check(!tempo.Ready(.5) && tempo.Ready(1.2), "The opening table has a readable beat before Auto");
var casting = passState with { Stack = [new("Spell", "", false, first) { Id = "spell" }] };
tempo.Observe(passState, casting, 2);
Check(!tempo.Ready(3), "A newly cast spell remains visible before Auto passes");
tempo.Observe(casting, casting with { Revision = 999, Decision = casting.Decision! with { Id = "republished" } }, 3);
Check(tempo.Ready(3.3), "Polling and fresh prompt handles do not restart the animation delay");
var attacking = casting with { Combat = new([new("attacker", 0, [], [])], [], null) };
tempo.Observe(casting, attacking, 4);
Check(!tempo.Ready(5.5) && tempo.Ready(5.7), "Attack declarations receive a longer beat to read the defenders");
tempo.Observe(attacking, attacking with { Decision = new() { Kind = "input", Intent = DecisionIntent.Block } }, 5.7);
Check(tempo.Ready(5.7), "A required blocking decision is immediately available during playback");
var concealed = attacking with { Players = [state.Viewer! with { Zones = [new("Battlefield", 1, [new() { VisualId = null!, FaceDown = true }], null)] }] };
tempo.Observe(attacking, concealed, 6);
tempo.Observe(concealed, concealed with { Revision = 1000 }, 8);
Check(tempo.Ready(8), "A concealed card with a null visual identity is safe to animate and poll");
var pacing = new AutoPriority();
var noStops = new HashSet<string>();
Check(pacing.Update(passState, true, false, noStops, false, false) is AutoPassPriority, "Eligible Auto priority dispatches on the first update without an artificial wait");
Check(pacing.Update(passState, true, false, noStops, false, false) == null, "Auto dispatches a decision only once");
var nextPass = passState with { ActivePlayerId = 1, Decision = passState.Decision! with { Id = "next-pass" } };
Check(TurnGuide.Confirm(nextPass) == "Pass priority", "An opponent's main phase asks to pass priority rather than advance their phase");
Check(pacing.Update(nextPass, true, false, noStops, false, true) == null, "An outstanding operation defers Auto without consuming the decision");
Check(pacing.Update(nextPass, true, false, noStops, false, false)?.DecisionId == "next-pass", "The next player's eligible window advances as soon as the connection is available");
Check(pacing.Update(nextPass with { Id = "other-game" }, true, false, noStops, false, false)?.GameId == "other-game", "Automatic passes remain scoped to their game");
pacing.Reset();
Check(pacing.Update(passState, true, false, new HashSet<string> { "MAIN1" }, false, false) == null, "Own-turn phase stops remain authoritative for Auto");
Check(pacing.Update(passState, false, false, noStops, false, false) == null, "Full control never passes automatically");
Check(pacing.Update(passState, true, true, noStops, false, false) == null, "Hold prevents even an immediately eligible pass");
Check(pacing.Update(passState, true, false, noStops, true, false) == null, "Reading or interacting with a card pauses Auto");
Check(pacing.Update(passState with { Decision = passState.Decision! with { CanAutoPass = false } }, true, false, noStops, false, false) == null, "A newly available response stops automatic passing");
Check(pacing.Update(passState with { Decision = passState.Decision! with { Intent = DecisionIntent.Mana } }, true, false, noStops, false, false) == null, "Required payment decisions cannot be skipped");
Check(pacing.Update(passState with { Decision = passState.Decision! with { Intent = DecisionIntent.Block } }, true, false, noStops, false, false) == null, "Auto never confirms no blocks, even with an incorrectly permissive flag");
Check(pacing.Update(tableChoice, true, false, noStops, false, false) == null, "Auto never submits a staged card choice");
Check(pacing.Update(passState with { Status = "finished" }, true, false, noStops, false, false) == null, "Finished games never submit automatic passes");
Check(pacing.Update(passState, true, false, noStops, false, false) is AutoPassPriority, "Leaving an inspection or Hold resumes an eligible pass immediately");

var ignoredAbility = new PriorityAbility("fetch-ability", first.Key, "Arid Mesa", "Pay 1 life, sacrifice: Search.", true);
var ignoredPriority = passState with { IgnoredPriorityAbilities = [ignoredAbility], Decision = passState.Decision! with { PriorityAbilities = [ignoredAbility] } };
Check(TurnGuide.CanAutoPass(ignoredPriority, true, false, noStops, false), "An ignored ability uses the engine's explicit permission to pass");
Check(!TurnGuide.CanAutoPass(ignoredPriority with { Decision = ignoredPriority.Decision! with { CanAutoPass = false } }, true, false, noStops, false), "Ignoring one ability cannot override another playable response");
var undoAbility = new RestorePriorityAbility(passState.Id, AbilityId: ignoredAbility.Id);
undoAbility.Validate(); Check(true, "Undo ability preferences are valid without a local decision");
try { new IgnorePriorityAbility(passState.Id, "", ignoredAbility.Id).Validate(); throw new Exception("Unscoped ignore was accepted"); } catch (ArgumentException) { Check(true, "Ignoring an ability requires a current decision handle"); }
try { (undoAbility with { AbilityId = "" }).Validate(); throw new Exception("Anonymous ability was accepted"); } catch (ArgumentException) { Check(true, "Ability preferences require the exact engine-owned ability handle"); }
var settingConnection = new SettingConnection(ignoredPriority with { Decision = null });
var settingClient = new ClientSession(settingConnection); settingClient.Accept(settingConnection.State);
Check(!settingClient.SubmitSetting(undoAbility with { GameId = "other-game" }), "An ability preference cannot change another match");
Check(settingClient.SubmitSetting(undoAbility), "Undo submits while another player has priority");
var settingDeadline = DateTime.UtcNow.AddSeconds(5);
while (settingClient.Busy && DateTime.UtcNow < settingDeadline) { settingClient.Pump(); await Task.Delay(5); }
Check(!settingClient.Busy && settingClient.State!.IgnoredPriorityAbilities.Length == 0 && settingConnection.Replies.SequenceEqual([undoAbility]), "Undo reconciles a fresh snapshot without advancing a gameplay decision");
var secondIgnoredAbility = ignoredAbility with { Id = "other-fetch-ability" };
var advancingConnection = new SettingConnection(ignoredPriority with { IgnoredPriorityAbilities = [ignoredAbility, secondIgnoredAbility] }) { HoldFirstPass = true };
var advancingClient = new ClientSession(advancingConnection); advancingClient.Accept(advancingConnection.State);
Check(advancingClient.Advance(_ => true, (_, _) => false), "An eligible ignored-ability window can start automatic progression");
await advancingConnection.PassStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(advancingClient.SubmitSetting(undoAbility) && !advancingClient.SubmitSetting(undoAbility), "Undo interrupts buffered Auto and queues only one setting behind the active command");
var undoSecondAbility = undoAbility with { AbilityId = secondIgnoredAbility.Id };
Check(advancingClient.SubmitSetting(undoSecondAbility) && !advancingClient.SubmitSetting(undoSecondAbility), "Undoing another ability queues independently without replacing the first preference");
Check(advancingConnection.Replies.Count == 1 && advancingConnection.Replies[0] is AutoPassPriority, "The preference waits for the in-flight pass to reconcile");
advancingConnection.ReleasePass.SetResult();
settingDeadline = DateTime.UtcNow.AddSeconds(5);
while (DateTime.UtcNow < settingDeadline) { advancingClient.Pump(); if (!advancingClient.Busy && advancingConnection.Replies.Count == 3) break; await Task.Delay(5); }
Check(!advancingClient.Busy && advancingConnection.Replies.SequenceEqual([new AutoPassPriority(ignoredPriority.Id, ignoredPriority.Decision!.Id), undoAbility, undoSecondAbility]), "Multiple Undos cancel further buffered passes and remain serialized in request order");
Check(advancingClient.State!.IgnoredPriorityAbilities.Length == 0, "The interrupted Auto snapshot cannot restore an undone preference");
var expiringConnection = new SettingConnection(ignoredPriority) { HoldFirstPass = true, ExpireOnPass = true };
var expiringClient = new ClientSession(expiringConnection); expiringClient.Accept(expiringConnection.State);
expiringClient.Advance(_ => true, (_, _) => false);
await expiringConnection.PassStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(expiringClient.SubmitSetting(undoAbility), "Undo can queue before the in-flight pass reaches the viewer's next turn");
expiringConnection.ReleasePass.SetResult();
settingDeadline = DateTime.UtcNow.AddSeconds(5);
while (DateTime.UtcNow < settingDeadline) { expiringClient.Pump(); if (!expiringClient.Busy) break; await Task.Delay(5); }
Check(!expiringClient.Busy && expiringConnection.Replies is [AutoPassPriority ] && expiringClient.State!.IgnoredPriorityAbilities.Length == 0,
    "A queued Undo is dropped without an RPC when the reconciled pass has already expired its preference");
Check(!expiringClient.SubmitSetting(undoAbility) && !expiringClient.Submit(undoAbility), "Already cleared ability preferences cannot submit stale Undo commands");

var connection = new ControlledConnection(); var client = new ClientSession(connection);
client.Accept(passState);
client.Poll(false, _ => { }); await connection.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
var command = new PassPriority(passState.Id, passState.Decision!.Id);
Check(client.Submit(command) && !client.Submit(command), "The client serializes duplicate submissions while a command is outstanding");
connection.Reads[0].SetResult(passState with { Revision = 999, Result = "obsolete-poll" });
connection.Reads[1].SetResult(passState with { Revision = 10, Decision = passState.Decision with { Id = "next" } });
var deadline = DateTime.UtcNow.AddSeconds(5);
while ((client.Busy || client.Polling) && DateTime.UtcNow < deadline) { client.Pump(); await Task.Delay(5); }
Check(client.State?.Decision?.Id == "next" && client.State.Result == null && connection.Replies == 1, "A late pre-command poll cannot replace the command's authoritative result");
Check(!client.Submit(command), "An old decision cannot be submitted after a new snapshot");
checks += await AppUpdateRegression.RunAsync();
await IncrementalUpdateRegression.RunAsync(Check);
HandPlacementRegression.Run(Check);
await QuietPriorityRegression.RunAsync(Check);
await SourceActionSessionRegression.RunAsync(Check);
await InteractionContractMatrix.RunAsync(Check);
AllocationRegression.Run(Check);
await RecoveryRegression.RunAsync(Check);
GameplayClarityChecks.Run(Check);
ImmutableStateChecks.Run(Check);
PublicStateDescriptorChecks.Run(Check);
DeckExperienceChecks.Run(Check);
await PrintingArtRegression.RunAsync(Check);
Console.WriteLine($"Passed {checks} headless checks.");

sealed class ControlledConnection : IMatchConnection
{
    private int observed;
    public int Replies;
    public TaskCompletionSource PollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<GameSnapshot?>[] Reads { get; } = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
    public Task<GameSnapshot?> ObserveAsync() { int index = Interlocked.Increment(ref observed) - 1; if (index == 0) PollStarted.SetResult(); return Reads[index].Task; }
    public Task ExecuteAsync(GameCommand reply) { Interlocked.Increment(ref Replies); return Task.CompletedTask; }
    public Task ConcedeAsync(string gameId) => Task.CompletedTask;
}

sealed class SettingConnection(GameSnapshot state) : IMatchConnection
{
    public GameSnapshot State { get; private set; } = state;
    public bool HoldFirstPass { get; init; }
    public bool ExpireOnPass { get; init; }
    public List<GameCommand> Replies { get; } = [];
    public TaskCompletionSource PassStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleasePass { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
    public async Task ExecuteAsync(GameCommand reply)
    {
        Replies.Add(reply);
        if (reply is AutoPassPriority) {
            PassStarted.SetResult(); if (HoldFirstPass) await ReleasePass.Task;
            State = State with { Revision = State.Revision + 1, Decision = State.Decision! with { Id = "next-pass" } };
            if (ExpireOnPass) State = State with { IgnoredPriorityAbilities = [], Turn = State.Turn + 1, ActivePlayerId = State.ViewerId };
        } else if (reply is RestorePriorityAbility restore) {
            State = State with { Revision = State.Revision + 1, IgnoredPriorityAbilities = State.IgnoredPriorityAbilities.Where(a => a.Id != restore.AbilityId).ToImmutableArray(), Decision = State.Decision == null ? null : State.Decision with { CanAutoPass = false } };
        } else throw new InvalidOperationException("Unexpected preference-test command");
    }
    public Task ConcedeAsync(string gameId) => Task.CompletedTask;
}

sealed class Oracle(string name, JsonObject rules, bool leak = false) : IConformanceEngine
{
    public string Name => name;
    private Checkpoint State() => new("decision", rules, new Dictionary<int, JsonObject> { [0] = new() { ["privateHand"] = leak ? "wrong-card" : "own-card" } }, []);
    public Task<Checkpoint> StartAsync(Scenario scenario, CancellationToken token) => Task.FromResult(State());
    public Task<Checkpoint> ApplyAsync(SemanticCommand command, CancellationToken token) => Task.FromResult(State());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
