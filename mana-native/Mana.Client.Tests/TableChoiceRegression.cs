using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Mana.Client;
using Mana.Contracts;

internal static class TableChoiceRegression
{
    private static readonly TableInspectionSubject Source = new(TableInspectionKind.BattlefieldCard, "creature-a");
    private static readonly TableChoicePreview Aim = new(Source, new(TableChoiceTargetKind.Player, "1"));

    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var kind in Enum.GetValues<PublicChoiceKind>()) {
            var owner = State(0, kind); var observer = State(1, kind);
            check(PublicChoiceFacts.IsCurrent(owner) && PublicChoiceFacts.IsCurrent(observer),
                "The same public choice capability binds actor and observer without borrowing the actor's private prompt: " + kind);
        }
        using (var run = new Run(State(0))) {
            run.Session.Update(0, null, true, true, Aim);
            var held = await run.Request();
            var changed = Aim with { Target = new(TableChoiceTargetKind.BattlefieldCard, "opposing-card") };
            for (int index = 1; index <= 400; index++) run.Session.Update(index / 1000d, null, true, true, changed);
            check(held.Update.Choice == Aim && held.Update.ChoiceContextId == "choice-context" && held.Update.ContextId == null
                && run.Port.Requests.Count == 1 && run.Session.InFlight,
                "Four hundred changing combat aims share one actual presence task without acquiring priority inspection authority");
            held.Reply.SetResult(Frame(run.State)); await run.Drain();
            run.Session.Update(.4, null, true, true, changed);
            var latest = await run.Request(2);
            check(latest.Update.Choice == changed && latest.Update.Sequence > held.Update.Sequence,
                "A held aim is replaced by the current target rather than replaying pointer history");
            latest.Reply.SetResult(Frame(run.State)); await run.Drain();
            run.Session.Update(.649, null, true, true, Aim);
            check(run.Port.Requests.Count == 2, "Choice previews share the existing four-exchanges-per-second budget");
            run.Session.Update(.65, null, true, true);
            var clear = await run.Request(3);
            check(clear.Update.Choice == null && clear.Update.ChoiceContextId == "choice-context",
                "Cancelling local consideration publishes an explicit empty latest preview");
            clear.Reply.SetResult(Frame(run.State)); await run.Drain();
        }
        using (var run = new Run(State(1))) {
            run.Session.Update(0, null, true, true, Aim); var request = await run.Request();
            check(request.Update.Choice == null && request.Update.ChoiceContextId == null,
                "A watching seat cannot impersonate the current attacking player");
            request.Reply.SetResult(Frame(run.State, Aim)); await run.Drain();
            check(run.Session.VisibleChoices(.2) is [{ PlayerId: 0, Preview: var preview }] && preview == Aim,
                "A remote legal public consideration appears under its own actor and authority context");
            check(run.Session.VisibleChoices(1.5).IsEmpty, "An abandoned combat aim expires without another rules publication");
        }
        foreach (string retirement in new[] { "context", "board", "audience", "status", "owner", "focus", "close" }) {
            using var run = new Run(State(1));
            run.Session.Update(0, null, true, true); var held = await run.Request();
            var replaced = retirement switch {
                "context" => run.State with { PublicChoice = run.State.PublicChoice! with { Id = "replacement" } },
                "board" => run.State with { BoardRevision = 11 },
                "audience" => run.State with { ViewerId = 2 },
                "status" => run.State with { Status = "resolving" },
                "owner" => run.State with { DecisionOwnerId = 2 },
                _ => run.State
            };
            if (retirement == "focus") { run.Session.Suspend(); run.Session.Update(.1, null, true, true); }
            else if (retirement == "close") run.Session.Close();
            else { run.Session.Observe(replaced); run.Session.Observe(run.State); }
            held.Reply.SetResult(Frame(run.State, Aim)); await run.Drain();
            check(run.Session.VisibleChoices(.2).IsEmpty,
                "A delayed preview cannot reappear after retirement and an identical-looking replacement: " + retirement);
        }
        using (var run = new Run(State(1))) {
            run.Session.Update(0, null, true, true); var held = await run.Request();
            run.Session.Update(2, null, true, true);
            held.Reply.SetResult(Frame(run.State, Aim)); await run.Drain();
            check(run.Session.VisibleChoices(2).IsEmpty, "Delayed preview delivery shortens the original lease rather than restarting it");
        }
        foreach (string hidden in new[] { "source", "card-target", "player-target" }) {
            using var run = new Run(State(1));
            var preview = hidden == "card-target" ? Aim with { Target = new(TableChoiceTargetKind.BattlefieldCard, "opposing-card") } : Aim;
            run.Session.Update(0, null, true, true); var request = await run.Request();
            request.Reply.SetResult(Frame(run.State, preview)); await run.Drain();
            var changed = run.State with { Players = run.State.Players.Select(player => player with {
                Eliminated = hidden == "player-target" && player.Id == 1 || player.Eliminated,
                Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card =>
                    card.VisualId == (hidden == "source" ? "creature-a" : hidden == "card-target" ? "opposing-card" : "")
                        ? card with { FaceDown = true } : card).ToImmutableArray() }).ToImmutableArray()
            }).ToImmutableArray() };
            run.Session.Observe(changed);
            check(run.Session.VisibleChoices(.2).IsEmpty,
                "Disclosure or participant loss clears an aim even if a provider reuses its board revision: " + hidden);
        }
        foreach (var invalid in new TableChoicePreview[] {
            new(new(TableInspectionKind.BattlefieldCard, "private-card")),
            Aim with { Target = new(TableChoiceTargetKind.BattlefieldCard, "private-card") },
            Aim with { Target = new(TableChoiceTargetKind.Player, "01") },
            Aim with { Target = new(TableChoiceTargetKind.Player, "999") },
            new(new(TableInspectionKind.BattlefieldCard, "opposing-card")),
            Aim with { Target = new(TableChoiceTargetKind.StackItem, "public-stack") }
        }) {
            using var run = new Run(State(0));
            run.Session.Update(0, null, true, true, invalid); var request = await run.Request();
            check(request.Update.Choice == null, "Hidden, foreign, malformed or non-combat endpoints cannot be sent as an attack preview");
            request.Reply.SetResult(Frame(run.State)); await run.Drain();
        }
        using (var run = new Run(State(1), supportsChoices: false)) {
            run.Session.Update(0, null, true, true, Aim); var request = await run.Request();
            request.Reply.SetResult(Frame(run.State, Aim)); await run.Drain();
            check(request.Update.ChoiceContextId == null && request.Update.Choice == null && run.Session.VisibleChoices(.1).IsEmpty
                && run.Session.Supported,
                "An older inspection provider keeps existing presence usable without enabling choice previews");
        }
        using (var run = new Run(State(1))) {
            run.Session.Update(0, null, true, true); var request = await run.Request();
            request.Reply.SetResult(new TablePresenceResult.Current(new(run.State.Id, 10, 1, []) {
                Choices = [new(0, "choice-context", Aim, 1500), new(0, "choice-context", Aim, 1500)]
            })); await run.Drain();
            check(run.Session.VisibleChoices(.1).IsEmpty, "Duplicate remote actors cannot multiply a transient aim into several arrows");
        }
        var targeting = State(0, PublicChoiceKind.Target);
        check(PublicChoiceFacts.AllowsPreview(targeting, Aim)
            && !PublicChoiceFacts.AllowsPreview(targeting, Aim with { Source = new(TableInspectionKind.BattlefieldCard, "opposing-card") })
            && !PublicChoiceFacts.IsCurrent(targeting with { PublicChoice = targeting.PublicChoice! with { Source = new(TableInspectionKind.BattlefieldCard, "private-card") } }),
            "Targeting previews retain the exact authority-authored public source and refuse private or substituted sources");
    }

    private static TablePresenceResult Frame(GameSnapshot state, TableChoicePreview? choice = null)
        => new TablePresenceResult.Current(new(state.Id, state.BoardRevision, 1, []) {
            Choices = choice == null ? [] : [new(0, "choice-context", choice, 1500)]
        });
    private static GameSnapshot State(int viewer, PublicChoiceKind kind = PublicChoiceKind.Attack)
    {
        string task = kind switch { PublicChoiceKind.Attack => "attack", PublicChoiceKind.Block => "block", _ => "targets" };
        DecisionPayload payload = kind switch {
            PublicChoiceKind.Attack => new AttackPayload(new("Declare", true)),
            PublicChoiceKind.Block => new BlockPayload(new("Declare", true)),
            _ => new TargetSelectionPayload(new("Confirm", true), new("Cancel", true), new(0, 1), [1])
        };
        return new("choice-game", "playing", null, null, viewer, 1, "Combat", "DECLARE_ATTACKERS", 0,
            Enumerable.Range(0, 4).Select(id => new Player(id, "Player " + id, 40, false, false, ImmutableDictionary<string, int>.Empty, [
                new("Battlefield", id < 2 ? 1 : 0, id == 0 ? [new Card { VisualId = "creature-a", CombatId = "combat-a", Name = "Creature" }]
                    : id == 1 ? [new Card { VisualId = "opposing-card", CombatId = "combat-b", Name = "Opponent creature" }] : [], null),
                new("Hand", 1, id == viewer ? [new Card { VisualId = "private-card", Name = "Private" }] : [], null)
            ])).ToImmutableArray(), [new StackItem("Public spell", "", false, new Card { Name = "Public spell" }) { Id = "public-stack" }], null,
            viewer == 0 ? new("choice", new DecisionContext(), payload) : null) {
                Revision = 10, BoardRevision = 10, DecisionOwnerId = 0,
                ActionClass = kind == PublicChoiceKind.Target ? "selection" : task,
                PublicChoice = new("choice-context", 10, 0, kind, kind == PublicChoiceKind.Target ? Source : null)
            };
    }
    private sealed class Run(GameSnapshot state, bool supportsChoices = true) : IDisposable
    {
        internal GameSnapshot State { get; } = state;
        internal Port Port { get; } = new(supportsChoices);
        private readonly ConcurrentQueue<Action> posts = new();
        private TablePresenceSession? session;
        internal TablePresenceSession Session { get { if (session == null) { session = new(Port, posts.Enqueue); session.Observe(State); } return session; } }
        internal async Task<Request> Request(int count = 1) { await Until(() => Port.Requests.Count >= count); return Port.Requests.ToArray()[count - 1]; }
        internal async Task Drain() { await Until(() => !posts.IsEmpty); while (posts.TryDequeue(out var action)) action(); }
        public void Dispose() => session?.Close();
    }
    private sealed record Request(TablePresenceUpdate Update, TaskCompletionSource<TablePresenceResult> Reply);
    private sealed class Port(bool supportsChoices) : ITablePresence
    {
        public bool SupportsTableChoices => supportsChoices;
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
        while (!ready()) { if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The controlled choice-preview boundary did not complete."); await Task.Yield(); }
    }
}
