using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class EncounterAdmissionRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var initial = Table(1, "attempt-1", EncounterStartPhase.Preparing);
        EncounterStartFacts.Validate(initial);
        var provider = new Provider();
        var client = new ClientSession(provider, provider);
        client.BindLobby(initial);
        client.Accept(EncounterSetupRegression.State("previous-game", "previous-attempt"));
        int notifications = 0;
        foreach (var (revision, phase) in new[] { (2L, EncounterStartPhase.Preparing), (3L, EncounterStartPhase.Recording),
            (4L, EncounterStartPhase.Failed), (5L, EncounterStartPhase.Cancelled) }) {
            provider.Arm(); client.Poll(true, _ => notifications++);
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            provider.Pending.SetResult(Table(revision, "attempt-1", phase)); await Drain(client);
            check(client.Lobby?.StartState?.Phase == phase && client.State == null && provider.Observations == 0
                    && !client.NeedsRecovery && client.Lobby.PartyProgress is { CompletedEncounters: 0, LastReceipt: null },
                "Pending, failed and cancelled admission stays in the lobby without querying an old game or inventing a saved outcome: " + phase);
        }
        check(notifications == 4, "Every ordered admission transition is published to presentation after its lobby is accepted");

        var newer = Table(10, "attempt-2", EncounterStartPhase.Preparing);
        client.AcceptLobby(newer);
        provider.Arm(); client.Poll(true, _ => notifications++);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Pending.SetResult(Table(9, "attempt-1", EncounterStartPhase.Cancelled)); await Drain(client);
        check(client.Lobby?.StartState?.AttemptId == "attempt-2" && notifications == 4 && !client.NeedsRecovery,
            "A late cancelled publication cannot revive a prior attempt after a newer admission is accepted");

        var active = Table(11, "attempt-2", EncounterStartPhase.Starting, active: true);
        var real = Frame("actual-game", "attempt-2");
        provider.Match = real;
        provider.Arm(); client.Poll(true, _ => notifications++);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Pending.SetResult(active); await Drain(client);
        check(client.State?.Id == real.Id && provider.Observations == 1 && notifications == 5
                && client.State.Encounter?.EncounterId == "attempt-2" && !client.NeedsRecovery,
            "Only an active admitted encounter causes a rules observation, and its actual starting frame is accepted with its real identity");

        provider.Match = Frame("foreign-game", "different-attempt");
        provider.Arm(); client.Poll(true, _ => notifications++);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Pending.SetResult(active with { Revision = 9 }); await Drain(client);
        check(client.State?.Id == real.Id && !client.NeedsRecovery && notifications == 5,
            "A rejected old lobby poll cannot fault or replace the current admission even when its accompanying rules frame is foreign");
        client.Close();

        var wrongProvider = new Provider { Match = Frame("previous-game", "previous-attempt") };
        var wrong = new ClientSession(wrongProvider, wrongProvider);
        int failures = 0; wrong.Failed += _ => failures++;
        wrong.BindLobby(newer);
        wrong.Poll(true, _ => throw new InvalidOperationException("A foreign frame reached presentation"));
        await wrongProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        wrongProvider.Pending.SetResult(active); await Drain(wrong);
        check(failures == 1 && wrong.State == null && wrongProvider.Observations == 1,
            "A real active flag cannot authorize a previous or foreign encounter snapshot; the client retains no fabricated match");
        wrong.Close();

        var reconnectProvider = new Provider();
        var reconnect = new ClientSession(reconnectProvider, reconnectProvider);
        reconnect.BindLobby(initial); reconnect.Poll(true, _ => throw new InvalidOperationException("A retired admission reached presentation"));
        await reconnectProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reconnect.BindLobby(null); reconnect.BindLobby(Table(0, "new-lifetime", EncounterStartPhase.Preparing));
        reconnectProvider.Pending.SetResult(initial with { Revision = 100 }); await Drain(reconnect);
        check(reconnect.Lobby?.StartState?.AttemptId == "new-lifetime" && reconnect.State == null && !reconnect.NeedsRecovery,
            "Leaving and rebinding even the same table ID fences a late admission publication from the retired connection lifetime");
        reconnect.Close();
    }

    private static Lobby Table(long revision, string attempt, EncounterStartPhase phase, bool active = false) =>
        new("hosting", "Admission", null, false, null, active, "", null, [], [
            new Seat(0, "You", "LOCAL", "Deck", true, true) { ParticipantId = "person-a", TeamId = 0, Role = MatchSeatRole.Party },
            new Seat(1, "AI", "AI", "Deck", true, false) { ParticipantId = "person-b", TeamId = 1, Role = MatchSeatRole.Opposition }
        ], "table") {
            Revision = revision, EncounterStartSupported = true,
            MatchSetupCapabilities = new([MatchRulesets.CooperativeCommander], 6, 4, 2), MatchSetupConsent = "reviewed-setup",
            MatchSetup = new(MatchRulesets.CooperativeCommander, [new(0, 0, MatchSeatRole.Party), new(1, 1, MatchSeatRole.Opposition)]),
            PartyProgress = new("party", active ? attempt : null, 0, 0, EncounterSaveState.Ready, null, false, null),
            StartState = new(attempt, phase, phase == EncounterStartPhase.Failed ? "Could not record the attempt." : null,
                phase == EncounterStartPhase.Failed)
        };
    private static GameSnapshot Frame(string game, string attempt) => new(game, "starting", null, null, 7, 0,
        "Pregame", "PREGAME", null, [], [], null, null) { Encounter = new("party", attempt) };
    private static async Task Drain(ClientSession client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (client.Polling && DateTime.UtcNow < deadline) { client.Pump(); await Task.Delay(5); }
        client.Pump();
        if (client.Polling) throw new TimeoutException("The admission poll did not complete");
    }
    private sealed class Provider : ITableSetup, IMatchConnection
    {
        internal GameSnapshot? Match;
        internal int Observations;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Lobby> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm() { Started = new(TaskCreationOptions.RunContinuationsAsynchronously); Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public Task<Lobby> LobbyAsync() { Started.TrySetResult(); return Pending.Task; }
        public Task<GameSnapshot?> ObserveAsync() { Observations++; return Task.FromResult(Match); }
        public Task ExecuteAsync(GameCommand command) => throw new NotSupportedException();
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
