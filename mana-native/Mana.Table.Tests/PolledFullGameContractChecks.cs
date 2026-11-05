using System.Collections.Immutable;
using Mana.AI;
using Mana.Client;
using Mana.Contracts;

namespace Mana.Table;

// Controlled ports exercise the actual connected-player owner. They do not
// simulate rules or turn a rich read into an ordinary accepted publication.
internal static class PolledFullGameContractChecks
{
    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        await OrdinaryOwner(profile, check);
        await RetiredRichReads(profile, check);
        await HeldRichRead(profile, check);
        await RejectedCommandWaitsForOrdinary(profile, check);
        await SourceActionUsesOrdinaryOwner(profile, check);
        await PolledEvidenceInboxChecks.RunAsync(profile, check);
    }

    private static async Task OrdinaryOwner(string profile, Action<bool, string> check)
    {
        var state = Priority();
        var extra = ExtraSource(state, "rich-only");
        var rich = state with { ActionSources = [extra] };
        using var run = new DriverCase(profile, "ordinary-owner", state, rich);
        await run.Prime();
        check(run.Driver.Client.ObservationDelivery == ObservationDeliveryMode.Polled
                && ReferenceEquals(run.Driver.Client.State, state) && run.Port.Reads == 1
                && run.Port.OwnedReads == 0 && run.Driver.OrdinaryPublications == 1,
            "The full-game connected player admits its initial state only through default Polled lobby/match ingress");
        await run.Driver.ObserveAsync();
        check(ReferenceEquals(run.Driver.Last?.State, state) && ReferenceEquals(run.Driver.Client.State, state)
                && run.Driver.Last is { State.ActionSources.IsEmpty: true } && run.Port.OwnedReads == 1
                && run.Port.Reads == 1 && run.Port.Commands.Count == 0,
            "A matching rich read may add readonly source knowledge without replacing the ordinary state, reading the rules port again or dispatching");
        run.Port.AfterExecute = state with { Revision = 11, BoardRevision = 11, Decision = null };
        await run.Driver.StepAsync();
        check(run.Policy.Evaluations == 1 && ReferenceEquals(run.Policy.PhysicalState, state)
                && run.Policy.PhysicalState is { ActionSources.IsEmpty: true }
                && run.Port.Commands is [PassPriority passed] && passed.GameId == state.Id && passed.DecisionId == state.Decision!.Id
                && run.Driver.ClientAdmissions == 1 && run.Driver.ProviderAcceptances == 1
                && run.Driver.OrdinaryPublications == 2 && run.Port.Reads == 2 && run.Port.OwnedReads == 1
                && run.Port.SourceReads == 0 && run.Port.HistoryReads == 0,
            "The scripted policy sees the ordinary physical payload and submits once through the normal client/receipt/readback lane with no second reader");
    }

    private static async Task RetiredRichReads(string profile, Action<bool, string> check)
    {
        var basic = Opening();
        var priority = Priority();
        var withSource = priority with { ActionSources = [ExtraSource(priority, "ordinary-source")] };
        foreach (string change in new[] { "newer", "older", "game", "audience", "private-face", "private-key", "source-key", "terminal" }) {
            var ordinary = change == "source-key" ? withSource : basic;
            var rich = change switch {
                "newer" => ordinary with { Revision = ordinary.Revision + 1 },
                "older" => ordinary with { Revision = ordinary.Revision - 1 },
                "game" => ordinary with { Id = "foreign-game" },
                "audience" => ordinary with { ViewerId = 1 },
                "private-face" => ChangeOwnHand(ordinary, card => card with { Name = "Unaccepted private face" }),
                "private-key" => ChangeOwnHand(ordinary, card => card with { Key = "unaccepted-private-key" }),
                "source-key" => ordinary with { ActionSources = [ordinary.ActionSources[0] with {
                    Card = ordinary.ActionSources[0].Card with { Key = "unaccepted-source-key" } }] },
                _ => ordinary with { Status = "finished", Decision = null,
                    Outcome = new(ordinary.Id, MatchOutcomeKind.Draw, [], [], "AllPlayersDrew") }
            };
            using var run = new DriverCase(profile, "retired-" + change, ordinary, rich);
            await run.Prime();
            await run.Driver.ObserveAsync();
            await run.Driver.StepAsync();
            check(ReferenceEquals(run.Driver.Client.State, ordinary) && ReferenceEquals(run.Driver.Last?.State, ordinary)
                    && run.Driver.Client.State is { Status: "playing" } && run.Driver.OrdinaryPublications == 1
                    && run.Driver.RetiredProposals == 1 && run.Policy.Evaluations == 0
                    && run.Port.Reads == 1 && run.Port.OwnedReads == 1 && run.Port.Commands.Count == 0,
                "A rich " + change + " mismatch grants no command, accepted state, private face or artificial terminal outcome");
            await run.Driver.ObserveAsync();
            check(run.Port.OwnedReads == 1 && run.Port.Reads == 1 && run.Port.Commands.Count == 0,
                "Retiring the rich " + change + " proposal cannot silently retry its unchanged ordinary context");
        }
    }

    private static async Task HeldRichRead(string profile, Action<bool, string> check)
    {
        var first = Opening();
        using var run = new DriverCase(profile, "held-rich", first, first);
        await run.Prime();
        run.Port.HoldOwned = true;
        var pending = run.Driver.ObserveAsync();
        await run.Port.OwnedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newer = first with { Revision = 11, BoardRevision = 11,
            Decision = first.Decision! with { Id = "new-ordinary-decision" } };
        run.Port.State = newer;
        run.Driver.Client.Poll(true, _ => { });
        await Drain(run.Driver.Client);
        check(ReferenceEquals(run.Driver.Client.State, newer) && run.Port.Reads == 2 && run.Port.Commands.Count == 0,
            "A held rich read does not block the same player's actual ordinary poll from admitting a newer current decision");
        run.Port.ReleaseOwned.TrySetResult(new(first, DecisionActionDomain.None, new()));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Driver.StepAsync();
        check(ReferenceEquals(run.Driver.Client.State, newer) && ReferenceEquals(run.Driver.Last?.State, newer)
                && run.Driver.RetiredProposals == 1 && run.Policy.Evaluations == 0 && run.Port.Commands.Count == 0,
            "A late old rich completion cannot revive its decision or authorize work after a newer ordinary publication");
    }

    private static async Task RejectedCommandWaitsForOrdinary(string profile, Action<bool, string> check)
    {
        var first = Opening();
        using var run = new DriverCase(profile, "rejected-command", first, first);
        await run.Prime();
        await run.Driver.ObserveAsync();
        run.Port.ExecuteError = new EngineCommandRejectedException("Controlled definite dispatch rejection");
        await run.Driver.StepAsync();
        check(run.Port.Commands.Count == 1 && run.Driver.ProviderAcceptances == 0 && !run.Driver.Client.NeedsRecovery,
            "A definite rejected full-game command records no provider acceptance and retires its current proposal");
        run.Port.HoldNextRead = true;
        run.Driver.Client.Poll(true, _ => { });
        await run.Port.NextReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Driver.ObserveAsync();
        await run.Driver.StepAsync();
        check(run.Port.Commands.Count == 1 && run.Port.OwnedReads == 1 && run.Policy.Evaluations == 1,
            "A held ordinary poll after rejection cannot replay the old proposal or mistake its old stamp for a fresh failure");
        var fresh = first with { Revision = 11, BoardRevision = 11,
            Decision = first.Decision! with { Id = "fresh-after-rejection" } };
        run.Port.State = run.Port.RichState = fresh;
        run.Port.ReleaseNextRead.TrySetResult(fresh);
        await Drain(run.Driver.Client);
        run.Port.ExecuteError = null;
        run.Port.AfterExecute = fresh with { Revision = 12, BoardRevision = 12, Decision = null };
        await run.Driver.ObserveAsync();
        await run.Driver.StepAsync();
        check(run.Port.Commands.Count == 2 && run.Port.Commands[0] is KeepOpeningHand { DecisionId: "opening-owner" }
                && run.Port.Commands[1] is KeepOpeningHand { DecisionId: "fresh-after-rejection" }
                && run.Driver.ProviderAcceptances == 1 && run.Policy.Evaluations == 2,
            "Only a freshly polled ordinary decision and matching rich read permit one new command after definite rejection");
    }

    private static async Task SourceActionUsesOrdinaryOwner(string profile, Action<bool, string> check)
    {
        var state = Priority();
        using (var run = new DriverCase(profile, "source-current", state, state, useSource: true)) {
            await run.Prime(); await run.Driver.ObserveAsync();
            run.Port.AfterExecute = state with { Revision = 11, BoardRevision = 11, Decision = null };
            await run.Driver.StepAsync();
            var card = state.Viewer!.Zone("Hand").Cards[0];
            check(run.Port.SourceReads == 1 && run.Port.Commands.Single() == new UseOfferedAction(
                    state.Id, state.Decision!.Id, card.Key, "controlled-play")
                    && run.Driver.ClientAdmissions == 1 && run.Driver.ProviderAcceptances == 1 && run.Port.Reads == 2,
                "A rich offered route still requires the ordinary current SourceActionSession query and normal Submit/ACK/readback before it counts as accepted");
        }
        using (var run = new DriverCase(profile, "source-retired", state, state, useSource: true)) {
            await run.Prime(); await run.Driver.ObserveAsync();
            run.Port.HoldSource = true;
            var pending = run.Driver.StepAsync();
            await run.Port.SourceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var newer = state with { Revision = 11, BoardRevision = 11,
                Decision = state.Decision! with { Id = "new-source-owner" } };
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            run.Driver.Client.Changed += (_, current) => { if (ReferenceEquals(current, newer)) accepted.TrySetResult(); };
            run.Port.State = newer;
            // The driver's existing pump remains the sole model owner while it
            // awaits the source. Schedule the poll on that same owner queue.
            run.Driver.Client.Post(() => run.Driver.Client.Poll(true, _ => { }));
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            run.Port.ReleaseSource.TrySetResult(Offers(state));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            check(ReferenceEquals(run.Driver.Client.State, newer) && run.Driver.RetiredProposals == 1
                    && run.Port.SourceReads is >= 1 and <= 2 && run.Port.Commands.Count == 0
                    && run.Driver.ClientAdmissions == 0 && run.Driver.ProviderAcceptances == 0,
                "A held source-query completion cannot dispatch its old rich proposal after a new ordinary owner is admitted through the same client pump");
        }
    }

    private static SourceActionSet Offers(GameSnapshot state)
    {
        var card = state.Viewer!.Zone("Hand").Cards[0];
        return new(state.Id, state.Decision!.Id, card.Key, CardViews.Identity(card),
            [new("controlled-play", "play", "Play this land")]) { Revision = state.Revision };
    }

    private static DecisionActionDomain Domain(GameSnapshot state) => new(state.Revision, ActionDomainCoverage.Complete,
        [Offers(state)], [new("controlled-play", ActionEffectKind.Land, [ActionEffectKind.Land], [], ActionAffordability.Payable)]);

    private static GameSnapshot Opening() => PresentationSmoke.Example() with {
        Revision = 10, BoardRevision = 10,
        Decision = new("opening-owner", new DecisionContext(Message: "Keep this hand?"),
            new OpeningHandPayload(new("Keep", true), new("Mulligan", true)))
    };

    private static GameSnapshot Priority() => PresentationSmoke.Example() with { Revision = 10, BoardRevision = 10 };

    private static ActionSourceOccurrence ExtraSource(GameSnapshot state, string id) => new(state.Decision!.Id,
        "Sideboard", state.ViewerId, new Card {
            Key = "source:" + id, VisualId = id, Name = "Known sideboard card", Type = "Creature", Selectable = false
        });

    private static GameSnapshot ChangeOwnHand(GameSnapshot state, Func<Card, Card> change) => state with {
        Players = state.Players.Select(player => player.Id != state.ViewerId ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with {
                Cards = zone.Cards.Select((card, index) => index == 0 ? change(card) : card).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray()
    };

    private static async Task Drain(ClientSession client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (client.Busy || client.Polling) {
            client.Pump();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The controlled ordinary client did not settle.");
            await Task.Yield();
        }
        client.Pump();
    }

    private sealed class DriverCase : IDisposable
    {
        private readonly AiCampaignDiagnostics evidence;
        internal Port Port { get; }
        internal ScriptedPolicy Policy { get; }
        internal ConnectedPlayerDriver Driver { get; }
        internal DriverCase(string profile, string name, GameSnapshot ordinary, GameSnapshot rich, bool useSource = false)
        {
            string directory = Path.Combine(profile, "polled-full-game-contracts", name);
            Directory.CreateDirectory(directory);
            evidence = new(directory);
            Port = new(ordinary, rich) { UseSource = useSource };
            Policy = new(useSource);
            Driver = new(Port, Port, Port, Port, Port, evidence, 261104,
                MixedGameDriveMode.PolledBackground, Policy, () => 0);
            Driver.BindLobby(Port.Lobby);
        }
        internal async Task Prime()
        {
            await Driver.ObserveAsync();
            await Port.OrdinaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Port.ReleaseOrdinary.TrySetResult(Port.State);
            await Drain(Driver.Client);
        }
        public void Dispose() { Driver.Dispose(); evidence.Dispose(); }
    }

    private sealed class ScriptedPolicy(bool useSource) : IAiPolicy
    {
        internal int Evaluations;
        internal GameSnapshot? PhysicalState;
        public AiPolicyDescriptor Descriptor { get; } = new("polled-full-game-contract.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            Evaluations++; PhysicalState = request.Frame.State;
            DecisionCommand answer = useSource ? new UseOfferedAction(PhysicalState.Id, PhysicalState.Decision!.Id,
                request.Frame.SourceActions[0].SourceCardKey, request.Frame.SourceActions[0].Actions[0].Id)
                : PhysicalState.Decision!.Payload is OpeningHandPayload
                ? new KeepOpeningHand(PhysicalState.Id, PhysicalState.Decision.Id)
                : new PassPriority(PhysicalState.Id, PhysicalState.Decision.Id);
            return Task.FromResult<AiProposal>(new AiProposal.Answer(answer, "controlled-owned-input"));
        }
    }

    private sealed class Port(GameSnapshot initial, GameSnapshot rich) : IMatchConnection, ITableSetup,
        ISourceActions, IOwnedDecisionObservations, IMatchHistory
    {
        private GameSnapshot state = initial;
        internal GameSnapshot State { get => Volatile.Read(ref state); set => Volatile.Write(ref state, value); }
        internal GameSnapshot RichState { get; set; } = rich;
        internal GameSnapshot? AfterExecute;
        internal Exception? ExecuteError;
        internal int Reads, OwnedReads, SourceReads, HistoryReads;
        internal bool HoldOwned, HoldNextRead, HoldSource, UseSource;
        internal List<GameCommand> Commands { get; } = [];
        internal TaskCompletionSource OrdinaryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<GameSnapshot?> ReleaseOrdinary { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource OwnedStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<OwnedDecisionObservation?> ReleaseOwned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource NextReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<GameSnapshot?> ReleaseNextRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SourceStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<SourceActionSet> ReleaseSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Lobby Lobby { get; } = new("host", "playing", null, false, null, true, "", null, [], [], "controlled-table");
        public async Task<GameSnapshot?> ObserveAsync()
        {
            if (Interlocked.Increment(ref Reads) == 1) { OrdinaryStarted.TrySetResult(); return await ReleaseOrdinary.Task; }
            if (HoldNextRead) { HoldNextRead = false; NextReadStarted.TrySetResult(); return await ReleaseNextRead.Task; }
            return State;
        }
        public Task ExecuteAsync(GameCommand command)
        {
            Commands.Add(command);
            if (ExecuteError is { } error) return Task.FromException(error);
            if (AfterExecute is { } next) State = next;
            return Task.CompletedTask;
        }
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("No synthetic concession is permitted.");
        public Task<OwnedDecisionObservation?> ObserveDecisionAsync(CancellationToken cancellation = default)
        {
            Interlocked.Increment(ref OwnedReads); OwnedStarted.TrySetResult();
            return HoldOwned ? ReleaseOwned.Task : Task.FromResult<OwnedDecisionObservation?>(new(RichState,
                UseSource ? Domain(RichState) : RichState.Decision?.Payload is PriorityPayload
                    ? new(RichState.Revision, ActionDomainCoverage.Complete, [], []) : DecisionActionDomain.None, new()));
        }
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
        {
            SourceReads++;
            if (!UseSource) throw new InvalidOperationException("The controlled decision has no source-action query.");
            if (HoldSource) { HoldSource = false; SourceStarted.TrySetResult(); return ReleaseSource.Task; }
            return Task.FromResult(Offers(State));
        }
        public bool SupportsHistory => false;
        public Task<HistoryBatch> HistoryAsync(string gameId, long afterSequence, int limit = 128)
        {
            HistoryReads++; throw new InvalidOperationException("No second history/state reader is enabled by this fixture.");
        }
        public Task<Lobby> LobbyAsync() => Task.FromResult(Lobby);
        public Task<SoloSetup> PrepareSoloAsync(string deckId) => throw new NotSupportedException();
        public Task StartSoloAsync(string deckId, ImmutableArray<string> opponentIds) => throw new NotSupportedException();
        public Task<Lobby> HostAsync(int players, bool automaticPortForwarding) => throw new NotSupportedException();
        public Task<Lobby> JoinAsync(string address) => throw new NotSupportedException();
        public Task<Lobby> SelectDeckAsync(LobbyDeckSelection selection) => throw new NotSupportedException();
        public Task SetReadyAsync(bool ready) => throw new NotSupportedException();
        public Task StartAsync() => throw new NotSupportedException();
        public Task ReturnToLobbyAsync() => throw new NotSupportedException();
        public Task LeaveAsync() => throw new NotSupportedException();
    }
}
