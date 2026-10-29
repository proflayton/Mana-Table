using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Client;
using Mana.Contracts;

internal static class GameplayUndoRegression
{
    internal static readonly Type[] CommandTypes = [typeof(UndoAction)];
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var state = State(); var command = UndoFacts.Command(state)!;
        check(GameCommandPermissions.Allows(state, command) && command is { OfferId: "offer", ActionId: "action", BarrierVersion: 7 },
            "Certified Undo uses the exact current actor, decision, one-use offer, action and authoritative barrier");
        foreach (var invalid in new[] { command with { GameId = "old" }, command with { DecisionId = "old" },
            command with { OfferId = "other" }, command with { ActionId = "other" }, command with { BarrierVersion = 6 } })
            check(!GameCommandPermissions.Allows(state, invalid), "A captured Undo command cannot substitute another game, input, offer, action or barrier");
        foreach (var invalid in new[] { state with { Undo = null }, state with { DecisionOwnerId = 1 },
            state with { PriorityPlayerId = 1 }, state with { Status = "resolving" },
            state with { Undo = state.Undo! with { Offer = state.Undo.Offer! with { SourceCardId = "missing" } } },
            state with { Undo = state.Undo! with { Offer = state.Undo.Offer! with { SourceCardId = null! } } },
            state with { Undo = state.Undo! with { Offer = state.Undo.Offer! with { SourceName = null! } } },
            state with { Undo = state.Undo! with { Offer = state.Undo.Offer! with { PlayerId = 1 } } },
            state with { Undo = state.Undo! with { Offer = state.Undo.Offer! with { BarrierVersion = 6 } } } })
            check(!UndoFacts.CanUndo(invalid) && !GameCommandPermissions.Allows(invalid, command),
                "Absent, foreign, undisclosed and stale Undo offers never confer authority");
        check(!GameCommandPermissions.Allows(state, new UndoLastAction(state.Id, state.Decision!.Id)),
            "A certified capability does not reactivate the removed legacy gameplay Undo command");
        OfferExecutionAuthority(state, command, check);
        var combatAlias = state with { Players = [state.Viewer! with { Zones = [new("Battlefield", 1,
            [state.Viewer.Zone("Battlefield").Cards.Single() with { CombatId = "combat-alias" }], null)] }, state.Players[1]],
            Undo = state.Undo! with { Offer = state.Undo.Offer! with { SourceCardId = "combat-alias" } } };
        check(!UndoFacts.CanUndo(combatAlias), "An Undo source requires its published visual occurrence rather than a different combat alias");
        var roundtrip = JsonSerializer.Deserialize<GameSnapshot>(JsonSerializer.Serialize(state))!;
        check(UndoFacts.Command(roundtrip) == command, "Certified offer fields survive an immutable contract roundtrip");
        var missing = JsonNode.Parse(JsonSerializer.Serialize(state.Undo))!; missing.AsObject().Remove("BarrierVersion");
        check(Reject(() => JsonSerializer.Deserialize<UndoState>(missing.ToJsonString())), "A partially specified Undo state cannot manufacture default authority values");
        var restored = Restored(state);
        UndoFacts.ValidateProgress(state, restored);
        var evicted = restored with { Executions = new(4, 3, [], []) };
        ExecutionFrames.Validate(evicted); UndoFacts.ValidateProgress(state, evicted);
        check(UndoFacts.RestoredSince(state, evicted) && !UndoFacts.CanUndo(evicted),
            "Retained restoration evidence survives execution eviction without becoming a fresh Undo capability");
        foreach (var invalid in new[] { restored with { Revision = state.Revision }, restored with { BoardRevision = state.BoardRevision },
            restored with { Undo = restored.Undo! with { BarrierVersion = 7 } } })
            check(Reject(() => UndoFacts.ValidateProgress(state, invalid)), "Restoration advances rules and publication fences instead of rolling history backward");
        var yield = new FiniteStackYieldState("old-grant", FiniteStackYieldStatus.Active, ["mana"], ["mana"], 1, 1);
        check(Reject(() => UndoFacts.ValidateProgress(state with { FiniteStackYield = yield }, restored with { FiniteStackYield = yield })),
            "A restored publication cannot retain the same old active server yield grant");
        check(Reject(() => UndoFacts.ValidateProgress(restored, restored with { Undo = null }))
                && Reject(() => UndoFacts.ValidateProgress(restored, restored with { Undo = restored.Undo! with {
                    LastRestoration = restored.Undo.LastRestoration! with { ActionId = "rewritten" } } })),
            "A retained restoration marker cannot disappear or be rewritten at the same version");
        await ClientReconciliation(state, restored, check);
        await UncertainReceipt(state, restored, check);
        var automatic = new Connection(state, restored); var advance = new DecisionAdvance(automatic);
        check((await advance.RunAsync(state, _ => true, (_, _) => false))?.Undo?.RestorationVersion == 1 && automatic.Commands == 1,
            "An automatic worker observes restoration and stops before passing the fresh restored decision");
    }

    private static void OfferExecutionAuthority(GameSnapshot state, UndoAction command, Action<bool, string> check)
    {
        var execution = state.Executions!.Occurrences.Single();
        var variants = new List<(ExecutionOccurrence Execution, string Reason)> {
            (execution with { ActorPlayerId = 1 }, "another actor"),
            (execution with { ActorPlayerId = null }, "an undisclosed actor"),
            (execution with { SourceCardId = "other-source" }, "another source occurrence"),
            (execution with { SourceCardId = null }, "an undisclosed source occurrence")
        };
        variants.AddRange(Enum.GetValues<ExecutionKind>().Where(kind => kind != ExecutionKind.Mana)
            .Select(kind => (execution with { Kind = kind }, "a " + kind + " execution")));
        variants.AddRange(Enum.GetValues<ExecutionStep>().Where(step => step != ExecutionStep.Complete)
            .Select(step => (execution with { Step = step, Outcome = null }, "unfinished " + step + " work")));
        variants.AddRange(Enum.GetValues<ExecutionOutcome>().Where(outcome => outcome != ExecutionOutcome.Resolved)
            .Select(outcome => (execution with { Outcome = outcome }, "a " + outcome + " result")));
        foreach (var (occurrence, reason) in variants) {
            var malformed = state with { Executions = state.Executions with { Occurrences = [occurrence], Transitions = [] } };
            ExecutionFrames.Validate(malformed); // A valid historical record still grants no unrelated inverse.
            check(!UndoFacts.CanUndo(malformed) && !GameCommandPermissions.Allows(malformed, command),
                "A certified Undo cannot borrow authority from " + reason);
            var client = new ClientSession(new Connection(state, Restored(state)));
            check(Reject(() => client.Accept(malformed)) && client.State == null,
                "Malformed live Undo execution causality is rejected before a client replica accepts " + reason);
        }
        var unrelated = execution with { Id = "unrelated", BudgetRootId = "unrelated", ActorPlayerId = 1, SourceCardId = "other-source" };
        var retargeted = state with {
            Executions = state.Executions with { Occurrences = [execution, unrelated] },
            Undo = state.Undo! with { Offer = state.Undo.Offer! with { ExecutionId = unrelated.Id } }
        };
        ExecutionFrames.Validate(retargeted);
        check(!UndoFacts.CanUndo(retargeted) && !GameCommandPermissions.Allows(retargeted, command),
            "A live offer cannot retarget an unrelated existing completed mana execution while retaining its source and actor claims");
    }

    private static async Task ClientReconciliation(GameSnapshot state, GameSnapshot restored, Action<bool, string> check)
    {
        var connection = new Connection(state, restored); var source = new Source();
        var client = new ClientSession(connection, sourceActions: source); client.Accept(state);
        var card = state.Viewer!.Zone("Battlefield").Cards.Single();
        client.SourceActions.Observe(new(state.Id, state.Decision!.Id, state.Revision, state.ViewerId, card.Key, card.VisualId));
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool reconciledBeforeChanged = false;
        client.Changed += (_, next) => {
            if (next?.Undo?.RestorationVersion == 1) reconciledBeforeChanged = client.RestorationChanged
                && client.SourceActions.View.Context == null && client.ExecutionChanges.Baseline
                && client.State?.Id == state.Id && !client.IsAdvancing;
        };
        check(client.Submit(UndoFacts.Command(state)!), "Certified Undo enters the ordinary serialized execute/observe lane");
        await Drain(client);
        check(reconciledBeforeChanged && client.State == restored && connection.Commands == 1,
            "Restoration revokes old action readers and baselines derived feedback before Changed without retiring the match");
        source.Result.SetResult(new(state.Id, state.Decision.Id, card.Key, card.VisualId, [new("old-action", "activate", "Old action")]));
        var freshCard = restored.Viewer!.Zone("Battlefield").Cards.Single();
        var freshContext = new SourceActionContext(restored.Id, restored.Decision!.Id, restored.Revision, restored.ViewerId, freshCard.Key, freshCard.VisualId);
        client.SourceActions.Observe(freshContext);
        await Until(() => { client.Pump(); return source.Calls == 2 && client.SourceActions.View.Status != SourceActionStatus.Loading; });
        check(client.SourceActions.View.Context == freshContext && client.SourceActions.View.Status == SourceActionStatus.Stale
                && !client.Submit(UndoFacts.Command(state)!),
            "A late pre-restoration source result or captured Undo gesture cannot recover old authority");
        client.Accept(restored);
        check(!client.RestorationChanged, "Repeated observations do not repeat restoration reconciliation");
        check(Reject(() => client.Accept(state with { Revision = restored.Revision + 100 })) && client.State == restored,
            "A newer transport envelope cannot regress retained authoritative restoration state");

        var pollConnection = new Connection(state, restored) { HeldRead = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var polled = new ClientSession(pollConnection); polled.Accept(state); polled.Poll(false, _ => { });
        await pollConnection.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        polled.Accept(restored); pollConnection.HeldRead.SetResult(state with { Revision = 1000 }); await Drain(polled);
        check(polled.State == restored && !polled.NeedsRecovery,
            "Restoration advances the client epoch so an earlier poll cannot resurrect old state even with a larger revision");
    }

    private static async Task UncertainReceipt(GameSnapshot state, GameSnapshot restored, Action<bool, string> check)
    {
        var connection = new Connection(state, restored) { LoseReceipt = true };
        var client = new ClientSession(connection); client.Accept(state);
        client.Submit(UndoFacts.Command(state)!); await Drain(client);
        check(client.NeedsRecovery && !client.Submit(UndoFacts.Command(state)!) && connection.Commands == 1,
            "A lost Undo receipt blocks replay until the actual state is reconciled");
        client.Poll(false, _ => { }); await Drain(client);
        check(client.State == restored && client.RestorationChanged && !client.NeedsRecovery && connection.Commands == 1,
            "Reconnection observes the retained restoration once without retrying the unknown command");
    }

    internal static GameSnapshot State()
    {
        var source = new Card { VisualId = "source", Key = "source-key", Name = "Sol Ring", Type = "Artifact", Tapped = true };
        var execution = new ExecutionOccurrence("mana", "mana", ExecutionKind.Mana, ExecutionStep.Complete) {
            ActorPlayerId = 0, SourceCardId = "source", SourceName = source.Name, Outcome = ExecutionOutcome.Resolved };
        return new("undo-game", "playing", null, null, 0, 3, "Main", "MAIN1", 0,
            [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty.Add("C", 2),
                [new("Battlefield", 1, [source], null)]),
             new(1, "Other", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
            new("priority", new(), new PriorityPayload(new("Pass", true), null, true))) {
            Revision = 2, BoardRevision = 1, PriorityPlayerId = 0, DecisionOwnerId = 0,
            Executions = new(1, 1, [execution], [new(1, execution.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }]),
            Undo = new(7, 0, new("offer", "action", "mana", "priority", 0, "source", source.Name, 7), null) };
    }

    internal static GameSnapshot Restored(GameSnapshot state, long version = 1) => state with {
        Revision = state.Revision + 2, BoardRevision = state.BoardRevision + 1, Decision = state.Decision! with { Id = "restored-" + version },
        Players = [state.Viewer! with { Mana = ImmutableDictionary<string, int>.Empty, Zones = [new("Battlefield", 1,
            [state.Viewer.Zone("Battlefield").Cards.Single() with { Tapped = false, Key = "fresh-key" }], null)] }, state.Players[1]],
        Undo = new(7 + version, version, null, new(version, "action", "mana", 0)) };

    private static bool Reject(Action action) { try { action(); return false; } catch (Exception error) when (error is ArgumentException or JsonException) { return true; } }
    private static async Task Drain(ClientSession client) => await Until(() => { client.Pump(); return !client.Busy && !client.Polling; });
    private static async Task Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(1);
        if (!ready()) throw new TimeoutException("Undo regression did not settle.");
    }
    private sealed class Connection(GameSnapshot initial, GameSnapshot restored) : IMatchConnection
    {
        private GameSnapshot state = initial;
        internal int Commands;
        internal bool LoseReceipt;
        internal TaskCompletionSource<GameSnapshot?>? HeldRead;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GameSnapshot?> ObserveAsync() { Started.TrySetResult(); return HeldRead?.Task ?? Task.FromResult<GameSnapshot?>(state); }
        public Task ExecuteAsync(GameCommand command) { Interlocked.Increment(ref Commands); state = restored;
            return LoseReceipt ? Task.FromException(new IOException("Receipt lost")) : Task.CompletedTask; }
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("Undo cannot concede.");
    }
    private sealed class Source : ISourceActions
    {
        internal int Calls;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<SourceActionSet> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey) { Interlocked.Increment(ref Calls); Started.TrySetResult(); return Result.Task; }
    }
}
