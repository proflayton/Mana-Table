using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Uses rendered occurrences and production pointer hit testing. The controlled
// engine validates the one deliberate action; no layout or game rules are mocked.
internal sealed class FullBattlefieldSmoke : ITableAutomation
{
    private readonly TableWindow.Probe view;
    private readonly InteractionScenarioEngine scenario;
    private readonly Queue<(double Delay, Func<bool>? Ready, Action Action)> steps = [];
    private readonly List<string> checks = [];
    private MouseState mouse;
    private double lastStep;
    private string actionGame = "", actionDecision = "", actionCard = "";
    private string namedOccurrence = "distinct-2-49";
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;

    internal static void Run(string profile, Point? size, bool realArt = false, bool readableCrowd = false)
    {
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = false, TextScale = 1.3f }.Save(profile);
        var scenario = new InteractionScenarioEngine();
        scenario.Present(Example("ten"));
        ICardArtSource images = new CardReadingArtFixture();
        if (realArt) {
            var source = new MagicArtSource(profile);
            foreach (string name in DistinctNames.Concat(new[] { "Forest", "Plains", "Island", "Toothy, Imaginary Friend", "Sol Ring", "Rhystic Study", "Treetop Village" }).Distinct()) {
                source.LoadAsync(new(name, "front"), CancellationToken.None).GetAwaiter().GetResult();
                File.AppendAllText(Path.Combine(profile, "real-art-progress.log"), name + "\n");
            }
            images.Dispose(); images = source;
        }
        using var game = new TableWindow(TestTableServices.For(scenario), profile, null, null, size, initialState: scenario.State, artSource: images);
        game.Automation = new FullBattlefieldSmoke(game.TestPort, scenario, readableCrowd);
        game.Run();
    }

    private FullBattlefieldSmoke(TableWindow.Probe view, InteractionScenarioEngine scenario, bool readableCrowd)
    {
        this.view = view; this.scenario = scenario;
        if (readableCrowd) { QueueReadableCrowd(0); return; }
        Step(1.7, () => { });
        Wait(() => BattlefieldReady(10), 3, () => {
            AssertExposed(10, "Ten creatures");
            Capture("01-ten-creatures");
            MoveCard("ten-1-9");
        });
        Click();
        Wait(() => Reading("ten-1-9"), 3, () => {
            Check(Reading("ten-1-9") && view.CardPaintCount("ten-1-9") == 1,
                "An opponent's tenth creature enlarges through its own exposed hit area and is painted once");
            Check(scenario.Replies.Count == 0, "Reading an opponent's tenth creature sends no game command");
            Capture("02-tenth-opponent-hover");
        });
        Step(0, () => Buttons(right: ButtonState.Pressed));
        Step(.04, () => Buttons());
        Step(.15, () => {
            Check(view.Overlay.Length == 0 && Reading("ten-1-9") && view.CardPaintCount("ten-1-9") == 1
                && view.Frame!.Cards.Single(card => card.Id == "ten-1-9").Card is { Power: 2, Toughness: 2 }
                && scenario.Replies.Count == 0,
                "Right-click keeps the opponent's exact tenth creature in its canonical reader with current stats and no game command");
            Capture("03-tenth-opponent-inspect");
            MoveCard("ten-1-9");
        }); Click();
        Wait(() => view.HoverPreview == null, 2, () => {
            Check(view.Overlay.Length == 0 && !Reading("ten-1-9") && view.HoverPreview == null
                && view.CardPaintCount("ten-1-9") == 1 && scenario.Replies.Count == 0,
                "Clicking the tenth creature again closes its reader and retains its single battlefield face without a game command");
            MoveAway();
        });
        Step(.6, () => {
            var card = view.Match!.Viewer!.Zone("Battlefield").Cards.Single(c => c.VisualId == "ten-0-9");
            actionGame = view.Match.Id; actionDecision = view.Match.Decision!.Id; actionCard = card.Key;
            MoveCard(card.VisualId);
        }); Click();
        Wait(() => Reading("ten-0-9") && view.Hits.Any(hit => hit.Id == "read-card-action"), 3, () => {
            Check(Reading("ten-0-9") && scenario.Replies.Count == 0 && view.Hits.Any(h => h.Id == "read-card-action"),
                "The local tenth creature reads first and exposes its explicit current activation");
            MoveHit("read-card-action");
        }); Click();
        Wait(() => scenario.Replies.Count == 1 && view.Match?.Decision?.Payload is AbilityChoicePayload, 3, () => {
            Check(scenario.Replies.Count == 1 && scenario.Replies[0] is ActivateCard reply
                && reply.GameId == actionGame && reply.DecisionId == actionDecision && reply.Card == actionCard,
                "The local tenth creature submits exactly one action scoped to its own game, decision and card handle");
            Check(view.Match?.Decision is { Context.Source.Card.VisualId: "ten-0-9", Payload: AbilityChoicePayload },
                "The ability chooser belongs to the local tenth creature");
            Capture("04-own-tenth-action");
            Publish(Example("mixed")); MoveAway();
        });
        Step(1.3, () => { });
        Wait(() => BattlefieldReady(20), 3, () => {
            AssertExposed(20, "Twenty mixed permanents");
            var scene = Scene();
            foreach (var player in view.Match!.Players) {
                var clusters = scene.Battlefield(player.Id);
                string prefix = "mixed-" + player.Id + "-";
                Check(clusters.Single(c => c.Kind == "Creatures").Cards.Any(c => c.Id == prefix + "0")
                    && !clusters.Single(c => c.Kind == "Lands").Cards.Any(c => c.Id == prefix + "0"),
                    $"Seat {player.Id}'s animated land stays with creatures instead of disappearing into the land rank");
                var variants = clusters.SelectMany(c => c.Cards).Where(c => Enumerable.Range(1, 8).Any(i => c.Id == prefix + i)).ToArray();
                Check(variants.Length == 8 && variants.All(c => c.GroupCount == 1),
                    $"Seat {player.Id}'s tapped, damaged, countered, airborne, phased and sick copies retain independent public states");
                var hidden = player.Zone("Battlefield").Cards.Single(c => c.CombatId == prefix + "9");
                Check(hidden is { FaceDown: true, Type.Length: 0, Power: null, Toughness: null, VisualId.Length: 0 }
                    && clusters.Single(c => c.Kind == "Support").Cards.Any(c => c.Id == hidden.CombatId)
                    && !CardPresentation.HasCombatStats(hidden),
                    $"Seat {player.Id}'s anonymous face-down permanent is retained without inventing a creature identity or stats");
            }
            Check(view.Match!.Players[1].Zone("Battlefield").Cards.Single(c => c.VisualId == "mixed-1-17").Attacking
                && view.Match.Viewer!.Zone("Battlefield").Cards.Single(c => c.VisualId == "mixed-0-18").Blocking,
                "Crowding preserves the authoritative attacking and blocking occurrences");
            Capture("05-twenty-mixed");
            MoveCard("mixed-1-9");
        });
        Step(.4, () => {
            Check(view.Hovered is { FaceDown: true } && view.HoverPreview == null && scenario.Replies.Count == 1,
                "A crowded unknown face-down occurrence remains reachable without revealing an enlarged face or sending a command");
            Publish(Example("fifty")); MoveAway();
        });
        Step(1.3, () => { });
        Wait(() => BattlefieldReady(50), 3, () => {
            AssertExposed(50, "Fifty identical tokens");
            Check(view.Hits.Any(h => h.Id == "handnext"), "The separate twelve-card hand retains its normal hand paging control");
            Capture("06-fifty-copies");
            MoveCard("fifty-2-49");
        });
        Click();
        Wait(() => Reading("fifty-2-49"), 3, () => {
            Check(Reading("fifty-2-49") && view.CardPaintCount("fifty-2-49") == 1 && scenario.Replies.Count == 1,
                "The fiftieth opposing token remains individually readable without a page change or game action");
            Capture("07-fiftieth-hover"); MoveCard(view.Frame!.Cards.Single(card => card.Focused).Id);
        });
        Click();
        Step(.1, () => MoveAway());
        Step(.6, () => MoveHit("settings")); Click();
        Step(.12, () => {
            Check(view.Overlay == "settings", "Reduced motion is enabled through the normal settings panel");
            MoveHit("motion");
        }); Click();
        Step(.12, () => {
            Check(PlayPreferences.Load(view.Profile).ReducedMotion, "The reduced-motion choice is saved");
            MoveHit("close-overlay");
        }); Click();
        Step(.15, () => MoveAway());
        Step(.2, () => {
            Check(view.Overlay.Length == 0 && view.HoverPreview == null, "Settings close without leaving a blocking panel or preview");
            AssertExposed(50, "Reduced-motion fifty-token battlefield");
            Check(scenario.Replies.Count == 1, "Crowded-board inspection and settings do not submit extra game actions");
            Capture("08-reduced-motion");
            Publish(Example("distinct")); MoveAway();
        });
        Step(1.3, () => {
            AssertExposed(50, "Fifty distinct creature families");
            foreach (var player in view.Match!.Players) {
                var creatures = Scene().Battlefield(player.Id).Single(c => c.Kind == "Creatures").Cards;
                bool labeled = creatures.All(card => !card.DenseLabels.IsEmpty);
                bool faces = creatures.All(card => card.Compact && card.DenseMiniature && card.DenseLabels.IsEmpty);
                Check(creatures.Length == 50 && creatures.All(c => c.GroupCount == 1)
                    && creatures.All(card => card.GroupFront && card.Owner == player.Id && card.Zone == "Battlefield"
                        && card.Card == player.Zone("Battlefield").Cards.Single(current => CardViews.Identity(current) == card.Id))
                    && creatures.Select(card => card.Id).Distinct().Count() == 50
                    && creatures.Select(c => c.Card.Name).Distinct().Count() == 50
                    && (labeled || faces) && creatures.All(card => card.Dense && InDenseArea(player.Id, card.Pose.Bounds)
                        && (card.DenseLabels.IsEmpty || InDenseArea(player.Id, card.DenseLabels))),
                    $"Seat {player.Id}'s fifty distinct names and current states retain independent exposed occurrences inside their seat's labeled or individual-face layout");
            }
            Check(scenario.Replies.Count == 1, "Displaying fifty distinct creatures per seat sends no additional game action");
            Capture("09-fifty-distinct");
            // Exercise the explicit reader on the compact individual-face
            // fallback when the current seat's measured labels cannot fit.
            namedOccurrence = Scene().Seats.SelectMany(seat => seat.Battlefield.SelectMany(cluster => cluster.Cards))
                .FirstOrDefault(card => card.DenseMiniature && card.DenseLabels.IsEmpty && card.Card.Name == "Alloy Myr")?.Id ?? "distinct-2-49";
            MoveCard(namedOccurrence);
        });
        Click();
        Wait(() => Reading(namedOccurrence), 3, () => {
            Check(Reading(namedOccurrence) && view.Frame!.Cards.Single(card => card.Id == namedOccurrence).Card
                is { Name: "Alloy Myr", Power: 2, Toughness: 2 } && view.CardPaintCount(namedOccurrence) == 1 && scenario.Replies.Count == 1,
                "The named Alloy Myr remains individually readable among fifty distinct creatures");
            Capture("10-named-occurrence");
        });
        Step(0, () => Buttons(right: ButtonState.Pressed));
        Step(.04, () => Buttons());
        Step(.15, () => {
            Check(view.Overlay.Length == 0 && Reading(namedOccurrence) && view.CardPaintCount(namedOccurrence) == 1
                && view.Frame!.Cards.Single(card => card.Id == namedOccurrence).Card is { Name: "Alloy Myr", Power: 2, Toughness: 2 }
                && scenario.Replies.Count == 1,
                "Right-click retains the exact named occurrence and its current stats in the canonical reader without a command");
            Capture("11-named-inspector");
            MoveCard(namedOccurrence);
        }); Click();
        Wait(() => view.HoverPreview == null, 2, () => {
            Check(view.Overlay.Length == 0 && !Reading(namedOccurrence) && view.HoverPreview == null
                && view.CardPaintCount(namedOccurrence) == 1 && scenario.Replies.Count == 1,
                "Clicking the named occurrence again closes its reader and preserves the battlefield without a command");
            MoveAway();
        });
        QueueReadableCrowd(1, finish: false);
        QueueDenseOrdering();
    }
    private void QueueReadableCrowd(int expectedReplies, bool finish = true)
    {
        Step(.15, () => { Publish(ReadableCrowd()); MoveAway(); });
        Step(.3, () => { });
        Wait(() => BattlefieldReady(66), 4, () => {
            // Flush a real-window artifact before any strict geometry failure,
            // so a failed crowd remains reviewable at its physical resolution.
            Capture("12-typed-counters-and-full-stats", captureWindow: false);
            File.WriteAllText(Path.Combine(view.Profile, "readable-crowd-observations.json"), JsonSerializer.Serialize(new {
                view.Now, view.TextScale, players = view.Match!.Players.Select(player => new { player.Id,
                    cards = Scene().Battlefield(player.Id).SelectMany(cluster => cluster.Cards).Select(node => new {
                        node.Id, node.Card.Name, node.GroupCount, node.GroupFront, pose = view.DisplayedPose(node.Id), labels = view.BattlefieldLabels(node.Id)
                    }) })
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        });
        Step(0, () => view.CaptureWindow(Path.Combine(view.Profile, "12-typed-counters-and-full-stats-window.png")));
        Step(0, () => {
            AssertExposed(66, "Fifteen distinct creature groups alongside support and lands");
            Check(view.TextScale == 1.3f, "Crowded battlefield state is checked with130% text at the requested physical window size");
            AssertReadableCrowd();
            Check(scenario.Replies.Count == expectedReplies, "Rendering and reading all crowded role lanes sends no additional game action");
            if (finish) Finish();
        });
    }
    private void QueueDenseOrdering()
    {
        int[] order = [2, 0, 1];
        string gameId = "", decisionId = "";
        Step(0, () => {
            var state = Example("mixed");
            var cards = new[] { 0, 1, 4 }.Select(index => state.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == "mixed-0-" + index)).ToArray();
            Publish(state with { Id = "full-battlefield-dense-order", Decision = new Decision("", new DecisionContext(Message: "Choose these creatures in order."), new OrderingPayload(new ChoiceSelection(cards.Select((card, index) => new Choice(index, card.Name,
                    card with { Key = "", VisualId = "", CombatId = "" }) { CardId = card.VisualId }).ToImmutableArray(), new SelectionRange(3, 3), []))) });
            MoveAway();
        });
        Wait(() => view.Match?.Decision?.Payload is OrderingPayload && TableChoices.IsDirect(view.Match) && BattlefieldReady(20), 4, () => {
            Check(view.Match?.Decision?.Payload is OrderingPayload && TableChoices.IsDirect(view.Match) && BattlefieldReady(20),
                "The fresh dense ordered decision and all twenty real occurrences must settle before clicking");
            gameId = view.Match!.Id; decisionId = view.Match.Decision!.Id;
            Check(view.Selections.Count == 0 && ((OrderingPayload)view.Match.Decision.Payload).Selection.Choices.All(choice => Scene().Battlefield(0).SelectMany(cluster => cluster.Cards)
                .Any(node => node.Id == choice.CardId && node.Dense && !node.DenseLabels.IsEmpty)),
                "Three ordered candidates use the actual measured dense battlefield, starting with no positions chosen");
            MoveCard(((OrderingPayload)view.Match.Decision.Payload).Selection.Choices.Single(choice => choice.Index == order[0]).CardId);
        });
        foreach (int position in Enumerable.Range(0, 3)) {
            int chosen = position;
            Click();
            Wait(() => view.Selections.SequenceEqual(order.Take(chosen + 1)) && DenseOrderBadges(order.Take(chosen + 1).ToArray()), 2, () => {
                Check(view.Selections.SequenceEqual(order.Take(chosen + 1)) && DenseOrderBadges(order.Take(chosen + 1).ToArray())
                    && scenario.Replies.Count == 1 && view.HoverPreview == null,
                    $"Dense ordering position {chosen + 1} remains complete, exposed and compact without an automatic reply");
                if (chosen < 2) MoveCard(((OrderingPayload)view.Match!.Decision!.Payload).Selection.Choices.Single(choice => choice.Index == order[chosen + 1]).CardId);
                else { Capture("13-dense-order-three", captureWindow: false); MoveHit("confirm"); }
            });
        }
        Step(0, () => view.CaptureWindow(Path.Combine(view.Profile, "13-dense-order-three-window.png")));
        Click();
        Wait(() => !view.Busy && scenario.Replies.Count == 2, 2, () => {
            Check(scenario.Replies[^1] is OrderOptions reply && reply.GameId == gameId
                && reply.DecisionId == decisionId && reply.Choices!.SequenceEqual(order),
                "Only explicit confirmation submits dense order [2,0,1] with its exact current game and decision scope");
            Finish();
        });
    }
    private bool DenseOrderBadges(int[] order) => view.ChoiceOrderBadges.Count == order.Length
        && order.Select((index, position) => view.ChoiceOrderBadges.TryGetValue(index, out var badge)
            && badge.Position == position + 1 && badge.Text.EndsWith((position + 1).ToString(), StringComparison.Ordinal)
            && badge.MeasuredWidth <= badge.AvailableWidth && badge.Bounds.Width >= 70
            && DenseOrderExposed(index, badge.Bounds)).All(valid => valid);
    private bool DenseOrderExposed(int index, Microsoft.Xna.Framework.Rectangle bounds)
    {
        string cardId = ((OrderingPayload)view.Match!.Decision!.Payload).Selection.Choices.Single(choice => choice.Index == index).CardId;
        foreach (int y in new[] { bounds.Top + 3, bounds.Center.Y, bounds.Bottom - 3 })
            for (int x = bounds.Left + 5; x < bounds.Right - 5; x += 6)
                if (view.HitAt(SmokePointer.Table(view, Pixel(new(x, y))))?.Card?.VisualId != cardId) return false;
        return true;
    }
    private void Finish()
    {
        File.WriteAllText(Path.Combine(view.Profile, "full-battlefield-results.json"),
            JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        view.Exit();
    }

    // Captures can consume wall time without advancing entrance motion. Observe
    // the exact painted/hit-test state; keep AssertExposed at the bounded deadline.
    private bool BattlefieldReady(int count)
    {
        if (view.Match is not { } state || view.HoverPreview != null || view.Overlay.Length != 0
            || view.Hits.Any(h => h.Id.StartsWith("rank-prev:", StringComparison.Ordinal)
                || h.Id.StartsWith("rank-next:", StringComparison.Ordinal))) return false;
        return state.Players.All(player => {
            var cards = player.Zone("Battlefield").Cards;
            var hits = view.Hits.Where(h => h.Zone == "Battlefield" && h.PlayerId == player.Id && h.Card != null).ToArray();
            var visible = cards.Select(CardViews.Identity).ToHashSet(StringComparer.Ordinal);
            return cards.Length == count && hits.Length >= count
                && hits.Select(h => CardViews.Identity(h.Card!)).Distinct().Count() == count
                && hits.All(hit => visible.Contains(CardViews.Identity(hit.Card!)))
                && cards.All(card => view.CardPaintCount(CardViews.Identity(card)) == 1
                    && ExposedPixel(CardViews.Identity(card)) != null);
        });
    }
    private void AssertReadableCrowd()
    {
        var scene = Scene();
        foreach (var player in view.Match!.Players) {
            Check(scene.Battlefield(player.Id).Select(cluster => (cluster.Kind, cluster.Cards.Length))
                .SequenceEqual([("Creatures", 60), ("Support", 3), ("Lands", 3)]),
                $"Seat {player.Id}'s 60 creatures, 3 support cards and 3 lands keep their complete role counts");
            var nodes = scene.Battlefield(player.Id).SelectMany(cluster => cluster.Cards).ToArray();
            Check(nodes.All(node => node.Dense && !node.DenseLabels.IsEmpty
                && InDenseArea(player.Id, node.Pose.Bounds) && InDenseArea(player.Id, node.DenseLabels)),
                $"Seat {player.Id}'s rich field keeps measured labels and every face inside its own dense territory");
            foreach (int group in Enumerable.Range(0, 15)) {
                string id = $"readable-{player.Id}-{group}-3";
                var node = nodes.Single(card => card.Id == id);
                var labels = view.BattlefieldLabels(id);
                var pt = labels.SingleOrDefault(label => label.Kind == "pt");
                var counter = labels.SingleOrDefault(label => label.Kind == "counter");
                Check(pt != null && pt.Text == $"{13 + group}/{13 + group}" && pt.MeasuredWidth <= pt.AvailableWidth,
                    $"Seat {player.Id}'s group {group} shows its complete authoritative P/T without numeric ellipsis");
                Check(counter != null && counter.Text == (10 + group).ToString() + (group == 1 ? "…" : "")
                    && counter.MeasuredWidth <= counter.AvailableWidth,
                    $"Seat {player.Id}'s group {group} keeps its counter marker on the shared card tile without summing counter types");
                Check(labels.Any(label => label.Kind == "copies" && label.Text == "×4")
                    && labels.All(label => label.Kind == "name" || label.MeasuredWidth <= label.AvailableWidth),
                    $"Seat {player.Id}'s group {group} distinguishes four copies from counters and preserves complete state values");
                Check(labels.Any(label => label.Kind == "name"),
                    $"Seat {player.Id}'s group {group} retains a named identity alongside its public state");
                int order = Array.IndexOf(nodes, node);
                foreach (var label in labels) {
                    var bounds = label.Bounds;
                    var points = new[] { new Point(bounds.Left + 2, bounds.Top + 2), new Point(bounds.Right - 3, bounds.Top + 2),
                        new Point(bounds.Left + 2, bounds.Bottom - 3), new Point(bounds.Right - 3, bounds.Bottom - 3), bounds.Center };
                    Check(!nodes.Skip(order + 1).Any(later => view.DisplayedPose(later.Id) is { } pose && points.Any(pose.Contains)),
                        $"Seat {player.Id}'s group {group} {label.Kind} header is not buried by a later battlefield face");
                    Check(points.All(point => view.HitAt(SmokePointer.Table(view, Pixel(point.ToVector2()))) is { Card: { } card }
                        && CardViews.Identity(card) == id),
                        $"Seat {player.Id}'s group {group} {label.Kind} strip belongs to the same exact occurrence through physical hit testing");
                }
            }
            Check(view.BattlefieldLabels($"readable-{player.Id}-1-3").Any(label => label.Kind == "counter" && label.Text.EndsWith("…", StringComparison.Ordinal))
                && view.BattlefieldLabels($"readable-{player.Id}-2-3").Any(label => label.Kind == "damage" && label.Text == "DMG 2"),
                $"Seat {player.Id}'s additional counter type and marked damage remain explicitly identified");
        }
    }
    private bool InDenseArea(int player, Microsoft.Xna.Framework.Rectangle bounds) => BattlefieldLayout.DenseAreas(Scene().Seat(player).Index)
        .Any(area => area.Contains(bounds));
    private void AssertExposed(int count, string description)
    {
        Check(view.HoverPreview == null && view.Overlay.Length == 0, description + ": exposure is checked on the resting table");
        Check(!view.Hits.Any(h => h.Id.StartsWith("rank-prev:", StringComparison.Ordinal)
            || h.Id.StartsWith("rank-next:", StringComparison.Ordinal)), description + ": no battlefield paging controls are present");
        foreach (var player in view.Match!.Players) {
            var cards = player.Zone("Battlefield").Cards;
            var hits = view.Hits.Where(h => h.Zone == "Battlefield" && h.PlayerId == player.Id && h.Card != null).ToArray();
            var visible = cards.Select(CardViews.Identity).ToHashSet(StringComparer.Ordinal);
            Check(cards.Length == count && hits.Length >= count && hits.Select(h => CardViews.Identity(h.Card!)).Distinct().Count() == count
                && hits.All(hit => visible.Contains(CardViews.Identity(hit.Card!))),
                $"{description}: all {count} seat {player.Id} occurrences are rendered with independent hit areas");
            foreach (var card in cards) {
                string id = CardViews.Identity(card);
                if (view.CardPaintCount(id) != 1 || ExposedPixel(id) == null)
                    Fail($"{description}: seat {player.Id} occurrence {id} must be painted once and expose a real physical 2×2 pointer area");
            }
            checks.Add($"{description}: every seat {player.Id} occurrence is painted once and reachable through actual hit testing");
        }
    }

    // Round-trip through actual viewport pixels before querying HitAt. A logical
    // sliver that rounds onto another card at 1000×740 cannot pass this check.
    private Point? ExposedPixel(string id)
    {
        if (view.DisplayedPose(id) is not { } pose) return null;
        bool Matches(Point physical) {
            var viewport = view.GraphicsDevice.Viewport;
            if (physical.X < 0 || physical.X >= viewport.Width || physical.Y < 0 || physical.Y >= viewport.Height) return false;
            float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
            var logical = new Point((int)((physical.X - (viewport.Width - 1600 * scale) / 2) / scale),
                (int)((physical.Y - (viewport.Height - view.DesignHeight * scale) / 2) / scale));
            return view.HitAt(logical) is { Zone: "Battlefield", Card: { } card } && CardViews.Identity(card) == id;
        }
        bool Patch(Point p) => Matches(p) && Matches(new(p.X + 1, p.Y)) && Matches(new(p.X, p.Y + 1)) && Matches(new(p.X + 1, p.Y + 1));
        float[] coordinates = [.5f, .025f, .06f, .1f, .16f, .25f, .4f, .6f, .75f, .9f, .975f];
        foreach (float v in coordinates) foreach (float u in coordinates) {
            var physical = Pixel(pose.Point(u, v));
            if (Patch(physical)) return physical;
        }
        var top = Pixel(new(pose.Bounds.Left, pose.Bounds.Top));
        var bottom = Pixel(new(pose.Bounds.Right, pose.Bounds.Bottom));
        for (int y = Math.Max(0, top.Y); y < bottom.Y; y++)
            for (int x = Math.Max(0, top.X); x < bottom.X; x++)
                if (Patch(new(x, y))) return new(x, y);
        return null;
    }
    private Point Pixel(Vector2 logical)
    {
        var viewport = view.GraphicsDevice.Viewport;
        float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        return new((int)MathF.Round((viewport.Width - 1600 * scale) / 2 + logical.X * scale),
            (int)MathF.Round((viewport.Height - view.DesignHeight * scale) / 2 + logical.Y * scale));
    }
    private void MoveCard(string id)
    {
        var point = ExposedPixel(id);
        if (point == null) Fail("No exposed physical pointer area for " + id);
        Move(point!.Value);
    }
    private void MoveHit(string id)
    {
        var hit = view.Hits.Single(h => h.Id == id);
        Move(Pixel(hit.Bounds.Center.ToVector2()));
    }
    private void MoveAway() => Move(Pixel(new(1590, 890)));
    private void Move(Point physical) => mouse = new(physical.X, physical.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    private void Buttons(ButtonState left = ButtonState.Released, ButtonState right = ButtonState.Released) => mouse = new(mouse.X, mouse.Y, 0, left, ButtonState.Released, right, ButtonState.Released, ButtonState.Released);
    private void Click() { Step(.03, () => Buttons(ButtonState.Pressed)); Step(.04, () => Buttons()); }
    private bool Reading(string id) => view.Frame?.Cards.Any(card => card.Id == id && card.Focused) == true
        && view.DisplayedPose(id) is { Size.X: >= 400 }
        && view.HoverPreview is { Width: >= 400, Height: >= 560 };
    private TableScene Scene() => view.TableScene ?? throw new InvalidOperationException("The production table scene is not ready");
    private void Publish(GameSnapshot state) { scenario.Present(state); view.Accept(scenario.State); }
    private void Step(double delay, Action action) => steps.Enqueue((delay, null, action));
    private void Wait(Func<bool> ready, double deadline, Action action) => steps.Enqueue((deadline, ready, action));
    private void Check(bool value, string description) { if (!value) Fail(description); checks.Add(description); }
    private void Fail(string description)
    {
        Capture("failure");
        File.WriteAllText(Path.Combine(view.Profile, "full-battlefield-failure.json"), JsonSerializer.Serialize(new {
            description, view.Now, view.Overlay, view.ZoneName, mouse.X, mouse.Y,
            hovered = view.Hovered == null ? null : CardViews.Identity(view.Hovered), view.HoverPreview,
            Frame = view.Frame?.Observe(),
            Scene = view.TableScene?.Seats.Select(seat => new { seat.PlayerId, seat.Index, seat.IsDense,
                Areas = BattlefieldLayout.DenseAreas(seat.Index),
                Groups = seat.Battlefield.Select(cluster => new { cluster.Kind,
                    Cards = cluster.Cards.Select(card => new { card.Id, card.Card.Name, card.Owner, card.Zone,
                        card.GroupCount, card.GroupFront, card.Dense, card.Compact, card.DenseMiniature, card.DenseScale,
                        card.DenseLabels, card.Pose, InSeatArea = InDenseArea(seat.PlayerId, card.Pose.Bounds),
                        LabelsInSeatArea = card.DenseLabels.IsEmpty || InDenseArea(seat.PlayerId, card.DenseLabels),
                        card.Card.Power, card.Card.Toughness, card.Card.Counters, card.Card.Damage }) }) }),
            hits = view.Hits.Select(h => new { h.Id, h.Zone, h.PlayerId, card = h.Card == null ? null : CardViews.Identity(h.Card), h.Bounds })
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        throw new InvalidOperationException(description);
    }
    private void Capture(string name, bool captureWindow = true) {
        using var output = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height);
        if (captureWindow) view.CaptureWindow(Path.Combine(view.Profile, name + "-window.png"));
    }
    public void Frame(Texture2D surface)
    {
        if (view.Now > 90) Fail("Full battlefield regression did not finish");
        if (view.Error.Length > 0) Fail(view.Error);
        if (steps.TryPeek(out var next) && (next.Ready?.Invoke() == true || view.Now - lastStep >= next.Delay)) {
            steps.Dequeue(); next.Action(); lastStep = view.Now;
        }
    }

    private static GameSnapshot Example(string mode)
    {
        int count = mode == "ten" ? 10 : mode == "mixed" ? 20 : 50;
        var state = PresentationSmoke.Example();
        var players = state.Players.Select(p => p with { Zones = p.Zones.Select(z => z.Name == "Battlefield"
            ? z with { Count = count, Cards = Enumerable.Range(0, count).Select(i => Permanent(mode, p.Id, i)).ToImmutableArray() }
            : z.Name == "Hand" && p.Id == 0 && mode != "ten" ? z with { Count = 12, Cards = Enumerable.Range(0, 12).Select(i => new Card {
                VisualId = "full-hand-" + i, CombatId = "full-hand-" + i, Name = "Forest", Type = "Basic Land — Forest"
            }).ToImmutableArray() } : z).ToImmutableArray() }).ToArray();
        Combat? combat = mode == "mixed" ? new([new("mixed-1-17", 0, ["mixed-0-18"], []) {
            Defender = new("player", "0", 0, "You"), Blocked = true
        }], [], null) : null;
        return state with { Id = "full-battlefield-" + mode, Players = players.ToImmutableArray(), Combat = combat, Stack = [], Activity = [],
            ActivePlayerId = mode == "mixed" ? 1 : 0, Decision = new Decision("", new DecisionContext(Message: "You have priority."), new PriorityPayload(new DecisionReply("OK", true), null, CanAutoPass: false, QuietYieldToken: "", Abilities: [])) };
    }
    private static GameSnapshot ReadableCrowd()
    {
        var state = PresentationSmoke.Example();
        Card Creature(int seat, int group, int copy) => new() {
            VisualId = $"readable-{seat}-{group}-{copy}", CombatId = $"readable-{seat}-{group}-{copy}",
            Name = DistinctNames[group], Type = "Creature", Power = 13 + group, Toughness = 13 + group,
            Counters = group == 1 ? ImmutableDictionary<string, int>.Empty.Add("+1/+1", 10 + group).Add("Shield", 2) : ImmutableDictionary<string, int>.Empty.Add("+1/+1", 10 + group),
            Damage = group == 2 ? 2 : 0, Sick = group % 4 == 0,
            CombatKeywords = group % 3 == 0 ? ["flying"] : group % 3 == 1 ? ["trample"] : []
        };
        return state with { Id = "full-battlefield-readable", Decision = null, Stack = [], Activity = [], Combat = null,
            Players = state.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Count = 66, Cards = [.. Enumerable.Range(0, 15).SelectMany(group => Enumerable.Range(0, 4).Select(copy => Creature(player.Id, group, copy))),
                    new() { VisualId = $"readable-support-{player.Id}-0", Name = "Sol Ring", Type = "Artifact" },
                    new() { VisualId = $"readable-support-{player.Id}-1", Name = "Rhystic Study", Type = "Enchantment" },
                    new() { VisualId = $"readable-support-{player.Id}-2", Name = "Doubling Season", Type = "Enchantment" },
                    new() { VisualId = $"readable-land-{player.Id}-0", Name = "Forest", Type = "Basic Land — Forest", Tapped = true },
                    new() { VisualId = $"readable-land-{player.Id}-1", Name = "Island", Type = "Basic Land — Island", Tapped = true },
                    new() { VisualId = $"readable-land-{player.Id}-2", Name = "Wastes", Type = "Basic Land", Tapped = true }]
            }).ToImmutableArray() }).ToImmutableArray() };
    }
    private static Card Permanent(string mode, int seat, int index)
    {
        string id = mode + "-" + seat + "-" + index;
        var card = new Card { VisualId = id, CombatId = id, Name = mode == "fifty" ? "Soldier Token" : "Llanowar Elves",
            Type = mode == "fifty" ? "Token Creature — Soldier" : "Creature — Elf Druid", Power = mode == "fifty" ? 1 : 2,
            Toughness = mode == "fifty" ? 1 : 2, Text = mode == "fifty" ? "" : "{T}: Add {G}.", Selectable = mode == "ten" && seat == 0 && index == 9 };
        if (mode == "distinct") return card with { Name = DistinctNames[index], Text = "" };
        if (mode != "mixed") return card;
        return index switch {
            0 => card with { Name = "Treetop Village", Type = "Land Creature — Ape", Power = 3, Toughness = 3, CombatKeywords = ["trample"] },
            2 => card with { Tapped = true },
            3 => card with { Damage = 1 },
            4 => card with { Power = 3, Toughness = 3, Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 1) },
            5 => card with { CombatKeywords = ["flying"] },
            6 => card with { CombatKeywords = ["vigilance"] },
            7 => card with { PhasedOut = true },
            8 => card with { Sick = true },
            9 => card with { VisualId = "", Name = "Face-down card", Type = "", Power = null, Toughness = null, Text = "", FaceDown = true },
            10 => card with { Name = "Sol Ring", Type = "Artifact", Power = null, Toughness = null },
            11 => card with { Name = "Rhystic Study", Type = "Enchantment", Power = null, Toughness = null },
            12 or 13 => card with { Name = "Plains", Type = "Basic Land — Plains", Power = null, Toughness = null, Tapped = index == 13 },
            14 => card with { Name = "Island", Type = "Basic Land — Island", Power = null, Toughness = null },
            15 => card with { Name = "Forest", Type = "Basic Land — Forest", Power = null, Toughness = null },
            16 => card with { Name = "Elf Token", Type = "Token Creature — Elf Warrior", Power = 1, Toughness = 1 },
            17 => card with { Name = "Grizzly Bears", Type = "Creature — Bear" },
            18 => card with { Name = "Elvish Visionary", Type = "Creature — Elf Shaman" },
            19 => card with { Name = "Treasure", Type = "Token Artifact — Treasure", Power = null, Toughness = null },
            _ => card
        };
    }
    // Real names make the distinct-occurrence exercise useful for art recognition.
    // These are controlled current characteristics, not a simulation of printed rules.
    private static readonly string[] DistinctNames = [
        "Grizzly Bears", "Runeclaw Bear", "Walking Corpse", "Savannah Lions", "Isamaru, Hound of Konda",
        "Elite Vanguard", "Memnite", "Ornithopter", "Phyrexian Walker", "Shield Sphere",
        "Goblin Guide", "Monastery Swiftspear", "Llanowar Elves", "Elvish Mystic", "Fyndhorn Elves",
        "Birds of Paradise", "Noble Hierarch", "Lotus Cobra", "Tireless Tracker", "Scavenging Ooze",
        "Tarmogoyf", "Snapcaster Mage", "Dark Confidant", "Stoneforge Mystic", "Thalia, Guardian of Thraben",
        "Mother of Runes", "Giver of Runes", "Delver of Secrets", "Young Pyromancer", "Baleful Strix",
        "Coiling Oracle", "Elvish Visionary", "Wall of Omens", "Wall of Blossoms", "Eternal Witness",
        "Reclamation Sage", "Acidic Slime", "Solemn Simulacrum", "Mulldrifter", "Kitchen Finks",
        "Restoration Angel", "Siege Rhino", "Questing Beast", "Questing Druid", "Silver Myr",
        "Gold Myr", "Iron Myr", "Leaden Myr", "Copper Myr", "Alloy Myr"
    ];
}
