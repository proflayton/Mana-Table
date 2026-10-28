using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class LobbyStateRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var deck = new DeckDetails("saved", "First name", 8, [], [], "", "") { LibraryToken = "saved-bytes-1" };
        var document = LobbyDeckDocument.Saved("table", deck);
        var selection = new LobbyDeckSession();
        selection.Observe(document);
        var first = selection.TryBegin()!;
        selection.Observe(LobbyDeckDocument.Saved("table", deck with { Revision = 0 }));
        check(selection.IsCurrent(first) && selection.TryBegin() == null,
            "Reopening the same saved bytes preserves one in-flight lobby selection despite an editor revision reset");
        var assigned = Table("table", 1, "assignment-1");
        check(selection.Succeed(first, assigned) && selection.Matches(assigned),
            "The saved document becomes synchronized only after its table acknowledges a public assignment identity");
        selection.Observe(document);
        check(selection.TryBegin() == null, "Polling the acknowledged saved selection does not resubmit its deck or withdraw readiness");

        var renamed = LobbyDeckDocument.Saved("table", deck with { Name = "Renamed", Revision = 9, LibraryToken = "saved-bytes-2" });
        selection.Observe(renamed);
        var rename = selection.TryBegin()!;
        check(!selection.Succeed(first, assigned) && selection.IsCurrent(rename),
            "A previous saved list's late acknowledgement cannot confirm a renamed or edited deck");
        check(selection.Fail(rename, "Connection interrupted") && !selection.Matches(assigned) && selection.TryBegin() == null,
            "A failed selection remains explicit and cannot retry each frame or claim the previous roster is current");
        selection.Observe(renamed);
        check(selection.TryBegin() == null && selection.Retry(), "Reobserving a failed saved identity waits for an explicit retry");
        var retry = selection.TryBegin()!;
        check(!selection.Fail(rename, "obsolete failure") && selection.Succeed(retry, Table("table", 2, "assignment-2")),
            "Retry revokes an older attempt and accepts only its own captured acknowledgement");
        selection.Reconcile(Table("table", 3, "assignment-2"));
        check(selection.Matches(Table("table", 4, "assignment-2")),
            "Unrelated ordered lobby updates preserve the acknowledged assignment");
        selection.Reconcile(Table("table", 5, "replacement-assignment"));
        check(selection.View.Status == LobbyDeckStatus.Pending && !selection.Matches(Table("table", 5, "replacement-assignment")),
            "A revoked or replaced public assignment must be synchronized again before becoming Ready");

        var obsolete = selection.TryBegin()!;
        selection.Observe(null); selection.Observe(renamed);
        var rejoined = selection.TryBegin()!;
        check(!selection.Succeed(obsolete, Table("table", 100, "late-assignment")) && selection.IsCurrent(rejoined),
            "Disconnecting and rejoining the same saved deck cannot resurrect a completion from the previous lobby lifetime");
        check(!selection.Succeed(rejoined, Table("another-table", 100, "foreign-assignment"))
                && selection.View.Status == LobbyDeckStatus.Failed,
            "A successful transport reply from another table cannot confirm the current deck");
        check(!LobbyDeckSession.Acknowledges(renamed, Table("table", 6))
                && !LobbyDeckSession.Acknowledges(renamed, assigned with { Seats = [assigned.Seats[0], assigned.Seats[0] with { Index = 1 }] }),
            "Ready cannot continue on a selection receipt with missing deck identity or ambiguous local seat ownership");
        selection.Observe(LobbyDeckDocument.Clear("table"));
        var clearing = selection.TryBegin()!;
        check(!selection.Succeed(clearing, Table("table", 6, "old-assignment")),
            "Withdrawing an unsaved deck requires a receipt that actually clears the old assignment");
        var staleName = Table("table", 7) with { Seats = [Table("table", 7).Seats[0] with { Deck = "Previous deck" }] };
        check(!LobbyDeckSession.Acknowledges(clearing.Document, staleName),
            "A withdrawn deck cannot be acknowledged while its previous public name remains in the roster");
        selection.Retry(); clearing = selection.TryBegin()!;
        check(selection.Succeed(clearing, Table("table", 7)) && selection.Matches(Table("table", 8)),
            "A cleared local seat has its own acknowledged state without retaining a previous deck identity");
        var legacy = deck with { LibraryToken = "" };
        check(LobbyDeckDocument.Saved("table", legacy) != LobbyDeckDocument.Saved("table", legacy with { Revision = 9 }),
            "Older providers without a saved-file token still distinguish changed editor revisions");

        var provider = new ControlledLobby();
        var client = new ClientSession(provider, provider);
        client.BindLobby(Table("table", 5, "assignment-1"));
        check(!client.AcceptLobby(Table("table", 4, "older")) && !client.AcceptLobby(Table("foreign", 99, "foreign"))
                && client.Lobby?.Seats.Single().DeckPreview?.SelectionId == "assignment-1",
            "The shared lobby owner rejects an older roster and a foreign table even when its revision is higher");
        int callbacks = 0;
        client.Poll(true, _ => callbacks++); await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.Run(() => { client.Post(() => client.AcceptLobby(Table("table", 6, "command-assignment"))); return Task.CompletedTask; });
        provider.Pending.SetResult(Table("table", 999, "pre-command-poll")); await Drain(client);
        check(callbacks == 0 && client.Lobby?.Seats.Single().DeckPreview?.SelectionId == "command-assignment",
            "A lobby poll begun before a deck command cannot overwrite that command's acknowledgement");

        provider.Arm(); client.Poll(true, _ => callbacks++);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.BindLobby(null); client.BindLobby(Table("table", 0, "rejoined"));
        provider.Pending.SetResult(Table("table", 1000, "retired-lifetime")); await Drain(client);
        check(callbacks == 0 && client.Lobby?.Seats.Single().DeckPreview?.SelectionId == "rejoined",
            "A late lobby poll cannot resurrect a disconnected lifetime, including a reconnect to the same table ID");

        provider.Arm(); client.Poll(true, _ => callbacks++);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Pending.SetResult(Table("table", 1, "fresh-poll")); await Drain(client);
        check(callbacks == 1 && client.Lobby?.Revision == 1 && client.Lobby.Seats.Single().DeckPreview?.SelectionId == "fresh-poll",
            "An ordered current lobby poll updates the shared owner before notifying presentation");
        provider.Arm(); client.Poll(true, _ => callbacks++);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.BindLobby(null);
        provider.Pending.SetException(new IOException("Retired connection failed")); await Drain(client);
        check(!client.NeedsRecovery && callbacks == 1,
            "A poll failure from a disconnected lobby cannot mark the next screen or connection stale");
        check(!client.AcceptLobby(Table("table", 2000, "after-leave")) && client.Lobby == null,
            "Ordinary acknowledgements cannot implicitly reconnect a disconnected lobby");
        client.Close(); client.BindLobby(Table("closed", 0));
        check(client.Lobby == null, "Closing the client prevents a new lobby binding");
    }

    private static Lobby Table(string id, long revision, string? selection = null) =>
        new("hosting", "", null, false, null, false, "", null, [],
            [new Seat(0, "You", "HUMAN", selection == null ? null : "Chosen deck", false, true) {
                DeckPreview = selection == null ? null : new(selection, "Chosen deck", [], null)
            }], id) { Revision = revision };

    private static async Task Drain(ClientSession client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((client.Busy || client.Polling) && DateTime.UtcNow < deadline) { client.Pump(); await Task.Delay(5); }
        client.Pump();
        if (client.Busy || client.Polling) throw new TimeoutException("Lobby state check did not finish");
    }

    private sealed class ControlledLobby : IMatchConnection, ITableSetup
    {
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Lobby> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm() { Started = new(TaskCreationOptions.RunContinuationsAsynchronously); Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public Task<Lobby> LobbyAsync() { Started.TrySetResult(); return Pending.Task; }
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(null);
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
