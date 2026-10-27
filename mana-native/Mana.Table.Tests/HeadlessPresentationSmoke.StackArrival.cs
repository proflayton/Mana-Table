using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Forge;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckStackArrival(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        var initial = Example();
        var source = initial.Viewer!.Zone("Battlefield").Cards.Single();
        const string effect = "Put two +1/+1 counters on each creature you control. Those creatures gain vigilance until end of turn.";
        var trigger = new StackItem(source.Name, effect, StackActionKind.TriggeredAbility, source) { Id = "announced-trigger", ControllerId = initial.ViewerId };
        var stacked = initial with { Stack = [trigger], Tempo = new(
            new(TempoPreset.Fast, TempoExpiry.Pass, 30, 60, "stack-clock-policy"),
            [new(initial.ViewerId, "stack-response-clock", TempoPhase.Thinking, 12_000, 30_000, 0)]) };
        var emphasis = new StackArrivalPresentation();
        emphasis.Observe(null, stacked, 0);
        check(emphasis.Ready(0), "Joining a table does not announce retained stack entries again");
        emphasis.Observe(initial, stacked, 10);
        check(!emphasis.Ready(10) && emphasis.Strength(trigger.Id, 10) == 1, "A new actual stack occurrence receives bounded arrival emphasis");
        emphasis.Observe(stacked, stacked with { Revision = stacked.Revision + 1 }, 12);
        check(emphasis.Ready(13), "Repainting or polling an unchanged stack cannot extend the announcement");
        emphasis.Observe(stacked, initial, 13);
        check(emphasis.Strength(trigger.Id, 13) == 0, "Removing the occurrence immediately revokes its announcement");

        var burst = initial with { Stack = Enumerable.Range(1, 3).Reverse().Select(index => new StackItem("Action " + index,
            "Actual action " + index, StackActionKind.Spell, null) { Id = "rapid-" + index, ControllerId = 1 }).ToImmutableArray() };
        var arrivals = Enumerable.Range(1, 3).Select(index => new ActivityEntry(index, 1, "MAIN1", "cast", 1,
            "Cast action " + index, null, "Action " + index) { StackId = "rapid-" + index }).ToImmutableArray();
        emphasis.Clear(); emphasis.Observe(initial, burst, 20, arrivals);
        check(emphasis.PendingCount == 3 && emphasis.Current(20)?.Name == "Action 1",
            "Rapid public casts queue every exact occurrence in event order instead of retaining only the newest observed stack item");
        emphasis.Hold(21, true);
        check(emphasis.Current(30)?.Name == "Action 1" && !emphasis.Ready(30),
            "Inspection holds a readable announcement without changing any current engine decision or resetting a game timer");
        emphasis.Hold(30, false);
        check(emphasis.Current(32)?.Name == "Action 2" && emphasis.PendingCount == 2
            && emphasis.ProgressReady(32) && !emphasis.Ready(32),
            "A finite stack continuation can progress after one readable beat while the remaining public announcements stay queued");
        emphasis.Observe(burst, burst, 32, arrivals);
        check(emphasis.PendingCount == 2, "Exact activity-to-stack identities suppress duplicate cast announcements without joining by source name or text");
        check(emphasis.Current(35)?.Name == "Action 3", "The third rapid cast receives its own public announcement rather than being skipped by later observations");
        emphasis.Clear();
        var large = initial with { Stack = Enumerable.Range(0, StackArrivalPresentation.Capacity + 1)
            .Select(index => trigger with { Id = "bounded-" + index }).ToImmutableArray() };
        emphasis.Observe(initial, large, 40);
        check(emphasis.PendingCount == StackArrivalPresentation.Capacity && emphasis.IncompleteCount == 1,
            "An oversized public announcement backlog is bounded and reports incomplete history instead of silently claiming every event was displayed");
        emphasis.Clear();
        var oldBurst = initial with { Stack = Enumerable.Range(0, 71).Select(index => trigger with { Id = "old-trigger-" + index }).ToImmutableArray(),
            StackInterruptionVersion = 1 };
        emphasis.Observe(initial, oldBurst, 50);
        var response = new StackItem("A real response", "Counter target ability.", StackActionKind.Spell, null) { Id = "new-response", ControllerId = 1 };
        var answered = oldBurst with { Stack = oldBurst.Stack.Insert(0, response), StackInterruptionVersion = 2 };
        emphasis.Observe(oldBurst, answered, 50.1);
        check(emphasis.Current(50.1)?.StackId == response.Id && emphasis.PendingCount == 72,
            "A new real response immediately owns attention ahead of old repeated-trigger notices without dropping that earlier public workload");
        emphasis.Clear();
        var legacyBurst = oldBurst with { StackInterruptionVersion = null };
        emphasis.Observe(initial, legacyBurst, 60);
        emphasis.Observe(legacyBurst, legacyBurst with { Stack = legacyBurst.Stack.Insert(0, response) }, 60.1);
        check(emphasis.Current(60.1)?.StackId == response.Id && emphasis.PendingCount == 72,
            "A new exact typed spell receives current attention even when a legacy provider has no continuation fence");

        var threeSources = Mana.Scenarios.StackTriggersScenario.Snapshot(1);
        threeSources = threeSources with { Stack = threeSources.Stack.Add(response with { Id = "third-source" }) };
        using (var run = new Runner(profile, "fourth-source-current-next", threeSources, trace, performance)) {
            run.View.SetTableStudy(true); run.Tick("three-source-piles"); run.Settle("three-source-ready");
            var fourth = response with { Id = "fourth-source", Name = "Newest response" };
            run.Present(threeSources with { Stack = threeSources.Stack.Insert(0, fourth) });
            run.Tick("fourth-source-next");
            check(run.View.StackWorkloads.Count == 3
                && run.View.StackWorkloads.Single(group => group.IsNext).MemberIds.SequenceEqual(new[] { fourth.Id })
                && run.Frame.Hits.Any(hit => hit.Id == "stack-workload:" + fourth.Id)
                && run.Frame.Hits.Any(hit => hit.Id == "stack-all-sources"),
                "A fourth physical source keeps the actual next group visible while the remaining current source stays reachable through inspection");
        }

        var oneSource = Mana.Scenarios.StackTriggersScenario.Snapshot(1);
        var sample = oneSource.Stack[0];
        oneSource = oneSource with { Stack = Enumerable.Range(0, 71).Select(index => sample with { Id = "same-source-" + index }).ToImmutableArray() };
        using (var run = new Runner(profile, "all-71-source-references", oneSource, trace, performance)) {
            run.View.SetTableStudy(true); run.Tick("pile"); run.Settle("pile-ready");
            run.ClickHit("stack-workload:same-source-0"); run.Settle("all-references");
            check(run.Frame.Hits.Count(hit => hit.Id.StartsWith("stack-workload-member:", StringComparison.Ordinal)) == 71
                && !run.Frame.Hits.Any(hit => hit.Id is "stack-workload-all" or "stack-newer" or "stack-older")
                && run.View.StackRows.All(row => row.Bounds.Top >= 64 && row.Bounds.Bottom < 650),
                "A 71-entry single-source fan retains every current member within the inspection space without a truncation ceiling or paged fallback");
            run.Input(run.Hit("stack-workload-member:same-source-70").Bounds.Center, label: "last-exact-reference"); run.Settle("last-reference-readable");
            check(run.View.ReferencePreviewStackId == "same-source-70" && run.Engine.Replies.Count == 0,
                "The final member of a large physical source pile remains directly hover-readable without choosing a trigger or sending a game command");
        }

        foreach (bool study in new[] { false, true }) {
            using var run = new Runner(profile, "stack-announcement-" + study, initial, trace, performance);
            run.Tick("before-stack"); run.View.SetTableStudy(study); run.Tick("layout");
            run.Present(stacked); run.Tick("trigger-arrives");
            check(Text(run.Frame).Contains("Triggered · You"), "A new trigger announces its actual kind and controller in the production stack surface");
            string hit = (study ? "study-stack-effect:" : "stack-effect:") + trigger.Id;
            var hover = run.Hit(hit);
            run.Input(hover.Bounds.Center, label: "hover-pending-ability"); run.Settle("read-pending-ability");
            var preview = run.View.ReferencePreviewBounds;
            string reading = string.Join(" ", run.Frame.Drawing.Commands.OfType<Mana.Renderer.DrawCommand.Text>()
                .Where(text => preview?.Contains(text.X, text.Y) == true).Select(text => text.Value));
            check(run.View.ReferencePreviewCardId == source.VisualId && reading.Contains("Triggered ability")
                    && reading.Contains("Those creatures gain vigilance until end of turn.")
                    && run.Engine.Replies.Count == 0,
                "Hovering the actual effect reads its complete chosen ability in a source-art card face without answering or activating");
            check(preview != null && run.View.StackRows.All(row => !preview.Value.Intersects(row.Bounds)),
                "The chosen ability reader keeps its pending stack row available");
            for (int frame = 0; frame < 60; frame++) run.Tick("reading-with-running-clock");
            check(Text(run.Frame).Contains("11") && run.Engine.State.Tempo!.Clocks.Single().RemainingMs == 12_000
                    && run.Engine.Replies.Count == 0,
                "Reading keeps the current decision countdown running without resetting its budget or sending a game command");
            run.Present(initial); run.Tick("trigger-removed");
            check(run.View.ReferencePreviewCardId == null && !Text(run.Frame).Contains("Triggered ability") && run.Engine.Replies.Count == 0,
                "An ability leaving the stack revokes its held hover face without replaying a command");
        }

        var spell = initial.Viewer!.Zone("Hand").Cards.First() with { Text = "Draw two cards." };
        using (var run = new Runner(profile, "stack-spell-rules", initial, trace, performance)) {
            run.Tick("before-cast"); run.View.SetTableStudy(true); run.Tick("layout");
            run.Present(initial with { Stack = [new(spell.Name, spell.Text, StackActionKind.Spell, spell) { Id = "full-art-spell", ControllerId = initial.ViewerId }],
                Players = initial.Players.Select(player => player.Id != initial.ViewerId ? player : player with {
                    Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with {
                        Cards = zone.Cards.RemoveAt(0), Count = zone.Count - 1 }).ToImmutableArray()
                }).ToImmutableArray() });
            run.Tick("spell-announced");
            check(Text(run.Frame).Contains("You cast"), "A spell arrival uses its engine kind and announces the acting player");
            run.Input(run.Hit("study-stack-effect:full-art-spell").Bounds.Center, label: "read-spell-effect"); run.Settle("spell-rules-readable");
            var preview = run.View.ReferencePreviewBounds;
            string reading = string.Join(" ", run.Frame.Drawing.Commands.OfType<Mana.Renderer.DrawCommand.Text>()
                .Where(text => preview?.Contains(text.X, text.Y) == true).Select(text => text.Value));
            check(reading.Contains("Draw two cards.") && run.View.ReferencePreviewCardId == spell.VisualId && run.Engine.Replies.Count == 0,
                "A spell's effect hover supplies actual readable text independently of the chosen printing's artwork");
        }

        foreach (var (wireKind, ability, expected) in new[] { ("spell", false, StackActionKind.Spell),
                ("activated", true, StackActionKind.ActivatedAbility), ("triggered", true, StackActionKind.TriggeredAbility),
                ("", true, StackActionKind.Ability) }) {
            var raw = JsonSerializer.SerializeToNode(initial, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
            raw["stack"] = JsonSerializer.SerializeToNode(new[] { new { id = "wire-stack", name = source.Name, text = effect, kind = wireKind, ability, card = source, targets = Array.Empty<StackTarget>() } });
            var decoded = ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(raw))!;
            check(decoded.Stack.Single().Kind == expected, "The provider boundary maps actual stack action kind with conservative legacy compatibility");
            raw["stack"]![0]!["ability"] = !ability;
            bool rejected = false;
            try { ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(raw)); } catch (IOException) { rejected = true; }
            check(wireKind.Length == 0 || rejected, "Explicit contradictory spell/ability classification fails at the provider boundary");
        }
    }
}
