using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;
using Mana.Scenarios;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckManaReadingLifecycle(string profile, Action<bool, string> check,
        List<object> trace, List<object> performance)
    {
        var initial = ManaPaymentScenario.Snapshot(0) with { DecisionOwnerId = 0, PriorityPlayerId = 0 };
        var island = new Card { VisualId = ManaPaymentScenario.Island, CombatId = ManaPaymentScenario.Island,
            Name = "Island", Type = "Basic Land — Island", Text = "{T}: Add {U}." };
        initial = initial with { Players = initial.Players.Select(player => player with {
            Mana = ImmutableDictionary<string, int>.Empty.Add("C", player.Id == 0 ? 4 : 9),
            Zones = player.Zones.Select(zone => player.Id == 0 && zone.Name == "Battlefield"
                ? zone with { Count = 1, Cards = [island] } : zone).ToImmutableArray()
        }).ToImmutableArray() };
        void SourceReader(Runner run) => run.Engine.SourceActionRead = (game, decision, key) =>
            new(game, decision, key, ManaPaymentScenario.Spell, [new("current-cast", "cast", "Cast Faerie Vandal")]) {
                Revision = run.View.Match!.Revision
            };
        void Accept(Runner run, GameSnapshot next)
        {
            // The fixture owner publishes exact accepted facts without the
            // scenario engine's deliberate per-Present decision renewal.
            next = next with { Revision = run.View.Match!.Revision + 1 };
            run.View.Post(() => run.View.Accept(next)); run.Settle("accepted-mana-reading-facts");
        }
        using (var run = new Runner(profile, "current-mana-reading-values", initial, trace, performance)) {
            run.View.SetTableStudy(true); SourceReader(run); run.Tick("current-mana-reading-ready");
            run.ClickCard(ManaPaymentScenario.Spell);
            run.Until(() => run.Frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Focused)
                && run.Frame.Hits.Any(hit => hit.Id == "read-card-action"), "current-mana-reading-pin");
            var key = run.Frame.Cards.Single(card => card.Id == ManaPaymentScenario.Spell).Card.Key;
            string decision = run.View.Match!.Decision!.Id;
            run.ClickHit("mana-pool:0:C"); run.Settle("current-mana-reading-open");
            foreach (var amounts in new[] {
                ImmutableDictionary<string, int>.Empty.Add("C", 1024).Add("U", 3).Add("W", 1).Add("unreported", 7),
                ImmutableDictionary<string, int>.Empty.Add("C", 2), ImmutableDictionary<string, int>.Empty
            }) {
                var next = run.View.Match! with { Players = run.View.Match!.Players.Select(player => player.Id == 0
                    ? player with { Mana = amounts } : player).ToImmutableArray() };
                Accept(run, next);
                var expected = Mana.Magic.PublicResources.Mana(next.Viewer!, includeEmpty: true);
                trace.Add(new { scenario = "current-mana-reading-values", amounts, frame = run.Frame.Observe(),
                    detail = run.View.ManaPoolDetail is { } detail ? detail with { Parts = detail.Parts.ToArray() } : null,
                    journey = run.View.ManaJourney, replies = run.Engine.Replies.Count });
                check(run.View.Overlay == "mana" && run.View.Match!.Decision!.Id == decision
                    && run.View.ManaPoolDetail is { } current && current.Total == Mana.Magic.PublicResources.ManaTotal(next.Viewer!)
                    && current.Parts.Select(part => (part.Key, part.Name, part.Amount))
                        .SequenceEqual(expected.Select(part => (part.Key, part.Name, part.Amount.ToString())))
                    && run.Frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Card.Key == key && card.Focused)
                    && run.View.ManaJourney is { Payment: false } && ManaPaymentSmoke.PoolReadingClearsContext(run.View)
                    && ManaPaymentSmoke.PoolReadingIsolated(run.View) && run.Engine.Replies.Count == 0,
                    "Same-decision mana reading immediately uses the newest full names and quantities, including 1024, literal unknown keys, color disappearance and an empty actual pool");
            }
            Accept(run, run.View.Match! with { Players = run.View.Match!.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with {
                    Cards = zone.Cards.Select(card => card with { FaceDown = true }).ToImmutableArray()
                }).ToImmutableArray()
            }).ToImmutableArray() });
            check(run.View.Overlay == "mana" && !run.Frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Focused)
                && run.View.ManaJourney == null && run.Engine.Replies.Count == 0,
                "A source becoming private retires its reader and bill during same-decision mana reading without retaining a face or action handle");
            run.ClickHit("close-overlay"); run.Settle("close-after-source-private");
            check(!run.Frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Focused) && run.View.ManaJourney == null,
                "Closing after privacy loss cannot restore the former pinned reader or its cost");
            run.ClickHit("mana-pool:1:C"); run.Settle("read-remote-seat-mana");
            check(run.View.ManaPoolDetail is { Total: 9 } && ManaPaymentSmoke.PoolReadingClearsContext(run.View, 1)
                && ManaPaymentSmoke.PoolReadingIsolated(run.View) && run.Engine.Replies.Count == 0,
                "Reading another seat's public mana expands that seat's exact symbols beside its own portrait without local payment authority");
        }
        var retirements = new (string Name, Func<GameSnapshot, GameSnapshot> Change)[] {
            ("decision", state => state with { Decision = state.Decision! with { Id = state.Decision.Id + "-next" } }),
            ("game", state => state with { Id = state.Id + "-next" }),
            ("viewer", state => state with { ViewerId = 1 }),
            ("seat-removed", state => state with { Players = state.Players.Where(player => player.Id != 1).ToImmutableArray() }),
            ("seat-eliminated", state => state with { Players = state.Players.Select(player => player.Id == 1
                ? player with { Eliminated = true } : player).ToImmutableArray() }),
            ("viewer-eliminated", state => state with { Players = state.Players.Select(player => player.Id == 0
                ? player with { Eliminated = true } : player).ToImmutableArray() }),
            ("result", state => state with { Status = "finished", Result = "Victory", Decision = null }),
            ("error", state => state with { Status = "error", Error = "Controlled terminal interruption", Decision = null })
        };
        foreach (var retirement in retirements) {
            using var run = new Runner(profile, "mana-reading-retires-" + retirement.Name, initial, trace, performance);
            SourceReader(run); run.Tick("retirement-ready"); run.ClickCard(ManaPaymentScenario.Spell);
            run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"), "retirement-current-reader");
            var old = run.Hit("read-card-action");
            string currentKey = run.Frame.Cards.Single(card => card.Id == ManaPaymentScenario.Spell && card.Focused).Card.Key;
            run.ClickHit("mana-pool:1:C"); run.Settle("remote-reading-before-retirement");
            var before = run.View.Match!;
            var next = retirement.Change(before);
            Accept(run, next);
            check(run.View.Overlay != "mana" && run.View.ManaPoolDetail == null && run.Engine.Replies.Count == 0,
                "An accepted " + retirement.Name + " change retires mana reading before composition without submitting an action");
            if (next.Id != before.Id || next.ViewerId != before.ViewerId || next.Decision?.Id != before.Decision?.Id)
                check(!run.View.CurrentHit(old) && !run.View.Activate(old) && run.Engine.Replies.Count == 0,
                    "A changed game, viewer or decision rejects the prior source-action scope rather than reviving it after reading closes");
            if (retirement.Name is "seat-removed" or "seat-eliminated")
                check(run.Frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Card.Key == currentKey && card.Focused)
                    && run.View.ManaJourney is { Payment: false } && run.Engine.Replies.Count == 0,
                    "Losing the inspected remote seat closes its reading while preserving the still-current owned source and bill");
        }
        CheckManaReadingRecovery(profile, initial, check, trace);
    }

    private static void CheckManaReadingRecovery(string profile, GameSnapshot state, Action<bool, string> check, List<object> trace)
    {
        string directory = Path.Combine(profile, "mana-reading-recovery"); Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(directory);
        var engine = new InteractionScenarioEngine(); engine.Present(state);
        using var run = new PresentationRun(TestTableServices.For(engine), directory, engine.State, new(1600, 900),
            ObservationDeliveryMode.Polled, allowErrors: true);
        void Tick(Point point, ButtonState left = ButtonState.Released, bool active = true) =>
            run.Tick(TimeSpan.FromSeconds(1d / 60), new(point.X, point.Y, 0, left, ButtonState.Released,
                ButtonState.Released, ButtonState.Released, ButtonState.Released), active: active);
        for (int index = 0; index < 4; index++) Tick(new(20, 60));
        var pip = run.Frame.Hits.Single(hit => hit.Id == "mana-pool:0:C").Bounds.Center;
        Tick(pip, ButtonState.Pressed); Tick(pip); Tick(pip);
        check(run.View.Overlay == "mana", "The recovery continuation begins from the real current mana inspection click");
        Tick(pip, ButtonState.Pressed); run.Deactivate(); Tick(pip, active: false); Tick(pip); Tick(new(20, 60));
        check(run.View.Overlay == "mana" && engine.Replies.Count == 0,
            "Foreground loss discards a pressed mana-reading gesture without submitting a command on reactivation");
        engine.ObserveOverride = () => Task.FromException<GameSnapshot?>(new IOException("Controlled mana-reading observation failure"));
        for (int index = 0; index < 180 && !run.View.NeedsRecovery; index++) { Tick(new(20, 60)); Thread.Yield(); }
        Tick(new(20, 60));
        trace.Add(new { scenario = "mana-reading-recovery", run.View.NeedsRecovery, run.View.Overlay,
            frame = run.Frame.Observe(), replies = engine.Replies.Count });
        check(run.View.NeedsRecovery && run.View.Overlay != "mana" && run.View.ManaPoolDetail == null && engine.Replies.Count == 0,
            "A real failed observation closes mana reading before the recovery frame without spending, passing or reviving a source");
    }
}
