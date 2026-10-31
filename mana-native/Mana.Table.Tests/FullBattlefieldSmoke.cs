using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Scenarios;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
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
    private readonly HashSet<string> unavailableArt;
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

    internal static void Run(string profile, Point? size, bool realArt = false, bool readableCrowd = false, string? artCache = null)
    {
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = false, TextScale = 1.3f }.Save(profile);
        var scenario = new InteractionScenarioEngine();
        scenario.Present(readableCrowd ? ReadableCrowd() : Example("seven"));
        ICardArtSource images = new CardReadingArtFixture();
        var unavailableArt = new HashSet<string>(StringComparer.Ordinal);
        if (realArt) {
            var lab = new ExperienceLabSession(CounterPaymentScenario.Id); lab.Replay(13);
            var snapshots = new[] { scenario.State, Example("mixed"), Example("fifty"), Example("distinct"), lab.State }
                .Concat(TappedResourcesScenario.Create().Select(stage => stage.Snapshot));
            var requests = snapshots.SelectMany(state => state.Players).SelectMany(player => player.Zones)
                .SelectMany(zone => zone.Cards).Select(CardPresentation.Artwork).OfType<ArtRequest>().DistinctBy(art => art.Key).ToArray();
            var requiredArt = new HashSet<string>(["Forest", "Mountain", "Plains", "Sol Ring"], StringComparer.Ordinal);
            ICardArtSource source;
            if (artCache != null) {
                string cache = Path.GetFullPath(artCache);
                var manifest = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var art in requests) {
                    string file = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(art.Key))) + ".jpg";
                    if (File.Exists(Path.Combine(cache, file))) manifest.Add(art.Key, file);
                    else if (requiredArt.Contains(art.Name)) throw new InvalidOperationException("The cache-only full-card check requires actual artwork for " + art.Name);
                    else unavailableArt.Add(art.Key);
                }
                File.WriteAllText(Path.Combine(profile, "artwork-source.json"), JsonSerializer.Serialize(new { Mode = "local-cache-only", Directory = cache, Manifest = manifest }));
                source = new LocalArtSource(cache, manifest);
            } else {
                source = new MagicArtSource(profile);
                foreach (var art in requests) {
                    try {
                        if (source.LoadAsync(art, CancellationToken.None).GetAwaiter().GetResult() is not { Length: > 0 })
                            throw new InvalidOperationException("The full-card art check did not receive image bytes for " + art.Name);
                    } catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound && !requiredArt.Contains(art.Name)) {
                        unavailableArt.Add(art.Key);
                    } catch (Exception error) {
                        throw new InvalidOperationException("Full-card artwork preload failed for " + art.Name + " (" + art.Key + ")", error);
                    }
                }
                File.WriteAllText(Path.Combine(profile, "artwork-source.json"), "{\"Mode\":\"public-network-and-cache\"}");
            }
            File.WriteAllText(Path.Combine(profile, "unavailable-art.json"), JsonSerializer.Serialize(unavailableArt.Order().ToArray()));
            images.Dispose(); images = source;
        }
        using var game = new TableWindow(TestTableServices.For(scenario), profile, null, null, size,
            initialState: scenario.State, artSource: images, enableLiveTableStudy: true);
        game.Automation = new FullBattlefieldSmoke(game.TestPort, scenario, readableCrowd, unavailableArt);
        game.Run();
    }

    private FullBattlefieldSmoke(TableWindow.Probe view, InteractionScenarioEngine scenario, bool readableCrowd, HashSet<string> unavailableArt)
    {
        this.view = view; this.scenario = scenario; this.unavailableArt = unavailableArt;
        GameSnapshot? threePlayerSnapshot = null;
        Dictionary<string, SceneCard> threePlayerCards = [];
        void CheckThreePlayerCamera(string name)
        {
            var scene = view.Frame!.Scene!;
            var expectedPlayers = threePlayerSnapshot!.Players.Select(player => player.Id).ToHashSet();
            Check(scene.Seats.Count == 3 && scene.Seats.Select(seat => seat.PlayerId).ToHashSet().SetEquals(expectedPlayers)
                    && ReferenceEquals(view.Match, threePlayerSnapshot)
                    && scene.BattlefieldDrawOrder.Count == threePlayerCards.Count
                    && scene.BattlefieldDrawOrder.All(card => threePlayerCards.TryGetValue(card.Id, out var original)
                        && ReferenceEquals(card.Card, original.Card) && card.Owner == original.Owner && card.Pose == original.Pose)
                    && scenario.Replies.Count == 0,
                name + ": navigating the three-seat table retains the exact authorized players, card occurrences, controllers and canonical poses without a game command");
            Check(!view.Frame.Hits.Any(hit => hit.Id.StartsWith("playmat", StringComparison.OrdinalIgnoreCase)),
                name + ": physical play mats add no competing input targets");
        }
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
                Step(1.4, () => {
                    CheckFaces(mode);
                    Capture("03-" + mode);
                    if (mode == "mixed") Check(view.BattlefieldLabels("mixed-0-4").Any(label => label.Kind == "pt" && label.Text == "3/3"
                                && label.MeasuredWidth <= label.AvailableWidth)
                            && view.BattlefieldLabels("mixed-0-4").Any(label => label.Kind == "counter" && label.Text == "1"
                                && label.MeasuredWidth <= label.AvailableWidth),
                        "The full printed creature keeps its actual 3/3 characteristics and one-counter marker instead of reverting to its printing; actual labels: "
                            + JsonSerializer.Serialize(view.BattlefieldLabels("mixed-0-4")));
                    if (mode == "fifty") {
                        var front = view.Frame!.Scene!.Battlefield(0).SelectMany(cluster => cluster.Cards).Single(card => card.GroupFront);
                        Check(front.GroupCount == 50 && view.BattlefieldLabels(front.Id).Any(label => label.Kind == "copies" && label.Text == "×50"),
                            "The full printed token face still reports all fifty distinct current copies in its counted pile");
                    }
                });
            }
        }
        Step(0, () => MoveHit("focus:3")); Click();
        Step(.65, () => {
            Check(view.StudyFocusOwner == 3 && scenario.Replies.Count == 0,
                "An actual opponent nameplate frames the same battlefield without changing game state");
            CheckPrintedFaces("Focused battlefield");
            CheckResourceOcclusion("Focused battlefield");
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
        });
        Step(0, () => Publish(TappedResourcesScenario.Create()[3].Snapshot));
        Step(1.5, () => {
            Check(view.Match!.Players.All(player => Mana.Magic.PublicResources.Lands(player) is { Total: 9, Untapped: 3, Tapped: 6 }),
                "The mixed-land case retains exact total/untapped/tapped counts for every controller");
            CheckPrintedFaces("Mixed-land overview");
            Capture("09-counted-mixed-lands");
            selected = view.Frame!.Scene!.Battlefield(view.Match.ViewerId).SelectMany(cluster => cluster.Cards)
                .First(card => card.GroupFront && card.Card.Type.StartsWith("Basic Land", StringComparison.Ordinal) && !card.Card.Tapped).Id;
            MoveCard(selected);
        });
        Step(.45, () => {
            Check(view.Hovered is { } card && CardViews.Identity(card) == selected && scenario.Replies.Count == 0,
                "Hovering the actual untapped land highlights its exact occurrence without producing mana or opening a different object");
            Capture("09b-hover-physical-land"); MoveCard(selected);
        }); Click();
        Step(.65, () => {
            Check(view.Frame!.Cards.Single(card => card.Id == selected) is { Focused: true, Display.Size.X: >= 300 }
                    && view.HoverPreview != null && scenario.Replies.Count == 0,
                "Clicking that same land reads its complete printed card without tapping it or submitting a mana action");
            CheckPrintedFaces("Explicit land reader");
            Capture("09c-read-physical-land"); keyboard = new(Keys.Escape);
        });
        Step(.04, () => keyboard = new());
        Step(.65, () => {
            Check(view.HoverPreview == null && view.Match!.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == selected).Tapped == false
                    && scenario.Replies.Count == 0,
                "Closing the land reader returns the same untapped card to its counted pile without a rules command");
            MoveHit("focus:3");
        }); Click();
        Step(.65, () => {
            Check(view.StudyFocusOwner == 3 && scenario.Replies.Count == 0,
                "Focusing a seat preserves the same mixed tapped/untapped land occurrences without spending mana");
            CheckPrintedFaces("Focused mixed-land battlefield");
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
            CheckPrintedFaces("All tapped lands");
            Check(scenario.Replies.Count == 0, "Inspecting tap-state counts never spends or produces mana");
            Capture("10-all-lands-tapped");
        });
        Step(0, () => {
            var threePlayer = Example("mixed");
            // The four-player example's epoch was retired when the resource lab
            // replaced it. A separate authored table must use a fresh identity.
            threePlayer = threePlayer with { Id = "full-battlefield-three-player",
                Players = threePlayer.Players.Where(player => player.Id != 3).ToImmutableArray() };
            int topId = TableLayout.Opponents(threePlayer)[1].Id;
            Publish(threePlayer with {
                Players = threePlayer.Players.Select(player => player.Id != topId ? player : player with {
                    Mana = new[] { "W", "U", "B", "R", "G", "C" }.ToImmutableDictionary(symbol => symbol, _ => 1),
                    States = [new("counter:energy", "Energy", "counter", 2, null, "Two public energy counters."),
                        new("counter:poison", "Poison", "counter", 3, null, "Three public poison counters.")],
                    Zones = player.Zones.Select(zone => zone.Name != "Command" ? zone : zone with {
                        Count = 2,
                        Cards = [zone.Cards[0], zone.Cards[0] with {
                            Key = "p:full-field-partner-" + player.Id,
                            VisualId = "full-field-partner-" + player.Id,
                            CombatId = "full-field-partner-" + player.Id
                        }]
                    }).ToImmutableArray()
                }).ToImmutableArray()
            });
        });
        Step(.65, () => keyboard = new(Keys.Home));
        Step(.04, () => keyboard = new());
        Step(.85, () => {
            threePlayerSnapshot = view.Match!;
            threePlayerCards = view.Frame!.Scene!.BattlefieldDrawOrder.ToDictionary(card => card.Id);
            Check(threePlayerSnapshot.Players.Length == 3 && threePlayerSnapshot.ViewerId == 0
                    && threePlayerSnapshot.Players.All(player => player.Id != 3)
                    && view.TableStudyEnabled && view.BoardView.IsIdentity && view.StudyFocusOwner == null,
                "Opening a three-player table and pressing Home retains the authorized viewer and an unoccupied fourth seat"
                    + $" (players={string.Join(',', threePlayerSnapshot.Players.Select(player => player.Id))}, viewer={threePlayerSnapshot.ViewerId}, study={view.TableStudyEnabled}, camera={view.BoardView}, focus={view.StudyFocusOwner})");
            CheckFaces("Three-player battlefield");
            CheckThreePlayerCamera("Three-player overview");
            Capture("11-three-player-field"); MoveHit("settings");
        }); Click();
        Step(.1, () => MoveHit("motion")); Click();
        Step(.1, () => {
            Check(PlayPreferences.Load(view.Profile).ReducedMotion,
                "The actual settings gesture enables and persists reduced motion before three-player camera navigation");
            keyboard = new(Keys.Escape);
        });
        Step(.04, () => { keyboard = new(); Move(new(80, 55)); });
        Step(.1, () => {
            CheckThreePlayerCamera("Three-player reduced motion");
            Capture("12-three-player-reduced-motion");
            beforePan = view.BoardView.Offset; keyboard = new(Keys.W);
        });
        Step(.08, () => keyboard = new());
        Step(.1, () => {
            Check(view.BoardView.Offset != beforePan,
                "W moves the three-player table while reduced motion is enabled");
            CheckThreePlayerCamera("Three-player reduced-motion pan");
            Capture("13-three-player-reduced-pan");
            beforeZoom = view.BoardView.Scale; Move(new(800, 420)); Wheel(480);
        });
        Step(.1, () => {
            Check(view.BoardView.Scale > beforeZoom,
                "Wheel zoom enlarges the same three-player table while reduced motion is enabled");
            CheckThreePlayerCamera("Three-player reduced-motion zoom");
            CheckResourceOcclusion("Three-player reduced-motion zoom");
            int topId = TableLayout.Opponents(view.Match!)[1].Id;
            Check(view.CounterBadge(topId) == null
                    && !view.Frame!.Hits.Any(hit => hit.Id.StartsWith("player-state:" + topId + ":", StringComparison.Ordinal)),
                "The actual zoomed top resource group retires both paint and counter controls when a fixed commander would obscure its subtotal");
            Capture("14-three-player-reduced-zoom"); keyboard = new(Keys.Home);
        });
        Step(.04, () => keyboard = new());
        Step(.1, () => {
            Check(view.BoardView.IsIdentity && view.StudyFocusOwner == null,
                "Home immediately restores the whole three-player table with reduced motion enabled");
            CheckThreePlayerCamera("Three-player reduced-motion reset");
            CheckPrintedFaces("Three-player reduced-motion reset");
            int topId = TableLayout.Opponents(view.Match!)[1].Id;
            Check(view.CounterBadge(topId)?.Chips.Length == 2,
                "Home restores both unobscured public counter chips after zoomed readout occlusion");
            CheckResourceOcclusion("Three-player reduced-motion reset");
            Capture("15-three-player-reduced-reset");
            File.WriteAllText(Path.Combine(view.Profile, "full-battlefield-results.json"), JsonSerializer.Serialize(checks));
            File.WriteAllText(Path.Combine(view.Profile, "review.json"), "{\"humanReview\":\"pending\",\"note\":\"Mechanical checks do not establish visual readability.\"}");
            view.Exit();
        });
    }
    private void CheckResourceOcclusion(string name)
    {
        var frame = view.Frame!;
        var fixedCards = frame.Cards.Where(card => !BoardView.AffectsZone(card.Zone)).ToArray();
        var worldTotals = frame.Observe().Text.Where(text => text.Bounds.Height > 0
            && (text.Value.EndsWith(" lands", StringComparison.Ordinal) || text.Value.EndsWith(" land", StringComparison.Ordinal)
                || text.Value.Contains("untapped", StringComparison.Ordinal))).ToArray();
        Check(worldTotals.All(text => fixedCards.All(card => !card.Display.Bounds.Intersects(
                new Rectangle(text.Bounds.X, text.Bounds.Y, text.Bounds.Width, text.Bounds.Height))))
                && scenario.Replies.Count == 0,
            name + ": actual projected resource totals do not cross fixed public cards or submit game commands");
    }
    private void CheckFaces(string name)
    {
        var cards = view.Frame!.Scene!.Seats.SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards).ToArray();
        Check(cards.All(card => card.ScanFace) && cards.Length == view.Match!.Players.Sum(player => player.Zone("Battlefield").Cards.Length),
            name + ": all authorized occurrences use the normal battlefield face and retain their controller");
        Check(scenario.Replies.Count == 0, name + ": presentation sends no game command");
        CheckPrintedFaces(name);
    }
    private void CheckPrintedFaces(string name)
    {
        var frame = view.Frame!;
        var fronts = frame.Scene!.BattlefieldDrawOrder.Where(card => card.GroupFront || card.Attachment != null).ToArray();
        var fullFaces = frame.Drawing.Commands.OfType<DrawCommand.Surface>().Where(surface => surface.Texture != null)
            .Select(surface => surface.Pose).ToHashSet();
        var crops = frame.Drawing.Commands.OfType<DrawCommand.Sprite>().Where(sprite => sprite.Crop != null)
            .Select(sprite => sprite.Center).ToHashSet();
        Check(fronts.Length > 0 && fronts.All(node => {
            var card = frame.Cards.Single(card => card.Id == node.Id);
            float aspect = card.Display.Size.X / card.Display.Size.Y;
            return aspect is >= .68f and <= .76f
                && (CardPresentation.Artwork(node.Card) is not { } art || unavailableArt.Contains(art.Key) || fullFaces.Contains(card.Display))
                && !crops.Contains(card.Display.Center);
        }), name + ": actual GPU drawing retains complete portrait cards for creatures, artifacts, enchantments, lands and attachments without cropping their authorized faces");
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
        view.CaptureWindow(Path.Combine(view.Profile, name + "-window.png"));
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
            10 => card with { Name = "Sol Ring", Type = "Artifact", Power = null, Toughness = null, Tapped = seat == 1 },
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
