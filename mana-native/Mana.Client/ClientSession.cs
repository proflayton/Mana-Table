using System.Collections.Concurrent;
using Mana.Contracts;

namespace Mana.Client;

public enum ObservationDeliveryMode { Polled, External }
public enum ClientSessionAuthority { Interactive, ObserveOnly }

/// <summary>Serial commands and ordered snapshots for every connection, without a graphics dependency.</summary>
public sealed partial class ClientSession
{
    private readonly IMatchConnection connection;
    private readonly ITableSetup? setup;
    private readonly ConcurrentQueue<Action> completions = new();
    private readonly SessionReplica<GameSnapshot> replica = new(s => s.Id, s => s.Revision);
    private SessionReplica<Lobby> lobbyReplica = new(s => s.TableId, s => s.Revision);
    private readonly ExecutionCursor executions = new();
    private readonly DecisionAdvance advance;
    private readonly ClientCommandDiagnostics diagnostics;
    private readonly TimeProvider time;
    private readonly object observationGate = new();
    private ObservationIngress? observationIngress;
    private int queuedExternalObservations;
    private bool recoveryRequired;
    private CancellationTokenSource? advancing;
    private IFiniteDecisionPolicy? finiteIntent;
    private Task commandTask = Task.CompletedTask;
    private readonly Queue<RestorePriorityAbility> pendingSettings = [];
    private RestorePriorityAbility? activeSetting;
    private GameCommand? pendingManualCommand;
    public ClientSession(IMatchConnection connection, ITableSetup? setup = null, TimeProvider? clock = null,
        ISourceActions? sourceActions = null, ObservationDeliveryMode observationDelivery = ObservationDeliveryMode.Polled,
        IMatchHistory? history = null, ClientSessionAuthority authority = ClientSessionAuthority.Interactive)
    {
        if (!Enum.IsDefined(observationDelivery)) throw new ArgumentOutOfRangeException(nameof(observationDelivery));
        if (!Enum.IsDefined(authority)) throw new ArgumentOutOfRangeException(nameof(authority));
        Authority = authority;
        this.connection = connection;
        this.setup = setup;
        time = clock ?? TimeProvider.System;
        diagnostics = new(RecordDiagnostic, () => replica.Epoch, () => DiagnosticRecorded != null,
            context => CurrentSourceContext(State, context.SourceCardKey) == context);
        advance = new(connection, diagnostics, AdmitAdvanceObservationAsync, time);
        SourceActions = new(authority == ClientSessionAuthority.Interactive ? sourceActions : null, Post) { Diagnostics = diagnostics };
        History = new(history, Post);
        ObservationDelivery = observationDelivery;
    }
    public ObservationDeliveryMode ObservationDelivery { get; }
    public ClientSessionAuthority Authority { get; }
    public SourceActionSession SourceActions { get; }
    public MatchHistorySession History { get; }
    public GameSnapshot? State => replica.Current;
    public Lobby? Lobby => lobbyReplica.Current;
    public ExecutionBatch ExecutionChanges { get; private set; } = ExecutionBatch.Empty;
    public bool RestorationChanged { get; private set; }
    public string Scope => (State?.Id ?? "lobby") + ":" + (State?.Decision?.Id ?? "");
    public bool Busy { get; private set; }
    public bool IsAdvancing => advancing is { IsCancellationRequested: false };
    public bool Polling { get; private set; }
    public bool Closed { get; private set; }
    public DateTimeOffset? LastSynchronizedAt { get; private set; }
    public string LastConnectionError { get; private set; } = "";
    private static readonly TimeSpan SynchronizationHealthLimit = TimeSpan.FromSeconds(5);
    public bool NeedsRecovery => recoveryRequired || State != null && Polling && LastSynchronizedAt is { } synchronized
        && time.GetUtcNow() - synchronized > SynchronizationHealthLimit;
    /// <summary>The existing stalled-poll health deadline, without starting a
    /// poll or changing recovery authority. Null means no health timer is due.</summary>
    public TimeSpan? TimeUntilSynchronizationHealthCheck
    {
        get {
            if (Closed) return null;
            if (recoveryRequired) return TimeSpan.Zero;
            if (State == null || !Polling || LastSynchronizedAt is not { } synchronized) return null;
            // NeedsRecovery uses a strict greater-than comparison. Wake on the
            // first eligible timer millisecond rather than rounding a tick
            // down to exact equality and spinning before recovery is due.
            var remaining = SynchronizationHealthLimit - (time.GetUtcNow() - synchronized) + TimeSpan.FromTicks(1);
            return remaining > TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)) : TimeSpan.Zero;
        }
    }
    public bool UncertainCommand { get; private set; }
    public bool LastFailureWasCommand { get; private set; }
    public event Action<Exception>? Failed;
    public event Action? Completed;
    public event Action<GameSnapshot?, GameSnapshot?>? Changed;
    public event Action? Recovered;
    /// <summary>May run on the owner thread or a dispatch worker. Subscribers
    /// must be fast and thread-safe; their exceptions cannot interrupt play.</summary>
    public event Action<ClientDiagnostic>? DiagnosticRecorded;
    private void RecordDiagnostic(ClientDiagnostic diagnostic)
    {
        if (DiagnosticRecorded is not { } recorded) return;
        foreach (Action<ClientDiagnostic> subscriber in recorded.GetInvocationList()) {
            try { subscriber(diagnostic); } catch { /* A failed observer cannot interrupt play or another observer. */ }
        }
    }
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (queuedWorkGate) {
            completions.Enqueue(action);
            queuedWork.TrySetResult();
        }
    }
    public void Pump()
    {
        // External producers can append while handling Changed. Freeze one
        // bounded prefix so their follow-up observations belong to the next
        // update, preserving FIFO order with ordinary posted completions.
        int remaining = ObservationDelivery == ObservationDeliveryMode.External
            ? completions.Count : int.MaxValue;
        while (remaining-- > 0 && completions.TryDequeue(out var action)) if (!Closed) action();
        DrainPriorityContinuation();
        DrainFiniteRevocation();
        if (!Busy && !Polling && !NeedsRecovery && finiteRevocations.Count == 0 && priorityRevocations.Count == 0
            && pendingManualCommand is { } manual) {
            pendingManualCommand = null;
            if (State != null && GameCommandPermissions.Allows(State, manual)) Submit(manual);
        }
        while (!Busy && !NeedsRecovery && pendingSettings.TryDequeue(out var setting)) Submit(setting);
        DrainPriorityObservation();
    }
    public ObservationIngress OpenObservationIngress(string gameId, int viewerId)
    {
        if (string.IsNullOrWhiteSpace(gameId) || viewerId < -1) throw new ArgumentException("An observation ingress requires an exact game and viewer.");
        lock (observationGate) {
            if (ObservationDelivery != ObservationDeliveryMode.External || Closed)
                throw new InvalidOperationException("Only an open externally driven session can admit an observation ingress.");
            if (State is { } current && (current.Id != gameId || current.ViewerId != viewerId))
                throw new ArgumentException("An observation ingress cannot replace another game or viewer.");
            RevokeObservationIngress();
            return observationIngress = new(this, gameId, viewerId);
        }
    }

    // A scoped producer capability, not a second replica or rules connection.
    // Posting is thread-safe; acceptance still happens only in the session pump.
    // Opening/resetting/pumping the session remain owner-thread operations.
    public sealed class ObservationIngress : IDisposable
    {
        public const int MaximumPendingObservations = 512;
        private readonly ClientSession owner;
        private readonly string gameId;
        private readonly int viewerId;
        private readonly HashSet<PendingObservation> pending = [];
        private bool revoked;
        internal ObservationIngress(ClientSession owner, string gameId, int viewerId)
            => (this.owner, this.gameId, this.viewerId) = (owner, gameId, viewerId);
        public bool TryPost(GameSnapshot state)
        {
            ArgumentNullException.ThrowIfNull(state);
            lock (owner.observationGate) {
                if (revoked || owner.Closed || !ReferenceEquals(owner.observationIngress, this)
                    || state.Id != gameId || state.ViewerId != viewerId
                    || owner.queuedExternalObservations >= MaximumPendingObservations) return false;
                var entry = new PendingObservation(state, owner.replica.Epoch);
                pending.Add(entry); owner.queuedExternalObservations++;
                owner.Post(() => Deliver(entry));
                return true;
            }
        }
        private void Deliver(PendingObservation entry)
        {
            GameSnapshot? state;
            lock (owner.observationGate) {
                owner.queuedExternalObservations--; pending.Remove(entry);
                state = Active ? entry.State : null;
                entry.State = null;
            }
            if (state != null) owner.Accept(state, entry.Epoch, this, observationFailurePhase: CommandFailurePhase.Observation);
        }
        public void Dispose()
        {
            lock (owner.observationGate) {
                Revoke();
                if (ReferenceEquals(owner.observationIngress, this)) owner.observationIngress = null;
            }
        }
        internal void Revoke()
        {
            revoked = true;
            foreach (var entry in pending) entry.State = null;
            pending.Clear();
        }
        internal bool Matches(GameSnapshot? state) => state?.Id == gameId && state.ViewerId == viewerId;
        internal bool Active => !revoked && ReferenceEquals(owner.observationIngress, this);
        private sealed class PendingObservation(GameSnapshot state, long epoch)
        {
            internal GameSnapshot? State = state;
            internal long Epoch { get; } = epoch;
        }
    }

    private void RevokeObservationIngress()
    {
        lock (observationGate) {
            observationIngress?.Revoke(); observationIngress = null;
        }
    }
    private long BeginCommand() { lock (observationGate) return replica.BeginCommand(); }
    public bool Accept(GameSnapshot? state) => Accept(state, replica.Epoch);
    // Connect/disconnect establishes ownership. Ordinary command receipts and
    // observations can update only that bound table and cannot roll it back.
    public void BindLobby(Lobby? state)
    {
        if (Closed) return;
        lobbyReplica = new(s => s.TableId, s => s.Revision);
        lobbyReplica.Accept(state, lobbyReplica.Epoch);
    }
    public bool AcceptLobby(Lobby state) => !Closed && Lobby != null && state.TableId == Lobby.TableId
        && lobbyReplica.Accept(state, lobbyReplica.Epoch);
    public void Reset() { RevokeObservationIngress(); StopFiniteIntent(); CancelAdvance(); SourceActions.Clear(); pendingSettings.Clear(); pendingManualCommand = null; LastFailureWasCommand = false; BeginCommand(); Accept(null); }
    private bool Accept(GameSnapshot? state, long epoch, ObservationIngress? ingress = null, bool commandObservation = false,
        CommandFailurePhase? observationFailurePhase = null)
    {
        bool diagnosticAccepted = false, diagnosticAudience = false, diagnosticReported = false;
        GameSnapshot? diagnosticPrevious = State;
        try {
        GameSnapshot? previous;
        bool recovered, restored;
        try { lock (observationGate) {
            previous = State;
            diagnosticAudience = previous == null || state == null || state.Id == previous.Id && state.ViewerId == previous.ViewerId;
            if (Closed || ingress != null && !ingress.Active || !replica.CanAccept(state, epoch)) return false;
            try {
                restored = ValidateIncomingObservation(previous, state);
            } catch (Exception error) when (observationFailurePhase.HasValue && error is not FiniteYieldOwnershipException
                && error is ArgumentException or InvalidOperationException or IOException) {
                // This catch surrounds only pre-commit contract validators.
                // Presentation callbacks and direct Accept keep their behavior.
                throw new CommandExchangeException(observationFailurePhase.Value, error);
            }
            bool audienceChanged = previous != null && (state == null || state.Id != previous.Id || state.ViewerId != previous.ViewerId);
            // An explicit audience handoff retires work admitted for the old
            // viewer. Capture its negative consent before replacing the replica.
            if (audienceChanged) {
                StopFiniteIntent(); RevokePriorityContinuation(); advancing?.Cancel();
                // Cleanup still belongs to the existing connection/audience.
                // The owner must retry handoff after its exact negative ACK;
                // neither replacement state nor a different port proves it.
                if (state != null && (finiteRevocations.Count > 0 || priorityRevocations.Count > 0)) return false;
            }
            // Reset or explicit null can clear presentation before cleanup is
            // observed. Its negative capability still belongs to the old audience.
            if (state != null && finiteRevocations.Values.Any(item => item.Command.GameId != state.Id || item.ViewerId != state.ViewerId))
                return false;
            if (state != null && priorityRevocations.Values.Any(item => item.Command.GameId != state.Id || item.Viewer != state.ViewerId)) return false;
            if (!replica.Accept(state, epoch)) return false;
            acceptedObservationVersion++;
            RestorationChanged = restored;
            if (RestorationChanged || audienceChanged) {
                // Restoration, retirement and audience handoff replace current
                // action ownership before Changed.
                finiteIntent?.Stop(); finiteIntent = null;
                advance.RetireFiniteReadCadence();
                advancing?.Cancel(); SourceActions.Clear(); pendingSettings.Clear(); activeSetting = null; pendingManualCommand = null;
                ClearAllOptionalPaymentPolicies(); optionalSubmissions.Clear(); optionalPaymentsPaused = false;
                if (RestorationChanged) {
                    foreach (var item in finiteRevocations.Where(item => item.Key.Game == state!.Id
                        && item.Value.ViewerId == state.ViewerId).ToArray()) {
                        finiteRevocations.Remove(item.Key);
                    }
                }
                BeginCommand();
            }
            if (observationIngress != null && !observationIngress.Matches(state))
                RevokeObservationIngress();
            if (finiteIntent != null && (state == null || NeedsRecovery || !finiteIntent.CanContinue(state))) StopFiniteIntent();
            if (SourceActions.View.Context is { } context && CurrentSourceContext(state, context.SourceCardKey) != context)
                SourceActions.Clear();
            recovered = NeedsRecovery;
            if (Authority == ClientSessionAuthority.Interactive) {
                ReconcileFiniteYield(state, recovered);
                ReconcilePriorityContinuation(state, recovered);
            }
            PruneFiniteAcknowledgement(state);
            ExecutionChanges = executions.Observe(state, baseline: recovered || RestorationChanged);
            ObserveOptionalPayments(previous, state, recovered || RestorationChanged);
            LastSynchronizedAt = time.GetUtcNow();
            recoveryRequired = UncertainCommand = false; LastConnectionError = "";
        } } catch (FiniteYieldOwnershipException error) { ReportFailure(error, true); return false; }
        catch (CommandExchangeException error) { ReportFailure(error, error.Phase != CommandFailurePhase.Observation); return false; }
        // Revocation and state commit share one boundary. Notification runs
        // outside it, so callbacks cannot block a background source on the gate.
        History.Observe(state);
        diagnosticAccepted = true;
        diagnosticReported = true;
        diagnostics.Publication(diagnosticPrevious, state, true, diagnosticAudience, commandObservation);
        Changed?.Invoke(previous, state);
        if (recovered) Recovered?.Invoke();
        return true;
        } finally {
            if (!diagnosticReported) diagnostics.Publication(diagnosticPrevious, state, diagnosticAccepted, diagnosticAudience, commandObservation);
        }
    }

    private bool ValidateIncomingObservation(GameSnapshot? previous, GameSnapshot? state)
    {
        UndoFacts.ValidateProgress(previous, state);
        bool restored = state != null && UndoFacts.RestoredSince(previous, state);
        if (restored && state!.FiniteStackYield is { Status: FiniteStackYieldStatus.Active } restoringGrant
            && finiteRevocations.Values.Any(item => item.Command.GameId == state.Id && item.ViewerId == state.ViewerId
                && item.Command.GrantId == restoringGrant.GrantId)) throw new FiniteYieldOwnershipException();
        executions.Validate(state);
        if (state != null) { FiniteResolutionFacts.Validate(state); FiniteStackYieldFacts.Validate(state);
            PriorityContinuationFacts.Validate(state); HumanParticipationFacts.Validate(state); }
        ValidateFiniteAcknowledgement(state);
        ValidatePriorityAcknowledgement(state);
        return restored;
    }

    // A worker can validate its own immutable read prefix, but only the session
    // thread knows whether a newer accepted cursor has superseded that prefix.
    // This validation ACK grants no publication. The existing observed/final
    // checkpoints still decide when presentation receives a frame.
    private async Task<ObservationAdmission> AdmitAdvanceObservationAsync(GameSnapshot state, CommandFailurePhase phase, long epoch,
        CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<ObservationAdmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        int claimed = 0;
        // Hold returns the already-sent result for final publication. Reset and
        // Close also release this wait even when the owner no longer pumps.
        using var registration = cancellation.Register(() => {
            if (Interlocked.CompareExchange(ref claimed, 2, 0) == 0) completion.TrySetResult(ObservationAdmission.Cancelled);
        });
        Post(() => {
            if (Interlocked.CompareExchange(ref claimed, 1, 0) != 0) return;
            try { lock (observationGate) {
                if (Closed || !replica.CanAccept(state, epoch)) { completion.TrySetResult(ObservationAdmission.Retired); return; }
                ValidateIncomingObservation(State, state);
            } }
            catch (FiniteYieldOwnershipException error) { completion.TrySetException(error); return; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException) {
                completion.TrySetException(new CommandExchangeException(phase, error)); return;
            }
            completion.TrySetResult(ObservationAdmission.Validated);
        });
        return await completion.Task;
    }
    public void Run(Func<Task> operation)
    {
        if (Busy || Closed || Authority != ClientSessionAuthority.Interactive) return;
        Run(_ => operation());
    }
    // Catalog/setup reads do not withdraw match consent. Operations that leave,
    // replace or reconnect a match explicitly use this boundary instead.
    public void RunMatchOperation(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Busy || Closed || Authority != ClientSessionAuthority.Interactive) return;
        StopFiniteIntent();
        RevokePriorityContinuation();
        ClearAllOptionalPaymentPolicies();
        var pending = finiteRevocations.Values.ToArray();
        var pendingPriority = priorityRevocations.Values.ToArray();
        Run(async epoch => {
            foreach (var item in pendingPriority) {
                var next = await ObservePriorityRevocationAsync(item);
                if (!PriorityRevoked(next, item)) throw new IOException("Priority withdrawal must be observed before changing the match connection.");
                Post(() => RecordPriorityRevocation(next, item));
            }
            foreach (var item in pending) {
                var next = await ObserveFiniteRevocationAsync(item);
                if (RemainsActive(next, item.Command))
                    throw new IOException("The finite-yield revocation must be observed before changing the match connection.");
                // The following operation may replace the match. Retain only
                // this negative acknowledgement, never publish its old scope.
                Post(() => {
                    RecordFiniteRevocation(next, item.Command);
                    finiteRevocations.Remove((item.Command.GameId, item.Command.GrantId));
                });
            }
            if (pending.Length > 0 || pendingPriority.Length > 0) Post(() => RecoverRetiredConsentCleanup(epoch));
            await operation();
        });
    }
    private void Run(Func<long, Task> operation)
    {
        if (Busy || Closed || Authority != ClientSessionAuthority.Interactive) return;
        long epoch = BeginCommand(); Busy = true;
        commandTask = Task.Run(async () => {
            try { await operation(epoch); }
            catch (Exception error) { Post(() => { if (epoch == replica.Epoch) ReportFailure(error, true); }); }
            finally { Post(() => { Busy = false; activeSetting = null; Completed?.Invoke(); }); }
        });
    }
    public void Poll(bool withLobby, Action<Lobby?> lobbyChanged)
    {
        if (ObservationDelivery == ObservationDeliveryMode.External || Busy || Polling || Closed) return;
        Polling = true; long epoch = replica.Epoch;
        var lobbyOwner = lobbyReplica;
        ObservationAudience? audience = State is { } current ? new(current) : null;
        // Called only by posted owner callbacks. A bootstrap read can bind an
        // empty replica, but cannot replace an audience accepted while it waited.
        bool OwnsObservation() => epoch == replica.Epoch && (audience != null || State == null)
            && (!withLobby || ReferenceEquals(lobbyOwner, lobbyReplica));
        _ = Task.Run(async () => {
            try {
                var lobby = withLobby ? await (setup ?? throw new InvalidOperationException("No lobby connection")).LobbyAsync() : null;
                GameSnapshot? state;
                try { state = !withLobby || lobby?.MatchActive == true ? await connection.ObserveAsync() : null; }
                // Discovery produced no observation or command receipt. Keep the
                // current replica and let the next ordinary poll read afresh.
                catch (EngineObservationSupersededException) { return; }
                Exception? admissionFailure = null;
                try { audience?.Validate(state); }
                    catch (IOException error) { admissionFailure = error; }
                if (lobby != null) try { EncounterStartFacts.ValidateMatch(lobby, state); }
                    catch (InvalidOperationException error) { admissionFailure = new IOException("The table did not publish its admitted encounter's rules frame.", error); }
                Post(() => {
                    if (!OwnsObservation() || withLobby && (lobby == null || !AcceptLobby(lobby))) return;
                    if (admissionFailure != null) { ReportFailure(admissionFailure, false); return; }
                    if (Accept(state, epoch, observationFailurePhase: CommandFailurePhase.Observation)) lobbyChanged(lobby);
                });
            } catch (Exception error) { Post(() => {
                if (OwnsObservation()) ReportFailure(error, false);
            }); }
            finally { Post(() => Polling = false); }
        });
    }
    public bool Submit(GameCommand command)
    {
        if (Authority != ClientSessionAuthority.Interactive) return false;
        ClientDiagnosticFailure? decline = Busy ? ClientDiagnosticFailure.Busy : Closed ? ClientDiagnosticFailure.Closed
            : NeedsRecovery ? ClientDiagnosticFailure.RecoveryRequired : State == null ? ClientDiagnosticFailure.MissingState
            : !GameCommandPermissions.Allows(State, command) ? ClientDiagnosticFailure.NotOffered : null;
        if (decline is { } failure) { diagnostics.Decline(State, command, failure); return false; }
        if (command is UseOfferedAction offered && (CurrentSourceContext(State, offered.Card) is not { } context
            || SourceActions.TryCreateCommand(context, offered.ActionId, offered.ManaOutput?.Colors) == null)) {
            diagnostics.Decline(State, command, ClientDiagnosticFailure.SourceOfferChanged); return false;
        }
        command.Validate();
        StopFiniteIntent();
        if (command is UseManaSource or SpendPoolMana) RevokePriorityContinuation();
        if (priorityRevocations.Count > 0 && finiteRevocations.Count == 0) {
            // This command has not entered transport. Preserve one explicit
            // gesture through withdrawal, then recheck its exact prompt offer.
            if (pendingManualCommand != null) return false;
            pendingManualCommand = command; return true;
        }
        if (finiteRevocations.Count > 0) { diagnostics.Decline(State, command, ClientDiagnosticFailure.FiniteRevocationPending); return false; }
        if (command is RestorePriorityAbility restore) activeSetting = restore;
        var initial = State!;
        Run(async epoch => {
            var exchange = new CommandExchange(connection, diagnostics);
            var attempt = diagnostics.Begin(initial, command, ClientDiagnosticOrigin.Direct, epoch);
            await exchange.ExecuteAsync(command, attempt);
            var state = await exchange.ObserveAsync(initial, attempt);
            var phase = exchange.ObservationFailurePhase;
            Post(() => Accept(state, epoch, commandObservation: true, observationFailurePhase: phase));
        });
        return true;
    }
    // Undo is a match preference, so it can be submitted while another player
    // has priority. Finish any in-flight command before sending the setting.
    public bool SubmitSetting(RestorePriorityAbility command)
    {
        if (Closed || NeedsRecovery || Authority != ClientSessionAuthority.Interactive || State == null || !GameCommandPermissions.Allows(State, command)) return false;
        command.Validate(); StopFiniteIntent(); CancelAdvance();
        if (activeSetting == command || pendingSettings.Contains(command)) return false;
        if (!Busy && finiteRevocations.Count == 0) return Submit(command);
        pendingSettings.Enqueue(command); return true;
    }
    public bool Advance(Func<GameSnapshot, bool> canPass, Func<GameSnapshot, GameSnapshot, bool> shouldPresent)
        => StartAdvance(canPass, (initial, cancellation, _) => advance.RunAsync(initial, canPass, shouldPresent, cancellation));
    public void CancelAdvance() { advancing?.Cancel(); advance.RetireFiniteReadCadence(); QueueFiniteRevocation(); RevokePriorityContinuation(); }
    public bool AdvanceQuietly(string token, Func<GameSnapshot, bool> canContinue,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent)
        => StartAdvance(initial => QuietPriorityPolicy.CanPass(initial, token) && canContinue(initial),
            (initial, cancellation, _) => advance.RunQuietlyAsync(initial, token, canContinue, shouldPresent, cancellation));
    public bool AdvanceStack(StackResolutionIntent intent, Func<GameSnapshot, GameSnapshot, bool> shouldPresent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(shouldPresent);
        return StartAdvance(state => intent.CanContinue(state) && state.Decision?.Payload is PriorityPayload,
            (initial, cancellation, _) => advance.RunStackAsync(initial, intent, shouldPresent, cancellation), intent);
    }

    public bool AdvanceFinite(FiniteResolutionIntent intent, Func<GameSnapshot, GameSnapshot, bool> shouldPresent)
        => AdvanceFiniteCore(intent, shouldPresent);

    internal bool AdvanceFiniteCore(IFiniteDecisionPolicy intent, Func<GameSnapshot, GameSnapshot, bool> shouldPresent)
    {
        ArgumentNullException.ThrowIfNull(intent); ArgumentNullException.ThrowIfNull(shouldPresent);
        RevokePriorityContinuation();
        return StartAdvance(state => !intent.IsPaused && intent.CanContinue(state),
            (initial, cancellation, observed) => advance.RunFiniteAsync(initial, intent, shouldPresent, observed, cancellation),
            intent, streamObservations: true);
    }
    private bool StartAdvance(Func<GameSnapshot, bool> canStart,
        Func<GameSnapshot, CancellationToken, Action<GameSnapshot>?, Task<GameSnapshot?>> operation,
        IFiniteDecisionPolicy? nextFiniteIntent = null, bool streamObservations = false)
    {
        if (Busy || Polling || Closed || NeedsRecovery || Authority != ClientSessionAuthority.Interactive || finiteRevocations.Count > 0 || PriorityContinuationReserved
            || State is not { } initial || !canStart(initial)) return false;
        if (!ReferenceEquals(finiteIntent, nextFiniteIntent)) StopFiniteIntent();
        if (finiteRevocations.Count > 0) return false;
        finiteIntent = nextFiniteIntent;
        var cancellation = advancing = new CancellationTokenSource();
        Run(async epoch => {
            try {
                advance.SetOperationEpoch(epoch);
                GameSnapshot? lastPosted = initial;
                void Observed(GameSnapshot state) {
                    lastPosted = state;
                    var phase = advance.ObservationFailurePhase;
                    Post(() => Accept(state, epoch, commandObservation: true, observationFailurePhase: phase));
                }
                var state = await operation(initial, cancellation.Token, streamObservations ? Observed : null);
                // Generated sealed-record equality compares the complete stored
                // payload. Equal headers alone cannot suppress changed data.
                if (!advance.SuppressFinalObservation && !Equals(lastPosted, state))
                {
                    var phase = advance.ObservationFailurePhase;
                    Post(() => Accept(state, epoch, commandObservation: true, observationFailurePhase: phase));
                }
            } finally {
                Post(() => { if (advancing == cancellation) advancing = null; cancellation.Dispose(); });
            }
        });
        return true;
    }
    private void ReportFailure(Exception error, bool command)
    {
        var phase = (error as CommandExchangeException)?.Phase;
        if (error is CommandExchangeException exchange) error = exchange.InnerException!;
        // Only the Execute phase can establish a definite rejection. A read
        // after an accepted dispatch, or a partially committed batch, cannot.
        bool rejected = phase == CommandFailurePhase.DispatchRejected
            || phase == null && command && error is EngineCommandRejectedException;
        if (phase != null) command = phase != CommandFailurePhase.Observation;
        StopFiniteIntent();
        ClearAllOptionalPaymentPolicies();
        pendingManualCommand = null;
        LastFailureWasCommand = command;
        if (State != null || finiteRevocations.Count > 0 || priorityRevocations.Count > 0) {
            CancelAdvance();
            if (!rejected) {
                recoveryRequired = true; UncertainCommand |= command;
                LastConnectionError = error.Message;
            }
        }
        Failed?.Invoke(error);
    }
    private static SourceActionContext? CurrentSourceContext(GameSnapshot? state, string key)
    {
        if (state is not { Decision: { } decision } || key.Length == 0) return null;
        // Embedded decision/choice faces can describe history. Only a currently
        // disclosed physical occurrence can carry authority to act on a source.
        var card = CardViews.FindActionSource(state, key)?.Card;
        return card == null || !CardViews.CanReadActionSource(state, key) ? null
            : new(state.Id, decision.Id, state.Revision, state.ViewerId, card.Key, CardViews.Identity(card));
    }
    private void StopFiniteIntent() { QueueFiniteRevocation(); finiteIntent?.Stop(); finiteIntent = null; advance.RetireFiniteReadCadence(); }
    public void Close()
    {
        ClearAllOptionalPaymentPolicies();
        StopFiniteIntent(); CancelAdvance(); SourceActions.Close(); History.Close(); pendingSettings.Clear();
        pendingManualCommand = null;
        lock (observationGate) { Closed = true; RevokeObservationIngress(); BeginCommand(); }
        WakeQueuedWorkWaiters();
        commandTask = RevokeAtCloseAsync(commandTask, finiteRevocations.Values.ToArray());
        commandTask = RevokePriorityAtCloseAsync(commandTask, priorityRevocations.Values.ToArray());
    }
}
