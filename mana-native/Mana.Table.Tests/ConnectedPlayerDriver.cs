using Mana.AI;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;
using System.Text.Json;

namespace Mana.Table;

// Test driver for an ordinary connected player. It never receives the host's
// automated-seat port. The same policy is used to exercise decisions, not to
// claim that a scripted player supplies human UX acceptance.
internal sealed class ConnectedPlayerDriver : IDisposable
{
    private readonly IOwnedDecisionObservations owned;
    private readonly IMatchHistory ownedHistory;
    private readonly AiCampaignDiagnostics evidence;
    private readonly long seed;
    private readonly ClientSession client;
    private readonly ReceiptConnection connection;
    private readonly MixedGameDriveMode mode;
    private readonly IAiPolicy? testPolicy;
    private readonly Func<long> ticks;
    private readonly AiCausalHistoryReader history = new();
    private AiSeatSession? policy;
    private AiDecisionStamp? rejected;
    private Exception? failure;
    private AutomatedSeatObservation? proposal;
    private GameSnapshot? queried;
    private AiDecisionStamp? retired;
    private long nextPoll, audienceGeneration;
    internal AutomatedSeatObservation? Last { get; private set; }
    internal ClientSession Client => client;
    internal AiSeatHistory? History => policy?.History;
    internal int ClientAdmissions { get; private set; }
    internal int ProviderAcceptances { get; private set; }
    internal int PollCallbacks { get; private set; }
    internal int OrdinaryPublications { get; private set; }
    internal int RetiredProposals { get; private set; }
    internal RetiredOwnedReadEvidence? LastRetiredOwnedRead { get; private set; }

    internal sealed record RetiredOwnedReadEvidence(GameSnapshot CapturedOrdinary, OwnedDecisionObservation ReturnedRich,
        string Reason, long CapturedGeneration, long CurrentGeneration, long? CurrentRevision, long? CurrentBoard,
        int RetiredProposals)
    {
        public string Authority => "None: latest retired read retained only for private final diagnostics.";
    }

    internal ConnectedPlayerDriver(ForgeEngine engine, AiCampaignDiagnostics evidence, long seed,
        MixedGameDriveMode mode = MixedGameDriveMode.ManualStepped)
        : this(engine, engine, engine, engine, engine, evidence, seed, mode) { }

    internal ConnectedPlayerDriver(IMatchConnection rules, ITableSetup setup, ISourceActions sources,
        IOwnedDecisionObservations owned, IMatchHistory ownedHistory, AiCampaignDiagnostics evidence, long seed,
        MixedGameDriveMode mode, IAiPolicy? testPolicy = null, Func<long>? ticks = null)
    {
        this.owned = owned; this.ownedHistory = ownedHistory; this.evidence = evidence; this.seed = seed;
        this.mode = mode; this.testPolicy = testPolicy; this.ticks = ticks ?? (() => Environment.TickCount64);
        connection = new(rules);
        client = mode == MixedGameDriveMode.PolledBackground
            ? new(connection, setup, sourceActions: sources)
            : new(connection, sourceActions: sources, observationDelivery: ObservationDeliveryMode.External);
        client.Failed += error => failure = error;
        if (mode == MixedGameDriveMode.PolledBackground) client.Changed += (previous, current) => {
            if (current == null) {
                audienceGeneration++; proposal = null;
                if (previous != null) throw new InvalidDataException("A polled campaign player lost its bound match.");
                return;
            }
            if (previous != null && (current.Id != previous.Id || current.ViewerId != previous.ViewerId
                || current.Revision < previous.Revision || current.BoardRevision < previous.BoardRevision))
                throw new InvalidDataException("A polled campaign player changed its audience or rolled back an admitted frame.");
            if (previous == null) audienceGeneration++;
            if (!AiCampaignVisibility.PrivateWindowsAreDisclosed(current))
                throw new InvalidDataException("An ordinary polled player received an unauthorized private window.");
            OrdinaryPublications++;
            Last = new(current.ViewerId, current, DecisionActionDomain.None, new());
            if (proposal != null && !SameOrdinaryPublication(current, proposal.State)) proposal = null;
        };
    }

    internal void BindLobby(Lobby lobby) => client.BindLobby(lobby);

    // Rich reads can contribute domain/semantics only for the full current
    // ordinary payload. Extra read-only rich action-source rows are not copied
    // into the ordinary replica or the policy's physical state.
    internal static bool SameOrdinaryPublication(GameSnapshot left, GameSnapshot right) => ReferenceEquals(left, right)
        || SamePublicationIdentity(left, right) && JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private static bool SamePublicationIdentity(GameSnapshot ordinary, GameSnapshot rich) =>
        ordinary.Id == rich.Id && ordinary.ViewerId == rich.ViewerId && ordinary.Revision == rich.Revision
        && ordinary.BoardRevision == rich.BoardRevision && ordinary.Decision?.Id == rich.Decision?.Id;

