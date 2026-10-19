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
    private readonly TableGame.Probe view;
    private readonly InteractionScenarioEngine scenario;
    private readonly Queue<(double Delay, Func<bool>? Ready, Action Action)> steps = [];
    private readonly List<string> checks = [];
    private MouseState mouse;
    private double lastStep;
    private string actionGame = "", actionDecision = "", actionCard = "";
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;

    internal static void Run(string profile, Point? size)
    {
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = false }.Save(profile);
        var scenario = new InteractionScenarioEngine();
        scenario.Present(Example("ten"));
        using var game = new TableGame(scenario, profile, null, null, size, initialState: scenario.State,
            artSource: new CardReadingArtFixture());
        game.Automation = new FullBattlefieldSmoke(game.TestPort, scenario);
        game.Run();
    }

    private FullBattlefieldSmoke(TableGame.Probe view, InteractionScenarioEngine scenario)
    {
        this.view = view; this.scenario = scenario;
        Step(1.7, () => {
            AssertExposed(10, "Ten creatures");
            Capture("01-ten-creatures");
            MoveCard("ten-1-9");
        });
        Wait(() => Reading("ten-1-9"), 3, () => {
            Check(Reading("ten-1-9") && view.CardPaintCount("ten-1-9") == 1,
                "An opponent's tenth creature enlarges through its own exposed hit area and is painted once");
            Check(scenario.Replies.Count == 0, "Reading an opponent's tenth creature sends no game command");
            Capture("02-tenth-opponent-hover");
        });
        Step(0, () => Buttons(right: ButtonState.Pressed));
        Step(.04, () => Buttons());
        Step(.15, () => {
            Check(view.Overlay == "inspect" && scenario.Replies.Count == 0,
                "Right-click inspects the opponent's tenth creature without a game command");
            Capture("03-tenth-opponent-inspect");
            MoveHit("close-overlay");
        }); Click();
        Step(.15, () => {
            Check(view.Overlay.Length == 0, "The tenth-creature inspector closes normally");
            MoveAway();
        });
        Step(.6, () => {
            var card = view.Match!.Viewer!.Zone("Battlefield").Cards.Single(c => c.VisualId == "ten-0-9");
            actionGame = view.Match.Id; actionDecision = view.Match.Decision!.Id; actionCard = card.Key;
            MoveCard(card.VisualId);
        }); Click();
        Wait(() => scenario.Replies.Count == 1 && view.Match?.Decision?.Context == "playAbility", 3, () => {
            Check(scenario.Replies.Count == 1 && scenario.Replies[0] is { Action: ReplyAction.SelectCard } reply
                && reply.GameId == actionGame && reply.DecisionId == actionDecision && reply.Card == actionCard,
                "The local tenth creature submits exactly one action scoped to its own game, decision and card handle");
            Check(view.Match?.Decision is { Context: "playAbility", SourceCard.VisualId: "ten-0-9" },
                "The ability chooser belongs to the local tenth creature");
            Capture("04-own-tenth-action");
            Publish(Example("mixed")); MoveAway();
        });
        Step(1.3, () => {
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
        Step(1.3, () => {
            AssertExposed(50, "Fifty identical tokens");
            Check(view.Hits.Any(h => h.Id == "handnext"), "The separate twelve-card hand retains its normal hand paging control");
            Capture("06-fifty-copies");
            MoveCard("fifty-2-49");
        });
        Wait(() => Reading("fifty-2-49"), 3, () => {
            Check(Reading("fifty-2-49") && view.CardPaintCount("fifty-2-49") == 1 && scenario.Replies.Count == 1,
                "The fiftieth opposing token remains individually readable without a page change or game action");
            Capture("07-fiftieth-hover"); MoveAway();
        });
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
                Check(creatures.Length == 50 && creatures.All(c => c.GroupCount == 1)
                    && creatures.Select(c => c.Card.Name).Distinct().Count() == 50,
                    $"Seat {player.Id}'s fifty distinct names retain fifty independently exposed occurrences");
            }
            Check(scenario.Replies.Count == 1, "Displaying fifty distinct creatures per seat sends no additional game action");
            Capture("09-fifty-distinct");
            File.WriteAllText(Path.Combine(view.Profile, "full-battlefield-results.json"),
                JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            view.Exit();
        });
    }

    private void AssertExposed(int count, string description)
    {
        Check(view.HoverPreview == null && view.Overlay.Length == 0, description + ": exposure is checked on the resting table");
        Check(!view.Hits.Any(h => h.Id.StartsWith("rank-prev:", StringComparison.Ordinal)
            || h.Id.StartsWith("rank-next:", StringComparison.Ordinal)), description + ": no battlefield paging controls are present");
        foreach (var player in view.Match!.Players) {
            var cards = player.Zone("Battlefield").Cards;
            var hits = view.Hits.Where(h => h.Zone == "Battlefield" && h.PlayerId == player.Id && h.Card != null).ToArray();
            Check(cards.Length == count && hits.Length == count && hits.Select(h => CardViews.Identity(h.Card!)).Distinct().Count() == count,
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
    private bool Reading(string id) => view.Hovered is { } card && CardViews.Identity(card) == id
        && view.HoverPreview is { Width: >= 400, Height: >= 560 };
    private TableScene Scene() => TableScene.Build(view.Match!, 0, new(-1, -1), null, null, false, CardPresentation.IsResource);
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
            hits = view.Hits.Select(h => new { h.Id, h.Zone, h.PlayerId, card = h.Card == null ? null : CardViews.Identity(h.Card), h.Bounds })
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        throw new InvalidOperationException(description);
    }
    private void Capture(string name) { using var output = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height); }
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
            ? z with { Count = count, Cards = Enumerable.Range(0, count).Select(i => Permanent(mode, p.Id, i)).ToArray() }
            : z.Name == "Hand" && p.Id == 0 && mode != "ten" ? z with { Count = 12, Cards = Enumerable.Range(0, 12).Select(i => new Card {
                VisualId = "full-hand-" + i, CombatId = "full-hand-" + i, Name = "Forest", Type = "Basic Land — Forest"
            }).ToArray() } : z).ToArray() }).ToArray();
        Combat? combat = mode == "mixed" ? new([new("mixed-1-17", 0, ["mixed-0-18"], []) {
            Defender = new("player", "0", 0, "You"), Blocked = true
        }], [], null) : null;
        return state with { Id = "full-battlefield-" + mode, Players = players, Combat = combat, Stack = [], Activity = [],
            ActivePlayerId = mode == "mixed" ? 1 : 0, Decision = new() { Kind = "input", Intent = DecisionIntent.Priority,
                Message = "You have priority.", Ok = "OK", OkEnabled = true, CanAutoPass = false } };
    }
    private static Card Permanent(string mode, int seat, int index)
    {
        string id = mode + "-" + seat + "-" + index;
        var card = new Card { VisualId = id, CombatId = id, Name = mode == "fifty" ? "Soldier Token" : "Llanowar Elves",
            Type = mode == "fifty" ? "Token Creature — Soldier" : "Creature — Elf Druid", Power = mode == "fifty" ? 1 : 2,
            Toughness = mode == "fifty" ? 1 : 2, Text = mode == "fifty" ? "" : "{T}: Add {G}.", Selectable = mode == "ten" && seat == 0 && index == 9 };
        if (mode == "distinct") return card with { Name = $"Distinct creature {index + 1:00}", Text = "" };
        if (mode != "mixed") return card;
        return index switch {
            0 => card with { Name = "Treetop Village", Type = "Land Creature — Ape", Power = 3, Toughness = 3, CombatKeywords = ["trample"] },
            2 => card with { Tapped = true },
            3 => card with { Damage = 1 },
            4 => card with { Power = 3, Toughness = 3, Counters = new() { ["+1/+1"] = 1 } },
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
}
