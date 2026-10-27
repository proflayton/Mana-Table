using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Scenarios;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

// Production rendering/input check. Occurrence counts establish retention and
// command safety; captured windows still need human UX review.
internal sealed class FullBattlefieldSmoke : ITableAutomation
{
    private readonly TableWindow.Probe view;
    private readonly InteractionScenarioEngine scenario;
    private readonly Queue<(double Delay, Action Action)> steps = [];
    private readonly List<string> checks = [];
    private MouseState mouse;
    private KeyboardState keyboard;
    private double lastStep;
    private string selected = "";
    private float beforeZoom;
    private Vector2 beforePan;
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;
    public KeyboardState? ReadKeyboard() => keyboard;

    internal static void Run(string profile, Point? size, bool realArt = false, bool readableCrowd = false)
    {
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = false, TextScale = 1.3f }.Save(profile);
        var scenario = new InteractionScenarioEngine();
        scenario.Present(readableCrowd ? ReadableCrowd() : Example("seven"));
        ICardArtSource images = new CardReadingArtFixture();
        if (realArt) {
            var source = new MagicArtSource(profile);
            foreach (string name in DistinctNames.Concat(new[] { "Forest", "Plains", "Island", "Toothy, Imaginary Friend", "Sol Ring", "Rhystic Study" }).Distinct())
                source.LoadAsync(new(name, "front"), CancellationToken.None).GetAwaiter().GetResult();
            images.Dispose(); images = source;
        }
        using var game = new TableWindow(TestTableServices.For(scenario), profile, null, null, size,
            initialState: scenario.State, artSource: images, enableLiveTableStudy: true);
        game.Automation = new FullBattlefieldSmoke(game.TestPort, scenario, readableCrowd);
        game.Run();
    }

    private FullBattlefieldSmoke(TableWindow.Probe view, InteractionScenarioEngine scenario, bool readableCrowd)
    {
        this.view = view; this.scenario = scenario;
        Step(1.5, () => { CheckFaces("Initial field"); Capture("01-normal-field"); });
        if (!readableCrowd) {
            Step(0, () => Publish(Example("eight")));
            Step(.65, () => {
                CheckFaces("Crossing the former density threshold");
                Check(view.Frame!.Scene!.BattlefieldCard(0, "threshold-0-0") is { ScanFace: true, Card.Name: "Grizzly Bears" },
                    "The same named occurrence keeps its normal face across the former count threshold");
                Capture("02-eight-creatures");
            });
            foreach (string mode in new[] { "mixed", "fifty", "distinct" }) {
                Step(0, () => Publish(Example(mode)));
                Step(1.4, () => { CheckFaces(mode); Capture("03-" + mode); });
            }
        }
        Step(0, () => MoveHit("focus:3")); Click();
        Step(.65, () => {
            Check(view.StudyFocusOwner == 3 && scenario.Replies.Count == 0,
                "An actual opponent nameplate frames the same battlefield without changing game state");
            Capture("04-player-camera");
            selected = view.Frame!.Scene!.Battlefield(3).SelectMany(cluster => cluster.Cards)
                .Last(card => card.GroupFront && !card.Card.FaceDown).Id;
            MoveCard(selected); beforeZoom = view.BoardView.Scale; Wheel(480);
        });
        Step(.65, () => {
            Check(view.BoardView.Scale > beforeZoom && scenario.Replies.Count == 0,
                "Wheel zoom enlarges the same occurrences without a rules command");
            Capture("05-camera-zoom"); MoveCard(selected);
        }); Click();
        Step(.65, () => {
            Check(view.Frame!.Cards.Any(card => card.Id == selected && card.Focused) && scenario.Replies.Count == 0,
                "A crowded opposing card remains individually readable after camera zoom");
            Capture("06-read-actual-card"); keyboard = new(Keys.Escape);
        });
        Step(.04, () => { keyboard = new(); Move(new(750, 430), middle: ButtonState.Pressed); beforePan = view.BoardView.Offset; });
        Step(.04, () => Move(new(820, 450), middle: ButtonState.Pressed));
        Step(.04, () => Move(new(820, 450)));
        Step(.65, () => {
            Check(view.BoardView.Offset != beforePan && scenario.Replies.Count == 0,
                "Middle-drag moves through the same table without selecting or submitting a card");
            Capture("07-camera-pan"); keyboard = new(Keys.Home);
        });
        Step(.04, () => keyboard = new());
        Step(.65, () => {
            Check(view.BoardView.IsIdentity && view.StudyFocusOwner == null && scenario.Replies.Count == 0,
                "Home returns to the shared table while preserving every seat and decision");
            var lab = new ExperienceLabSession(CounterPaymentScenario.Id); lab.Replay(13); Publish(lab.State);
        });
        Step(1.5, () => {
            CheckFaces("Canonical 480-permanent field"); Capture("08-canonical-crowd");
            Check(view.Frame!.Cards.Where(card => card.Zone == "Hand").All(card => card.Rest.Size.X > 50),
                "A crowded battlefield does not replace or compress the local hand");
            Publish(TappedResourcesScenario.Create()[3].Snapshot);
        });
        Step(1.5, () => {
            Check(view.Match!.Players.All(player => Mana.Magic.PublicResources.Lands(player) is { Total: 9, Untapped: 3, Tapped: 6 }),
                "The mixed-land case retains exact total/untapped/tapped counts for every controller");
            Capture("09-counted-mixed-lands");
            MoveHit("focus:3");
        }); Click();
        Step(.65, () => {
            Check(view.StudyFocusOwner == 3 && scenario.Replies.Count == 0,
                "Focusing a seat preserves the same mixed tapped/untapped land occurrences without spending mana");
            Capture("09a-focused-mixed-lands"); keyboard = new(Keys.Home);
        });
        Step(.04, () => keyboard = new());
        Step(.65, () => {
            Check(view.BoardView.IsIdentity && view.StudyFocusOwner == null && scenario.Replies.Count == 0,
                "Home returns from the focused mixed-land view to the same whole table without a rules command");
            Publish(TappedResourcesScenario.Create()[0].Snapshot);
        });
        Step(1.5, () => {
            Check(view.Match!.Players.All(player => Mana.Magic.PublicResources.Lands(player) is { Total: 3, Untapped: 0, Tapped: 3 }),
                "All tapped lands remain counted while reporting zero untapped lands");
            Check(scenario.Replies.Count == 0, "Inspecting tap-state counts never spends or produces mana");
            Capture("10-all-lands-tapped");
            File.WriteAllText(Path.Combine(view.Profile, "full-battlefield-results.json"), JsonSerializer.Serialize(checks));
            File.WriteAllText(Path.Combine(view.Profile, "review.json"), "{\"humanReview\":\"pending\",\"note\":\"Mechanical checks do not establish visual readability.\"}");
            view.Exit();
        });
    }
    private void CheckFaces(string name)
    {
        var cards = view.Frame!.Scene!.Seats.SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards).ToArray();
        Check(cards.All(card => card.ScanFace) && cards.Length == view.Match!.Players.Sum(player => player.Zone("Battlefield").Cards.Length),
            name + ": all authorized occurrences use the normal battlefield face and retain their controller");
        Check(scenario.Replies.Count == 0, name + ": presentation sends no game command");
    }
    private void Publish(GameSnapshot state) { scenario.Present(state); view.Accept(scenario.State); }
    private void Step(double delay, Action action) => steps.Enqueue((delay, action));
    private void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); checks.Add(text); }
    private Point Physical(Vector2 point)
    {
        var viewport = view.GraphicsDevice.Viewport;
        return RenderViewport.Fit(new(viewport.Width, viewport.Height), view.DesignHeight).ToPhysical(point).ToPoint();
    }
    private void Move(Point logical, ButtonState left = ButtonState.Released, ButtonState middle = ButtonState.Released)
    {
        var point = Physical(logical.ToVector2());
        mouse = new(point.X, point.Y, mouse.ScrollWheelValue, left, middle, default, default, default);
    }
    private void MoveHit(string id) => Move(view.Frame!.Hits.Last(hit => hit.Id == id).Bounds.Center);
    private void MoveCard(string id)
    {
        var card = view.Frame!.Cards.Single(card => card.Id == id); Move(card.Display.Center.ToPoint());
        if (view.HitAt(card.Display.Center.ToPoint())?.Card is not { } hit || !CardViews.Same(hit, card.Card))
            throw new InvalidOperationException("The actual center of " + id + " is covered.");
    }
    private void Wheel(int amount) => mouse = new(mouse.X, mouse.Y, mouse.ScrollWheelValue + amount, default, default, default, default, default);
    private void Click()
    {
        Step(.03, () => mouse = new(mouse.X, mouse.Y, mouse.ScrollWheelValue, ButtonState.Pressed, default, default, default, default));
        Step(.04, () => mouse = new(mouse.X, mouse.Y, mouse.ScrollWheelValue, default, default, default, default, default));
    }
    private void Capture(string name)
    {
        using var output = File.Create(Path.Combine(view.Profile, name + ".png"));
        view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height);
        File.WriteAllText(Path.Combine(view.Profile, name + ".frame.json"), JsonSerializer.Serialize(view.Frame!.Observe()));
    }
    public void Frame(Texture2D surface)
    {
        if (view.Now > 40) throw new TimeoutException("Normal battlefield/camera journey did not finish.");
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (!view.Loaded || view.StartupVisible) return;
        if (steps.TryPeek(out var step) && view.Now - lastStep >= step.Delay) { steps.Dequeue(); step.Action(); lastStep = view.Now; }
    }

    private static GameSnapshot Example(string mode)
    {
        int count = mode == "seven" ? 7 : mode == "eight" ? 8 : mode == "ten" ? 10 : mode == "mixed" ? 20 : 50;
        var state = PresentationSmoke.Example();
        var players = state.Players.Select(p => p with { Zones = p.Zones.Select(z => z.Name == "Battlefield"
            ? z with { Count = count, Cards = Enumerable.Range(0, count).Select(i => Permanent(mode, p.Id, i)).ToImmutableArray() }
            : z.Name == "Hand" && p.Id == 0 && mode != "ten" ? z with { Count = 12, Cards = Enumerable.Range(0, 12).Select(i => new Card {
                VisualId = "full-hand-" + i, CombatId = "full-hand-" + i, Name = "Forest", Type = "Basic Land — Forest"
            }).ToImmutableArray() } : z).ToImmutableArray() }).ToArray();
        Combat? combat = mode == "mixed" ? new([new("mixed-1-17", 0, ["mixed-0-18"], []) {
            Defender = new("player", "0", 0, "You"), Blocked = true
        }], [], null) : null;
        return state with { Id = "full-battlefield", Players = players.ToImmutableArray(), Combat = combat, Stack = [], Activity = [],
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
        string id = (mode is "seven" or "eight" ? "threshold" : mode) + "-" + seat + "-" + index;
        var card = new Card { VisualId = id, CombatId = id, Name = mode == "fifty" ? "Soldier Token" : "Llanowar Elves",
            Type = mode == "fifty" ? "Token Creature — Soldier" : "Creature — Elf Druid", Power = mode == "fifty" ? 1 : 2,
            Toughness = mode == "fifty" ? 1 : 2, Text = mode == "fifty" ? "" : "{T}: Add {G}.", Selectable = mode == "ten" && seat == 0 && index == 9 };
        if (mode is "distinct" or "seven" or "eight") return card with { Name = DistinctNames[index], Text = "" };
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
