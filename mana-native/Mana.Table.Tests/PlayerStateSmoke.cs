using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Magic;

namespace Mana.Table;

internal sealed partial class CardReadingSmoke
{
    // Runs inside the existing critical card-state worker. Snapshots contain
    // only public player state; inspection still goes through real UI input.
    private void QueuePlayerStateChecks()
    {
        const string speedDetail = "Speed starts at 1. Once each of this player's turns, when an opponent loses life, speed increases by 1, up to 4. It stays after its source leaves. Cards may have abilities at max speed.";
        int repliesBefore = 0;
        Step(.1, () => {
            repliesBefore = scenario.Replies.Count;
            Move(new(10, 70));
            Publish(scenario.State with { Decision = new Decision("", new DecisionContext(), new PriorityPayload(new DecisionReply("Confirm", true), null, CanAutoPass: false, QuietYieldToken: "", Abilities: [])),
                Players = scenario.State.Players.Select(p => p with { Counters = ImmutableDictionary<string, int>.Empty.Add("Energy", 2), States = [
                    new("counter:energy", "Energy", "counter", 2, null, "Energy counters belong to this player."),
                    new("speed", "Speed", "progress", 1, 4, speedDetail),
                    new("monarch", "Monarch", "status", null, null, "At this player's end step, they draw a card. Combat damage can transfer the monarchy.")
                ] }).ToImmutableArray() });
        });
        StepWhen(() => view.Match!.Players.All(p => view.CounterBadge(p.Id)?.Chips.Any(c => c.Id == "speed" && c.Text == "Speed 1/4") == true), 2, () => {
            Check(view.TextScale == 1.3f, "Public player-state inspection uses real 130% text settings");
            Check(view.Match!.Players.All(p => view.CounterBadge(p.Id) is { } badge
                && badge.Chips.Any(c => c.Id == "speed" && c.Text == "Speed 1/4")
                && badge.Chips.All(c => c.MeasuredWidth <= c.AvailableWidth)
                && badge.Bounds.Left >= 0 && badge.Bounds.Right <= 1600 && badge.Bounds.Top >= 0 && badge.Bounds.Bottom <= 900),
                "Every seat visibly paints the complete current progress total within its player-state ribbon");
            foreach (var hit in view.Hits.Where(h => h.Id.StartsWith("player-state:", StringComparison.Ordinal)))
                Check(view.HitAt(SmokePointer.Table(view, SmokePointer.Hit(view, hit)))?.Id == hit.Id,
                    hit.Id + " retains a reachable physical inspection target");
            CapturePlayerState("11-public-player-states");
            MoveHit("player-state:0:speed");
        }); Click();
        StepWhen(() => view.Overlay == "player" && view.CounterBadge(0)?.DetailStateId == "speed", 2, () => {
            Check(view.Overlay == "player" && view.CounterBadge(0) is { DetailCaption: "Speed 1/4", DetailText: speedDetail },
                "Clicking a progress chip paints its authoritative rules explanation and current total");
            Check(scenario.Replies.Count == repliesBefore, "Inspecting public player state submits no game command");
            CapturePlayerState("12-speed-rule-context");
            MoveHit("counter-details-next");
        }); Click();
        StepWhen(() => view.CounterBadge(0)?.DetailStateId == "counter:energy", 2, () => {
            Check(view.CounterBadge(0)?.DetailCaption == "Energy 2", "The same detail viewer browses counter totals without mechanic-specific UI");
            MoveHit("counter-details-next");
        }); Click();
        StepWhen(() => view.CounterBadge(0)?.DetailStateId == "monarch", 2, () => {
            Check(view.CounterBadge(0)?.DetailCaption == "Monarch" && scenario.Replies.Count == repliesBefore,
                "Public statuses remain accessible through whole-state pages without inventing numeric values");
            MoveHit("close-overlay");
        }); Click();
        Step(.1, () => Publish(scenario.State with { Players = scenario.State.Players.Select(p => p with {
            States = p.States.Select(s => s.Id == "speed" ? s with { Value = 4 } : s).ToImmutableArray()
        }).ToImmutableArray() }));
        StepWhen(() => view.Match!.Players.All(p => view.CounterBadge(p.Id)?.Chips.Any(c => c.Text == "Speed 4/4 MAX") == true), 2, () => {
            Check(view.Match!.Players.All(p => view.CounterBadge(p.Id)?.Chips.Any(c => c.Text == "Speed 4/4 MAX") == true),
                "An authoritative progress transition to its maximum updates every seat with an explicit MAX label");
            CapturePlayerState("13-player-state-maximum");
            Publish(scenario.State with { Players = scenario.State.Players.Select(p => p.Id != 0 ? p : p with { States = [
                new("future-track", "Momentum", "progress", 6, 6, "An unfamiliar public progress track supplied by the engine."),
                .. p.States.Where(s => s.Id != "speed"),
                .. Enumerable.Range(0, 6).Select(i => new PlayerState("future-status-" + i, "Public status " + (i + 1), "status", null, null, "An unfamiliar public status supplied by the engine."))
            ] }).ToImmutableArray() });
        });
        StepWhen(() => view.CounterBadge(0) is { HiddenTypes: > 0 } badge && badge.Chips.Any(c => c.Text == "Momentum 6/6 MAX"), 2, () => {
            Check(view.CounterBadge(0) is { HiddenTypes: > 0 } badge && badge.Chips.Any(c => c.Text == "Momentum 6/6 MAX")
                && view.Hits.Any(h => h.Id == "player-state-more:0"),
                "An unfamiliar engine-defined track renders its own maximum of six generically, with explicit access to crowded additional states");
            CapturePlayerState("14-generic-state-overflow");
            MoveHit("player-state-more:0");
        }); Click();
        StepWhen(() => view.Overlay == "player", 2, () => {
            Check(view.Hits.Any(h => h.Id == "counter-details-next") && scenario.Replies.Count == repliesBefore,
                "Overflow opens the current complete state list without sending a choice");
            MoveHit("close-overlay");
        }); Click();
        Step(.1, () => Publish(scenario.State with { Players = scenario.State.Players.Select(p => p with { States = [], Counters = ImmutableDictionary<string, int>.Empty }).ToImmutableArray() }));
        StepWhen(() => view.Match!.Players.All(p => view.CounterBadge(p.Id) == null), 2, () => {
            Check(view.Match!.Players.All(p => view.CounterBadge(p.Id) == null) && scenario.Replies.Count == repliesBefore,
                "Removing authoritative player state clears all stale ribbons and never submits an action");
            Move(new(10, 70));
        });
    }

    private void CapturePlayerState(string name)
    {
        view.CaptureWindow(Path.Combine(view.Profile, name + "-window.png"));
    }
}
