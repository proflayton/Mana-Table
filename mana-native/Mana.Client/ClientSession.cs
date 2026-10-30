using System.Collections.Concurrent;
using Mana.Contracts;

namespace Mana.Client;

public enum ObservationDeliveryMode { Polled, External }

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
    public ClientSession(IMatchConnection connection, ITableSetup? setup = null, TimeProvider? clock = null,
        ISourceActions? sourceActions = null, ObservationDeliveryMode observationDelivery = ObservationDeliveryMode.Polled)
    {
        if (!Enum.IsDefined(observationDelivery)) throw new ArgumentOutOfRangeException(nameof(observationDelivery));
        this.connection = connection;
        this.setup = setup;
        time = clock ?? TimeProvider.System;
        diagnostics = new(RecordDiagnostic, () => replica.Epoch, () => DiagnosticRecorded != null);
        advance = new(connection, diagnostics);
        SourceActions = new(sourceActions, Post);
        ObservationDelivery = observationDelivery;
    }
    public ObservationDeliveryMode ObservationDelivery { get; }
    public SourceActionSession SourceActions { get; }
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
    public bool NeedsRecovery => recoveryRequired || State != null && Polling && LastSynchronizedAt is { } synchronized
        && time.GetUtcNow() - synchronized > TimeSpan.FromSeconds(5);
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
    public void Post(Action action) => completions.Enqueue(action);
    public void Pump()
    {
        // External producers can append while handling Changed. Freeze one
        // bounded prefix so their follow-up observations belong to the next
        // update, preserving FIFO order with ordinary posted completions.
        int remaining = ObservationDelivery == ObservationDeliveryMode.External
            ? completions.Count : int.MaxValue;
        while (remaining-- > 0 && completions.TryDequeue(out var action)) if (!Closed) action();
        DrainFiniteRevocation();
        while (!Busy && !NeedsRecovery && pendingSettings.TryDequeue(out var setting)) Submit(setting);
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
                owner.completions.Enqueue(() => Deliver(entry));
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
            if (state != null) owner.Accept(state, entry.Epoch, this);
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
    public void Reset() { RevokeObservationIngress(); StopFiniteIntent(); CancelAdvance(); SourceActions.Clear(); pendingSettings.Clear(); LastFailureWasCommand = false; BeginCommand(); Accept(null); }
    private bool Accept(GameSnapshot? state, long epoch, ObservationIngress? ingress = null, bool commandObservation = false)
    {
        bool diagnosticAccepted = false, diagnosticAudience = false, diagnosticReported = false;
        GameSnapshot? diagnosticPrevious = State;
        try {
        GameSnapshot? previous;
        bool recovered, restored;
        try { lock (observationGate) {
            previous = State;
            diagnosticAudience = previous == null || state == null || state.Id == previous.Id && state.ViewerId == previous.ViewerId;
            if (Closed || ingress != null && !ingress.Active || epoch != replica.Epoch
                || state != null && previous != null && state.Id == previous.Id && state.Revision < previous.Revision) return false;
            UndoFacts.ValidateProgress(previous, state);
            restored = state != null && UndoFacts.RestoredSince(previous, state);
            if (restored && state!.FiniteStackYield is { Status: FiniteStackYieldStatus.Active } restoringGrant
                && finiteRevocations.Values.Any(item => item.Command.GameId == state.Id && item.ViewerId == state.ViewerId
                    && item.Command.GrantId == restoringGrant.GrantId)) throw new FiniteYieldOwnershipException();
            executions.Validate(state);
            if (state != null) { FiniteResolutionFacts.Validate(state); FiniteStackYieldFacts.Validate(state); }
            ValidateFiniteAcknowledgement(state);
            if (!replica.Accept(state, epoch)) return false;
            RestorationChanged = restored;
            if (RestorationChanged) {
                // Restore is a new authoritative publication in this same game.
                // Revoke old work before Changed, without retiring its identity.
                finiteIntent?.Stop(); finiteIntent = null;
                advancing?.Cancel(); SourceActions.Clear(); pendingSettings.Clear(); activeSetting = null;
                ClearAllOptionalPaymentPolicies(); optionalSubmissions.Clear(); optionalPaymentsPaused = false;
                foreach (var item in finiteRevocations.Where(item => item.Key.Game == state!.Id
                    && item.Value.ViewerId == state.ViewerId).ToArray()) {
                    finiteRevocations.Remove(item.Key);
                }
                BeginCommand();
            }
            if (observationIngress != null && !observationIngress.Matches(state))
                RevokeObservationIngress();
            if (finiteIntent != null && (state == null || NeedsRecovery || !finiteIntent.CanContinue(state))) StopFiniteIntent();
            if (SourceActions.View.Context is { } context && CurrentSourceContext(state, context.SourceCardKey) != context)
                SourceActions.Clear();
            recovered = NeedsRecovery;
            ReconcileFiniteYield(state, recovered);
            PruneFiniteAcknowledgement(state);
            ExecutionChanges = executions.Observe(state, baseline: recovered || RestorationChanged);
            ObserveOptionalPayments(previous, state, recovered || RestorationChanged);
            LastSynchronizedAt = time.GetUtcNow();
            recoveryRequired = UncertainCommand = false; LastConnectionError = "";
        } } catch (FiniteYieldOwnershipException error) { ReportFailure(error, true); return false; }
        // Revocation and state commit share one boundary. Notification runs
        // outside it, so callbacks cannot block a background source on the gate.
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
    public void Run(Func<Task> operation)
    {
        if (Busy || Closed) return;
        Run(_ => operation());
    }
    // Catalog/setup reads do not withdraw match consent. Operations that leave,
    // replace or reconnect a match explicitly use this boundary instead.
    public void RunMatchOperation(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Busy || Closed) return;
        StopFiniteIntent();
        ClearAllOptionalPaymentPolicies();
        var pending = finiteRevocations.Values.ToArray();
        Run(async epoch => {
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
            if (pending.Length > 0) Post(() => RecoverRetiredFiniteCleanup(epoch));
            await operation();
        });
    }
    private void Run(Func<long, Task> operation)
    {
        if (Busy || Closed) return;
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
        _ = Task.Run(async () => {
            try {
                var lobby = withLobby ? await (setup ?? throw new InvalidOperationException("No lobby connection")).LobbyAsync() : null;
                var state = !withLobby || lobby?.MatchActive == true ? await connection.ObserveAsync() : null;
                Post(() => {
                    if (epoch != replica.Epoch || withLobby && (!ReferenceEquals(lobbyOwner, lobbyReplica)
                        || lobby == null || !AcceptLobby(lobby))) return;
                    if (Accept(state, epoch)) lobbyChanged(lobby);
                });
            } catch (Exception error) { Post(() => {
                if (epoch == replica.Epoch && (!withLobby || ReferenceEquals(lobbyOwner, lobbyReplica))) ReportFailure(error, false);
            }); }
            finally { Post(() => Polling = false); }
        });
    }
    public bool Submit(GameCommand command)
    {
        ClientDiagnosticFailure? decline = Busy ? ClientDiagnosticFailure.Busy : Closed ? ClientDiagnosticFailure.Closed
            : NeedsRecovery ? ClientDiagnosticFailure.RecoveryRequired : State == null ? ClientDiagnosticFailure.MissingState
            : !GameCommandPermissions.Allows(State, command) ? ClientDiagnosticFailure.NotOffered : null;
        if (decline is { } failure) { diagnostics.Decline(State, command, failure); return false; }
        if (command is UseOfferedAction offered && (CurrentSourceContext(State, offered.Card) is not { } context
            || SourceActions.TryCreateCommand(context, offered.ActionId) != offered)) {
            diagnostics.Decline(State, command, ClientDiagnosticFailure.SourceOfferChanged); return false;
        }
        command.Validate();
        StopFiniteIntent();
        if (finiteRevocations.Count > 0) { diagnostics.Decline(State, command, ClientDiagnosticFailure.FiniteRevocationPending); return false; }
        if (command is RestorePriorityAbility restore) activeSetting = restore;
        var initial = State!;
        Run(async epoch => {
            var attempt = diagnostics.Begin(initial, command, ClientDiagnosticOrigin.Direct, epoch);
            await diagnostics.ExecuteAsync(connection, command, attempt);
            var state = await diagnostics.ObserveAsync(connection, attempt);
            Post(() => Accept(state, epoch, commandObservation: true));
        });
        return true;
    }
    // Undo is a match preference, so it can be submitted while another player
    // has priority. Finish any in-flight command before sending the setting.
    public bool SubmitSetting(RestorePriorityAbility command)
    {
        if (Closed || NeedsRecovery || State == null || !GameCommandPermissions.Allows(State, command)) return false;
        command.Validate(); StopFiniteIntent(); CancelAdvance();
        if (activeSetting == command || pendingSettings.Contains(command)) return false;
        if (!Busy && finiteRevocations.Count == 0) return Submit(command);
        pendingSettings.Enqueue(command); return true;
    }
    public bool Advance(Func<GameSnapshot, bool> canPass, Func<GameSnapshot, GameSnapshot, bool> shouldPresent)
        => StartAdvance(canPass, (initial, cancellation, _) => advance.RunAsync(initial, canPass, shouldPresent, cancellation));
    public void CancelAdvance() { advancing?.Cancel(); QueueFiniteRevocation(); }
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
        return StartAdvance(state => !intent.IsPaused && intent.CanContinue(state),
            (initial, cancellation, observed) => advance.RunFiniteAsync(initial, intent, shouldPresent, observed, cancellation),
            intent, streamObservations: true);
    }
    private bool StartAdvance(Func<GameSnapshot, bool> canStart,
        Func<GameSnapshot, CancellationToken, Action<GameSnapshot>?, Task<GameSnapshot?>> operation,
        IFiniteDecisionPolicy? nextFiniteIntent = null, bool streamObservations = false)
    {
        if (Busy || Polling || Closed || NeedsRecovery || finiteRevocations.Count > 0 || State is not { } initial || !canStart(initial)) return false;
        if (!ReferenceEquals(finiteIntent, nextFiniteIntent)) StopFiniteIntent();
        if (finiteRevocations.Count > 0) return false;
        finiteIntent = nextFiniteIntent;
        var cancellation = advancing = new CancellationTokenSource();
        Run(async epoch => {
            try {
                advance.SetDiagnosticEpoch(epoch);
                GameSnapshot? lastPosted = streamObservations ? initial : null;
                void Observed(GameSnapshot state) { lastPosted = state; Post(() => Accept(state, epoch, commandObservation: true)); }
                var state = await operation(initial, cancellation.Token, streamObservations ? Observed : null);
                if (!streamObservations || state == null && lastPosted != null
                    || state != null && (lastPosted == null || state.Id != lastPosted.Id || state.ViewerId != lastPosted.ViewerId
                        || state.Revision != lastPosted.Revision || state.Decision?.Id != lastPosted.Decision?.Id))
                    Post(() => Accept(state, epoch, commandObservation: true));
            } finally {
                Post(() => { if (advancing == cancellation) advancing = null; cancellation.Dispose(); });
            }
        });
        return true;
    }
    private void ReportFailure(Exception error, bool command)
    {
        StopFiniteIntent();
        ClearAllOptionalPaymentPolicies();
        LastFailureWasCommand = command;
        if (State != null || finiteRevocations.Count > 0) {
            CancelAdvance();
            if (!command || error is not EngineCommandRejectedException) {
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
        return card is not { FaceDown: false } ? null : new(state.Id, decision.Id, state.Revision, state.ViewerId, card.Key, CardViews.Identity(card));
    }
    private void StopFiniteIntent() { QueueFiniteRevocation(); finiteIntent?.Stop(); finiteIntent = null; }
    public void Close()
    {
        ClearAllOptionalPaymentPolicies();
        StopFiniteIntent(); CancelAdvance(); SourceActions.Close(); pendingSettings.Clear();
        lock (observationGate) { Closed = true; RevokeObservationIngress(); BeginCommand(); }
        commandTask = RevokeAtCloseAsync(commandTask, finiteRevocations.Values.ToArray());
    }
}
