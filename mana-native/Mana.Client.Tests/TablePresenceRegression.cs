using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Mana.Client;
using Mana.Contracts;

internal static class TablePresenceRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var publicA = new TableInspectionSubject(TableInspectionKind.BattlefieldCard, "public-a");
        var publicB = new TableInspectionSubject(TableInspectionKind.BattlefieldCard, "public-b");
        var privateCard = new TableInspectionSubject(TableInspectionKind.BattlefieldCard, "private-hand");
        var owner = State(0);
        using (var run = new Run(owner)) {
            run.Session.Update(0, publicA, true, true);
            var first = await run.Request();
            for (int frame = 1; frame <= 200; frame++) run.Session.Update(frame / 1000d, publicB, true, true);
            check(run.Port.Requests.Count == 1 && run.Session.InFlight && first.Update.Subject == publicA,
                "A held presence exchange retains one actual request despite 200 newer pointer updates");
            first.Reply.SetResult(new TablePresenceResult.Current(new(owner.Id, owner.BoardRevision, 1, []))); await run.Drain();
            run.Session.Update(.249, publicB, true, true);
            check(run.Port.Requests.Count == 1, "Presence dispatch cannot exceed four exchanges per second");
            run.Session.Update(.25, publicB, true, true);
            var second = await run.Request(2);
            check(second.Update.Subject == publicB && second.Update.Sequence > first.Update.Sequence,
                "The next presence exchange sends only the current object rather than replaying pointer history");
            second.Reply.SetResult(new TablePresenceResult.Current(new(owner.Id, owner.BoardRevision, 2, []))); await run.Drain();
            run.Session.Update(.5, privateCard, true, true);
            var hidden = await run.Request(3);
            check(hidden.Update.Subject == null, "A readable own hand card never becomes a public presence subject");
            hidden.Reply.SetResult(new TablePresenceResult.Current(new(owner.Id, owner.BoardRevision, 3, []))); await run.Drain();
            run.Session.Update(.75, publicA, true, false);
            check(run.Port.Requests.Count == 3, "Gameplay reservation prevents another presence request from entering transport");
        }
        using (var run = new Run(State(1))) {
            run.Session.Update(0, publicA, true, true);
            var request = await run.Request();
            check(request.Update.Subject == null && request.Update.ContextId == null,
                "An observing seat polls presence without publishing as the ordinary-priority owner");
            request.Reply.SetResult(Frame(run.State, publicA)); await run.Drain();
            check(run.Session.Visible(.2) is [{ PlayerId: 0, Subject: var subject }] && subject == publicA,
                "A current public occurrence is visible only under its authority-authored actor and input context");
            check(run.Session.Visible(1.5).Length == 0,
                "Attention expires at its original dispatch lease even without a further observation");
        }
        foreach (string change in new[] { "board", "input", "audience", "background", "close" }) {
            using var run = new Run(State(1));
            run.Session.Update(0, null, true, true);
            var request = await run.Request();
            if (change == "board") {
                run.Session.Observe(run.State with { BoardRevision = 11 }); run.Session.Observe(run.State);
            } else if (change == "input") {
                run.Session.Observe(run.State with { PublicInspection = run.State.PublicInspection! with { Id = "replacement" } });
                run.Session.Observe(run.State);
            } else if (change == "audience") {
                run.Session.Observe(run.State with { ViewerId = 2 }); run.Session.Observe(run.State);
            } else if (change == "background") {
                run.Session.Suspend(); run.Session.Update(.1, null, true, true);
            } else run.Session.Close();
            request.Reply.SetResult(Frame(run.State, publicA)); await run.Drain();
            check(run.Session.Visible(.2).Length == 0,
                "Retired presence cannot reappear after an A-to-B-to-A or lifecycle transition: " + change);
        }
        using (var run = new Run(State(1))) {
            run.Session.Update(0, null, true, true);
            var request = await run.Request();
            request.Reply.SetResult(Frame(run.State, publicA)); await run.Drain();
            var concealed = run.State with { Players = run.State.Players.Select(player => player with {
                Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card =>
                    card.VisualId == publicA.Id ? card with { FaceDown = true } : card).ToImmutableArray() }).ToImmutableArray()
            }).ToImmutableArray() };
            run.Session.Observe(concealed);
            check(run.Session.Visible(.1).Length == 0,
                "A concealed occurrence revokes its cue even if a malformed publisher reuses the board and input context");
        }
        using (var run = new Run(State(1))) {
            run.Session.Update(0, null, true, true);
            var request = await run.Request();
            run.Session.Update(2, null, true, true);
            request.Reply.SetResult(Frame(run.State, publicA)); await run.Drain();
            check(run.Session.Visible(2).Length == 0,
                "A delayed reply cannot restart its lease at arrival and resurrect expired attention");
        }
        using (var run = new Run(State(1))) {
            run.Session.Update(0, null, true, true);
            var request = await run.Request(); request.Reply.SetException(new IOException("presence failed")); await run.Drain();
            run.Session.Update(.999, null, true, true);
            check(run.Port.Requests.Count == 1 && run.Session.Visible(.999).Length == 0,
                "Presence failure clears activity and backs off independently of match execution");
            run.Session.Update(1, null, true, true);
            var unsupported = await run.Request(2); unsupported.Reply.SetResult(new TablePresenceResult.Unsupported()); await run.Drain();
            run.Session.Update(100, publicA, true, true);
            check(!run.Session.Supported && run.Port.Requests.Count == 2,
                "An older provider's absent optional presence port disables polling without affecting the match");
        }
        using (var run = new Run(State(1))) {
            run.Session.Update(0, null, true, true); var request = await run.Request();
            request.Reply.SetResult(new TablePresenceResult.Current(new(run.State.Id, 10, 1, [
                new(0, "inspection-context", privateCard, 1500), new(2, "inspection-context", publicA, 1500),
                new(0, "old-input", publicA, 1500), new(0, "inspection-context", publicB, 1501)
            ]))); await run.Drain();
            check(run.Session.Visible(.1).Length == 0,
                "Remote cues reject private identities, another actor, replaced input and overlong leases");
        }
        using (var run = new Run(State(1))) {
            run.Session.Update(0, null, true, true); var old = await run.Request();
            old.Reply.SetResult(new TablePresenceResult.Unsupported()); await run.Drain();
            var capable = run.State with { Id = "capable-new-table" };
            run.Session.Observe(capable); run.Session.Update(.25, null, true, true);
            var fresh = await run.Request(2); fresh.Reply.SetResult(Frame(capable, publicA)); await run.Drain();
            check(run.Session.Supported && run.Session.Visible(.3).Length == 1,
                "An older table's absent presence capability does not disable inspection after joining a capable new table");
        }
        using (var run = new Run(State(1))) {
            run.Session.Update(0, null, true, true); var old = await run.Request();
            run.Session.Observe(run.State with { Id = "temporary-table" }); run.Session.Observe(run.State);
            run.Session.Update(10, publicA, true, true);
            check(run.Port.Requests.Count == 1 && run.Session.InFlight,
                "A long-running actual provider task remains single-flight across elapsed leases and game A-to-B-to-A");
            old.Reply.SetResult(new TablePresenceResult.Unsupported()); await run.Drain();
            run.Session.Update(10.25, null, true, true);
            var fresh = await run.Request(2); fresh.Reply.SetResult(Frame(run.State, publicA)); await run.Drain();
            check(run.Session.Supported && run.Session.Visible(10.3).Length == 1,
                "A late unsupported reply from a retired game binding cannot disable its fresh replacement");
        }
        using (var run = new Run(State(1))) {
            run.Session.Update(0, null, true, true); var old = await run.Request();
            old.Reply.SetResult(new TablePresenceResult.Current(new(run.State.Id, 10, 50,
                [new(0, "inspection-context", publicA, 1500)]))); await run.Drain();
            run.Session.Update(.25, null, true, true);
            var superseded = await run.Request(2);
            superseded.Reply.SetResult(new TablePresenceResult.Superseded()); await run.Drain();
            run.Session.Update(.5, null, true, true);
            var fresh = await run.Request(3); fresh.Reply.SetResult(Frame(run.State, publicA)); await run.Drain();
            check(run.Session.Supported && run.Session.Visible(.6).Length == 1,
                "Provider transport retirement with identical public context stays superseded rather than permanently disabling presence");
        }
    }

    private static TablePresenceResult Frame(GameSnapshot state, TableInspectionSubject subject)
        => new TablePresenceResult.Current(new(state.Id, state.BoardRevision, 1, [new(0, "inspection-context", subject, 1500)]));

    private static GameSnapshot State(int viewer) => new("presence-match", "playing", null, null, viewer, 3, "Main phase", "MAIN1", 0,
        Enumerable.Range(0, 4).Select(id => new Player(id, "Player " + id, 40, id == 0, false, ImmutableDictionary<string, int>.Empty, [
            new("Battlefield", id == 0 ? 2 : 0, id == 0 ? [new Card { VisualId = "public-a", Name = "Public A" }, new Card { VisualId = "public-b", Name = "Public B" }] : [], null),
            new("Hand", 1, id == viewer ? [new Card { VisualId = "private-hand", Name = "Private" }] : [], null)
        ])).ToImmutableArray(), [], null, null) {
            BoardRevision = 10, Revision = 30, PublicInspection = new("inspection-context", 10, 0)
        };

    private sealed class Run(GameSnapshot state) : IDisposable
    {
        internal GameSnapshot State { get; } = state;
        internal Port Port { get; } = new();
        private readonly ConcurrentQueue<Action> posts = new();
        private TablePresenceSession? session;
        internal TablePresenceSession Session {
            get {
                if (session == null) { session = new(Port, posts.Enqueue); session.Observe(State); }
                return session;
            }
        }
        internal async Task<Request> Request(int count = 1)
        {
            await Until(() => Port.Requests.Count >= count);
            return Port.Requests.ToArray()[count - 1];
        }
        internal async Task Drain()
        {
            await Until(() => !posts.IsEmpty);
            while (posts.TryDequeue(out var action)) action();
        }
        public void Dispose() { session?.Close(); }
    }
    private sealed record Request(TablePresenceUpdate Update, TaskCompletionSource<TablePresenceResult> Reply);
    private sealed class Port : ITablePresence
    {
        internal ConcurrentQueue<Request> Requests { get; } = new();
        public Task<TablePresenceResult> ExchangePresenceAsync(TablePresenceUpdate update, CancellationToken cancellation = default)
        {
            var reply = new TaskCompletionSource<TablePresenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Enqueue(new(update, reply)); return reply.Task;
        }
    }
    private static async Task Until(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready()) {
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The controlled presence boundary did not complete.");
            await Task.Yield();
        }
    }
}
