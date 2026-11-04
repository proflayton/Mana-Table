using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Mana.AI;
using Mana.Client;
using Mana.Contracts;

internal static class PrivateBattlefieldSourceRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var physical = new Card { Key = "priority:manifest", CombatId = "anonymous-permanent", Name = "Face-down card",
            FaceDown = true, Type = "Creature", Power = 2, Toughness = 2 };
        var known = new Card { Name = "Elvish Mystic", ArtName = "Elvish Mystic", Type = "Creature — Elf Druid",
            ManaCost = "{G}", Text = "{T}: Add {G}.", Power = 1, Toughness = 1 };
        var state = new GameSnapshot("private-source", "playing", null, null, 0, 2, "Main", "MAIN1", 0,
            [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", 1, [physical], null)]),
             new(1, "Other", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
            new("priority", new(), new PriorityPayload(new("Pass", true)))) {
                Revision = 7, BoardRevision = 5, DecisionOwnerId = 0, PriorityPlayerId = 0 };
        ActionSourceOccurrence Row(Card? reading = null) => JsonSerializer.Deserialize<ActionSourceOccurrence>(JsonSerializer.Serialize(new {
            DecisionId = "priority", Zone = "Battlefield", PlayerId = 0, Card = physical, KnownFace = reading ?? known
        }))!;
        var row = Row();
        var current = state with { ActionSources = [row] };
        check(CardViews.IsCurrentActionSource(current, row),
            "A current explicit own-known face-down source can authorize actions without changing its physical battlefield face");
        check(CardViews.FindActionSource(current, physical.Key)?.Card is { FaceDown: true, Power: 2, Toughness: 2, Name: "Face-down card" }
                && CardViews.InZones(current).Single().Card == physical && !CardViews.Visible(current).Any(card => card.Name == known.Name),
            "Private reading knowledge cannot replace the masked physical source, its current 2/2 characteristics, or public visible cards");
        check(CardViews.KnownActionFace(current, physical) is { Name: "Elvish Mystic", Key.Length: 0, VisualId.Length: 0, CombatId.Length: 0, Power: 1 }
                && CardViews.CanReadActionSource(current, physical.Key)
                && !CardViews.CanReadActionSource(state, physical.Key)
                && CardViews.KnownActionFace(current with { ActionSources = [row, row] }, physical) == null,
            "Only one explicit current private descriptor grants inspection; a public face-down card or duplicated grant cannot");
        var offer = new SourceActionSet(state.Id, "priority", physical.Key, physical.CombatId,
            [new("turn-face-up", "activate", "Turn this creature face up")]);
        var domain = new DecisionActionDomain(1, ActionDomainCoverage.Complete, [offer],
            [new("turn-face-up", ActionEffectKind.Permanent, [ActionEffectKind.Permanent], [], ActionAffordability.Payable)]);
        check(new AiDecisionFrame(current, domain: domain).Allows(new UseOfferedAction(state.Id, "priority", physical.Key, "turn-face-up")),
            "An exact private source offer uses the physical opaque identity through the ordinary command gate");
        foreach (var invalid in new[] {
            current with { ActionSources = [row with { PlayerId = 1 }] },
            current with { ViewerId = 1, DecisionOwnerId = 1, PriorityPlayerId = 1 },
            current with { Decision = state.Decision! with { Id = "next-priority" } },
            current with { ActionSources = [row with { Card = physical with { CombatId = "different-permanent" } }] },
            current with { ActionSources = [row with { Card = physical with { Key = "different-key" } }] },
            current with { ActionSources = [row with { Card = physical with { FaceDown = false } }] },
            current with { ActionSources = [row with { Card = physical with { Power = 1 } }] },
            current with { ActionSources = [row with { KnownFace = null }] },
            current with { ActionSources = [Row(known with { Key = "borrowed-command-key" })] },
            current with { ActionSources = [Row(known with { VisualId = "private-physical-link" })] },
            current with { ActionSources = [Row(known with { CombatId = "private-combat-link" })] },
            current with { ActionSources = [Row(known with { AttachedTo = new("private-attachment", "Source", "Creature") })] },
            current with { ActionSources = [Row(known with { Attachments = [new("private-recipient", "Source", "Creature")] })] },
            current with { ActionSources = [Row(known with { DefenderId = 1 })] },
            current with { ActionSources = [Row(known with { Selectable = true })] },
            current with { ActionSources = [Row(known with { Highlighted = true })] },
            current with { ActionSources = [Row(known with { FaceDown = true })] }
        }) check(!CardViews.IsCurrentActionSource(invalid, invalid.ActionSources[0])
                && CardViews.KnownActionFace(invalid, physical) == null && !CardViews.CanReadActionSource(invalid, physical.Key),
            "Opponent, revoked, mismatched or actionable reading knowledge cannot become current private source authority");
        await SubmissionOwnership(current, physical, check);
    }

    private static async Task SubmissionOwnership(GameSnapshot granted, Card physical, Action<bool, string> check)
    {
        var sources = new ControlledSources();
        var connection = new Connection();
        var client = new ClientSession(connection, sourceActions: sources, observationDelivery: ObservationDeliveryMode.External);
        int failures = 0; client.Failed += _ => failures++;
        SourceActionContext Context(GameSnapshot state) => new(state.Id, state.Decision!.Id, state.Revision,
            state.ViewerId, physical.Key, CardViews.Identity(CardViews.FindActionSource(state, physical.Key)!.Value.Card));
        async Task Until(Func<bool> ready)
        {
            var elapsed = Stopwatch.StartNew();
            while (!ready()) {
                client.Pump();
                if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The held private source journey did not reach its controlled gate.");
                await Task.Yield();
            }
        }
        async Task<Query> Read(GameSnapshot state, int count)
        {
            client.SourceActions.Observe(Context(state));
            await Until(() => sources.Requests.Count >= count);
            return sources.Requests.ToArray()[count - 1];
        }
        static SourceActionSet Catalog(SourceActionContext context, string action) => new(context.GameId, context.DecisionId,
            context.SourceCardKey, context.SourceCardId, [new(action, "activate", "Turn this creature face up")]) { Revision = context.Revision };
        try {
            check(client.Accept(granted), "The actual ClientSession admits the current private-source observation");
            var first = await Read(granted, 1);
            first.Result.SetResult(Catalog(Context(granted), "initial-turn-up"));
            await Until(() => client.SourceActions.View.Status == SourceActionStatus.Ready);
            var captured = client.SourceActions.TryCreateCommand(Context(granted), "initial-turn-up")!;
            var revoked = granted with { Revision = 8, ActionSources = [] };
            check(client.Accept(revoked) && client.SourceActions.View.Status == SourceActionStatus.Idle
                    && !client.Submit(captured) && connection.Commands == 0,
                "Removing a ready private grant revokes the actual client's source action before any captured command can dispatch");
            var middle = granted with { Revision = 9 };
            check(client.Accept(middle), "A newly accepted own grant can start a fresh source query");
            var held = await Read(middle, 2);
            check(client.Accept(revoked with { Revision = 10 }) && client.SourceActions.View.Status == SourceActionStatus.Idle,
                "An in-flight source read loses ownership immediately when its explicit private grant is removed");
            var fresh = granted with { Revision = 11 };
            check(client.Accept(fresh), "The same physical permanent can receive a later independent private reading grant");
            client.SourceActions.Observe(Context(fresh));
            held.Result.SetResult(Catalog(Context(middle), "retired-turn-up"));
            await Until(() => sources.Requests.Count == 3);
            check(client.SourceActions.View is { Status: SourceActionStatus.Loading, Actions.Count: 0 }
                    && client.SourceActions.TryCreateCommand(Context(middle), "retired-turn-up") == null
                    && !client.Submit(new UseOfferedAction(granted.Id, granted.Decision!.Id, physical.Key, "retired-turn-up"))
                    && connection.Commands == 0,
                "Processing a held retired query cannot resurrect its private offer or dispatch while the fresh query owns the source");
            sources.Requests.ToArray()[2].Result.SetResult(Catalog(Context(fresh), "fresh-turn-up"));
            await Until(() => client.SourceActions.View.Status == SourceActionStatus.Ready);
            var command = client.SourceActions.TryCreateCommand(Context(fresh), "fresh-turn-up")!;
            connection.Next = fresh with { Revision = 12, Decision = null, ActionSources = [] };
            check(client.Submit(command),
                "An explicit current private source action survives actual ClientSession admission through its physical context");
            await Until(() => !client.Busy);
            check(connection.Commands == 1 && connection.Observations == 1 && connection.Last == command
                    && client.State?.Revision == 12 && failures == 0 && !client.NeedsRecovery,
                "Only the freshly read private offer dispatches exactly once through the normal connection and reconciles its own observation");
        } finally { client.Close(); }
    }

    private sealed record Query
    {
        internal readonly TaskCompletionSource<SourceActionSet> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ControlledSources : ISourceActions
    {
        internal readonly ConcurrentQueue<Query> Requests = new();
        public bool SupportsSourceActions => true;
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
        {
            var query = new Query(); Requests.Enqueue(query); return query.Result.Task;
        }
    }
    private sealed class Connection : IMatchConnection
    {
        internal GameSnapshot? Next;
        internal int Commands, Observations;
        internal GameCommand? Last;
        public Task ExecuteAsync(GameCommand command) { Interlocked.Increment(ref Commands); Last = command; return Task.CompletedTask; }
        public Task<GameSnapshot?> ObserveAsync() { Interlocked.Increment(ref Observations); return Task.FromResult(Next); }
        public Task ConcedeAsync(string gameId) => throw new Exception("A private source journey never concedes.");
    }
}
