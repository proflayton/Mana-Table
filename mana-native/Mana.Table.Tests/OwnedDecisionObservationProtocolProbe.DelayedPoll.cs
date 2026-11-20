using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static partial class OwnedDecisionObservationProtocolProbe
{
    // Authored pipe scheduling, not Magic gameplay: an old poll reaches its
    // provider only after the explicit command has captured its own readback.
    private static async Task CheckDelayedLobbyPollAsync(string profile, string executable, Action<bool, string> check)
    {
        const string mode = "publication-admission-delayed-lobby";
        string directory = Path.Combine(profile, mode); Directory.CreateDirectory(directory);
        await using var engine = new ForgeEngine(new(executable, Marker, mode, directory));
        await engine.InitializeAsync();
        var setup = new DelayedPollSetup();
        var client = new ClientSession(engine, setup); var failures = new List<ClientOperationFailure>();
        client.Failed += failures.Add;
        try {
            var initial = (await engine.ObserveAsync())!; client.Accept(initial);
            client.Poll(true, _ => throw new InvalidOperationException("The obsolete lobby callback must not run"));
            await setup.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(client.Submit(new PassPriority(initial.Id, initial.Decision!.Id)),
                "An explicit gameplay command can start while its earlier discovery waits for the lobby");
            await WaitFor(directory, () => File.Exists(Path.Combine(directory, "readback-held.txt")), "delayed-lobby command readback");
            setup.Reply.TrySetResult(new("client", "playing", null, false, null, true, "", null, [], []));
            await PublicationPumpUntil(client, () => !client.Polling);
            await engine.EnableAutomatedSeatsAsync(); // Release only the actual command readback.
            await PublicationPumpUntil(client, () => !client.Busy);
            Console.WriteLine($"Delayed lobby witness: actions={Count(directory, "matchAction")}, states={Count(directory, "matchState")}, "
                + $"revision={client.State?.Revision}, failure={failures.FirstOrDefault()}");
            check(failures.Count == 0 && !client.NeedsRecovery && client.State is { Revision: 11, Decision.Id: "publication-after-command" }
                    && Count(directory, "matchAction") == 1 && Count(directory, "matchState") == 2,
                "A retired lobby poll cannot acquire post-command provider authority or poison the one actual command readback");
        } finally { setup.Reply.TrySetCanceled(); client.Close(); }
    }

    private sealed class DelayedPollSetup : ITableSetup
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<Lobby> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Lobby> LobbyAsync() { Started.TrySetResult(); return Reply.Task; }
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
