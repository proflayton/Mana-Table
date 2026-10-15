using System.Collections.Concurrent;
using Mana.Contracts;

namespace Mana.Client;

/// <summary>Serial commands and ordered snapshots for every connection, without a graphics dependency.</summary>
public sealed class ClientSession(IMatchConnection connection, ITableSetup? setup = null)
{
    public ClientSession(ICardEngine engine) : this(engine, engine) { }
    private readonly ConcurrentQueue<Action> completions = new();
    private readonly SessionReplica<GameSnapshot> replica = new(s => s.Id, s => s.Revision);
    private readonly DecisionAdvance advance = new(connection);
    private CancellationTokenSource? advancing;
    private readonly Queue<DecisionReply> pendingSettings = [];
    private DecisionReply? activeSetting;
    public GameSnapshot? State => replica.Current;
    public string Scope => (State?.Id ?? "lobby") + ":" + (State?.Decision?.Id ?? "");
    public bool Busy { get; private set; }
    public bool Polling { get; private set; }
    public bool Closed { get; private set; }
    public event Action<Exception>? Failed;
    public event Action? Completed;
    public event Action<GameSnapshot?, GameSnapshot?>? Changed;
    public void Post(Action action) => completions.Enqueue(action);
    public void Pump()
    {
        while (completions.TryDequeue(out var action)) if (!Closed) action();
        while (!Busy && pendingSettings.TryDequeue(out var setting)) Submit(setting);
    }
    public bool Accept(GameSnapshot? state) => Accept(state, replica.Epoch);
    public void Reset() { CancelAdvance(); pendingSettings.Clear(); replica.BeginCommand(); Accept(null); }
    private bool Accept(GameSnapshot? state, long epoch)
    {
        var previous = State;
        if (!replica.Accept(state, epoch)) return false;
        Changed?.Invoke(previous, state); return true;
    }
    public void Run(Func<Task> operation) => Run(_ => operation());
    private void Run(Func<long, Task> operation)
    {
        if (Busy || Closed) return;
        long epoch = replica.BeginCommand(); Busy = true;
        _ = Task.Run(async () => {
            try { await operation(epoch); }
            catch (Exception error) { Post(() => Failed?.Invoke(error)); }
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
            } catch (Exception error) { Post(() => { if (epoch == replica.Epoch) Failed?.Invoke(error); }); }
            finally { Post(() => Polling = false); }
        });
    }
    public bool Submit(DecisionReply reply)
    {
        bool undoSetting = reply is { Action: ReplyAction.SetAbilityAutoPass, Ignore: false, DecisionId.Length: 0 };
        if (Busy || Closed || State?.Id != reply.GameId || !undoSetting && State.Decision?.Id != reply.DecisionId) return false;
        if (undoSetting && !State.IgnoredPriorityAbilities.Any(a => a.Id == reply.AbilityId)) return false;
        reply.Validate();
        if (undoSetting) activeSetting = reply;
        Run(async epoch => { await connection.ReplyAsync(reply); var state = await connection.ObserveAsync(); Post(() => Accept(state, epoch)); });
        return true;
    }
    // Undo is a match preference, so it can be submitted while another player
    // has priority. Finish any in-flight command before sending the setting.
    public bool SubmitSetting(DecisionReply reply)
    {
        if (Closed || State?.Id != reply.GameId || reply is not { Action: ReplyAction.SetAbilityAutoPass, Ignore: false, DecisionId.Length: 0 }) return false;
        if (!State.IgnoredPriorityAbilities.Any(a => a.Id == reply.AbilityId)) return false;
        reply.Validate(); CancelAdvance();
        if (activeSetting == reply || pendingSettings.Contains(reply)) return false;
        if (!Busy) return Submit(reply);
        pendingSettings.Enqueue(reply); return true;
    }
    public bool Advance(Func<GameSnapshot, bool> canPass, Func<GameSnapshot, GameSnapshot, bool> shouldPresent)
    {
        if (Busy || Polling || Closed || State is not { } initial || !canPass(initial)) return false;
        var cancellation = advancing = new CancellationTokenSource();
        Run(async epoch => {
            try {
                var state = await advance.RunAsync(initial, canPass, shouldPresent, cancellation.Token);
                Post(() => Accept(state, epoch));
            } finally {
                Post(() => { if (advancing == cancellation) advancing = null; cancellation.Dispose(); });
            }
        });
        return true;
    }
    public void CancelAdvance() => advancing?.Cancel();
    public void Close() { CancelAdvance(); pendingSettings.Clear(); Closed = true; replica.BeginCommand(); }
}