    internal static bool MatchesOwnedObservation(GameSnapshot ordinary, GameSnapshot rich)
    {
        if (!SamePublicationIdentity(ordinary, rich)) return false;
        if (JsonSerializer.Serialize(ordinary with { ActionSources = [] }) != JsonSerializer.Serialize(rich with { ActionSources = [] })) return false;
        return ordinary.ActionSources.All(source => rich.ActionSources.Any(candidate =>
            JsonSerializer.Serialize(source) == JsonSerializer.Serialize(candidate)));
    }

    internal async Task ObserveAsync()
    {
        client.Pump();
        evidence.DrainPending();
        if (mode == MixedGameDriveMode.PolledBackground) { await ObservePolledAsync(); return; }
        var own = await owned.ObserveDecisionAsync();
        if (own == null) throw new InvalidDataException("The connected player lost its match.");
        if (!AiCampaignVisibility.PrivateWindowsAreDisclosed(own.State, own.Semantics, own.Domain))
            throw new InvalidDataException("A connected player received an unauthorized private window.");
        if (!client.Accept(own.State)) throw new InvalidDataException("The player's client rejected its current owned observation.");
        Last = new(own.State.ViewerId, own.State, own.Domain, own.Semantics);
        evidence.Observe(Last);
        await history.DrainOwnedAsync(ownedHistory, own, evidence.QualificationEvidence);
    }

    private async Task ObservePolledAsync()
    {
        RequireHealthy();
        long now = ticks();
        if (!client.Busy && !client.Polling && now >= nextPoll) {
            nextPoll = now + (client.State?.Decision == null ? 50 : 120);
            client.Poll(true, _ => PollCallbacks++);
        }
        client.Pump(); evidence.DrainPending(); RequireHealthy();
        if (client.State is not { } state || client.Busy || client.Polling) return;
        if (state.Status is "error" || state.Error != null) throw new InvalidDataException(state.Error ?? "Rules error.");
        if (queried != null && SameOrdinaryPublication(state, queried)) return;
        queried = state;
        if (state.Status != "playing" || state.Decision == null || state.Viewer?.Eliminated != false) {
            proposal = null;
            Last = new(state.ViewerId, state, DecisionActionDomain.None, new());
            evidence.Observe(Last);
            await history.DrainOwnedAsync(ownedHistory, new(state, DecisionActionDomain.None, new()), evidence.QualificationEvidence);
            return;
        }
        var generation = audienceGeneration;
        var own = await owned.ObserveDecisionAsync();
        client.Pump(); evidence.DrainPending(); RequireHealthy();
        if (own == null) throw new InvalidDataException("The connected player lost its owned observation.");
        var current = client.State;
        bool currentOwner = generation == audienceGeneration && current != null && SameOrdinaryPublication(state, current);
        if (!currentOwner || !MatchesOwnedObservation(current!, own.State)) {
            proposal = null; RetiredProposals++;
            LastRetiredOwnedRead = new(state, own, currentOwner ? "rich-payload-mismatch" : "ordinary-owner-changed",
                generation, audienceGeneration, current?.Revision, current?.BoardRevision, RetiredProposals);
            return;
        }
        if (!AiCampaignVisibility.PrivateWindowsAreDisclosed(own.State, own.Semantics, own.Domain))
            throw new InvalidDataException("A connected player received an unauthorized private window.");
        var accepted = current!;
        proposal = new(accepted.ViewerId, accepted, own.Domain, own.Semantics);
        Last = proposal;
        evidence.Observe(proposal);
        await history.DrainOwnedAsync(ownedHistory, new(accepted, own.Domain, own.Semantics), evidence.QualificationEvidence);
    }

    private void RequireHealthy()
    {
        if (failure != null) throw failure;
        if (client.NeedsRecovery || client.UncertainCommand)
            throw new InvalidDataException("A polled campaign player entered recovery or command uncertainty.");
    }

