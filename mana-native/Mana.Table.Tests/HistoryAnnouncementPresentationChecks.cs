using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Scenarios;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Authored retained facts use the production history detail surface. No live
// card, target selector or action catalog supplies this historical reading.
internal static class HistoryAnnouncementPresentationChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) })
        foreach (string knowledge in new[] { "snapshots", "Known", "Unknown", "legacy" }) {
            string path = Path.Combine(profile, "announced-history-" + size.X + "-" + knowledge);
            Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(path);
            var example = PresentationSmoke.Example();
            var state = example with { Id = "announced-history", Revision = 1, Stack = [], Combat = null, Activity = [], Executions = null,
                Players = example.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                    Cards = [], TopCard = null, Count = zone.Name == "Library" ? 50 : 0
                }).ToImmutableArray() }).ToImmutableArray() };
            var engine = new InteractionScenarioEngine(); engine.Present(state);
            using var run = new PresentationRun(TestTableServices.For(engine), path, state, size);
            run.Tick(TimeSpan.Zero, default);
            var occurrence = new ExecutionOccurrence("recorded-spell", "recorded-spell", ExecutionKind.Spell, ExecutionStep.Stack) {
                ActorPlayerId = state.ViewerId, SourceName = "Recorded spell" };
            var records = ImmutableArray.Create(new HistoryRecord(1, 20, "UPKEEP", "public") {
                ExecutionId = occurrence.Id, Execution = new(occurrence) });
            if (knowledge != "legacy") {
                ImmutableArray<HistoryAnnouncedTarget> targets = knowledge == "snapshots" ? ImmutableArray.Create(new HistoryAnnouncedTarget("card", 0, 0) {
                    CardId = "historic-land", Name = "Recorded Forest", Type = "Basic Land", Zone = "Battlefield",
                    ControllerId = 1, OwnerId = 1, AllocatedAmount = 3 }) : [];
                records = records.Add(new(2, 20, "UPKEEP", "public") { ExecutionId = occurrence.Id,
                    Announcement = new("announced", "historic-stack", knowledge == "snapshots" ? "Known" : knowledge, targets) });
                if (knowledge == "snapshots") records = records.Add(new(3, 20, "UPKEEP", "public") {
                    ExecutionId = occurrence.Id, Announcement = new("retargeted", "historic-stack", "Known", [
                        new("card", 0, 0) { CardId = "historic-creature", Name = "Recorded creature", Type = "Creature",
                            Zone = "Battlefield", ControllerId = 2 }
                    ]) });
            }
            check(run.View.AcceptHistory(new(knowledge == "legacy" ? 1 : 2, state.Id, state.ViewerId,
                    1, records.Length, records.Length, false, records)),
                "The actual history owner accepts the retained " + knowledge + " target receipt at " + size);
            var frame = run.Tick(TimeSpan.FromSeconds(.016), default);
            check(run.View.Activate(frame.Hits.Single(hit => hit.Id == "history")), "The existing History gesture opens announced targets at " + size);
            frame = run.Tick(TimeSpan.FromSeconds(.016), default);
            string details = run.View.HistoryDetailText;
            check(knowledge switch {
                "snapshots" => details.Contains("ANNOUNCED TARGETS") && details.Contains("TARGETS CHANGED")
                    && details.Contains("Recorded Forest") && details.Contains("Basic Land") && details.Contains("assigned 3")
                    && details.Contains("controlled by player 1 at announcement") && details.Contains("Recorded creature")
                    && details.Contains("controlled by player 2 at announcement"),
                "Known" => details.Contains("No targets.") && !details.Contains("not disclosed"),
                "Unknown" => details.Contains("not disclosed at announcement") && !details.Contains("No targets."),
                _ => details.Contains("Not reported in this retained history.") && !details.Contains("No targets.")
            }, "History details distinguish immutable announced/retargeted snapshots, known empty, undisclosed and legacy absence at " + size);
            check(run.View.Overlay == "history" && !frame.Hits.Any(hit => hit.Id is "history-read-source" or "read-card-action")
                    && engine.Replies.Count == 0 && state.Players.All(player => player.Zones.All(zone => zone.Cards.IsEmpty)),
                "Historical target names and identities stay read-only when every current card is absent at " + size);
        }
    }
}
