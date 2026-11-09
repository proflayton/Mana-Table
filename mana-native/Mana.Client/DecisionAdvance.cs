using Mana.Contracts;

namespace Mana.Client;

/// <summary>Bounded continuation through current authorized decisions on the same
/// seat connection. Intent policies own consent; rules still run in the engine.</summary>
public sealed class DecisionAdvance
{
    private readonly IMatchConnection connection;
    private readonly TimeProvider time;
    private static readonly TimeSpan MaximumSliceDuration = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan ServerProgressReadInterval = TimeSpan.FromMilliseconds(50);
    private readonly record struct FiniteReadAnchor(IFiniteDecisionPolicy Policy, FiniteServerProgressScope? Scope, long Started);
    private readonly object finiteReadGate = new();
    private long finiteReadGeneration;
    private FiniteReadAnchor? finiteReadAnchor;
    private readonly ClientCommandDiagnostics? diagnostics;
    private long operationEpoch;
    private ClientFailureContext? operationContext;
    private readonly Func<GameSnapshot, ClientFailurePhase, ClientFailureContext, CancellationToken, Task<ObservationAdmission>>? admit;
    internal ClientFailurePhase ObservationFailurePhase { get; private set; } = ClientFailurePhase.Observation;
    internal bool SuppressFinalObservation { get; private set; }
    private string? automaticSubmitted, quietSubmitted;
    public DecisionAdvance(IMatchConnection connection) : this(connection, TimeProvider.System) { }
    public DecisionAdvance(IMatchConnection connection, TimeProvider time)
    {
        this.connection = connection;
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }
    internal DecisionAdvance(IMatchConnection connection, ClientCommandDiagnostics diagnostics,
        Func<GameSnapshot, ClientFailurePhase, ClientFailureContext, CancellationToken, Task<ObservationAdmission>> admit, TimeProvider? clock = null)
        : this(connection, clock ?? TimeProvider.System) { this.diagnostics = diagnostics; this.admit = admit; }
    internal void SetOperationContext(ClientFailureContext context)
    {
        operationEpoch = context.Epoch ?? throw new ArgumentException("A session continuation requires its admitted epoch.", nameof(context));
        operationContext = context;
        ObservationFailurePhase = ClientFailurePhase.Observation;
        SuppressFinalObservation = false;
    }
    internal void RetireFiniteReadCadence()
    {
        lock (finiteReadGate) { finiteReadGeneration++; finiteReadAnchor = null; }
    }
    private void RetireFiniteReadCadence(long generation)
    {
        lock (finiteReadGate) if (finiteReadGeneration == generation) {
            finiteReadGeneration++; finiteReadAnchor = null;
        }
    }

    public Task<GameSnapshot?> RunAsync(GameSnapshot initial, Func<GameSnapshot, bool> canPass,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent, CancellationToken cancellation = default)
        => RunBatchAsync(initial, null, canPass, shouldPresent, cancellation);

    public async Task<GameSnapshot?> RunQuietlyAsync(GameSnapshot initial, string token, Func<GameSnapshot, bool> canContinue,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent, CancellationToken cancellation = default)
    {
        RetireFiniteReadCadence();
        if (!QuietPriorityPolicy.CanPass(initial, token) || !canContinue(initial)) return initial;
        return await RunBatchAsync(initial, token, canContinue, shouldPresent, cancellation);
    }

    public Task<GameSnapshot?> RunStackAsync(GameSnapshot initial, StackResolutionIntent intent,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent, CancellationToken cancellation = default)
        => RunFiniteAsync(initial, intent, shouldPresent, null, cancellation);

