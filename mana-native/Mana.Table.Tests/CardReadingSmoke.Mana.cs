using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class CardReadingSmoke
{
    private void QueueManaPoolChecks()
    {
        int replies = 0;
        double? lossStarted = null;
        Dictionary<string, int> Pool(int colorless) => new() { ["W"] = 1, ["U"] = 2, ["B"] = 3, ["R"] = 4, ["G"] = 5, ["C"] = colorless };
        Step(0, () => {
            replies = scenario.Replies.Count; Move(new(10, 70));
            Publish(scenario.State with { Decision = new() { Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = true },
                Players = scenario.State.Players.Select(player => player with { Mana = Pool(2), Counters = new() { ["Poison"] = 3 }, States = [
                    new("speed", "Speed", "progress", 2, 4, "Public progress supplied by the game."),
                    new("counter:poison", "Poison", "counter", 3, null, "Public poison counters."),
                    new("monarch", "Monarch", "status", null, null, "The current monarch.")
                ] }).ToArray() });
        });
        Prepared(() => view.Match!.Players.All(player => view.ManaPoolBadge(player.Id)?.Parts.Length == 6), 2, () => {
            Check(view.TextScale == 1.3f && ManaGeometryReady(), "All four icon halos expose six exact readable color totals beside life and public state at130%");
            Check(view.Match!.Players.All(player => view.ManaPoolBadge(player.Id)!.Parts.Single(part => part.Key == "W").Amount == "1"),
                "One mana is always shown as an explicit count rather than an unlabeled color orb");
            Check(view.ManaPoolBadge(0)!.Parts.Single(part => part.Key == "C") is { Amount: "2", Delta: 2, AnimationStarted: not null },
                "An actual painted color indicator receives the authoritative gain pulse as soon as mana is added");
            view.CaptureWindow(Path.Combine(view.Profile, "15-mana-halos-window.png"));
            MoveHit("mana-pool:0:C");
        }, "Every current mana type must paint for every seat"); Click();
        Prepared(() => view.Overlay == "mana" && view.ManaPoolDetail?.Parts.Length == 6, 2, () => {
            Check(view.ManaPoolDetail is { Total: 17 } detail && detail.Parts.Single(part => part.Key == "C") is { Name: "Colorless", Amount: "2" }
                && detail.Parts.All(part => part.MeasuredWidth <= part.AvailableWidth) && scenario.Replies.Count == replies,
                "Inspecting an icon indicator shows exact current colorless mana and all six pool values without submitting a game action");
            MoveHit("close-overlay");
        }, "The read-only mana detail must show the current complete pool"); Click();
        Step(0, () => Publish(scenario.State with { Decision = new() { Kind = "input", Intent = DecisionIntent.Mana, OkEnabled = true },
            Players = scenario.State.Players.Select(player => player with { Mana = Pool(1) }).ToArray() }));
        Prepared(() => view.ManaPoolBadge(0)?.Parts.SingleOrDefault(part => part.Key == "C") is { Amount: "1", Delta: -1 }, 2, () => {
            lossStarted = view.ManaPoolBadge(0)!.Parts.Single(part => part.Key == "C").AnimationStarted;
            Check(ManaGeometryReady() && view.Match?.Decision?.Intent == DecisionIntent.Mana,
                "Paying a cost keeps every current pool visible and immediately shows the exact remaining mana with a loss pulse");
            view.Accept(scenario.State);
        }, "A payment snapshot must update pool indicators without waiting for its animation");
        Step(.04, () => {
            Check(view.ManaPoolBadge(0)!.Parts.Single(part => part.Key == "C").AnimationStarted == lossStarted,
                "A repeated snapshot does not restart a mana transition");
            Publish(scenario.State with { PhaseKey = "COMBAT_BEGIN", Decision = new() { Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = true },
                Players = scenario.State.Players.Select(player => player with { Mana = Pool(0) }).ToArray() });
        });
        Prepared(() => view.ManaPoolBadge(0)?.Parts.SingleOrDefault(part => part.Key == "C") is { Amount: "0", Delta: -1, Opacity: < 1 }, 2, () =>
            Check(view.ManaPoolBadge(0)!.Parts.Single(part => part.Key == "C") is { Amount: "0", Opacity: > 0 and < 1 },
                "A disappearing color fades its authoritative zero rather than leaving an old positive amount"),
            "A zeroed color needs its brief loss feedback");
        Prepared(() => view.ManaPoolBadge(0)?.Parts.All(part => part.Key != "C") == true, 2, () => {
            Check(view.ManaPoolBadge(0)!.Total == 15, "After its fade, only the removed color disappears; retained colors remain exact");
            Publish(scenario.State with { Combat = new(Enumerable.Range(0, 10).Select(index => new Attack("incoming-fixture-" + index, 0, [], [])).ToArray(), [], null),
                Players = scenario.State.Players.Select(player => player with {
                Mana = new Dictionary<string, int>(new[] { "W", "U", "B", "R", "G", "C" }.ToDictionary(symbol => symbol, _ => int.MaxValue))
            }).ToArray() });
        }, "The loss indicator must expire without changing retained mana");
        Prepared(() => view.Match!.Players.All(player => view.ManaPoolBadge(player.Id)?.Total == 6L * int.MaxValue), 2, () => {
            Check(ManaGeometryReady() && view.Match!.Players.All(player => view.ManaPoolBadge(player.Id)!.Parts.All(part => part.Amount == int.MaxValue.ToString())),
                "Every large per-color count stays whole around all four icons and totals cannot overflow");
            view.CaptureWindow(Path.Combine(view.Profile, "16-mana-large-counts-window.png"));
            MoveHit("settings");
        }, "Large authoritative color amounts must remain readable"); Click();
        Prepared(() => view.Hits.Any(hit => hit.Id == "motion"), 2, () => MoveHit("motion"), "Reduced motion is changed through the real setting"); Click();
        Step(0, () => MoveHit("close-overlay")); Click();
        Step(0, () => Publish(scenario.State with { Players = scenario.State.Players.Select(player => player with { Mana = [] }).ToArray() }));
        Prepared(() => view.Match!.Players.All(player => view.ManaPoolBadge(player.Id)?.Parts.Length == 0), 2, () => {
            Check(PlayPreferences.Load(view.Profile).ReducedMotion && view.ManaPoolBadge(0) is { Total: 0, Parts.Length: 0 }
                && view.Hits.Any(hit => hit.Id == "mana-pool:0") && scenario.Replies.Count == replies,
                "Reduced motion immediately clears zeroed colors with an explicit own MANA0 readout and no lingering pulse or gameplay command");
        }, "Reduced motion must show current zero immediately without loss ghosts");
    }

    private bool ManaGeometryReady()
    {
        var all = view.Match!.Players.SelectMany(player => view.ManaPoolBadge(player.Id)?.Parts ?? []).ToArray();
        foreach (var player in view.Match.Players) {
            var badge = view.ManaPoolBadge(player.Id);
            if (badge == null || badge.Parts.Any(part => part.MeasuredWidth > part.AvailableWidth)) return false;
            int seat = player.Id == view.Match.ViewerId ? 3 : TableLayout.SeatIndex(view.Match, player.Id);
            var hero = TableLayout.Hero(seat); int radius = seat == 3 ? 51 : 45;
            var life = new Rectangle((int)hero.X - radius, (int)hero.Y - radius, radius * 2, radius * 2);
            foreach (var part in badge.Parts) {
                if (part.Bounds.Left < 0 || part.Bounds.Right > 1600 || part.Bounds.Top < 60 || part.Bounds.Bottom >= 790
                    || part.Bounds.Intersects(life) || view.CounterBadge(player.Id) is { } state && part.Bounds.Intersects(state.Bounds)) return false;
                if (all.Any(other => !ReferenceEquals(part, other) && part.Bounds.Intersects(other.Bounds))) return false;
                var hit = view.Hits.SingleOrDefault(hit => hit.Id == "mana-pool:" + player.Id + ":" + part.Key);
                if (hit == null || view.HitAt(SmokePointer.Table(view, SmokePointer.Hit(view, hit)))?.Id != hit.Id) return false;
                foreach (var card in view.Match.Players.SelectMany(owner => owner.Zone("Battlefield").Cards))
                    if (view.DisplayedPose(card.VisualId) is { } pose && part.Bounds.Intersects(pose.Bounds)) return false;
                if (view.Hits.Where(control => control.Id is "confirm" or "cancel" or "auto" or "settings" or "concede")
                    .Any(control => part.Bounds.Intersects(control.Bounds))) return false;
            }
        }
        return true;
    }
}
