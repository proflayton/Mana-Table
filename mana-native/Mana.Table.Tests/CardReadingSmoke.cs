using System.Text.Json;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Magic;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Exercises real pointer input and GPU output. The scenario records commands;
// the Forge UI smoke separately verifies that deliberate gestures play a land.
internal sealed class CardReadingSmoke : ITableAutomation
{
    private readonly TableGame.Probe view;
    private readonly InteractionScenarioEngine scenario;
    private readonly Queue<(double Delay, Action Action, Func<bool>? Ready)> steps = [];
    private readonly List<string> checks = [];
    private MouseState mouse;
    private double lastStep;
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;

    public CardReadingSmoke(TableGame.Probe view, InteractionScenarioEngine scenario)
    {
        this.view = view; this.scenario = scenario;
        Step(1.5, () => MoveCard("hand-3"));
        StepWhen(() => view.Hovered?.VisualId == "hand-3" && view.HoverPreview is { Width: >= 400, Height: >= 560 }
            && view.DisplayedPose("hand-3") is { Size.X: >= 400 } && view.CardPaintCount("hand-3") == 1, 3, () => {
            Check(view.Hovered?.VisualId == "hand-3" && view.HoverPreview is { Width: >= 400, Height: >= 560 }, "The hand card itself grows to reading size without a click");
            Check(view.DisplayedPose("hand-3") is { Size.X: >= 400 } && view.CardPaintCount("hand-3") == 1, "The enlarged hand card is the original scene occurrence, painted exactly once");
            var preview = view.HoverPreview!.Value;
            Check(preview.Left >= 0 && preview.Right <= 1600 && preview.Top >= 0 && preview.Bottom < 900, "The large preview stays within the table viewport");
            Check(scenario.Replies.Count == 0 && view.Overlay == "", "Reading a card neither submits an action nor opens a blocking panel");
            Check(view.Surface is RenderTarget2D { MultiSampleCount: >= 2 }, "The table render target uses multisample edge antialiasing");
            var texture = view.Art(view.Hovered!);
            Check(texture is { LevelCount: > 1 }, "Card artwork retains its full-resolution image and a mip chain for minification");
            var pixels = new Color[Math.Max(1, texture!.Width >> 2) * Math.Max(1, texture.Height >> 2)];
            texture.GetData(2, null, pixels, 0, pixels.Length);
            Check(pixels.Any(c => c.A > 0 && c.R > 0), "The GPU generated populated mip levels, not empty texture storage");
            Capture("01-hand-hover");
        });
        Click();
        Step(.6, () => Check(scenario.Replies.Count == 0 && view.Overlay == "", "A single click on a playable hand card is safe for reading"));
        Step(0, () => MoveCard("hand-4"));
        Step(.65, () => Check(view.Hovered?.VisualId == "hand-4" && view.DisplayedPose("hand-4") is { Size.X: >= 400 }, "The fan remains browsable when an enlarged card overlaps neighboring slots"));
        Step(0, () => MoveCard("hand-3"));
        Step(.65, () => { });
        Step(0, () => Move(new(10, 70)));
        Step(.07, () => {
            Check(view.DisplayedPose("hand-3") is { Size.X: > 150 and < 400 } && view.CardPaintCount("hand-3") == 1, "Leaving hover smoothly returns the same card to its hand slot");
            Capture("04-returning-to-hand");
        });
        StepWhen(() => view.DisplayedPose("hand-3") is { Size.X: < 140 }
            && view.CardPaintCount("hand-3") == 1 && view.Hovered == null && view.HoverPreview == null, 3,
            () => Check(view.DisplayedPose("hand-3") is { Size.X: < 140 } && view.CardPaintCount("hand-3") == 1
                && view.Hovered == null && view.HoverPreview == null, "The card settles back to its normal hand size"));
        Step(0, () => MoveCard("hand-3"));
        Step(.65, () => { });
        Step(0, () => Buttons(right: ButtonState.Pressed));
        Step(.05, () => Buttons());
        Step(.1, () => Check(view.Overlay == "inspect" && scenario.Replies.Count == 0, "Right-click inspects a playable card without playing it"));
        Step(0, () => MoveHit("close-overlay")); Click();
        Step(.6, () => MoveCard("hand-0"));
        Step(.5, () => { }); Click();
        Step(.02, () => MoveCard("hand-2")); Click();
        Step(.08, () => Check(scenario.Replies.Count == 0, "Fast clicks on different cards never combine into a play"));
        Step(0, () => Publish(scenario.State));
        Step(.04, () => MoveCard("hand-2")); Click();
        Step(.6, () => Check(scenario.Replies.Count == 0, "A new decision invalidates the first click of a pending double-click"));
        Step(0, () => MoveCard("hand-2")); Click();
        Step(.02, () => Move(new(0, 0))); Click();
        Step(.02, () => MoveCard("hand-2")); Click();
        Step(.6, () => Check(scenario.Replies.Count == 0, "Clicking empty space interrupts a pending double-click"));
        Step(0, () => MoveCard("hand-2")); Click(); Click();
        Step(.35, () => {
            Check(scenario.Replies.Count == 1 && scenario.Replies[0].Action == ReplyAction.SelectCard, "A deliberate double-click submits exactly one card command");
            Check(view.Match!.Viewer!.Zone("Battlefield").Cards.Any(c => c.VisualId == "hand-2"), "The played card follows the resulting engine projection onto the battlefield");
        });
        Step(.5, () => MoveCard("hand-4"));
        Step(.4, () => { }); Click();
        Step(.02, () => Buttons(ButtonState.Pressed));
        Step(.05, () => Move(new(1550, 80), ButtonState.Pressed));
        Step(.05, () => Buttons());
        Step(.05, () => MoveCard("hand-4")); Click();
        Step(.2, () => Check(scenario.Replies.Count == 1, "Cancelling a drag clears the pending play gesture"));
        Step(0, () => Publish(scenario.State with { Decision = new() { Kind = "input", Intent = DecisionIntent.Selection } }));
        Step(.5, () => MoveCard("hand-4")); Click();
        Step(.3, () => Check(scenario.Replies.Count == 2, "Required hand selections still submit with one click"));
        Step(0, () => {
            var state = scenario.State;
            Publish(state with { Decision = null, Players = state.Players.Select(p => p.Id != 0 ? p : p with { Zones = p.Zones.Select(z => z.Name != "Hand" ? z : z with {
                Cards = z.Cards.Select(c => c with { FaceDown = true, Selectable = false }).ToArray() }).ToArray() }).ToArray() });
            Move(new(0, 0));
        });
        Step(.6, () => MoveCard("hand-4"));
        Step(.4, () => Check(view.HoverPreview == null, "Concealed cards never gain a readable hover preview"));
        Step(0, () => {
            var state = PresentationSmoke.Example();
            Publish(state with { Players = state.Players.Select(p => p.Id != 0 ? p : p with { Zones = p.Zones.Select(z => z.Name != "Hand" ? z : z with { Cards = z.Cards.Select(c => c.VisualId != "hand-3" ? c : c with {
                Name = "No-art readability fixture", ArtName = "No-art readability fixture", Text = "Vigilance\nWhenever this creature attacks, draw a card.\nYou may play an additional land on each of your turns."
            }).ToArray() }).ToArray() }).ToArray() });
            Move(new(0, 0));
        });
        Step(.6, () => MoveCard("hand-3"));
        Step(.6, () => {
            Check(view.HoverPreview != null && view.Art(view.Hovered!) == null, "The readable rules fallback works even without card art");
            Capture("02-rules-without-art");
        });
        Step(0, () => MoveCard("seat-2-1"));
        Step(.5, () => {
            Check(view.Hovered?.VisualId == "seat-2-1" && view.HoverPreview is { Width: >= 400 }, "Opponent permanents also offer a large passive preview");
            Capture("03-opponent-hover");
        });
        Step(0, () => MoveHit("pile-card:0:Graveyard"));
        Step(.5, () => {
            Check(view.Hovered?.VisualId == "grave-0" && view.HoverPreview != null, "Visible graveyard top cards can be read without opening the pile");
        }); Click();
        Step(.1, () => Check(view.ZoneName == "Graveyard" && scenario.Replies.Count == 2, "An enlarged pile card still opens its browser with a single click"));
        Step(0, () => MoveHit("close-overlay")); Click();
        Step(0, () => {
            Publish(scenario.State with { ActivePlayerId = 1, PhaseKey = "COMBAT_END", Decision = new() { Kind = "input", Intent = DecisionIntent.Priority, CanAutoPass = false, OkEnabled = true } });
            MoveHit("auto");
        }); Click();
        Step(.05, () => {
            MoveCard("hand-0");
            Publish(scenario.State with { Decision = scenario.State.Decision! with { CanAutoPass = true } });
        });
        Step(1.2, () => {
            Check(scenario.Replies.Count == 2 && view.HoverPreview != null, "Auto priority waits while a card is being read on hover");
            Move(new(10, 70));
        });
        Step(.3, () => {
            Check(scenario.Replies.Count == 3 && scenario.Replies[^1].Action == ReplyAction.AutoPass && (view.Match?.Decision == null || view.Busy),
                "Leaving card inspection passes exactly once while waiting for the next decision");
        });
        Step(0, () => Publish(scenario.State with {
            Stack = [new("Llanowar Elves", "Creature spell", false, scenario.State.Viewer!.Zone("Hand").Cards[0]) { Id = "paced-spell" }],
            Decision = new() { Kind = "input", Intent = DecisionIntent.Priority, CanAutoPass = true, OkEnabled = true }
        }));
        Step(.45, () => {
            Check(scenario.Replies.Count == 3 && view.Match?.Stack.Length == 1, "Auto keeps a new spell visible while its presentation beat plays");
            Publish(scenario.State); // A refreshed decision must not restart playback.
        });
        Step(.9, () => Check(scenario.Replies.Count == 4 && scenario.Replies[^1].Action == ReplyAction.AutoPass,
            "Auto resumes after the visible action despite intervening snapshot refreshes"));
        QueueRevealChecks();
        QueueTableChoiceChecks();
        QueueStateChecks();
        Step(0, () => {
            File.WriteAllText(Path.Combine(view.Profile, "reading-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            view.Exit();
        });
    }
    private void QueueRevealChecks()
    {
        int repliesBeforeReveal = 0;
        // Forge's revealed faces have no persistent identity or actionable key.
        var card = new Card { Name = "Llanowar Elves", Type = "Creature — Elf Druid", ManaCost = "{G}", Text = "{T}: Add {G}.", Power = 1, Toughness = 1 };
        Step(0, () => {
            repliesBeforeReveal = scenario.Replies.Count;
            Publish(scenario.State with { Decision = new() { Kind = "reveal", Title = "Revealed cards", Choices = [new(0, card.Name, card), new(1, card.Name, card with { })] } });
        });
        Step(.15, () => MoveHit("choice:0"));
        Step(.6, () => {
            Check(view.HoverPreview is { Width: >= 400, Height: >= 560 } && view.CardPaintCount("choice-face:" + view.Scope + ":0") == 1,
                "An anonymous revealed face enlarges in place and is painted exactly once");
            Check(scenario.Replies.Count == repliesBeforeReveal && view.Selections.Count == 0, "Reading a revealed card does not select it or advance the decision");
            Capture("05-revealed-hover");
        }); Click();
        Step(.1, () => {
            Check(scenario.Replies.Count == repliesBeforeReveal && view.Match?.Decision?.Kind == "reveal" && view.HoverPreview is { Width: >= 400 }, "Clicking a view-only revealed face neither selects nor dismisses its reader");
            MoveHit("choice:1");
        });
        Step(.6, () => {
            Check(view.DisplayedPose("choice-face:" + view.Scope + ":1") is { Size.X: >= 400 }, "Identically named revealed cards remain independently browsable");
            var done = view.Hits.Single(h => h.Id == "choicesdone");
            Check(view.HitAt(done.Bounds.Center)?.Id == done.Id, "Continue stays accessible while a revealed card is enlarged");
            MoveHit("choicesdone");
        }); Click();
        Step(.15, () => {
            Check(scenario.Replies.Count == repliesBeforeReveal + 1 && scenario.Replies[^1].Action == ReplyAction.Choose && view.HoverPreview == null,
                "Only Continue acknowledges the reveal and removes its readable faces");
            Publish(scenario.State with { Decision = new() { Kind = "reveal", LibraryCards = Enumerable.Range(0, 8).Select(i => new LibraryChoice(null, card.Name, card with { })).ToArray() } });
        });
        Step(.15, () => MoveHit("reveal:0"));
        Step(.6, () => {
            Check(view.HoverPreview is { Width: >= 400 }, "Revealed library faces without choice indices also enlarge");
            MoveHit("reveal:6");
        });
        Step(.6, () => {
            Check(view.HoverPreview is { Width: >= 400 } && new[] { "choicesnext", "choicesfilter", "choicesdone" }.All(id => {
                var hit = view.Hits.Single(h => h.Id == id); return view.HitAt(hit.Bounds.Center)?.Id == id;
            }), "A full gallery keeps paging, search and Continue accessible beside an enlarged edge card");
            MoveHit("choicesnext");
        }); Click();
        Step(.2, () => {
            Check(view.HoverPreview == null && view.DisplayedPose("choice-face:" + view.Scope + ":6") == null,
                "Paging a reveal removes the previous page's enlarged occurrence");
            MoveHit("reveal:7");
        });
        Step(.6, () => {
            Check(view.HoverPreview is { Width: >= 400 }, "Read-only faces on later gallery pages retain their own hover identity");
            Publish(scenario.State with { Decision = new() { Kind = "reveal", LibraryCards = [new(null, "Face-down card", card with { FaceDown = true, Text = "", Name = "Face-down card" })] } });
        });
        Step(.25, () => {
            Check(view.HoverPreview == null, "Revoking a revealed face immediately removes its enlargement");
            Publish(scenario.State with { Decision = new() { Kind = "choice", Min = 1, Max = 3,
                Choices = [new(2, card.Name, card), new(5, card.Name, card with { }), new(9, card.Name, card with { })] } });
        });
        Step(.15, () => MoveHit("choice:2"));
        StepWhen(() => view.HoverPreview is { Width: >= 400 }, 2, () =>
            Check(view.HoverPreview is { Width: >= 400 } && view.Selections.Count == 0, "Selectable anonymous gallery cards can be read before making a choice")); Click();
        Step(0, () => {
            Check(view.Selections.SequenceEqual([2]) && scenario.Replies.Count == repliesBeforeReveal + 1,
                "Clicking an enlarged choice stages its authoritative index without submitting or playing it");
            Check(ChoiceCompact(0) && HitReachable("choice:5"),
                "Selecting a gallery face immediately restores its normal footprint and the adjacent face's real hit area");
            Capture("05b-choice-selected");
        });
        Step(.65, () => {
            Check(ChoiceCompact(0) && view.HoverPreview == null, "A selected anonymous face stays compact under a stationary pointer beyond the hover delay");
            MoveHit("choice:5");
        }); Click();
        Step(0, () => Check(view.Selections.SequenceEqual([2, 5]) && ChoiceCompact(1),
            "An adjacent compact face can be selected immediately without reopening the previous reader"));
        Step(.65, () => {
            Check(view.Selections.SequenceEqual([2, 5]) && ChoiceCompact(1) && view.HoverPreview == null,
                "A fast adjacent selection also stays compact when the pointer stops");
            MoveHit("choice:9");
        }); Click();
        Step(0, () => {
            Check(view.Selections.SequenceEqual([2, 5, 9]) && scenario.Replies.Count == repliesBeforeReveal + 1 && ChoiceCompact(2),
                "All three identical faces preserve their separate authoritative indices without automatic confirmation");
            Capture("05c-multichoice-selected"); Move(new(10, 70));
        });
        Step(.06, () => MoveHit("choice:2"));
        StepWhen(() => view.DisplayedPose("choice-face:" + view.Scope + ":0") is { Size.X: >= 400 }, 2, () => {
            Check(view.DisplayedPose("choice-face:" + view.Scope + ":0") is { Size.X: >= 400 } && view.Selections.SequenceEqual([2, 5, 9]),
                "Leaving and returning lets a selected anonymous face be read again without changing its selection");
            Capture("05d-selected-choice-reread");
            MoveHit("choice:5");
        }); Click();
        Step(0, () => Check(view.Selections.SequenceEqual([2, 9]) && ChoiceCompact(0) && ChoiceCompact(1),
            "Quickly deselecting a neighboring face collapses both the clicked face and the previous enlarged reader without changing other indices"));
        Step(.65, () => {
            Check(view.HoverPreview == null && ChoiceCompact(0) && ChoiceCompact(1) && scenario.Replies.Count == repliesBeforeReveal + 1,
                "A deselected face stays compact without a game command while the pointer remains stationary");
            Move(new(10, 70));
            Publish(scenario.State with { Decision = new() { Kind = "choice", Min = 1, Max = 3,
                LibraryCards = [new(10, card.Name, card), new(20, card.Name, card with { }), new(30, card.Name, card with { })] } });
        });
        Step(.15, () => MoveHit("choice:10"));
        StepWhen(() => view.HoverPreview is { Width: >= 400 }, 2, () =>
            Check(view.HoverPreview is { Width: >= 400 }, "Selectable library faces retain their normal preselection reader")); Click();
        Step(0, () => Check(view.Selections.SequenceEqual([10]) && ChoiceCompact(0) && HitReachable("choice:20"),
            "A library selection collapses by its scoped original slot and leaves the next library hit accessible"));
        Step(.65, () => Check(view.HoverPreview == null && ChoiceCompact(0) && scenario.Replies.Count == repliesBeforeReveal + 1,
            "Selecting from a library never restarts the reader or advances the game under a stationary pointer"));
    }
    private bool ChoiceCompact(int slot) => view.DisplayedPose("choice-face:" + view.Scope + ":" + slot) is { Size.X: <= 150 }
        && view.CardPaintCount("choice-face:" + view.Scope + ":" + slot) == 1;
    private bool HitReachable(string id)
    {
        var hit = view.Hits.Last(h => h.Id == id);
        if (view.HitAt(hit.Bounds.Center)?.Id == id) return true;
        for (int y = hit.Bounds.Top + 8; y < Math.Min(895, hit.Bounds.Bottom); y += 6)
            for (int x = hit.Bounds.Left + 8; x < hit.Bounds.Right; x += 6)
                if (view.HitAt(new(x, y))?.Id == id) return true;
        return false;
    }
    private void QueueTableChoiceChecks()
    {
        int repliesBefore = 0;
        string expectedGame = "", expectedDecision = "";
        const string battlefield = "seat-0-1", hand = "hand-0";
        Step(0, () => {
            Move(new(10, 70));
            var state = PresentationSmoke.Example();
            var creature = state.Viewer!.Zone("Battlefield").Cards.Single(c => c.VisualId == battlefield);
            var handCard = state.Viewer.Zone("Hand").Cards.Single(c => c.VisualId == hand);
            Publish(state with { Decision = new() { Kind = "choice", Min = 1, Max = 2, Choices = [
                new(4, creature.Name, creature) { CardId = battlefield }, new(9, handCard.Name, handCard) { CardId = hand }
            ] } });
            repliesBefore = scenario.Replies.Count;
        });
        Step(.5, () => MoveCard(battlefield));
        StepWhen(() => view.DisplayedPose(battlefield) is { Size.X: >= 400 }, 2, () =>
            Check(view.DisplayedPose(battlefield) is { Size.X: >= 400 }, "A direct battlefield choice can be read before selection")); Click();
        Step(0, () => {
            var adjacent = view.Hits.Last(h => h.Card?.VisualId == hand && h.Zone == "Hand");
            Check(view.Selections.SequenceEqual([4]) && view.DisplayedPose(battlefield) is { Size.X: <= 150 }
                && view.CardPaintCount(battlefield) == 1 && HitReachable(adjacent.Id),
                "Direct battlefield selection immediately shrinks its reader and leaves the hand's real hit area available");
            Capture("05e-direct-selected");
        });
        Step(.65, () => {
            Check(view.HoverPreview == null && view.DisplayedPose(battlefield) is { Size.X: <= 150 },
                "A selected battlefield occurrence stays compact while the pointer is stationary");
            MoveCard(hand);
        }); Click();
        Step(0, () => Check(view.Selections.SequenceEqual([4, 9]) && view.DisplayedPose(hand) is { Size.X: <= 150 }
            && scenario.Replies.Count == repliesBefore, "Direct hand and battlefield selections preserve exact indices without an automatic command"));
        Step(.65, () => {
            Check(view.HoverPreview == null && view.DisplayedPose(hand) is { Size.X: <= 150 },
                "A fast direct hand selection also stays compact after the normal hover delay");
            Move(new(10, 70));
        });
        Step(.06, () => MoveCard(hand));
        StepWhen(() => view.DisplayedPose(hand) is { Size.X: >= 400 }, 2, () => {
            Check(view.DisplayedPose(hand) is { Size.X: >= 400 } && view.Selections.SequenceEqual([4, 9]),
                "A selected hand occurrence can be read again after leaving and returning");
            Capture("05f-selected-hand-reread");
        }); Click();
        Step(0, () => Check(view.Selections.SequenceEqual([4]) && view.DisplayedPose(hand) is { Size.X: <= 150 },
            "Deselecting the enlarged hand card collapses it without losing the battlefield selection"));
        Step(.65, () => {
            Check(view.HoverPreview == null && scenario.Replies.Count == repliesBefore, "Direct deselection stays compact and never submits a command");
            expectedGame = view.Match!.Id; expectedDecision = view.Match.Decision!.Id; MoveHit("confirm");
        }); Click();
        StepWhen(() => scenario.Replies.Count == repliesBefore + 1, 2, () =>
            Check(scenario.Replies.Count == repliesBefore + 1 && scenario.Replies[^1] is { Action: ReplyAction.Choose } reply
                && reply.GameId == expectedGame && reply.DecisionId == expectedDecision && reply.Choices!.SequenceEqual([4]),
                "Only explicit confirmation submits the remaining exact selection with its current game and decision scope"));
    }
    private void QueueStateChecks()
    {
        Pose original = default;
        Step(0, () => { Move(new(10, 70)); Publish(PresentationSmoke.StateExample()); });
        Step(1, () => {
            var scene = TableScene.Build(view.Match!, 0, new(-1, -1), null, null, false, CardPresentation.IsResource);
            original = scene.Rank(0, false).Cards.Single(c => c.Id == "seat-0-1").Pose;
            var displayed = view.DisplayedPose("seat-0-1");
            Check(displayed is { Elevation: > 12 } && displayed.Value.Center.Y < original.Center.Y - 10, "A creature with projected flying rests above the battlefield");
            MoveCard("seat-0-1"); Capture("06-card-states");
        });
        StepWhen(() => view.Hovered?.VisualId == "seat-0-1" && view.HoverPreview is { Width: >= 400 }, 2, () => {
            Check(view.Hovered?.VisualId == "seat-0-1" && view.HoverPreview is { Width: >= 400 }, "An airborne creature uses its displayed pose for readable hover");
            Capture("07-current-state");
            Move(new(10, 70));
            var state = scenario.State;
            Publish(state with { Players = state.Players.Select(p => p with { Zones = p.Zones.Select(z => z with { Cards = z.Cards.Select(c => c.VisualId == "seat-0-1"
                ? c with { CombatKeywords = [], Counters = [], Power = 1, Toughness = 1 } : c).ToArray() }).ToArray() }).ToArray() });
        });
        Step(1, () => {
            Check(view.DisplayedPose("seat-0-1") is { Elevation: < 1 } pose && Microsoft.Xna.Framework.Vector2.Distance(pose.Center, original.Center) < 2,
                "Losing flying returns the same occurrence to the table");
            Check(view.Match!.Viewer!.Zone("Battlefield").Cards.Single(c => c.VisualId == "seat-0-1") is { Counters.Count: 0, CombatKeywords.Length: 0, Power: 1 },
                "Counter and ability removals replace the displayed state without stale markers");
            Capture("08-states-removed");
        });
        int repliesBeforePhasedClick = 0;
        bool IsleReadable() => view.Hovered is { VisualId: "seat-0-0", PhasedOut: true, CombatKeywords.Length: 0 }
            && view.HoverPreview is { Width: >= 400, Height: >= 560 }
            && view.DisplayedPose("seat-0-0") is { Size.X: >= 408, Size.Y: >= 570, Angle: >= -.02f and <= .02f, Tilt: >= .99f };
        Step(0, () => {
            foreach (int seat in new[] { 0, 1, 2, 3 })
                Check(view.DisplayedPose($"seat-{seat}-0") is { Elevation: > 12 }, $"Seat {seat}'s phased-out land floats even without combat abilities");
            MoveCard("seat-0-0");
        });
        StepWhen(IsleReadable, 2, () => {
            Check(IsleReadable() && view.CardPaintCount("seat-0-0") == 1,
                "A phased-out land settles upright at reading size using its floating hit geometry");
            repliesBeforePhasedClick = scenario.Replies.Count;
            Capture("09-phased-out-land");
        });
        Click();
        Step(.1, () => {
            Check(view.Overlay == "inspect" && scenario.Replies.Count == repliesBeforePhasedClick,
                "Clicking a phased-out land inspects it without submitting a game action");
            Check(!view.Hits.Any(h => h.Id == "activate-ability"), "A phased-out land's inspector exposes no enabled ability action");
            Capture("09b-phased-out-inspector");
            MoveHit("close-overlay");
        }); Click();
        Step(.1, () => {
            Check(view.Overlay.Length == 0 && scenario.Replies.Count == repliesBeforePhasedClick,
                "Closing phased-out inspection returns to the table without a game action");
            Move(new(10, 70));
            var state = scenario.State;
            Publish(state with { Players = state.Players.Select(p => p with { Zones = p.Zones.Select(z => z with {
                Cards = z.Cards.Select(c => c.PhasedOut ? c with { PhasedOut = false, Tapped = false } : c).ToArray()
            }).ToArray() }).ToArray() });
        });
        Step(1, () => {
            var scene = TableScene.Build(view.Match!, 0, new(-1, -1), null, null, false, CardPresentation.IsResource);
            var isleRest = scene.Rank(0, true).Cards.Single(c => c.Id == "seat-0-0").Pose;
            Check(view.DisplayedPose("seat-0-0") is { Elevation: < 1, Angle: >= -.02f and <= .02f } pose
                && Microsoft.Xna.Framework.Vector2.Distance(pose.Center, isleRest.Center) < 2,
                "Phasing in settles the same untapped land occurrence into its current battlefield cell without floating");
            Check(view.Match!.Viewer!.Zone("Battlefield").Cards.Single(c => c.VisualId == "seat-0-0") is { PhasedOut: false, Tapped: false },
                "The returning land renders the engine's untapped state without stale phasing");
            Capture("10-phased-in-land");
        });
    }
    private void Step(double delay, Action action) => steps.Enqueue((delay, action, null));
    // Saving a GPU capture blocks Draw; allow subsequent motion to settle within a
    // bounded deadline rather than counting that capture time as animation frames.
    private void StepWhen(Func<bool> ready, double timeout, Action action) => steps.Enqueue((timeout, action, ready));
    private void Click() { Step(.025, () => Buttons(ButtonState.Pressed)); Step(.04, () => Buttons()); }
    private void Buttons(ButtonState left = ButtonState.Released, ButtonState right = ButtonState.Released) => mouse = new(mouse.X, mouse.Y, 0, left, ButtonState.Released, right, ButtonState.Released, ButtonState.Released);
    private void Move(Point point, ButtonState left = ButtonState.Released)
    {
        var viewport = view.GraphicsDevice.Viewport;
        float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        mouse = new((int)((viewport.Width - 1600 * scale) / 2 + point.X * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + point.Y * scale), 0,
            left, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    private void MoveCard(string id)
    {
        var hit = view.Hits.Last(h => h.Card?.VisualId == id);
        if (hit.Zone == "Hand") {
            // Approach the fan through its exposed strip, as a player does.
            // A face still returning from hover has a moving center over the table.
            var hand = view.Match!.Viewer!.Zone("Hand").Cards;
            int index = Array.FindIndex(hand, c => c.VisualId == id);
            var rest = TableLayout.Hand(index % 11, Math.Min(11, hand.Length));
            for (int y = 890; y >= Math.Max(810, rest.Bounds.Top + 8); y -= 5)
                for (int x = rest.Bounds.Left + 8; x < rest.Bounds.Right; x += 5)
                    if (rest.Contains(new(x, y)) && view.HitAt(new(x, y))?.Id == hit.Id) { Move(new(x, y)); return; }
            // After a cancelled drag, wait at the fan for the card to return;
            // chasing its in-flight center can click an unrelated permanent.
            Move(new((int)rest.Center.X, Math.Min(885, (int)rest.Center.Y))); return;
        }
        MoveHit(hit.Id);
    }
    private void MoveHit(string id)
    {
        var hit = view.Hits.Last(h => h.Id == id);
        if (view.HitAt(hit.Bounds.Center)?.Id == id) { Move(hit.Bounds.Center); return; }
        for (int y = hit.Bounds.Top + 8; y < Math.Min(895, hit.Bounds.Bottom); y += 6)
            for (int x = hit.Bounds.Left + 8; x < hit.Bounds.Right; x += 6)
                if (view.HitAt(new(x, y))?.Id == id) { Move(new(x, y)); return; }
        throw new InvalidOperationException("No visible hit for " + id);
    }
    private void Publish(GameSnapshot state) { scenario.Present(state); view.Accept(scenario.State); }
    private void Check(bool condition, string description)
    {
        if (!condition) {
            Capture("failure");
            File.WriteAllText(Path.Combine(view.Profile, "failure.json"), JsonSerializer.Serialize(new { description, view.Now, view.Overlay, view.ZoneName,
                hovered = view.Hovered?.VisualId, view.HoverPreview, hoveredPose = view.Hovered == null ? null : view.DisplayedPose(view.Hovered.VisualId),
                handPose = view.DisplayedPose("hand-3"), handPaintCount = view.CardPaintCount("hand-3"), point = new { mouse.X, mouse.Y }, replies = scenario.Replies.Count,
                hits = view.Hits.Select(h => h.Id).ToArray() }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            throw new InvalidOperationException(description);
        }
        checks.Add(description);
    }
    private void Capture(string name) { using var output = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height); }
    public void Frame(Texture2D surface)
    {
        if (view.Now > 50) throw new TimeoutException("Reading smoke did not finish");
        if (view.Error.Length > 0) Check(false, view.Error);
        if (steps.TryPeek(out var next) && (next.Ready?.Invoke() == true || view.Now - lastStep >= next.Delay)) { steps.Dequeue(); next.Action(); lastStep = view.Now; }
    }
}
