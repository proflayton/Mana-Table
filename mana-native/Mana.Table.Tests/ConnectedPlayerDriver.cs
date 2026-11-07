using Mana.AI;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;
using System.Collections.Immutable;
using System.Text.Json;

namespace Mana.Table;

internal enum ConnectedPlayerPassingMode { ServerNoResponse, FullControl, ServerAuto }

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
    private readonly ConnectedPlayerPassingMode passing;
    private readonly IAiPolicy? testPolicy;
    private readonly Func<long> ticks;
    private readonly ImmutableArray<string> phaseStops;
    private readonly PriorityAttention attention = new();
    private sealed record QuietWindow(string Game, int Viewer, int Turn, int? Active, string Token, long Activity);
    private QuietWindow? quiet;
    private readonly HashSet<(string Game, int Viewer, int Turn, int? Active, string Token)> quietWindows = [];
    private bool autoHeld;
    private (string Game, int Turn, int? Active)? autoTurn;
    private readonly AiCausalHistoryReader history = new();
    private AiSeatSession? policy;
    private AiDecisionStamp? rejected;
    private Exception? failure;
    private AutomatedSeatObservation? proposal;
    private GameSnapshot? queried;
    private AiDecisionStamp? retired;
    private long nextPoll, audienceGeneration;
    private readonly Dictionary<string, (PriorityContinuationMode Mode, int Count)> automaticPasses = [];
    internal AutomatedSeatObservation? Last { get; private set; }
    internal ClientSession Client => client;
    internal bool AutoNeedsResponse => attention.NeedsResponse;
    internal AiSeatHistory? History => policy?.History;
    internal int ClientAdmissions { get; private set; }
    internal int ProviderAcceptances { get; private set; }
    internal int PollCallbacks { get; private set; }
    internal int OrdinaryPublications { get; private set; }
    internal int RetiredProposals { get; private set; }
    internal ConnectedPlayerAutoEvidence AutoEvidence {
        get {
            var counts = connection.Counts;
            return new(counts.Grants, counts.Withdrawals, automaticPasses.Values.Sum(item => item.Count), counts.Passes) {
            NoResponseAcceptedGrants = counts.NoResponse, QuietAcceptedGrants = counts.Quiet,
            NoResponseCertifiedAutomaticPasses = automaticPasses.Values.Where(item => item.Mode == PriorityContinuationMode.NoResponse).Sum(item => item.Count),
            QuietCertifiedAutomaticPasses = automaticPasses.Values.Where(item => item.Mode == PriorityContinuationMode.Quiet).Sum(item => item.Count),
            QuietConsentWindows = quietWindows.Count
            };
        }
    }
    internal RetiredOwnedReadEvidence? LastRetiredOwnedRead { get; private set; }
    internal ConnectedPlayerConsentEvidence ConsentEvidence {
        get {
            var snapshot = connection.ConsentSnapshot;
            return new(snapshot.Total, Math.Max(0, snapshot.Total - snapshot.Commands.Length),
                snapshot.Commands.Select(command => new ConnectedPlayerConsentReceipt(command,
                    automaticPasses.GetValueOrDefault(command.GrantId).Count)).ToImmutableArray());
        }
    }
    internal TimeSpan? TimeUntilNextPoll => mode != MixedGameDriveMode.PolledBackground
        || client.Closed || client.Busy || client.Polling ? null
        : TimeSpan.FromMilliseconds(Math.Max(0, nextPoll - ticks()));

    internal sealed record RetiredOwnedReadEvidence(GameSnapshot CapturedOrdinary, OwnedDecisionObservation ReturnedRich,
        string Reason, long CapturedGeneration, long CurrentGeneration, long? CurrentRevision, long? CurrentBoard,
        int RetiredProposals)
    {
        public string Authority => "None: latest retired read retained only for private final diagnostics.";
    }

    internal ConnectedPlayerDriver(ForgeEngine engine, AiCampaignDiagnostics evidence, long seed,
        MixedGameDriveMode mode = MixedGameDriveMode.ManualStepped,
        ConnectedPlayerPassingMode passing = ConnectedPlayerPassingMode.ServerNoResponse)
        : this(engine, engine, engine, engine, engine, evidence, seed, mode, passing: passing) { }

    internal ConnectedPlayerDriver(IMatchConnection rules, ITableSetup setup, ISourceActions sources,
        IOwnedDecisionObservations owned, IMatchHistory ownedHistory, AiCampaignDiagnostics evidence, long seed,
        MixedGameDriveMode mode, IAiPolicy? testPolicy = null, Func<long>? ticks = null,
        ConnectedPlayerPassingMode passing = ConnectedPlayerPassingMode.ServerNoResponse, ImmutableArray<string> phaseStops = default)
    {
        if (!Enum.IsDefined(passing)) throw new ArgumentOutOfRangeException(nameof(passing));
        this.phaseStops = phaseStops.IsDefault ? [] : phaseStops;
        if (!PriorityContinuationFacts.Stops(this.phaseStops)) throw new ArgumentException("A player uses the same closed own-turn stops as native Auto.", nameof(phaseStops));
        this.owned = owned; this.ownedHistory = ownedHistory; this.evidence = evidence; this.seed = seed;
        this.mode = mode; this.testPolicy = testPolicy; this.ticks = ticks ?? (() => Environment.TickCount64);
        this.passing = passing;
        connection = new(rules);
        client = mode == MixedGameDriveMode.PolledBackground
            ? new(connection, setup, sourceActions: sources)
            : new(connection, sourceActions: sources, observationDelivery: ObservationDeliveryMode.External);
        client.Failed += error => failure = error;
        if (mode == MixedGameDriveMode.PolledBackground) client.Changed += (previous, current) => {
            if (passing == ConnectedPlayerPassingMode.ServerAuto) {
                attention.Observe(current);
                var turn = current == null ? ((string, int, int?)?)null : (current.Id, current.Turn, current.ActivePlayerId);
                if (autoTurn != turn) { autoTurn = turn; autoHeld = false; }
            }
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
            if (current.PriorityContinuation is { GrantId.Length: > 0, Mode: { } observedMode } continuation
                && connection.AcceptedGrant(continuation.GrantId, observedMode))
                automaticPasses[continuation.GrantId] = (observedMode,
                    Math.Max(automaticPasses.GetValueOrDefault(continuation.GrantId).Count, continuation.AutomaticPasses));
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
        if (passing == ConnectedPlayerPassingMode.ServerAuto) {
            if (client.State is { } automaticState) UpdateServerAuto(automaticState);
        }
        if (client.State is not { } state || client.Busy || client.Polling) return;
        if (state.Status is "error" || state.Error != null) throw new InvalidDataException(state.Error ?? "Rules error.");
        // The script uses the same standing authority as the shipping client.
        // A routine prompt is never answered by a second, per-prompt owner.
        if (passing == ConnectedPlayerPassingMode.ServerNoResponse
            && state.Status == "playing" && state.Viewer is { Eliminated: false } && client.SupportsPriorityContinuation
            && !client.PriorityContinuationReserved) {
            client.EnsurePriorityContinuation(PriorityContinuationMode.NoResponse, phaseStops);
            if (client.Busy || client.Polling) return;
        }
        if (queried != null && SameOrdinaryPublication(state, queried)) return;
        queried = state;
        if (state.Status != "playing" || state.Decision == null || state.Viewer?.Eliminated != false || ServerOwnsRoutinePriority(state)) {
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

    private bool ServerOwnsRoutinePriority(GameSnapshot state) => client.SupportsPriorityContinuation
        && !autoHeld && !OwnStop(state) && client.PriorityContinuationReserved && state.Decision?.Payload is PriorityPayload priority
        && (client.PriorityContinuationPending || client.PriorityContinuationWithdrawing
            || state.PriorityContinuation?.Status == PriorityContinuationStatus.Active
            || passing == ConnectedPlayerPassingMode.ServerAuto && quiet is { } window && CanContinueQuiet(state, window)
            || priority.ResponseAvailability == PriorityResponseAvailability.Absent);

    // These are the shipping Auto attention and quiet-window rules, with no
    // presentation delay in this graphics-free driver. They install consent;
    // the server remains the only owner that executes automatic passes.
    private void UpdateServerAuto(GameSnapshot state)
    {
        if (autoHeld || client.NeedsRecovery || !client.SupportsPriorityContinuation
            || state.Status is not ("playing" or "resolving") || state.Viewer?.Eliminated != false) {
            StopQuiet(); client.RevokePriorityContinuation(); return;
        }
        if (quiet is { } prior && (OwnStop(state) || !CanContinueQuiet(state, prior))) {
            if (state.Id == prior.Game && state.ViewerId == prior.Viewer && state.Turn == prior.Turn && state.ActivePlayerId == prior.Active)
                attention.Interrupt();
            StopQuiet();
        }
        if (quiet == null && !client.Busy && !client.Polling && !OwnStop(state) && attention.CanStartAutomaticQuiet(state)) {
            var token = ((PriorityPayload)state.Decision!.Payload).QuietYieldToken;
            quiet = new(state.Id, state.ViewerId, state.Turn, state.ActivePlayerId, token,
                state.Activity.Select(item => item.Id).DefaultIfEmpty().Max());
            quietWindows.Add((state.Id, state.ViewerId, state.Turn, state.ActivePlayerId, token));
        }
        if (quiet is { } current) {
            client.EnsurePriorityContinuation(PriorityContinuationMode.Quiet, phaseStops, current.Token);
            return;
        }
        if (UndoFacts.CanUndo(state)) { client.RevokePriorityContinuation(); return; }
        if (state.Status == "playing") client.EnsurePriorityContinuation(PriorityContinuationMode.NoResponse, phaseStops);
    }

    private bool OwnStop(GameSnapshot state) => state.ActivePlayerId == state.ViewerId && phaseStops.Contains(state.PhaseKey);
    private static bool CanContinueQuiet(GameSnapshot state, QuietWindow window) => state.ViewerId == window.Viewer
        && QuietPriorityPolicy.CanWait(state, window.Game, window.Turn, window.Active, window.Token)
        && !QuietPriorityPolicy.HasInterruption(state, window.Activity);
    private void StopQuiet()
    {
        if (quiet == null) return;
        quiet = null; client.CancelAdvance();
    }
    internal void SetAutoHeld(bool held)
    {
        StopQuiet(); client.RevokePriorityContinuation(); autoHeld = held;
        if (held) attention.Interrupt(); else attention.Resume();
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
        if (mode == MixedGameDriveMode.PolledBackground && ServerOwnsRoutinePriority(state)) return;
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
            failure = null; connection.Expect(ticket.Command);
            if (passing == ConnectedPlayerPassingMode.ServerAuto) StopQuiet();
            if (!client.Submit(ticket.Command))
                throw new EngineCommandRejectedException("The connected player's client declined the command.");
            if (passing == ConnectedPlayerPassingMode.ServerAuto && ticket.Command is DecisionCommand submittedCommand)
                attention.Submitted(state, submittedCommand);
            submitted = true;
            ClientAdmissions++;
            await PumpUntilAsync(() => !client.Busy);
            if (!connection.ExpectedDispatched && mode == MixedGameDriveMode.PolledBackground && !Current()
                && !client.NeedsRecovery && !client.UncertainCommand) {
                // The command was queued behind negative consent, then its
                // exact prompt retired before dispatch. Withdrawal is no ACK
                // for the script action and must not count as one.
                policy.Report(ticket, AiExecutionReceipt.Rejected); recorded = true;
                proposal = null; RetiredProposals++; return;
            }
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
        // A locally queued manual gesture can begin its withdrawal on the next
        // pump; idle before that pump is not completion of the gesture.
        client.Pump(); evidence.DrainPending();
        while (!done()) {
            client.Pump();
            evidence.DrainPending();
            if (mode == MixedGameDriveMode.PolledBackground) RequireHealthy();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The connected player's client did not complete its operation.");
            if (!done()) await CampaignOwnerWait.WaitAsync(CampaignOwnerWait.Bound(deadline - DateTime.UtcNow,
                    client.TimeUntilSynchronizationHealthCheck),
                evidence.WaitForQueuedWorkAsync, client);
        }
    }

    public void Dispose() { client.Close(); policy?.Dispose(); }

    private sealed class ReceiptConnection(IMatchConnection inner) : IMatchConnection
    {
        internal bool Accepted;
        internal bool ExpectedDispatched;
        private GameCommand? expected;
        private int grantedContinuations, revokedContinuations, acceptedPriorityPasses, noResponseGrants, quietGrants;
        private readonly object grantsGate = new();
        private readonly Dictionary<string, PriorityContinuationMode> grants = [];
        private readonly Queue<GrantPriorityContinuation> consentCommands = [];
        internal (int Grants, int Withdrawals, int Passes, int NoResponse, int Quiet) Counts {
            get { lock (grantsGate) return (grantedContinuations, revokedContinuations, acceptedPriorityPasses, noResponseGrants, quietGrants); }
        }
        internal (int Total, ImmutableArray<GrantPriorityContinuation> Commands) ConsentSnapshot {
            get { lock (grantsGate) return (grantedContinuations, consentCommands.ToImmutableArray()); }
        }
        internal bool AcceptedGrant(string id, PriorityContinuationMode mode) {
            lock (grantsGate) return grants.TryGetValue(id, out var accepted) && accepted == mode;
        }
        public bool SupportsPriorityContinuation => inner.SupportsPriorityContinuation;
        internal void Expect(GameCommand command) { expected = command; Accepted = false; ExpectedDispatched = false; }
        public Task<GameSnapshot?> ObserveAsync() => inner.ObserveAsync();
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("Qualification never forces a player concession.");
        public async Task ExecuteAsync(GameCommand command)
        {
            bool matches = ReferenceEquals(command, expected);
            if (matches) ExpectedDispatched = true;
            await inner.ExecuteAsync(command);
            if (command is GrantPriorityContinuation grant) {
                lock (grantsGate) {
                    if (grants.TryAdd(grant.GrantId, grant.Mode)) {
                        consentCommands.Enqueue(grant);
                        if (consentCommands.Count > 128) consentCommands.Dequeue();
                        grantedContinuations++;
                        if (grant.Mode == PriorityContinuationMode.NoResponse) noResponseGrants++;
                        else quietGrants++;
                    } else if (grants[grant.GrantId] != grant.Mode) throw new InvalidDataException("An accepted grant cannot change its mode.");
                }
            }
            lock (grantsGate) {
                if (command is RevokePriorityContinuation) revokedContinuations++;
                if (command is PassPriority) acceptedPriorityPasses++;
            }
            if (matches) Accepted = true;
        }
    }
}

internal sealed record ConnectedPlayerConsentReceipt(GrantPriorityContinuation Command, int CertifiedAutomaticPasses);
internal sealed record ConnectedPlayerConsentEvidence(int TotalGrants, int RetiredPrefix,
    ImmutableArray<ConnectedPlayerConsentReceipt> Receipts)
{
    public string Scope => "Host-private bounded accepted-consent receipts; grants are metadata, never scripted gameplay success. Pass counts require accepted ordinary publications.";
}

internal sealed record ConnectedPlayerAutoEvidence(int AcceptedGrants, int AcceptedWithdrawals,
    int CertifiedAutomaticPasses, int AcceptedPolicyPriorityPasses)
{
    public int NoResponseAcceptedGrants { get; init; }
    public int QuietAcceptedGrants { get; init; }
    public int NoResponseCertifiedAutomaticPasses { get; init; }
    public int QuietCertifiedAutomaticPasses { get; init; }
    public int QuietConsentWindows { get; init; }
}
