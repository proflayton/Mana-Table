using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Scenarios;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckRulesReader(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        var initial = CounterPaymentScenario.Snapshot(4);
        using var run = new Runner(profile, "full-art-rules-reading", initial, trace, performance);
        run.Engine.SourceActionRead = (game, decision, key) => new(game, decision, key, CounterPaymentScenario.MareId,
            [new("readable-counter-draw", "activate", CounterPaymentScenario.MareAbility)]);
        run.Tick("source-reference-with-empty-stack"); run.View.SetTableStudy(true); run.Settle("shared-table-source");
        check(run.Frame.Cards.All(card => card.Zone != "Stack"),
            "Priority context never manufactures a second source card on an empty stack");
        var actual = run.Frame.Cards.Single(card => card.Id == CounterPaymentScenario.MareId);
        var center = actual.Display.Center.ToPoint();
        check(run.View.HitAt(center)?.Card?.VisualId == actual.Id,
            "The visible center of Loch Mare reaches the actual battlefield occurrence");
        run.Input(center, left: ButtonState.Pressed, label: "press-visible-mare-center");
        run.Input(center, label: "release-visible-mare-center"); run.Settle("mare-opened-from-center");
        run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"), "current-source-action");
        check(run.Frame.Cards.Single(card => card.Id == actual.Id).Focused && run.Engine.Replies.Count == 0,
            "Clicking the visible permanent center opens its reader without committing an activation");
        var reader = run.Frame.Cards.Single(card => card.Id == actual.Id).Display.Bounds;
        var ability = run.Hit("read-card-action").Bounds;
        check(ability.Left >= reader.Right && ability.Left - reader.Right < 32 && ability.Bottom <= reader.Bottom,
            "An inspected counter-bearing source keeps its exact offered ability in the adjacent live-state column");
        string prompt = run.View.Match!.Decision!.Id;
        run.ClickHit("read-card-rules"); run.Settle("mare-rules-face");
        check(Text(run.Frame).Contains("Remove") && Text(run.Frame).Contains("counter")
                && Text(run.Frame).Contains("1/2") && Text(run.Frame).Contains("3 -1/-1 counters"),
            "The rules face renders engine text, symbols and current P/T alongside counter state");
        check(run.Engine.Replies.Count == 0 && run.View.Selections.Count == 0 && run.View.Match.Decision!.Id == prompt,
            "Flipping to rules sends no command and neither chooses a counter nor answers the active decision");
        run.Input(new(20, 60), keys: new(Keys.R), label: "flip-to-artwork-with-keyboard");
        run.Input(new(20, 60)); run.Settle("artwork-retained");
        check(Text(run.Frame).Contains("Rules") && !Text(run.Frame).Contains("Artwork") && run.Engine.Replies.Count == 0,
            "R returns to the original artwork using the same read-only occurrence");

        run.ClickHit("read-card-rules"); run.Settle("current-rules-again");
        run.Present(run.Engine.State with { Players = run.Engine.State.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Cards = zone.Cards.Select(card => card with { Power = 2, Toughness = 3,
                    Counters = card.Counters.SetItem("-1/-1", 2) }).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray() }); run.Settle("rules-face-current-characteristics");
        check(Text(run.Frame).Contains("Artwork") && Text(run.Frame).Contains("2/3") && Text(run.Frame).Contains("2 -1/-1 counters"),
            "An open rules face survives a new prompt while showing current engine counters and stats");
        var stale = run.Hit("read-card-rules"); var staleAt = run.PointFor(stale);
        run.Input(staleAt, left: ButtonState.Pressed, label: "press-before-disclosure-revoked");
        run.Present(run.Engine.State with { Players = run.Engine.State.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Cards = zone.Cards.Select(card => new Card { VisualId = card.VisualId, CombatId = card.CombatId, FaceDown = true }).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray() });
        run.Input(staleAt, label: "release-after-disclosure-revoked"); run.Settle("concealed-card");
        check(run.Frame.Cards.All(card => !card.Focused) && run.Frame.Hits.All(hit => hit.Id != "read-card-rules")
                && !Text(run.Frame).Contains("Remove") && !run.View.CurrentHit(stale) && run.Engine.Replies.Count == 0,
            "Losing disclosure removes rules text and rejects a retained flip hit without revealing a reverse face");
        run.Input(new(20, 60), keys: new(Keys.H), label: "spread-hand-with-keyboard");
        run.Input(new(20, 60)); run.Settle("hand-spread");
        check(run.Frame.Scene?.Spread?.Target == Mana.Renderer.CardCollectionTarget.Hand && run.Engine.Replies.Count == 0,
            "H unfolds actual hand occurrences without the rejected fan icon or a game command");
        run.Input(new(20, 60), keys: new(Keys.Escape)); run.Input(new(20, 60)); run.Settle("fold-hand");
        var hand = run.Frame.Cards.Last(card => card.Zone == "Hand");
        var handPoint = run.PointFor(run.Hit(hand.Hit.Id));
        run.Input(handPoint, middle: ButtonState.Pressed, label: "middle-click-actual-hand-card");
        run.Input(handPoint); run.Settle("hand-object-spread");
        check(run.Frame.Scene?.Spread?.Target == Mana.Renderer.CardCollectionTarget.Hand && run.Engine.Replies.Count == 0,
            "Middle-clicking the actual hand opens its read-only collection without casting or selecting a card");
    }
}
