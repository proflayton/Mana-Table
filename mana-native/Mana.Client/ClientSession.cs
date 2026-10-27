using System.Collections.Concurrent;
using Mana.Contracts;

namespace Mana.Client;

/// <summary>Serial commands and ordered snapshots for every connection, without a graphics dependency.</summary>
public sealed class ClientSession
{
    private readonly IMatchConnection connection;
    private readonly ITableSetup? setup;
    private readonly ConcurrentQueue<Action> completions = new();
    private readonly SessionReplica<GameSnapshot> replica = new(s => s.Id, s => s.Revision);
    private readonly ExecutionCursor executions = new();
    private readonly DecisionAdvance advance;
    private readonly TimeProvider time;
    private bool recoveryRequired;
    private CancellationTokenSource? advancing;
    private StackResolutionIntent? stackIntent;
    private readonly Queue<RestorePriorityAbility> pendingSettings = [];
    private RestorePriorityAbility? activeSetting;
    public ClientSession(IMatchConnection connection, ITableSetup? setup = null, TimeProvider? clock = null,
        ISourceActions? sourceActions = null)
    {
        this.connection = connection;
        this.setup = setup;
        time = clock ?? TimeProvider.System;
        advance = new(connection);
        SourceActions = new(sourceActions, Post);
    }
    public SourceActionSession SourceActions { get; }
    public GameSnapshot? State => replica.Current;
    public ExecutionBatch ExecutionChanges { get; private set; } = ExecutionBatch.Empty;
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
    public void Post(Action action) => completions.Enqueue(action);
    public void Pump()
    {
        while (completions.TryDequeue(out var action)) if (!Closed) action();
        while (!Busy && !NeedsRecovery && pendingSettings.TryDequeue(out var setting)) Submit(setting);
    }
    public bool Accept(GameSnapshot? state) => Accept(state, replica.Epoch);
    public void Reset() { StopStackResolution(); CancelAdvance(); SourceActions.Clear(); pendingSettings.Clear(); LastFailureWasCommand = false; replica.BeginCommand(); Accept(null); }
    private bool Accept(GameSnapshot? state, long epoch)
    {
        var previous = State;
        if (Closed || epoch != replica.Epoch || state != null && previous != null && state.Id == previous.Id && state.Revision < previous.Revision) return false;
        executions.Validate(state);
        if (!replica.Accept(state, epoch)) return false;
        if (stackIntent != null && (state == null || !stackIntent.CanContinue(state))) StopStackResolution();
        if (SourceActions.View.Context is { } context && CurrentSourceContext(state, context.SourceCardKey) != context)
            SourceActions.Clear();
        bool recovered = NeedsRecovery;
        ExecutionChanges = executions.Observe(state, baseline: recovered);
        LastSynchronizedAt = time.GetUtcNow();
        recoveryRequired = UncertainCommand = false; LastConnectionError = "";
        Changed?.Invoke(previous, state);
        if (recovered) Recovered?.Invoke();
        return true;
    }
    public void Run(Func<Task> operation)
    {
        if (Busy || Closed) return;
        StopStackResolution(); Run(_ => operation());
    }
    private void Run(Func<long, Task> operation)
    {
        if (Busy || Closed) return;
        long epoch = replica.BeginCommand(); Busy = true;
        _ = Task.Run(async () => {
            try { await operation(epoch); }
            catch (Exception error) { Post(() => { if (epoch == replica.Epoch) ReportFailure(error, true); }); }
            finally { Post(() => { Busy = false; activeSetting = null; Completed?.Invoke(); }); }
        });
    }
    public void Poll(bool withLobby, Action<Lobby?> lobbyChanged)
    {
        if (Busy || Polling || Closed) return;
        Polling = true; long epoch = replica.Epoch;
        _ = Task.Run(async () => {
            try {
                var lobby = withLobby ? await (setup ?? throw new InvalidOperationException("No lobby connection")).LobbyAsync() : null;
                var state = !withLobby || lobby?.MatchActive == true ? await connection.ObserveAsync() : null;
                Post(() => { if (epoch == replica.Epoch && Accept(state, epoch)) lobbyChanged(lobby); });
            } catch (Exception error) { Post(() => { if (epoch == replica.Epoch) ReportFailure(error, false); }); }
            finally { Post(() => Polling = false); }
        });
    }
    public bool Submit(GameCommand command)
    {
        if (Busy || Closed || NeedsRecovery || State == null || !GameCommandPermissions.Allows(State, command)) return false;
        if (command is UseOfferedAction offered && (CurrentSourceContext(State, offered.Card) is not { } context
            || SourceActions.TryCreateCommand(context, offered.ActionId) != offered)) return false;
        command.Validate();
        StopStackResolution();
        if (command is RestorePriorityAbility restore) activeSetting = restore;
        Run(async epoch => { await connection.ExecuteAsync(command); var state = await connection.ObserveAsync(); Post(() => Accept(state, epoch)); });
        return true;
    }
    // Undo is a match preference, so it can be submitted while another player
    // has priority. Finish any in-flight command before sending the setting.
    public bool SubmitSetting(RestorePriorityAbility command)
    {
        if (Closed || NeedsRecovery || State == null || !GameCommandPermissions.Allows(State, command)) return false;
        command.Validate(); StopStackResolution(); CancelAdvance();
        if (activeSetting == command || pendingSettings.Contains(command)) return false;
        if (!Busy) return Submit(command);
        pendingSettings.Enqueue(command); return true;
    }
    public bool Advance(Func<GameSnapshot, bool> canPass, Func<GameSnapshot, GameSnapshot, bool> shouldPresent)
        => StartAdvance(canPass, (initial, cancellation) => advance.RunAsync(initial, canPass, shouldPresent, cancellation));
    public void CancelAdvance() => advancing?.Cancel();
    public bool AdvanceQuietly(string token, Func<GameSnapshot, bool> canContinue,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent)
        => StartAdvance(initial => QuietPriorityPolicy.CanPass(initial, token) && canContinue(initial),
            (initial, cancellation) => advance.RunQuietlyAsync(initial, token, canContinue, shouldPresent, cancellation));
    public bool AdvanceStack(StackResolutionIntent intent, Func<GameSnapshot, GameSnapshot, bool> shouldPresent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(shouldPresent);
        return StartAdvance(state => intent.CanContinue(state) && state.Decision?.Payload is PriorityPayload,
            (initial, cancellation) => advance.RunStackAsync(initial, intent, shouldPresent, cancellation), intent);
    }
    private bool StartAdvance(Func<GameSnapshot, bool> canStart, Func<GameSnapshot, CancellationToken, Task<GameSnapshot?>> operation,
        StackResolutionIntent? nextStackIntent = null)
    {
        if (Busy || Polling || Closed || NeedsRecovery || State is not { } initial || !canStart(initial)) return false;
        if (!ReferenceEquals(stackIntent, nextStackIntent)) StopStackResolution();
        stackIntent = nextStackIntent;
        var cancellation = advancing = new CancellationTokenSource();
        Run(async epoch => {
            try {
                var state = await operation(initial, cancellation.Token);
                Post(() => Accept(state, epoch));
            } finally {
                Post(() => { if (advancing == cancellation) advancing = null; cancellation.Dispose(); });
            }
        });
        return true;
    }
    private void ReportFailure(Exception error, bool command)
    {
        StopStackResolution();
        LastFailureWasCommand = command;
        if (State != null) {
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
    private void StopStackResolution() { stackIntent?.Stop(); stackIntent = null; }
    public void Close() { StopStackResolution(); CancelAdvance(); SourceActions.Close(); pendingSettings.Clear(); Closed = true; replica.BeginCommand(); }
}
