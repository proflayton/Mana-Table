using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Planning;
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
        CheckManaPoolPreviewClarity(profile, check, trace, performance);
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

    private static void CheckManaPoolPreviewClarity(string profile, Action<bool, string> check,
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
        var funding = new ManaFundingContext(SemanticKnowledge.Known, initial.Viewer!.Mana,
            [new(island.VisualId, [new([new(ActionCostKind.Tap, 1, true, "")],
                new(SemanticKnowledge.Known, [new(ImmutableDictionary<string, int>.Empty.Add("U", 1))]), SemanticKnowledge.Known)])]);
        var funded = new ManaCostPreview([new(ActionCostKind.Mana, 1, false, "") { ManaSymbols = ["{U}"] }], SemanticKnowledge.Known, funding);
        ManaCostPreview? preview = funded;
        bool ambiguous = false, stale = false;
        using var run = new Runner(profile, "floating-mana-cost-preview", initial, trace, performance);
        run.Engine.SourceActionRead = (game, decision, key) => {
            var state = run.Engine.State;
            var source = state.Viewer!.Zone("Hand").Cards.Single(card => card.Key == key);
            return new(game, decision, key, source.VisualId,
                ambiguous ? [new("cast-preview", "cast", "Cast Faerie Vandal") { CostPreview = preview }, new("other-preview", "cast", "Another mode") { CostPreview = preview }]
                    : [new("cast-preview", "cast", "Cast Faerie Vandal") { CostPreview = preview }]) { Revision = state.Revision + (stale ? 1 : 0) };
        };
        DrawCommand.Ring[] Rings(int player, string color, float width = 1) => run.View.ManaPoolBadge(player)?.Parts.FirstOrDefault(part => part.Key == color) is { } pip
            ? run.Frame.Drawing.Commands.OfType<DrawCommand.Ring>().Where(ring => ring.Radius == 14 && ring.Width == width
                && ring.Center == new Microsoft.Xna.Framework.Vector2(pip.Bounds.X + 11, pip.Bounds.Center.Y)).ToArray() : [];
        void Ready(string label) => run.Until(() => run.View.ManaJourney?.FundingStatus == ManaFundingStatus.Funded, label);
        void Refresh(ManaCostPreview? next, string label)
        {
            run.Input(new(20, 60), label: label + "-leave-action");
            preview = next; run.Present(initial); run.Tick(label + "-accepted");
            check(Rings(0, "C").Length == 0, "Replacing the exact source-query scope withdraws the previous pool estimate before its new read: " + label);
            if (!run.Frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Focused)) run.ClickCard(ManaPaymentScenario.Spell);
            run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"), label + "-read");
        }
        run.Tick("floating-preview-initial");
        run.Input(run.Frame.Cards.Single(card => card.Id == ManaPaymentScenario.Spell).Display.Center.ToPoint(), label: "hover-hand-cost");
        Ready("mixed-pool-and-island-preview");
        var pool = run.View.ManaPoolBadge(0)!;
        trace.Add(new { scenario = "floating-mana-cost-preview-facts", label = "mixed-pool-and-island-preview",
            journey = run.View.ManaJourney, sources = run.View.ManaSources.ToArray(), pool = pool with { Parts = pool.Parts.ToArray() },
            poolIntent = run.Hit("mana-pool:0:C").Intent.GetType().Name, quietPoolRings = Rings(0, "C").Length,
            replies = run.Engine.Replies.Count, islandTapped = run.Engine.State.Viewer!.Zone("Battlefield").Cards.Single().Tapped });
        check(Rings(0, "C").Length == 1 && Rings(1, "C").Length == 0
            && run.View.ManaJourney!.Symbols.SequenceEqual(["{1}", "{U}"])
            && run.View.ManaSources is [{ SourceId: ManaPaymentScenario.Island }]
            && run.View.ManaPoolBadge(0) is { Total: 4, Parts: [{ Key: "C", Amount: "4" }] }
            && run.Hit("mana-pool:0:C").Intent is MatchIntent.OpenManaPool
            && run.Engine.Replies.Count == 0 && !run.Engine.State.Viewer!.Zone("Battlefield").Cards.Single().Tapped,
            "Hovering a funded {1}{U} cost connects the actual C4 portrait pip and one Island without changing counts, taps, inspection intent or commands");
        run.Input(new(20, 60), label: "leave-cost-source"); run.Settle("cost-preview-withdrawn");
        check(run.View.ManaJourney == null && Rings(0, "C").Length == 0 && run.Engine.Replies.Count == 0,
            "Leaving the unpinned hand source withdraws its pool estimate in the current frame");
        run.ClickCard(ManaPaymentScenario.Spell); Ready("read-floating-cost");
        run.ClickHit("mana-pool:0:C"); run.Settle("inspect-estimated-floating-pool");
        check(run.View.ManaPoolDetail is { Total: 4 } && Rings(0, "C").Length == 0 && run.Engine.Replies.Count == 0,
            "A suggested pip opens the existing full actual-pool inspection without spending or retaining the preview over it");
        run.ClickHit("close-overlay"); run.ClickCard(ManaPaymentScenario.Spell); Ready("return-to-funded-preview");

        Refresh(funded with { Costs = [new(ActionCostKind.Mana, 1, false, "") { ManaSymbols = ["{C}"] }],
            Funding = funding with { Sources = [] } }, "pool-only-exact-colorless-and-generic");
        Ready("pool-only-witness");
        check(Rings(0, "C").Length == 1 && run.View.ManaSources.Count == 0 && run.View.ManaPoolBadge(0)!.Parts.Single().Amount == "4"
            && run.View.ManaJourney!.Advice == "Suggested mana · estimate",
            "A pool-only {1}{C} witness accents one actual C4 pip once even when the plan consumes that color in separate steps");
        initial = initial with { Players = initial.Players.Select(player => player.Id == 0
            ? player with { Mana = player.Mana.Add("R", 7) } : player).ToImmutableArray() };
        Refresh(funded with { Costs = [new(ActionCostKind.Mana, 0, false, "") { ManaSymbols = ["{C}", "{U}"] }],
            Funding = funding with { Pool = initial.Viewer!.Mana } }, "unrelated-positive-pool-color");
        Ready("mixed-exact-colorless-witness");
        check(Rings(0, "C").Length == 1 && Rings(0, "R").Length == 0 && Rings(1, "C").Length == 0
            && run.View.ManaPoolBadge(0) is { Total: 11 } && run.Engine.Replies.Count == 0,
            "Only the plan's exact own pool color joins the Island witness; unrelated positive colors and another seat's pips remain unaccented");
        initial = initial with { Players = initial.Players.Select(player => player.Id == 0
            ? player with { Mana = player.Mana.Remove("R") } : player).ToImmutableArray() };

        foreach (var unknown in new ManaCostPreview?[] {
            funded with { Funding = funding with { Knowledge = SemanticKnowledge.Unknown, Sources = [] } },
            funded with { Funding = funding with { Sources = [] } },
            funded with { ManaCostKnowledge = SemanticKnowledge.Unsupported },
            funded with { Costs = [new(ActionCostKind.Mana, null, false, "") { ManaSymbols = ["{X}"] }] },
            funded with { Costs = [new(ActionCostKind.Mana, 1025, false, "")] }, null
        }) {
            Refresh(unknown, "uncertain-or-unreported-cost");
            check(Rings(0, "C").Length == 0 && run.View.ManaJourney?.FundingStatus != ManaFundingStatus.Funded
                && run.Engine.Replies.Count == 0, "Unknown, shortfall, unsupported, X, bounded-out and absent previews do not reuse a floating-mana suggestion");
        }
        ambiguous = true; Refresh(funded, "ambiguous-casting-modes");
        check(Rings(0, "C").Length == 0 && run.View.ManaJourney?.FundingStatus == null,
            "An unchosen casting mode keeps the printed-cost fallback without borrowing a funded pool witness");
        ambiguous = false; stale = true; preview = funded; run.Present(initial);
        run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "source-actions-retry"), "stale-source-reply");
        check(Rings(0, "C").Length == 0 && run.Engine.Replies.Count == 0,
            "A source reply with the wrong revision cannot restore a pool witness");
        stale = false; Refresh(funded, "fresh-source-reply"); Ready("funded-again");
        foreach (bool hidden in new[] { false, true }) {
            run.Present(initial with { Players = initial.Players.Select(player => player.Id != 0 ? player : player with {
                Mana = hidden ? player.Mana : ImmutableDictionary<string, int>.Empty,
                Zones = player.Zones.Select(zone => !hidden || zone.Name != "Hand" ? zone : zone with {
                    Cards = zone.Cards.Select(card => card with { FaceDown = true }).ToImmutableArray()
                }).ToImmutableArray()
            }).ToImmutableArray() });
            run.Settle("pool-drained-or-source-hidden");
            check(Rings(0, "C").Length == 0 && run.Engine.Replies.Count == 0,
                "Draining the actual pool or concealing the source withdraws its former estimate without reviving a fading pip");
            Refresh(funded, "restore-visible-source-and-pool"); Ready("visible-funded-again");
        }
        foreach (var foreign in new[] { initial with { DecisionOwnerId = 1 }, initial with { PriorityPlayerId = 1 },
            initial with { DecisionOwnerId = null }, initial with { PriorityPlayerId = null },
            initial with { Players = initial.Players.Select(player => player.Id == 0 ? player with { Eliminated = true } : player).ToImmutableArray() } }) {
            run.Present(foreign); run.Settle("foreign-or-retired-viewer");
            check(Rings(0, "C").Length == 0 && Rings(1, "C").Length == 0 && run.Engine.Replies.Count == 0,
                "A foreign or unknown priority owner/responder, or an eliminated seat, withdraws the own-hand pool estimate");
        }
        Refresh(funded, "restore-own-priority");
        Ready("restored-own-hand-cost");
        var paying = ManaPaymentScenario.Snapshot(1) with { DecisionOwnerId = 0 };
        var payment = (PaymentPayload)paying.Decision!.Payload;
        run.Engine.ReplyOverride = command => {
            if (command is not UseOfferedAction { ActionId: "cast-preview" } || !GameCommandPermissions.Allows(run.Engine.State, command))
                throw new InvalidOperationException("Only the current exact cast offer may leave this preview.");
            run.Engine.Replies.Add(command);
            run.Engine.Present(paying with { Players = run.Engine.State.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with {
                    Count = zone.Count - 1, Cards = zone.Cards.Where(card => card.VisualId != ManaPaymentScenario.Spell).ToImmutableArray()
                }).ToImmutableArray()
            }).ToImmutableArray(),
                Decision = paying.Decision with { Payload = payment with { UsablePoolManaColors = ["C"], FundingContext = funding } } });
            return Task.CompletedTask;
        };
        run.ClickHit("read-card-action");
        check(Rings(0, "C").Length == 0, "Beginning the explicit cast retires the nominal floating-pool accent");
        run.Until(() => !run.View.Busy && run.View.ManaJourney is { Payment: true }, "actual-payment-takes-over");
        check(Rings(0, "C").Length == 0 && Rings(0, "C", 1.5f).Length == 1
            && run.Hit("mana-pool:0:C").Intent is MatchIntent.SpendFloatingMana
            && run.View.ManaPoolBadge(0)!.Parts.Single().Amount == "4" && run.Engine.Replies.Count == 1,
            "The actual payment replaces the estimate with its unchanged authoritative Spend ring and route, retaining the full C4 count after exactly one cast command");
    }
}