    internal async Task<GameSnapshot?> RunFiniteAsync(GameSnapshot initial, IFiniteDecisionPolicy intent,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent, Action<GameSnapshot>? observed,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        long readGeneration;
        lock (finiteReadGate) {
            if (finiteReadAnchor is { } previous && !ReferenceEquals(previous.Policy, intent)) {
                finiteReadGeneration++; finiteReadAnchor = null;
            }
            readGeneration = finiteReadGeneration;
        }
        long started = time.GetTimestamp();
        var state = initial;
        int passes = 0;
        var context = operationContext ?? ClientFailureContext.Capture(null, initial);
        var exchange = new CommandExchange(connection, context, diagnostics);
        ObservationFailurePhase = ClientFailurePhase.Observation;
        SuppressFinalObservation = false;
        try {
            while (!cancellation.IsCancellationRequested) {
                if (UndoFacts.RestoredSince(initial, state)) { intent.Stop(); return state; }
                if (intent.IsPaused || !intent.CanContinue(state) || shouldPresent(initial, state)) return state;
                var step = intent.NextStep(state);
                var command = step switch {
                    FiniteNextStep.DispatchCommand dispatch => dispatch.Command,
                    FiniteNextStep.ObserveCurrentInput or FiniteNextStep.AwaitAcknowledgedServerProgress => null,
                    _ => throw new InvalidOperationException("A finite policy must identify its next operation.")
                };
                ClientDiagnosticAttempt? attempt = null;
                if (!intent.IsActive) return state;
                if (command != null) {
                    if (!GameCommandPermissions.Allows(state, command)) { intent.Stop(); return state; }
                    if (cancellation.IsCancellationRequested) return state;
                    attempt = diagnostics?.Begin(state, command, ClientDiagnosticOrigin.Finite, operationEpoch);
                    await exchange.ExecuteAsync(command, attempt);
                    intent.Submitted(state, command); passes++;
                }
                else if (step is FiniteNextStep.AwaitAcknowledgedServerProgress waiting
                    && !await WaitForServerProgressAsync(intent, waiting.Scope, started, readGeneration, cancellation)) return state;
                // Cancellation never hides the result of a pass already sent.
                ObservationFailurePhase = exchange.ObservationFailurePhase;
                // Full command readbacks stay immediate and establish the next
                // passive read's start-to-start anchor, including the grant ACK.
                lock (finiteReadGate) if (finiteReadGeneration == readGeneration)
                    finiteReadAnchor = new(intent, (step as FiniteNextStep.AwaitAcknowledgedServerProgress)?.Scope, time.GetTimestamp());
                var next = await exchange.ObserveAsync(state, attempt);
                if (next != null && next.Revision >= state.Revision && admit != null
                    && await AdmitAsync(next, context, cancellation) != ObservationAdmission.Validated) return next;
                if (next == null) { intent.Stop(); return null; }
                if (next.Revision >= state.Revision) {
                    bool changed = !Equals(state, next);
                    state = next;
                    if (changed) observed?.Invoke(state);
                }
                // The caller owns visible checkpoints. Ordinary stack passing
                // keeps every effect beat; explicit finite presentation can
                // aggregate only its captured work while observing every result.
                if (UndoFacts.RestoredSince(initial, state)) { intent.Stop(); return state; }
                if (intent.IsPaused || !intent.CanContinue(state) || shouldPresent(initial, state)) return state;
                if (time.GetElapsedTime(started) >= MaximumSliceDuration || passes >= 32) return state;
                if (step is not FiniteNextStep.AwaitAcknowledgedServerProgress && (command == null || state.Decision == null)) {
                    try { await Task.Delay(10, cancellation); }
                    catch (OperationCanceledException) { return state; }
                }
            }
            return state;
        } catch { intent.Stop(); throw; }
        finally {
            if (cancellation.IsCancellationRequested || intent.IsPaused || !intent.IsActive)
                RetireFiniteReadCadence(readGeneration);
        }
    }

