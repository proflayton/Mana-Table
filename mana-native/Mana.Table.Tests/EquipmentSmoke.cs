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
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

// Real pointer gestures and painted output exercise attachment affordances.
// Java tests separately establish the real equip rules and privacy projection.
internal sealed class EquipmentSmoke : ITableAutomation
{
    private readonly TableWindow.Probe view;
    private readonly InteractionScenarioEngine scenario;
    private readonly Queue<(double Delay, Func<bool>? Ready, Action Action)> steps = [];
    private readonly List<string> checks = [];
    private MouseState mouse;
    private double lastStep;
    private int step;
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;

    internal static void Run(string profile, Point? size)
    {
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true, TextScale = 1.3f }.Save(profile);
        var scenario = new InteractionScenarioEngine(); scenario.Present(Example());
        using var game = new TableWindow(TestTableServices.For(scenario), profile, null, null, size, initialState: scenario.State, artSource: new CardReadingArtFixture());
        game.Automation = new EquipmentSmoke(game.TestPort, scenario); game.Run();
    }

    private EquipmentSmoke(TableWindow.Probe view, InteractionScenarioEngine scenario)
    {
        this.view = view; this.scenario = scenario;
        Step(.4, () => { });
        Wait(() => view.AttachmentBadge("boots-2-0") != null, 3, () => {
            Check(view.TextScale == 1.3f, "The attachment journey uses130% text at the actual requested window size");
            foreach (int seat in Enumerable.Range(0, 4)) {
                foreach (string name in new[] { "sword", "boots-1", "boots-2", "elf-1", "elf-2" }) AssertBadge(name + "-" + seat);
                Check(view.AttachmentBadge("elf-3-" + seat) == null, $"Seat {seat}'s unattached duplicate has no equipped marker");
            }
            AssertDistinctRelationships();
            Check(CardPresentation.AttachmentBadge(Local("elf-1-0")) == "EQUIPPED ×2", "Multiple Equipment retain their exact count in the full state label");
            Capture("01-equipped-table"); MoveCard("boots-2-0");
        });
        Click();
        Wait(() => Reading("boots-2-0"), 3, () => {
            Check(Reading("boots-2-0"), "Clicking the exact Equipment opens its reader without a game action");
            AssertConnection("boots-2-0", "elf-2-0");
            Check(CardPresentation.StateLabels(Local("boots-2-0")).Contains("Equipped to: Llanowar Elves"),
                "Equipment retains its target name in current-state details");
            Capture("02-equipment-hover");
        });
        Step(0, () => mouse = Mouse(right: ButtonState.Pressed));
        Step(0, () => mouse = Mouse());
        Wait(() => view.Overlay == "inspect", 3, () => {
            Check(view.Overlay == "inspect" && view.AttachmentConnections.Count == 0,
                "Right-click opens the inspector and suppresses table attachment lines behind it");
            Check(scenario.Replies.Count == 0, "Equipment reading and inspection consume no game decision");
            Capture("03-equipment-inspector");
        });
        ClickHit("close-overlay");
        Step(.15, () => {
            Check(view.Overlay.Length == 0, "The equipment inspector closes through its normal control");
            MoveAway();
            Publish(ChangeCards(c => c.VisualId == "boots-2-0" ? c with { AttachedTo = c.AttachedTo! with { Id = "elf-3-0" } }
                : c.VisualId == "elf-2-0" ? c with { Attachments = [] }
                : c.VisualId == "elf-3-0" ? c with { Attachments = [new("boots-2-0", "Swiftfoot Boots", "Artifact — Equipment")] } : c));
        });
        Step(.3, () => {
            AssertBadge("elf-3-0");
            Check(view.AttachmentBadge("elf-2-0") == null && Local("boots-2-0").AttachedTo?.Id == "elf-3-0",
                "Re-equipping transfers the marker between identical creatures without leaving a stale marker");
            Capture("04-reequipped"); MoveCard("boots-2-0");
        });
        Click();
        Wait(() => Reading("boots-2-0"), 3, () => {
            Check(Reading("boots-2-0"), "The re-equipped occurrence remains explicitly readable");
            AssertConnection("boots-2-0", "elf-3-0");
            Check(view.AttachmentConnections.All(c => c.TargetId != "elf-2-0"), "The Equipment being read no longer links to its former identical target");
        });
        Step(0, () => MoveCard(view.Frame!.Cards.Single(card => card.Focused).Id)); Click();
        Step(0, () => { MoveAway(); Publish(Crowded()); });
        // Arrivals deliberately show a temporary state banner for1.1seconds.
        // Check persistent crowded markers after those banners have cleared.
        Step(1.25, () => {
            var scene = Scene();
            foreach (var player in view.Match!.Players) {
                Check(scene.Battlefield(player.Id).Sum(c => c.Cards.Length) == 50,
                    $"Seat {player.Id}'s crowded attachment scene renders all50 public occurrences");
                foreach (string name in new[] { "sword", "boots-1", "boots-2", "elf-1", "elf-2" }) AssertBadge(name + "-" + player.Id);
            }
            Check(Enumerable.Range(0, 4).Any(id => view.AttachmentBadge("elf-2-" + id) is { Compact: true }),
                "Crowded attached creatures use a compact marker that fits their own face");
            AssertDistinctRelationships();
            Capture("05-crowded-equipment"); MoveCard("boots-2-1");
        });
        Click();
        Wait(() => Reading("boots-2-1"), 3, () => {
            Check(Reading("boots-2-1"), "Clicking the crowded opponent's Equipment opens that exact occurrence");
            AssertConnection("boots-2-1", "elf-2-1");
            Check(scenario.Replies.Count == 0, "Reading a crowded opponent's exact Equipment sends no game command");
            Capture("06-crowded-opponent-hover");
        });
        Step(0, () => MoveCard(view.Frame!.Cards.Single(card => card.Focused).Id)); Click();
        Step(0, () => { MoveAway(); Publish(ChangeCards(c => c with { AttachedTo = null, Attachments = [] })); });
        Step(.3, () => {
            Check(view.Match!.Players.SelectMany(p => p.Zone("Battlefield").Cards).All(c => view.AttachmentBadge(c.CombatId) == null)
                && view.AttachmentConnections.Count == 0, "Detaching removes all painted attachment markers and links on the next snapshot");
            Check(scenario.Replies.Count == 0, "The complete attachment walkthrough submits no accidental game action");
            Capture("07-detached");
            File.WriteAllText(Path.Combine(view.Profile, "equipment-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        });
        Step(.05, () => view.Exit());
    }

    private static GameSnapshot Example()
    {
        var state = PresentationSmoke.Example();
        return state with { Decision = null, Players = state.Players.Select(p => p with { Zones = p.Zones.Select(z => z.Name == "Hand"
            ? z with { Count = 0, Cards = [] } : z.Name != "Battlefield" ? z : z with { Count = 6, Cards = [
                Card(p.Id, "elf-1", "Llanowar Elves", "Creature — Elf Druid") with { Power = 2, Toughness = 1,
                    Attachments = [new($"sword-{p.Id}", "Shuko", "Artifact — Equipment"), new($"boots-1-{p.Id}", "Swiftfoot Boots", "Artifact — Equipment")] },
                Card(p.Id, "elf-2", "Llanowar Elves", "Creature — Elf Druid") with { Power = 1, Toughness = 1,
                    Attachments = [new($"boots-2-{p.Id}", "Swiftfoot Boots", "Artifact — Equipment")] },
                Card(p.Id, "elf-3", "Llanowar Elves", "Creature — Elf Druid") with { Power = 1, Toughness = 1 },
                Card(p.Id, "sword", "Shuko", "Artifact — Equipment") with { AttachedTo = new($"elf-1-{p.Id}", "Llanowar Elves", "Creature — Elf Druid") },
                Card(p.Id, "boots-1", "Swiftfoot Boots", "Artifact — Equipment") with { AttachedTo = new($"elf-1-{p.Id}", "Llanowar Elves", "Creature — Elf Druid") },
                Card(p.Id, "boots-2", "Swiftfoot Boots", "Artifact — Equipment") with { AttachedTo = new($"elf-2-{p.Id}", "Llanowar Elves", "Creature — Elf Druid") }
            ] }).ToImmutableArray() }).ToImmutableArray() };
    }
    private static GameSnapshot Crowded()
    {
        var state = Example();
        return state with { Players = state.Players.Select(p => p with { Zones = p.Zones.Select(z => z.Name != "Battlefield" ? z : z with {
            Count = 50, Cards = [.. z.Cards, .. Enumerable.Range(0, 44).Select(i => Card(p.Id, "crowded-" + i, "Construct " + i, "Artifact Creature — Construct") with { Power = 1, Toughness = 1 })]
        }).ToImmutableArray() }).ToImmutableArray() };
    }
    private static Card Card(int seat, string id, string name, string type) => new() {
        Key = $"p:{id}-{seat}", VisualId = $"{id}-{seat}", CombatId = $"{id}-{seat}", Name = name, Type = type
    };
    private Card Local(string id) => view.Match!.Viewer!.Zone("Battlefield").Cards.Single(c => c.VisualId == id);
    private GameSnapshot ChangeCards(Func<Card, Card> edit) => view.Match! with { Players = view.Match!.Players.Select(p => p with {
        Zones = p.Zones.Select(z => z with { Cards = z.Cards.Select(edit).ToImmutableArray() }).ToImmutableArray()
    }).ToImmutableArray() };
    private TableScene Scene() => TableScene.Build(view.Match!, 0, new(-100, -100), null, null, false, CardPresentation.IsResource);
    private void AssertDistinctRelationships()
    {
        var scene = Scene();
        foreach (var player in view.Match!.Players) {
            var cards = scene.Battlefield(player.Id).SelectMany(c => c.Cards).ToArray();
            Check(new[] { "elf-2-", "elf-3-", "boots-1-", "boots-2-" }.All(prefix => cards.Single(c => c.Id == prefix + player.Id).GroupCount == 1),
                $"Seat {player.Id}'s identical creatures and Equipment with different attachment relationships never share a matching-state fan");
        }
    }
    private void AssertBadge(string id)
    {
        var badge = view.AttachmentBadge(id);
        var pose = view.DisplayedPose(id);
        var card = view.Match!.Players.SelectMany(p => p.Zone("Battlefield").Cards).Single(c => c.CombatId == id);
        var stats = badge?.IncludesCombatStats == true ? view.BattlefieldLabels(id).SingleOrDefault(label => label.Kind == "pt") : null;
        var statsHit = badge?.IncludesCombatStats == true
            ? view.HitAt(SmokePointer.Table(view, SmokePointer.Screen(view, badge.Bounds.Center))) : null;
        bool fits = badge != null && pose != null && (badge.IncludesCombatStats
            ? CardPresentation.HasCombatStats(card) && badge.Text == $"{card.Power}/{card.Toughness}"
                && stats != null && stats.Text == badge.Text && stats.Bounds == badge.Bounds
                && stats.MeasuredWidth <= stats.AvailableWidth && badge.MeasuredWidth <= stats.AvailableWidth
                && statsHit is { Card: { } hitCard } && statsHit.Id == "card:" + card.Key + ":" + card.VisualId
                && statsHit.Scope == view.Scope && CardViews.Identity(hitCard) == id
            : pose.Value.Bounds.Contains(badge.Bounds));
        int pixels = badge == null ? 0 : BadgePixels(badge.Bounds, badge.IncludesCombatStats);
        var physicalBadge = badge == null ? Rectangle.Empty
            : RenderViewport.ForSurface(new(view.Surface.Width, view.Surface.Height), view.DesignHeight).ToSurface(badge.Bounds);
        // A tiny ten-unit cue has only about six pixels per side at 1000px.
        // Keep an actual coverage requirement proportional to that footprint.
        int requiredPixels = Math.Clamp(physicalBadge.Width * physicalBadge.Height / 8, 1, 9);
        if (badge != null && pose != null && (pixels < requiredPixels || !fits))
            throw new InvalidOperationException($"{id}: badge={badge}; face={pose.Value.Bounds}; stats={stats}; hit={statsHit?.Id}; pixels={pixels}/{requiredPixels}");
        Check(fits && badge != null
            && badge.MeasuredWidth <= badge.Bounds.Width && pixels >= requiredPixels,
            $"{id}'s painted attachment cue fits its own face or gold-outlined current P/T ribbon, retains full current stats and owns its physical target at130% text");
    }
    private int BadgePixels(Rectangle bounds, bool sharedStats)
    {
        var gold = new Color(226, 190, 120);
        var panel = view.Frame!.Drawing.Commands.OfType<DrawCommand.Panel>()
            .Where(command => command.Trim == gold && (sharedStats ? command.Bounds.Contains(bounds) : command.Bounds == bounds))
            .OrderBy(command => (long)command.Bounds.Width * command.Bounds.Height).FirstOrDefault();
        // The actual command must identify this exact attachment ribbon. Art
        // behind an absent cue cannot satisfy the color readback by coincidence.
        if (panel == null || !sharedStats && panel.FillColor != new Color(48, 39, 22)) return 0;
        var mapping = RenderViewport.ForSurface(new(view.Surface.Width, view.Surface.Height), view.DesignHeight);
        // The P/T hit can occupy one cell of a larger state ribbon. Inspect its
        // actual containing frame, including the ribbon's surrounding padding.
        var paintedBounds = panel.Bounds;
        var physical = mapping.ToSurface(paintedBounds);
        var region = Rectangle.Intersect(physical, new(0, 0, view.Surface.Width, view.Surface.Height));
        if (region.Width <= 0 || region.Height <= 0) return 0;
        var pixels = new Color[region.Width * region.Height];
        view.Surface.GetData(0, region, pixels, 0, pixels.Length);
        if (!sharedStats) return pixels.Count(c => c.R == 48 && c.G == 39 && c.B == 22);
        int painted = 0;
        for (int y = 0; y < region.Height; y++) for (int x = 0; x < region.Width; x++) {
            float logicalX = (region.X + x + .5f) / mapping.Scale.X;
            float logicalY = (region.Y + y + .5f) / mapping.Scale.Y;
            // Sample the straight edges of the actual transformed gold border,
            // away from rounded corners and the white P/T glyphs inside it.
            bool horizontal = logicalX >= paintedBounds.Left + panel.Radius && logicalX <= paintedBounds.Right - panel.Radius
                && (Math.Abs(logicalY - paintedBounds.Top) <= 1.5f || Math.Abs(logicalY - paintedBounds.Bottom) <= 1.5f);
            bool vertical = logicalY >= paintedBounds.Top + panel.Radius && logicalY <= paintedBounds.Bottom - panel.Radius
                && (Math.Abs(logicalX - paintedBounds.Left) <= 1.5f || Math.Abs(logicalX - paintedBounds.Right) <= 1.5f);
            if ((horizontal || vertical) && GoldCoverage(pixels[y * region.Width + x], panel.FillColor, gold)) painted++;
        }
        return painted;
    }
    private static bool GoldCoverage(Color pixel, Color backing, Color gold)
    {
        // A one-logical-pixel border is subpixel at 1000px. MSAA blends its
        // gold with the backing, so an exact opaque RGB match is not promised.
        float r = gold.R - backing.R, g = gold.G - backing.G, b = gold.B - backing.B;
        float coverage = ((pixel.R - backing.R) * r + (pixel.G - backing.G) * g + (pixel.B - backing.B) * b)
            / (r * r + g * g + b * b);
        float errorR = pixel.R - (backing.R + coverage * r);
        float errorG = pixel.G - (backing.G + coverage * g);
        float errorB = pixel.B - (backing.B + coverage * b);
        return coverage is >= .15f and <= 1.05f && errorR * errorR + errorG * errorG + errorB * errorB <= 20 * 20;
    }
    private void AssertConnection(string source, string target)
    {
        var connection = view.AttachmentConnections.SingleOrDefault();
        var from = view.DisplayedPose(source)!.Value; var to = view.DisplayedPose(target)!.Value;
        Check(connection is { } c && c.SourceId == source && c.TargetId == target
            && Vector2.Distance(c.Start, from.Center) > 4 && Vector2.Distance(c.End, to.Center) > 4
            && Vector2.Distance(c.Start, c.End) > 4,
            $"Hovering {source} paints a nonzero edge-to-edge connection to exact occurrence {target}");
    }
    private void Publish(GameSnapshot state) { scenario.Present(state); view.Accept(scenario.State); }
    private void Step(double delay, Action action) => steps.Enqueue((delay, null, action));
    private void Wait(Func<bool> ready, double maximum, Action action) => steps.Enqueue((maximum, ready, action));
    private void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); checks.Add(description); }
    private void MoveAway() { var p = SmokePointer.Screen(view, new(1500, 70)); mouse = new(p.X, p.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released); }
    private void MoveCard(string id) => MoveHit(view.Hits.Single(h => h.Card?.VisualId == id && h.Id.StartsWith("card:", StringComparison.Ordinal)));
    private void MoveHit(Mana.Table.Interaction.TableHit hit) { var p = SmokePointer.Hit(view, hit); mouse = new(p.X, p.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released); }
    private MouseState Mouse(ButtonState left = ButtonState.Released, ButtonState right = ButtonState.Released) => new(mouse.X, mouse.Y, 0, left, ButtonState.Released, right, ButtonState.Released, ButtonState.Released);
    private bool Reading(string id) => view.DisplayedPose(id) is { Size.X: >= 400 } && view.HoverPreview is { Width: >= 400 };
    private void Click() { Step(0, () => mouse = Mouse(ButtonState.Pressed)); Step(0, () => mouse = Mouse()); }
    private void ClickHit(string id) {
        Step(0, () => MoveHit(view.Hits.Single(h => h.Id == id)));
        Step(0, () => mouse = Mouse(ButtonState.Pressed)); Step(0, () => mouse = Mouse());
    }
    private void Capture(string name) {
        using var file = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(file, view.Surface.Width, view.Surface.Height);
        view.CaptureWindow(Path.Combine(view.Profile, name + "-window.png"));
    }
    public void Frame(Texture2D surface)
    {
        try {
            if (view.Now > 40) throw new TimeoutException("Equipment journey exceeded40s at step " + step);
            if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
            if (steps.Count == 0) return;
            var next = steps.Peek();
            if (view.Now - lastStep < next.Delay && (next.Ready == null || !next.Ready())) return;
            steps.Dequeue(); step++; next.Action(); lastStep = view.Now;
        } catch {
            File.WriteAllText(Path.Combine(view.Profile, "equipment-failure.json"), JsonSerializer.Serialize(new { step, view.Now, checks,
                view.Scope, view.Overlay, view.Hovered, connections = view.AttachmentConnections, replies = scenario.Replies, state = view.Match,
                hits = view.Hits.Select(h => new { h.Id, h.Bounds }) }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            Capture("failure"); throw;
        }
    }
}
