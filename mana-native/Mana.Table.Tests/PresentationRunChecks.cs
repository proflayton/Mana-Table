using System.Collections.Immutable;
using Mana.Contracts;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Mana.Table;

internal static class PresentationRunChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "shared-presentation-session"); Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(directory);
        var engine = new InteractionScenarioEngine();
        var original = engine.State;
        string cardId = original.Viewer!.Zone("Battlefield").Cards[0].VisualId;
        GameSnapshot Issued(long revision, string suffix) => original with {
            Revision = revision, BoardRevision = revision,
            Decision = original.Decision! with { Id = "issued-decision-" + suffix },
            Players = original.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                Cards = zone.Cards.Select(card => card.VisualId == cardId ? card with { Key = "issued-card-" + suffix } : card).ToImmutableArray()
            }).ToImmutableArray() }).ToImmutableArray()
        };
        var initial = Issued(700, "baseline");
        var next = Issued(701, "next");
        var exact = Issued(702, "exact");
        var connection = new Connection(original);
        using var run = new PresentationRun(TestTableServices.For(engine) with { Match = connection }, directory, initial, new(1600, 900));
        var accepted = new List<(GameSnapshot State, TimeSpan At)>();
        run.Accepted += (state, at, _) => {
            accepted.Add((state, at));
            // The follow-up belongs to the next pump. A hidden multi-frame
            // resampler would wrongly consume it during the same caller step.
            if (state.Revision == initial.Revision) run.QueueObservation(next);
        };
        check(run.ElapsedTicks == 0 && accepted.Count == 0 && connection.Observations == 0 && connection.Commands == 0
                && Rejected(() => run.ReadFrame()),
            "Constructing the shared presentation session queues its baseline without updating, polling or preparing an invented frame");
        var neutral = new MouseState(20, 60, 0, default, default, default, default, default);
        run.Tick(TimeSpan.FromSeconds(.7), neutral, active: false);
        check(accepted is [{ State.Revision: 700 }] && accepted[0].At == TimeSpan.FromSeconds(.7)
                && run.Frame.Stamp.Revision == 700 && run.Frame.Stamp.Time == .7 && run.Elapsed == TimeSpan.FromSeconds(.7),
            "A caller's seven-tenths-second step is exactly one inactive update and accepts only its original queued prefix on that clock");
        run.Tick(TimeSpan.Zero, neutral, active: false);
        check(accepted.Select(entry => entry.State.Revision).SequenceEqual([700L, 701L])
                && accepted.All(entry => entry.At == TimeSpan.FromSeconds(.7))
                && run.Frame.Stamp.Revision == 701 && run.View.Match!.Decision!.Id == "issued-decision-next"
                && run.View.Match.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == cardId).Key == "issued-card-next",
            "The following zero-time pump preserves the exact supplied revision, decision and card capability without fixture restamping");
        run.QueueObservation(exact);
        var boundary = TimeSpan.FromTicks(9000023);
        run.UpdateAt(boundary, neutral);
        check(accepted.Count == 3 && ReferenceEquals(accepted[2].State, exact) && accepted[2].At == boundary
                && run.ElapsedTicks == boundary.Ticks && run.Frame.Stamp.Revision == 702
                && run.Frame.Stamp.Time == boundary.TotalSeconds && run.View.Match!.Decision!.Id == "issued-decision-exact",
            "Exact queued observations become accepted only in UpdateAt and use its supplied sub-frame timestamp");
        for (int index = 0; index < 8; index++) run.Tick(TimeSpan.FromMilliseconds(50), neutral, active: index % 2 == 0);
        var frame = run.ReadFrame(); var time = run.Elapsed;
        check(ReferenceEquals(frame, run.ReadFrame()) && run.Elapsed == time && accepted.Count == 3
                && connection.Observations == 0 && connection.Commands == 0 && engine.Replies.Count == 0,
            "Idle/inactive stepping and repeated frame reads cannot poll a provider, submit commands or manufacture accepted observations");
        check(Rejected(() => run.UpdateAt(time - TimeSpan.FromTicks(1), neutral))
                && run.Elapsed == time && ReferenceEquals(frame, run.ReadFrame()) && accepted.Count == 3,
            "A backwards clock request fails before changing presentation time, frame or accepted publications");
        run.Dispose(); run.Dispose();
        check(!run.TryQueueObservation(exact) && Rejected(() => run.QueueObservation(exact))
                && Rejected(() => run.UpdateAt(time, neutral)) && Rejected(() => run.ReadFrame())
                && !connection.Disposed && connection.Observations == 0 && connection.Commands == 0,
            "Disposal revokes presentation delivery and updates without owning or invoking the caller's provider lifetime");
        connection.DisposeAsync().GetAwaiter().GetResult();
        check(connection.Disposed, "The caller can explicitly dispose its retained provider after the presentation session ends");
    }

    private static bool Rejected(Action action)
    {
        try { action(); return false; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return true; }
    }

    private sealed class Connection(GameSnapshot state) : IMatchConnection, IAsyncDisposable
    {
        internal int Observations, Commands;
        internal bool Disposed;
        public Task<GameSnapshot?> ObserveAsync() { Observations++; return Task.FromResult<GameSnapshot?>(state); }
        public Task ExecuteAsync(GameCommand command) { Commands++; return Task.FromException(new InvalidOperationException("Unexpected command from a read-only driver check.")); }
        public Task ConcedeAsync(string gameId) { Commands++; return Task.FromException(new InvalidOperationException("Unexpected concession from a read-only driver check.")); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
