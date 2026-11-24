using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class PollInvocationRegression
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (string scenario in new[] { "normal-lobby", "normal-direct", "reset", "game", "viewer", "restoration", "lobby" }) {
            var initial = new GameSnapshot("poll-invocation", "playing", null, null, 0, 1, "Main phase", "MAIN1", 0,
                [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, []),
                 new(1, "Other", 40, true, false, ImmutableDictionary<string, int>.Empty, [])], [], null, null) {
                    Revision = 10, BoardRevision = 1
                };
            var port = new Port { State = initial with { Revision = 11, BoardRevision = 2 } };
            var client = new ClientSession(port, port); client.Accept(initial); client.BindLobby(Table("original"));
            int callbacks = 0; var failures = new List<ClientOperationFailure>(); client.Failed += failures.Add;
            try {
                client.Poll(scenario != "normal-direct", _ => callbacks++);
                if (scenario != "normal-direct") await port.LobbyStarted.Task.WaitAsync(Guard);
                switch (scenario) {
                    case "reset": client.Reset(); break;
                    case "game": client.Accept(initial with { Id = "replacement", Revision = 20 }); break;
                    case "viewer": client.Accept(initial with { ViewerId = 1, Revision = 20 }); break;
                    case "restoration": client.Accept(initial with { Revision = 11, BoardRevision = 2,
                        Undo = new(1, 1, null, new(1, "restored-action", "restored-execution", 0)) }); break;
                    case "lobby": client.BindLobby(Table("replacement")); break;
                }
                var retained = client.State;
                port.LobbyReply.TrySetResult(Table("original") with { Revision = 1 });
                using var guard = new CancellationTokenSource(Guard);
                while (true) {
                    client.Pump(); if (!client.Polling) break;
                    await client.WaitForQueuedWorkAsync(guard.Token);
                }
                bool normal = scenario.StartsWith("normal-", StringComparison.Ordinal);
                check(port.Observations == (normal ? 1 : 0) && callbacks == (normal ? 1 : 0)
                        && failures.Count == 0 && port.Commands == 0
                        && (normal ? ReferenceEquals(client.State, port.State) : ReferenceEquals(client.State, retained)),
                    "Poll invocation ownership skips retired work and preserves normal discovery without commands or retries: " + scenario);
            } finally { port.LobbyReply.TrySetCanceled(); client.Close(); }
        }
    }

    private static Lobby Table(string id) => new("client", "playing", null, false, null, true, "", null, [], [], id);
    private sealed class Port : IMatchConnection, ITableSetup
    {
        internal GameSnapshot? State;
        internal int Observations, Commands;
        internal readonly TaskCompletionSource LobbyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<Lobby> LobbyReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Lobby> LobbyAsync() { LobbyStarted.TrySetResult(); return LobbyReply.Task; }
        public Task<GameSnapshot?> ObserveAsync() { Observations++; return Task.FromResult(State); }
        public Task ExecuteAsync(GameCommand command) { Commands++; throw new NotSupportedException(); }
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
        public Task<SoloSetup> PrepareSoloAsync(string deckId) => throw new NotSupportedException();
        public Task StartSoloAsync(string deckId, ImmutableArray<string> opponents) => throw new NotSupportedException();
        public Task<Lobby> HostAsync(int players, bool forwarding) => throw new NotSupportedException();
        public Task<Lobby> JoinAsync(string address) => throw new NotSupportedException();
        public Task<Lobby> SelectDeckAsync(LobbyDeckSelection selection) => throw new NotSupportedException();
        public Task SetReadyAsync(bool ready) => throw new NotSupportedException();
        public Task StartAsync() => throw new NotSupportedException();
        public Task ReturnToLobbyAsync() => throw new NotSupportedException();
        public Task LeaveAsync() => throw new NotSupportedException();
    }
}
