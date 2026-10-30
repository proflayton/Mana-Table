using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Interaction;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckManaPoolPaymentClarity(string profile, Action<bool, string> check,
        List<object> trace, List<object> performance)
    {
        const string excluded = "Not usable for the remaining cost";
        var original = ManaPaymentScenario.Snapshot(2) with { DecisionOwnerId = 0 };
        var payment = (PaymentPayload)original.Decision!.Payload;
        GameSnapshot Payment(string bill, string color, int amount, ImmutableArray<string>? usable, int? payer = 0) => original with {
            Players = original.Players.Select(player => player.Id == 0
                ? player with { Mana = ImmutableDictionary<string, int>.Empty.Add(color, amount) } : player).ToImmutableArray(),
            Decision = original.Decision with { Payload = payment with {
                UnpaidManaParts = [new(bill, 1)], PayerId = payer, UsablePoolManaColors = usable, FundingContext = null
            } }
        };
        using var run = new Runner(profile, "floating-payment-clarity", original, trace, performance);
        run.Settle("unspent-colorless-before-hover");
        check(!Text(run.Frame).Contains(excluded), "An unusable floating pip does not add a persistent warning to the battlefield");
        void Hover(string color, int seat = 0)
        {
            var point = run.PointFor(run.Hit($"mana-pool:{seat}:{color}"));
            run.Input(point, label: "hover-floating-" + color);
            run.Tick("current-floating-hover");
        }
        void Show(GameSnapshot state, string color, string label, int seat = 0)
        {
            run.Present(state); run.Settle(label); Hover(color, seat);
        }
        Hover("C");
        var oldExcluded = run.Hit("mana-pool:0:C");
        check(Text(run.Frame).Contains("Colorless mana · 1") && Text(run.Frame).Contains(excluded)
            && oldExcluded.Intent is MatchIntent.OpenManaPool && run.Engine.Replies.Count == 0,
            "Current own colorless mana explains its exact quantity and exclusion from the remaining blue cost without sending a command");
        var metrics = new TextMetrics();
        var captions = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>()
            .Where(text => text.Value is "Colorless mana · 1" or excluded).ToArray();
        check(captions.Length == 2 && run.View.TextScale == 1.3f && captions.All(text => {
                var size = metrics.TextSize(text.Value!, text.Size, text.Width, text.MaxLines, text.Bold, run.Frame.Drawing.TextScale);
                var bounds = new Rectangle(text.X, text.Y, size.X, size.Y);
                return text.X >= 0 && text.Y >= 0 && bounds.Right <= 1600 && bounds.Bottom <= run.View.DesignHeight
                    && !bounds.Intersects(run.View.ManaPoolBadge(0)!.Bounds)
                    && (text.MaxLines > 1 || metrics.TextWidth(text.Value!, text.Size, text.Bold, run.Frame.Drawing.TextScale) <= text.Width);
            }), "Both complete hover lines fit at 130% text and stay separate from the portrait's physical mana pips");
        check(run.View.ManaSources.Any(source => source.SourceId == ManaPaymentScenario.Dual && !source.OutputBounds.IsEmpty)
            && ManaPaymentSmoke.PoolCaptionClearsPayment(run.View, "Colorless mana · 1", excluded, out _),
            "The entire hover backing clears the current source faces and output glyphs as well as the current payment bill and its source card");
        run.ClickHit(oldExcluded.Id); run.Settle("inspect-unusable-pool");
        check(run.View.Overlay == "mana" && run.View.ManaPoolDetail is { Total: 1 }
            && run.Engine.Replies.Count == 0 && run.View.Match!.Decision!.Id == run.Engine.State.Decision!.Id,
            "Clicking the excluded pip preserves read-only pool inspection and the exact unanswered payment");
        run.ClickHit("close-overlay"); run.Settle("return-to-current-payment");

        // Retire a pressed inspection target while the same pip becomes usable.
        oldExcluded = run.Hit("mana-pool:0:C");
        var point = run.PointFor(oldExcluded);
        run.Input(point, left: ButtonState.Pressed, label: "press-old-excluded-pip");
        run.Present(Payment("{1}", "C", 1, ["C"]));
        run.Input(point, label: "release-after-bill-change"); run.Settle("generic-now-uses-colorless"); Hover("C");
        check(!run.View.CurrentHit(oldExcluded) && run.View.Overlay.Length == 0 && run.Engine.Replies.Count == 0
            && !Text(run.Frame).Contains(excluded) && Text(run.Frame).Contains("Spend")
            && run.Hit("mana-pool:0:C").Intent is MatchIntent.SpendFloatingMana,
            "A changed bill revokes the old inspection press and replaces its cue with the current usable-pip route");
        var oldSpend = run.Hit("mana-pool:0:C"); point = run.PointFor(oldSpend);
        run.Input(point, left: ButtonState.Pressed, label: "press-current-usable-pip");
        run.Present(original); run.Input(point, label: "release-after-usable-color-revoked");
        run.Settle("blue-cost-restored"); Hover("C");
        check(!run.View.CurrentHit(oldSpend) && run.Engine.Replies.Count == 0 && Text(run.Frame).Contains(excluded)
            && run.Hit("mana-pool:0:C").Intent is MatchIntent.OpenManaPool,
            "An accepted observation that excludes a previously usable color prevents a stale pressed pip from spending it");

        Show(Payment("{1}", "U", 2, ["U"]), "U", "blue-can-pay-generic");
        check(Text(run.Frame).Contains("Blue mana · 2") && !Text(run.Frame).Contains(excluded)
            && run.Hit("mana-pool:0:U").Intent is MatchIntent.SpendFloatingMana,
            "Blue mana remains usable for an authoritative generic payment without being confused with an exact colorless requirement");
        foreach (string bill in new[] { "{C}", "{1}", "{U}" }) {
            Show(Payment(bill, "U", 2, []), "U", "known-exclusion-" + bill);
            check(Text(run.Frame).Contains("Blue mana · 2") && Text(run.Frame).Contains(excluded)
                && run.Hit("mana-pool:0:U").Intent is MatchIntent.OpenManaPool,
                "The actual usable-color facts govern exclusions even for generic or matching printed symbols, including restricted pool mana: " + bill);
        }
        Show(Payment("{C}", "C", 1, ["C"]), "C", "exact-colorless-usable");
        check(!Text(run.Frame).Contains(excluded) && run.Hit("mana-pool:0:C").Intent is MatchIntent.SpendFloatingMana,
            "Actual colorless mana remains usable for the current exact colorless bill");

        foreach (var state in new[] {
            Payment("{U}", "C", 1, null), Payment("{U}", "C", 1, [], null),
            Payment("{U}", "C", 1, [], 1), original with { DecisionOwnerId = 1 },
            original with { Decision = new("", new(), new PriorityPayload(new("Pass", true))) }
        }) {
            Show(state, "C", "unknown-or-foreign-payment");
            check(!Text(run.Frame).Contains(excluded) && run.Hit("mana-pool:0:C").Intent is MatchIntent.OpenManaPool,
                "Legacy, foreign-payer, foreign-owner and nonpayment snapshots cannot turn missing current applicability into a negative claim");
        }
        Show(Payment("{1}", "U", 2, ["U"], null), "U", "legacy-positive-route");
        check(!Text(run.Frame).Contains(excluded) && run.Hit("mana-pool:0:U").Intent is MatchIntent.SpendFloatingMana,
            "The explanation preserves the existing legacy positive Spend route without inventing a negative payer fact");
        var otherPool = original with { Players = original.Players.Select(player => player.Id == 1
            ? player with { Mana = ImmutableDictionary<string, int>.Empty.Add("C", 3) } : player).ToImmutableArray() };
        Show(otherPool, "C", "another-seats-pool", 1);
        check(Text(run.Frame).Contains("Colorless mana · 3") && !Text(run.Frame).Contains(excluded)
            && run.Hit("mana-pool:1:C").Intent is MatchIntent.OpenManaPool,
            "Another seat's public mana remains inspectable without claiming it can pay the viewer's current bill");
        Show(original, "C", "own-pip-before-empty");
        var retired = run.Hit("mana-pool:0:C");
        run.Present(Payment("{U}", "C", 0, [])); run.Settle("pool-drained");
        check(!Text(run.Frame).Contains(excluded) && !Text(run.Frame).Contains("Colorless mana · 1")
            && !run.View.CurrentHit(retired) && !run.Frame.Hits.Any(hit => hit.Id == "mana-pool:0:C")
            && run.Engine.Replies.Count == 0,
            "A drained pool removes the old positive quantity, hover explanation and hit without submitting a payment");
    }
}
