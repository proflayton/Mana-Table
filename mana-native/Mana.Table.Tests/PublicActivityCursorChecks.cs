using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal static class PublicActivityCursorChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var initial = PresentationSmoke.Example();
        ActivityEntry Event(long id) => new(id, initial.Turn, initial.PhaseKey, "resolved", 1, "Outcome " + id, null, null);
        var retained = initial with { Activity = [Event(10)] };
        var cursor = new PublicActivityCursor();
        var baseline = cursor.Observe(null, retained);
        var changed = retained with { Activity = [.. retained.Activity, Event(11)] };
        var batch = cursor.Observe(retained, changed);
        check(baseline.IsBaseline && baseline.Before == null && baseline.Entries.IsEmpty && !baseline.HasHistoryGap
            && !batch.IsBaseline && ReferenceEquals(batch.Before, retained) && ReferenceEquals(batch.After, changed)
            && batch.Entries.Select(entry => entry.Id).SequenceEqual([11L]),
            "Joining establishes a nonreplaying baseline; one immutable batch binds new events to the exact accepted before and after snapshots");
        check(cursor.Observe(changed, changed).Entries.IsEmpty && cursor.Observe(changed, retained).Entries.IsEmpty
            && cursor.Observe(retained, changed).Entries.IsEmpty,
            "Repeated observations and an older retained feed cannot roll back the stream watermark or replay a consumed event");
        cursor.Clear();
        check(cursor.Observe(changed, changed).Entries.IsEmpty
            && new PublicActivityCursor().Observe(retained, changed).Entries.Select(entry => entry.Id).SequenceEqual([11L]),
            "A same-viewer presentation reset or late attachment seeds the established before watermark without replaying its history");
        var overlap = changed with { Activity = [Event(11), Event(100)] };
        var contiguous = changed with { Activity = [Event(101)] };
        var gap = changed with { Activity = [Event(200)] };
        check(!cursor.Observe(changed, overlap).HasHistoryGap && !cursor.Observe(overlap, contiguous).HasHistoryGap
            && cursor.Observe(contiguous, gap).HasHistoryGap,
            "Filtered IDs and contiguous feed truncation remain valid; only missing retained overlap plus a skipped next event marks old card anchors uncertain");
        var switched = gap with { ViewerId = 1 };
        var viewerBatch = cursor.Observe(gap, switched);
        var rematch = cursor.Observe(switched, switched with { Id = "activity-rematch" });
        check(viewerBatch.IsBaseline && viewerBatch.Before == null && viewerBatch.Entries.IsEmpty
            && rematch.IsBaseline && rematch.Before == null && rematch.Entries.IsEmpty,
            "Changing viewer or game discards the previous scope and its private before snapshot instead of replaying its retained events");
        var maximum = retained with { Activity = [Event(long.MaxValue)] };
        cursor.Observe(null, maximum);
        check(cursor.Observe(maximum, maximum).Entries.IsEmpty && !cursor.Observe(maximum, maximum).HasHistoryGap,
            "The activity watermark at the maximum event ID neither overflows the history-gap check nor replays an event");

        var presence = new TablePresence(); var outcomes = new ActionFeedback(); var counters = new PublicChangeFeedback();
        var impacts = new ImpactFeedback(); var effects = new SceneEffects();
        void Observe(PublicActivityBatch activity, double now) {
            presence.Observe(activity, now); outcomes.Observe(activity, now); counters.Observe(activity, now);
            impacts.Observe(activity, now); effects.Observe(activity, now);
        }
        cursor.Clear(); Observe(cursor.Observe(null, initial), 0);
        var target = initial.Viewer!.Zone("Battlefield").Cards.First(card => !card.FaceDown);
        var final = initial with { Status = "finished", Players = initial.Players.Select(player => player.Id == 1 ? player with { Life = 38 } : player).ToImmutableArray(), Activity = [
            new(1, initial.Turn, initial.PhaseKey, "draw", 1, "Drawn", null, null) { Count = 2 },
            new(2, initial.Turn, initial.PhaseKey, "counters", 0, "Counters", target.VisualId, target.Name) { Change = new("counters", "+1/+1", 0, 2) },
            new(3, initial.Turn, initial.PhaseKey, "life", 1, "Life", null, null) { Impact = new("life", -2, PlayerId: 1, Before: 40, After: 38) },
            Event(4) with { Outcome = "resolved" }
        ] };
        Observe(cursor.Observe(initial, final), 1);
        check(presence.Cues.Count == 0 && outcomes.Last is { Title: "Outcome 4" }
            && counters.Latest?.Changes.Count == 1 && impacts.Cues.Any(cue => cue.Impact is { Kind: "life", Amount: -2 }),
            "One final observation feeds every consumer; finished-table presence suppression cannot swallow another consumer's actual outcome, counters or life event");
        Observe(cursor.Observe(final, final), 1.1);
        check(outcomes.Last?.Started == 1 && counters.Latest?.Updated == 1 && impacts.Cues.Count == 1,
            "The shared batch prevents duplicate polling from restarting any retained result consumer");
        Observe(cursor.Observe(final, final with { ViewerId = 1 }), 1.2);
        check(outcomes.Last == null && outcomes.LastPublicOutcome == null && counters.Groups.Count == 0
            && impacts.Cues.Count == 0 && effects.Cards.Count == 0 && presence.Cues.Count == 0,
            "A viewer boundary clears every feedback consumer together without retaining another seat's outcome labels or card anchors");
    }

    internal static void RunPresentation(string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "activity-viewer-boundary"); Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(directory);
        var privateFace = new Card { Key = "old-private-key", VisualId = "old-private-face", Name = "Previous viewer private face", Type = "Instant" };
        var initial = PresentationSmoke.Example() with { Activity = [], Stack = [], Combat = null,
            Decision = new Decision("activity-priority", new DecisionContext(), new PriorityPayload(new DecisionReply("Confirm", true), null, CanAutoPass: false, QuietYieldToken: "", Abilities: [])) };
        initial = initial with { Players = initial.Players.Select(player => player.Id != initial.ViewerId ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with { Count = 1, Cards = [privateFace] }).ToImmutableArray()
        }).ToImmutableArray() };
        var engine = new InteractionScenarioEngine(); engine.Present(initial);
        using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State) { Automated = true };
        app.Initialize();
        PresentationFrame Tick(double seconds, Point point = default, ButtonState left = ButtonState.Released) {
            var time = new GameTime(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.01));
            app.Update(time, new(point.X, point.Y, 0, left, default, default, default, default), new(), new(1600, 900));
            return app.ReadPresentation();
        }
        var frame = Tick(10);
        var readerPoint = frame.Hits.Single(hit => hit.Card?.VisualId == privateFace.VisualId && hit.Zone == "Hand").Bounds.Center;
        check(app.TestPort.HitAt(readerPoint)?.Card?.VisualId == privateFace.VisualId,
            "The previous viewer's private hand has an actual exposed pointer target before the scope transition");
        Tick(10.01, readerPoint, ButtonState.Pressed); Tick(10.02, readerPoint);
        var victim = initial.Players[1].Zone("Battlefield").Cards.First(card => !card.FaceDown);
        var target = initial.Viewer!.Zone("Battlefield").Cards.First(card => !card.FaceDown);
        var changed = initial with { Revision = initial.Revision + 1,
            Players = initial.Players.Select(player => player.Id != 1 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name == "Battlefield" ? zone with { Count = zone.Count - 1, Cards = zone.Cards.Where(card => card.VisualId != victim.VisualId).ToImmutableArray() }
                    : zone.Name == "Graveyard" ? zone with { Count = zone.Count + 1, Cards = [.. zone.Cards, victim] } : zone).ToImmutableArray()
            }).ToImmutableArray(), Activity = [
                new(1, initial.Turn, initial.PhaseKey, "counters", 0, "Old counter event", target.VisualId, target.Name) { Change = new("counters", "+1/+1", 0, 1) },
                new(2, initial.Turn, initial.PhaseKey, "resolved", 0, "Previous viewer outcome marker", null, null) { Outcome = "resolved" }
            ] };
        engine.Present(changed); app.TestPort.Accept(engine.State); frame = Tick(10.1);
        check(frame.Cards.Any(card => card.Id == privateFace.VisualId && card.Focused)
            && app.TestPort.LastResolution?.Title == "Previous viewer outcome marker" && app.TestPort.PublicChanges.Count == 1
            && app.TestPort.PublicDepartures.Any(departure => departure.CardId == victim.VisualId),
            "The production application retains a real private reader, reported outcome, public departure and counter history before changing viewer");
        var nextViewer = changed with { ViewerId = 1, Revision = changed.Revision + 1,
            Players = changed.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with { Cards = [] }).ToImmutableArray()
            }).ToImmutableArray() };
        engine.Present(nextViewer); app.TestPort.Accept(engine.State);
        check(app.Frame == null && app.TestPort.Hits.Count == 0
            && app.TestPort.DisplayedPose(privateFace.VisualId) == null && app.TestPort.LastResolution == null,
            "A viewer switch immediately revokes prior-seat hits, faces and feedback before composition");
        frame = Tick(10.2);
        check(app.TestPort.LastResolution == null && app.TestPort.PublicChanges.Count == 0 && app.TestPort.PublicDepartures.Count == 0
            && frame.Cards.All(card => !card.Focused && card.Card.VisualId != privateFace.VisualId)
            && frame.Hits.All(hit => hit.Card?.VisualId != privateFace.VisualId)
            && !frame.Observe().Text.Any(text => text.Value.Contains(privateFace.Name) || text.Value.Contains("Previous viewer outcome marker"))
            && engine.Replies.Count == 0,
            "An accepted same-game viewer switch clears prior readers, event labels, history and departure faces in the actual next frame without issuing a game command");
        app.TestPort.Accept(engine.State); Tick(10.3);
        check(app.TestPort.LastResolution == null && app.TestPort.PublicChanges.Count == 0,
            "Reobserving the new viewer's retained feed does not replay the preceding seat's outcomes or counter history");
    }
}