    private async Task<bool> WaitForServerProgressAsync(IFiniteDecisionPolicy intent, FiniteServerProgressScope scope,
        long sliceStarted, long generation, CancellationToken cancellation)
    {
        // One latest read, never a per-decision cache. Original grant terms and
        // the policy instance identify one instruction across worker slices.
        while (!cancellation.IsCancellationRequested && intent.IsActive && !intent.IsPaused) {
            FiniteReadAnchor? captured;
            lock (finiteReadGate) {
                if (finiteReadGeneration != generation) return false;
                captured = finiteReadAnchor;
            }
            if (captured is not { } anchor || !ReferenceEquals(anchor.Policy, intent)
                || anchor.Scope != null && anchor.Scope != scope) return true;
            long now = time.GetTimestamp();
            var sliceRemaining = MaximumSliceDuration - time.GetElapsedTime(sliceStarted, now);
            if (sliceRemaining <= TimeSpan.Zero) return false;
            var remaining = ServerProgressReadInterval - time.GetElapsedTime(anchor.Started, now);
            if (remaining <= TimeSpan.Zero) return true;
            var delay = remaining < sliceRemaining ? remaining : sliceRemaining;
            // System timers may round fractional milliseconds down. Recheck the
            // monotonic clock after every wake; never start a read early.
            if (delay < TimeSpan.FromMilliseconds(1)) delay = TimeSpan.FromMilliseconds(1);
            try { await Task.Delay(delay, time, cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        }
        return false;
    }

    private async Task<GameSnapshot?> RunBatchAsync(GameSnapshot initial, string? quietToken, Func<GameSnapshot, bool> policy,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent, CancellationToken cancellation)
    {
        RetireFiniteReadCadence();
        long started = time.GetTimestamp();
        var state = initial;
        int passes = 0;
        var context = operationContext ?? ClientFailureContext.Capture(null, initial);
        var exchange = new CommandExchange(connection, context, diagnostics);
        ObservationFailurePhase = ClientFailurePhase.Observation;
        SuppressFinalObservation = false;
        long activity = quietToken == null ? 0 : initial.Activity.Select(entry => entry.Id).DefaultIfEmpty().Max();
        while (!cancellation.IsCancellationRequested) {
            if (UndoFacts.RestoredSince(initial, state)) return state;
            if (quietToken == null) {
                if (state.Id != initial.Id || state.Status is "finished" or "error" || shouldPresent(initial, state)) return state;
            } else if (!QuietPriorityPolicy.CanWait(state, initial.Id, initial.Turn, initial.ActivePlayerId, quietToken)
                || QuietPriorityPolicy.HasInterruption(state, activity) || !policy(state) || shouldPresent(initial, state)) return state;
            string scope = state.Id + ":" + state.Decision?.Id;
            string? submitted = quietToken == null ? automaticSubmitted : quietSubmitted;
            bool waiting = state.Decision == null || submitted == scope;
            ClientDiagnosticAttempt? attempt = null;
            if (!waiting) {
                if (state.Decision is not { } decision || quietToken == null && !policy(state)) return state;
                // The two intents share transport, never authority. A token
                // cannot broaden Auto, and no required choice is a batch action.
                GameCommand command = quietToken == null ? new AutoPassPriority(state.Id, decision.Id)
                    : new PassForNow(state.Id, decision.Id, quietToken);
                if (!GameCommandPermissions.Allows(state, command)) return state;
                if (cancellation.IsCancellationRequested) return state;
                attempt = diagnostics?.Begin(state, command, quietToken == null ? ClientDiagnosticOrigin.Automatic : ClientDiagnosticOrigin.Quiet, operationEpoch);
                await exchange.ExecuteAsync(command, attempt);
                if (quietToken == null) automaticSubmitted = scope; else quietSubmitted = scope;
                submitted = scope; passes++;
            }
            // Always reconcile an already submitted command, even if Hold was pressed in flight.
            ObservationFailurePhase = exchange.ObservationFailurePhase;
            var next = await exchange.ObserveAsync(state, attempt);
            if (next != null && next.Revision >= state.Revision && admit != null
                && await AdmitAsync(next, context, cancellation) != ObservationAdmission.Validated) return next;
            if (next == null) return null;
            if (next.Revision >= state.Revision) state = next;
            // Yield periodically to present progress and avoid owning the connection indefinitely.
            if (time.GetElapsedTime(started) >= MaximumSliceDuration || passes >= 32) return state;
            if (state.Decision == null || submitted == state.Id + ":" + state.Decision.Id) {
                try { await Task.Delay(10, cancellation); }
                catch (OperationCanceledException) { return state; }
            }
        }
        return state;
    }

    private async Task<ObservationAdmission> AdmitAsync(GameSnapshot state, ClientFailureContext context, CancellationToken cancellation)
    {
        var result = await admit!(state, ObservationFailurePhase, context, cancellation);
        if (result == ObservationAdmission.Retired) SuppressFinalObservation = true;
        return result;
    }
}