    internal async Task StepAsync()
    {
        client.Pump(); evidence.DrainPending();
        if (mode == MixedGameDriveMode.PolledBackground) RequireHealthy();
        if ((mode == MixedGameDriveMode.PolledBackground ? proposal : Last) is not { } observation) return;
        var state = observation.State;
        long generation = audienceGeneration;
        bool Current() => mode != MixedGameDriveMode.PolledBackground || generation == audienceGeneration
            && !client.Busy && client.State is { } current && SameOrdinaryPublication(state, current);
        if (!Current()) { proposal = null; return; }
        policy ??= new(state.Id, state.ViewerId, evidence.Record(testPolicy ?? new CasualCommanderPolicy()), seed,
            new(TimeSpan.FromSeconds(10), 4096));
        policy.ObserveRestoration(state);
        if (state.Status != "playing" || state.Decision == null || state.Viewer?.Eliminated != false) {
            policy.Observe(null); return;
        }
        var frame = new AiDecisionFrame(state, domain: observation.Domain, semantics: observation.Semantics);
        if (mode == MixedGameDriveMode.PolledBackground && retired == frame.Stamp) return;
        if (rejected == frame.Stamp) throw new InvalidDataException("A rejected player command retained the same authority.");
        rejected = null;
        policy.Observe(frame);
        var view = await policy.EvaluateAsync();
        client.Pump(); evidence.DrainPending();
        if (mode == MixedGameDriveMode.PolledBackground) RequireHealthy();
        if (!Current()) { proposal = null; policy.Observe(null); RetiredProposals++; return; }
        if (view.Status is AiSeatStatus.Unavailable or AiSeatStatus.Invalid or AiSeatStatus.BudgetExceeded or AiSeatStatus.NeedsRecovery)
            throw new InvalidDataException("The connected-player test policy cannot answer: " + view);
        if (policy.TakeCommand() is not { } ticket) return;
        bool recorded = false;
        bool submitted = false;
        try {
            if (ticket.Command is UseOfferedAction offered) {
                var card = CardViews.FindActionSource(state, offered.Card)?.Card
                    ?? throw new EngineCommandRejectedException("The current source is no longer disclosed.");
                client.SourceActions.Observe(new(state.Id, state.Decision.Id, state.Revision,
                    state.ViewerId, card.Key, CardViews.Identity(card)));
                await PumpUntilAsync(() => client.SourceActions.View.Status != SourceActionStatus.Loading);
                if (mode == MixedGameDriveMode.PolledBackground) RequireHealthy();
                if (mode == MixedGameDriveMode.PolledBackground && (!Current()
                    || client.SourceActions.View.Status == SourceActionStatus.Stale)) {
                    policy.Report(ticket, AiExecutionReceipt.Rejected); retired = ticket.Stamp;
                    proposal = null; RetiredProposals++; return;
                }
                if (client.SourceActions.View.Status != SourceActionStatus.Ready && mode == MixedGameDriveMode.PolledBackground)
                    throw new InvalidDataException("The ordinary polled source reader failed: " + client.SourceActions.View.Status);
                if (client.SourceActions.View.Status != SourceActionStatus.Ready)
                    throw new EngineCommandRejectedException("The source query did not retain current authority.");
            }
            failure = null; connection.Accepted = false;
            if (!client.Submit(ticket.Command))
                throw new EngineCommandRejectedException("The connected player's client declined the command.");
            submitted = true;
            ClientAdmissions++;
            await PumpUntilAsync(() => !client.Busy);
            // Submit is local admission. Only a successful normal rules-port
            // receipt establishes provider acceptance. Neither proves resolution.
            if (connection.Accepted) {
                ProviderAcceptances++;
                evidence.Receipt(observation, ticket.Command, true, null);
                policy.Report(ticket, AiExecutionReceipt.Accepted); recorded = true;
                await history.DrainOwnedAsync(ownedHistory, new(observation.State, observation.Domain, observation.Semantics),
                    evidence.QualificationEvidence, force: true);
            }
            if (failure != null) throw failure;
            if (!connection.Accepted) throw new IOException("The player's command has no known provider receipt.");
        } catch (EngineCommandRejectedException error) when (!recorded) {
            if (mode == MixedGameDriveMode.PolledBackground && !submitted) {
                policy.Report(ticket, AiExecutionReceipt.Rejected);
                throw new InvalidDataException("The ordinary client declined a current proposed command before provider dispatch.", error);
            }
            evidence.Receipt(observation, ticket.Command, false, error.GetType().Name);
            policy.Report(ticket, AiExecutionReceipt.Rejected); rejected = ticket.Stamp;
            if (mode == MixedGameDriveMode.PolledBackground && !client.NeedsRecovery && !client.UncertainCommand) {
                failure = null; // A definite dispatch rejection still requires a newly admitted observation.
                proposal = null;
            }
        } catch {
            if (!recorded) policy.Report(ticket, mode == MixedGameDriveMode.PolledBackground && !submitted
                ? AiExecutionReceipt.Rejected : AiExecutionReceipt.Unknown);
            throw;
        }
    }

    private async Task PumpUntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!done()) {
            client.Pump();
            evidence.DrainPending();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The connected player's client did not complete its operation.");
            if (!done()) await Task.Delay(1);
        }
    }

    public void Dispose() { client.Close(); policy?.Dispose(); }

    private sealed class ReceiptConnection(IMatchConnection inner) : IMatchConnection
    {
        internal bool Accepted;
        public Task<GameSnapshot?> ObserveAsync() => inner.ObserveAsync();
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("Qualification never forces a player concession.");
        public async Task ExecuteAsync(GameCommand command) { await inner.ExecuteAsync(command); Accepted = true; }
    }
}
