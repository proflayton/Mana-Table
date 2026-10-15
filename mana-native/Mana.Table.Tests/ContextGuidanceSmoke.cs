using System.Text.Json;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

// Native pointer gestures exercise the same controls that the cues describe.
// Learning is checked through accepted engine results and persisted preferences.
internal static class ContextGuidanceSmoke
{
    internal static void Run(string profile, Point? size)
    {
        var checks = new List<string>();
        string run = Path.Combine(profile, "context-guidance-scenarios", Guid.NewGuid().ToString("N"));
        string Case(string name) { string path = Path.Combine(run, name); Directory.CreateDirectory(path); return path; }
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            checks.Add(message); File.AppendAllText(Path.Combine(profile, "context-guidance-progress.log"), message + "\n");
        }
        void Scene(string path, InteractionScenarioEngine engine, Action<Script, TableGame.Probe> arrange)
        {
            using var game = new TableGame(engine, path, null, null, size, initialState: engine.State, artSource: new EmptyArt());
            var script = new Script(game.TestPort); arrange(script, game.TestPort); game.Automation = script; game.Run();
        }
        static GameSnapshot Priority()
        {
            var original = PresentationSmoke.Example();
            return original with { Decision = original.Decision! with { CanAutoPass = false }, Players = original.Players.Select(p => p with { Zones = p.Zones.Select(z => {
                var cards = z.Name switch {
                    "Hand" => z.Cards.Where(c => p.Id == 0 && c.VisualId == "hand-1").ToArray(),
                    "Command" => z.Cards.Where(_ => p.Id == 0).ToArray(),
                    "Battlefield" => z.Cards.Where(c => c.VisualId == "seat-" + p.Id + "-1" && p.Id < 2).ToArray(),
                    _ => z.Cards
                };
                return z with { Cards = cards, Count = z.Name is "Hand" or "Command" or "Battlefield" ? cards.Length : z.Count };
            }).ToArray() }).ToArray() };
        }
        void Publish(TableGame.Probe view, InteractionScenarioEngine engine, GameSnapshot state) { engine.Present(state); view.Accept(engine.State); }

        string learnedPath = Case("learn-through-play");
        var play = new InteractionScenarioEngine(); play.Present(Priority());
        Scene(learnedPath, play, (s, view) => {
            s.Then(() => Check(!view.ExperienceOpen && !view.ActivityOpen && view.Hits.Any(h => h.Id == "confirm"), "Fresh matches expose the actual pending decision without a forced guide"));
            s.MoveCard("hand-1"); s.Wait(.35);
            s.Then(() => {
                Check(view.Hovered?.VisualId == "hand-1" && view.GuidanceCue is { Key: "hand-play", Text.Length: > 0 }, "A playable hand card gets its own play instruction at the hovered card");
                Check(view.GuidanceBounds is { Width: > 0, Height: > 0 } b && b.Left >= 0 && b.Right <= 1600 && b.Top >= 0 && b.Bottom <= 900, "Context guidance has a visible anchor inside the table");
                Check(!view.Hits.Any(h => h.Id.StartsWith("guidance-")), "Context cues add no hit regions that can intercept card or game controls");
                s.Capture("01-hand-cue");
            });
            s.Wait(1.1);
            s.Then(() => Check(view.GuidanceCue?.Key == "hand-play" && !view.LearnedControls.Contains("hand-play") && play.Replies.Count == 0 && !view.Automatic && !view.Held, "Waiting and hovering neither dismiss the lesson nor change game or playback state"));
            s.ClickCurrent();
            s.Then(() => Check(view.GuidanceCue is { Key: "hand-play" } staged && staged.Text.Contains("again", StringComparison.OrdinalIgnoreCase) && play.Replies.Count == 0 && !view.LearnedControls.Contains("hand-play"), "The first hand click explains the confirming click without playing or marking the control learned; cue=" + view.GuidanceCue + ", replies=" + play.Replies.Count));
            s.Wait(.7);
            s.Then(() => { play.RejectNextReply = true; s.AllowError = true; });
            s.DoubleClickCurrent();
            s.Until(() => play.RejectedReplies == 1 && !view.Busy);
            s.Then(() => Check(view.Error.Contains("Controlled rejected play") && !view.LearnedControls.Contains("hand-play") && play.Replies.Count == 0, "A rejected play cannot mark a control learned"));
            s.Click("dismiss");
            s.MoveCard("hand-1"); s.Wait(.25);
            s.Then(() => {
                s.AllowError = false;
                Check(view.Hovered?.VisualId == "hand-1" && view.GuidanceCue?.Key == "hand-play" && view.Error.Length == 0, "The play cue remains at the actual hovered card after a rejected command is dismissed");
            });
            s.DoubleClickCurrent();
            s.Until(() => !view.Busy && view.LearnedControls.Contains("hand-play"));
            s.Then(() => Check(play.Replies.Count == 1 && play.Replies[0].Action == ReplyAction.SelectCard && view.Match!.Viewer!.Zone("Hand").Cards.Length == 0 && view.GuidanceCue?.Key != "hand-play", "A completed real play retires the hand instruction and leaves the resulting table intact"));
            s.MoveCard("commander-0"); s.Wait(.25);
            s.Then(() => { Check(view.Hovered?.VisualId == "commander-0" && view.GuidanceCue?.Key == "commander-play", "The hovered command-zone card receives commander-specific play guidance"); s.Capture("02-commander-cue"); });
            s.DoubleClickCurrent();
            s.Until(() => !view.Busy && view.LearnedControls.Contains("commander-play"));
            s.Then(() => Check(play.Replies.Count == 2 && view.Match!.Viewer!.Zone("Command").Cards.Length == 0 && view.GuidanceCue?.Key != "commander-play", "An accepted commander play teaches its control without blocking the command-zone action"));
            s.MoveCard("seat-0-1"); s.Wait(.25);
            s.Then(() => { Check(view.Hovered?.VisualId == "seat-0-1" && view.GuidanceCue?.Key == "activate-ability", "An engine-available hovered battlefield ability gets an activation instruction"); s.Capture("03-ability-cue"); });
            s.ClickCurrent();
            s.Until(() => !view.Busy && view.LearnedControls.Contains("activate-ability"));
            s.Then(() => Check(play.Replies.Count == 3 && view.Match?.Decision?.Context == "playAbility", "The ability cue permits the real click and learns from its accepted ability chooser"));
            s.Click("choice-cancel");
            s.Until(() => !view.Busy && play.Replies.Count == 4);
            s.Then(() => { play.ChoosePlayer(1, 2); view.Accept(play.State); });
            s.Move(new(10, 70)); s.Wait(.1);
            s.Then(() => {
                Check(view.GuidanceCue?.Key == "choose-target" && !view.LearnedControls.Contains("choose-target"), "An actual player-target decision explains highlighted targets before one is chosen");
                Check(view.GuidanceBounds is { } bounds && !bounds.Intersects(new Rectangle(1266, 741, 296, 49)), "Target guidance preserves the engine's explanation of the required choice");
                s.Capture("04-target-cue");
            });
            s.MoveHit("auto"); s.Wait(.1);
            s.Then(() => {
                Check(view.GuidanceCue?.Key == "priority-controls" && view.GuidanceBounds is { } bounds && !bounds.Intersects(new Rectangle(1266, 741, 296, 49)), "Hovering Auto during a required target choice preserves the engine's instruction body");
                Check(play.Replies.Count == 4 && view.Match?.Decision?.Intent == DecisionIntent.Selection && !view.Automatic && !view.Held && !view.LearnedControls.Contains("priority-controls"), "Playback hover leaves the required target unanswered and changes no control preferences");
            });
            s.Click("player:1");
            s.Until(() => !view.Busy && view.LearnedControls.Contains("choose-target"));
            s.Then(() => Check(play.Replies.Count == 5 && play.Replies[^1].Action == ReplyAction.SelectPlayer && view.Match?.Decision == null, "Target guidance never intercepts the accepted player target"));
            s.Then(() => {
                var state = Priority();
                Publish(view, play, state with { Decision = state.Decision! with { CanAutoPass = true }, Players = state.Players.Select(p => p with { Zones = p.Zones.Select(z => z with { Cards = z.Cards.Select(c => c with { Selectable = false }).ToArray() }).ToArray() }).ToArray() });
            });
            s.MoveHit("hold"); s.Wait(.1);
            s.Then(() => Check(view.GuidanceCue?.Key == "priority-controls" && !view.Automatic && !view.Held, "Playback guidance appears by the controls without switching Auto or holding priority"));
            s.Click("hold");
            s.Then(() => Check(view.Held && !view.Automatic && view.LearnedControls.Contains("priority-controls") && play.Replies.Count == 5, "Only an explicit Hold click changes the playback policy and teaches that control"));
        });

        var mana = new InteractionScenarioEngine();
        var manaTable = PresentationSmoke.Example();
        mana.Present(manaTable with { Decision = new() { Kind = "input", Intent = DecisionIntent.Mana, OkEnabled = true, Message = "Pay the remaining green mana using an available source." } });
        Scene(Case("crowded-mana"), mana, (s, view) => {
            s.Move(new(10, 70)); s.Wait(.8);
            s.Then(() => {
                Check(view.GuidanceCue?.Key == "activate-ability" && view.GuidanceBounds is { } bounds && !bounds.Intersects(new Rectangle(1266, 741, 296, 49)), "Idle mana-source guidance on a crowded table preserves the required payment instruction");
                Check(view.Match?.Decision?.Intent == DecisionIntent.Mana && mana.Replies.Count == 0 && view.LearnedControls.Count == 0 && !view.Automatic && !view.Held, "Mana guidance leaves payment and playback settings under the player's control");
                s.Capture("07-crowded-mana-cue");
            });
        });

        var returning = new InteractionScenarioEngine(); returning.Present(Priority());
        Scene(learnedPath, returning, (s, view) => {
            s.Then(() => Check(new[] { "hand-play", "commander-play", "activate-ability", "choose-target", "priority-controls" }.All(view.LearnedControls.Contains), "Controls demonstrated through play persist across application launches"));
            foreach (var (card, key) in new[] { ("hand-1", "hand-play"), ("commander-0", "commander-play"), ("seat-0-1", "activate-ability") }) {
                s.MoveCard(card); s.Wait(.2);
                s.Then(() => Check(view.GuidanceCue?.Key != key && returning.Replies.Count == 0, "A returning player can read " + card + " without repeating its learned instruction"));
            }
            s.Click("settings");
            s.Click("guidance-reset");
            s.Then(() => Check(view.LearnedControls.Count == 0 && view.Overlay == "settings" && returning.Replies.Count == 0 && !view.Automatic && !view.Held, "The explicit Settings action restores hints without changing the game or playback policy"));
            s.Click("close-overlay");
            s.MoveCard("hand-1"); s.Wait(.2);
            s.Then(() => Check(view.GuidanceCue?.Key == "hand-play", "Resetting hints restores the instruction at its actual card"));
            s.DoubleClickCurrent();
            s.Until(() => !view.Busy && view.LearnedControls.Contains("hand-play"));
            s.Pulse(Keys.F1);
            s.Click("guidance-reset");
            s.Then(() => Check(!view.TableGuideOpen && view.LearnedControls.Count == 0 && returning.Replies.Count == 1 && !view.Automatic && !view.Held, "The optional F1 help can restore hints and return to the same game without submitting another choice"));
        });
        var afterReset = new InteractionScenarioEngine(); afterReset.Present(Priority());
        Scene(learnedPath, afterReset, (s, view) => s.Then(() => Check(view.LearnedControls.Count == 0 && !view.TableGuideOpen, "Reset hint preferences persist without forcing a guide on the next launch")));

        var combat = new InteractionScenarioEngine();
        combat.Present(combat.State with { Players = combat.State.Players.Select(p => p.Id != 0 ? p : p with { Zones = p.Zones.Select(z => z.Name != "Battlefield" ? z : z with { Count = 3, Cards = z.Cards.Where(c => c.VisualId is "own-0" or "own-9" or "own-12").ToArray() }).ToArray() }).ToArray() });
        Scene(Case("combat"), combat, (s, view) => {
            s.MoveCard("own-0"); s.Wait(.3);
            s.Then(() => {
                Check(view.GuidanceCue?.Key == "attack", "Eligible attackers receive combat guidance at the point of assignment");
                Check(view.GuidanceBounds is { } bounds && !bounds.Intersects(new Rectangle(1266, 741, 296, 49)), "Attack guidance keeps the authoritative combat instruction readable");
                s.Capture("05-attack-cue");
            });
            s.DragCard("own-0", "player:3");
            s.Then(() => Check(combat.Replies.Count == 0 && !view.LearnedControls.Contains("attack"), "An illegal attack gesture neither sends a command nor teaches combat"));
            s.MoveCard("own-0"); s.ClickCurrent();
            s.Then(() => Check(view.SelectedCombat == "own-0" && view.GuidanceCue?.Key == "attack" && !view.LearnedControls.Contains("attack"), "Selecting an attacker keeps its next-step guidance until a defender is assigned"));
            s.Click("player:1");
            s.Until(() => !view.Busy && view.LearnedControls.Contains("attack"));
            s.Then(() => { Check(combat.Replies.Count == 1 && combat.Replies[0].Action == ReplyAction.AssignAttack, "Assigning a projected legal defender retires attack guidance after engine acceptance"); combat.BeginBlocks(); view.Accept(combat.State); });
            s.MoveCard("own-9"); s.Wait(.2);
            s.Then(() => {
                Check(view.GuidanceCue?.Key == "block", "A required blocking decision receives its own combat instruction");
                Check(view.GuidanceBounds is { } bounds && !bounds.Intersects(new Rectangle(1266, 741, 296, 49)), "Block guidance leaves the engine's restrictions and problem text visible");
                s.Capture("06-block-cue");
            });
            s.DragCard("own-9", () => s.CardHit("enemy-b"));
            s.Then(() => Check(combat.Replies.Count == 1 && !view.LearnedControls.Contains("block"), "An ineligible blocking pair cannot complete a lesson"));
            s.MoveCard("own-9"); s.ClickCurrent();
            s.Click(() => s.CardHit("enemy-a"));
            s.Until(() => !view.Busy && view.LearnedControls.Contains("block"));
            s.Then(() => Check(combat.Replies.Count == 2 && combat.Replies[^1].Action == ReplyAction.AssignBlock && !view.Automatic && !view.Held, "A real legal block teaches its gesture while preserving playback settings"));
        });

        var permissions = new InteractionScenarioEngine(); permissions.Present(Priority());
        Scene(Case("authoritative-permissions"), permissions, (s, view) => {
            s.MoveCard("hand-1"); s.Wait(.25);
            s.Then(() => {
                Check(view.GuidanceCue?.Key == "hand-play", "The permission-revocation scenario starts with a real playable card");
                Publish(view, permissions, permissions.State with { Players = permissions.State.Players.Select(p => p with { Zones = p.Zones.Select(z => z with { Cards = z.Cards.Select(c => c with { Selectable = false }).ToArray() }).ToArray() }).ToArray() });
            });
            s.Wait(.1);
            s.Then(() => Check(view.GuidanceCue?.Key is not ("hand-play" or "commander-play" or "activate-ability"), "Revoked play permission removes the hint even while the pointer stays over the old occurrence"));
            s.Then(() => Publish(view, permissions, permissions.State with { Players = permissions.State.Players.Select(p => p.Id != 0 ? p : p with { Zones = p.Zones.Select(z => z.Name != "Hand" ? z : z with { Cards = z.Cards.Select(c => c with { FaceDown = true }).ToArray() }).ToArray() }).ToArray() }));
            s.Wait(.1);
            s.Then(() => Check(view.GuidanceCue?.Key is not ("hand-play" or "commander-play" or "activate-ability" or "inspect-card"), "A face-down card receives neither play nor card-reading instructions"));
            s.MoveCard("seat-1-1"); s.Wait(.25);
            s.Then(() => Check(view.GuidanceCue?.Key is not ("hand-play" or "commander-play" or "activate-ability") && permissions.Replies.Count == 0 && !view.LearnedControls.Contains("hand-play"), "Visible opposing cards never receive an instruction to play them or change learned controls"));
        });

        var automatic = new InteractionScenarioEngine();
        var autoState = Priority();
        automatic.Present(autoState with { Decision = autoState.Decision! with { CanAutoPass = true }, Players = autoState.Players.Select(p => p with { Zones = p.Zones.Select(z => z with { Cards = z.Cards.Select(c => c with { Selectable = false }).ToArray() }).ToArray() }).ToArray() });
        Scene(Case("auto-uninterrupted"), automatic, (s, view) => {
            s.MoveHit("auto");
            s.Then(() => { Check(view.GuidanceCue?.Key == "priority-controls", "An unlearned empty priority window can explain playback controls in place"); view.SetAutomatic(true); });
            s.Until(() => !view.Busy && automatic.Replies.Count == 1);
            s.Then(() => Check(automatic.Replies[0].Action == ReplyAction.AutoPass && view.Automatic && !view.Held && !view.LearnedControls.Contains("priority-controls"), "Context guidance permits Auto to advance and never treats an automatic pass as user learning"));
        });
        File.WriteAllText(Path.Combine(profile, "context-guidance-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class EmptyArt : ICardArtSource
    {
        public Task<byte[]?> LoadAsync(ArtRequest art, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
        public void Dispose() { }
    }

    private sealed class Script(TableGame.Probe view) : ITableAutomation
    {
        private readonly Queue<(Action? Action, Func<bool>? Ready)> steps = [];
        private MouseState mouse;
        private KeyboardState keyboard;
        private double until = .7;
        private Point target;
        private int mousePhase;
        internal bool AllowError { get; set; }
        public string? ExpectedClick { get; private set; }
        public bool ClickAccepted { get; set; }
        public KeyboardState? ReadKeyboard() => keyboard;
        public MouseState ReadMouse()
        {
            if (mousePhase is 1 or 2) { bool down = mousePhase++ == 1; At(target, down ? ButtonState.Pressed : ButtonState.Released); }
            return mouse;
        }
        internal void Then(Action action) => steps.Enqueue((action, null));
        internal void Until(Func<bool> ready) => steps.Enqueue((null, ready));
        internal void Wait(double seconds) => Then(() => until = view.Now + seconds);
        internal void Pulse(params Keys[] keys) { Then(() => keyboard = new(keys)); Then(() => keyboard = new()); }
        private Point Position => new((int)((mouse.X - (view.GraphicsDevice.Viewport.Width - 1600 * Scale) / 2) / Scale), (int)((mouse.Y - (view.GraphicsDevice.Viewport.Height - view.DesignHeight * Scale) / 2) / Scale));
        private float Scale => Math.Min(view.GraphicsDevice.Viewport.Width / 1600f, view.GraphicsDevice.Viewport.Height / (float)view.DesignHeight);
        private void At(Point point, ButtonState left = ButtonState.Released)
        {
            var viewport = view.GraphicsDevice.Viewport;
            mouse = new((int)((viewport.Width - 1600 * Scale) / 2 + point.X * Scale), (int)((viewport.Height - view.DesignHeight * Scale) / 2 + point.Y * Scale), 0, left, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
        }
        internal string CardHit(string id) => view.Hits.Last(h => h.Card?.VisualId == id).Id;
        private Point PointFor(string id)
        {
            var hit = view.Hits.LastOrDefault(h => h.Id == id) ?? throw new InvalidOperationException("Context smoke is missing " + id);
            if (hit.Bounds.Center.X is >= 0 and < 1600 && hit.Bounds.Center.Y >= 0 && hit.Bounds.Center.Y < view.DesignHeight && view.HitAt(hit.Bounds.Center)?.Id == hit.Id) return hit.Bounds.Center;
            for (int y = Math.Min(885, hit.Bounds.Bottom - 5); y > Math.Max(0, hit.Bounds.Top); y -= 8)
                for (int x = Math.Max(0, hit.Bounds.Left + 6); x < Math.Min(1600, hit.Bounds.Right); x += 8)
                    if (view.HitAt(new(x, y))?.Id == id) return new(x, y);
            throw new InvalidOperationException("Context smoke has no visible point for " + id);
        }
        internal void Move(Point point) => Then(() => At(point));
        internal void MoveHit(string id) => Then(() => At(PointFor(id)));
        internal void MoveCard(string id)
        {
            Move(new(10, 70)); Wait(.45);
            Then(() => {
                var hit = view.Hits.Last(h => h.Card?.VisualId == id);
                if (hit.Zone == "Hand") {
                    var hand = view.Match!.Viewer!.Zone("Hand").Cards;
                    int index = Array.FindIndex(hand, c => c.VisualId == id);
                    var rest = TableLayout.Hand(index % 11, Math.Min(11, hand.Length));
                    for (int y = 890; y >= Math.Max(810, rest.Bounds.Top + 8); y -= 5)
                        for (int x = rest.Bounds.Left + 8; x < rest.Bounds.Right; x += 5)
                            if (rest.Contains(new(x, y)) && view.HitAt(new(x, y))?.Id == hit.Id) { At(new(x, y)); return; }
                    throw new InvalidOperationException("No exposed hand slot remains for " + id);
                }
                At(PointFor(hit.Id));
            });
        }
        internal void Click(string id) => Click(() => id);
        internal void Click(Func<string> id) => Then(() => { ExpectedClick = id(); ClickAccepted = false; target = PointFor(ExpectedClick); mousePhase = 1; });
        internal void ClickCurrent() => Then(() => {
            target = Position;
            var hit = view.HitAt(target);
            if (hit?.Card == null) throw new InvalidOperationException("No card remains at the context smoke pointer");
            ExpectedClick = hit.Id; ClickAccepted = false; mousePhase = 1;
        });
        internal void DoubleClickCurrent() { ClickCurrent(); ClickCurrent(); }
        internal void DragCard(string card, string destination) => DragCard(card, () => destination);
        internal void DragCard(string card, Func<string> destination)
        {
            MoveCard(card); Then(() => At(Position, ButtonState.Pressed));
            Then(() => At(PointFor(destination()), ButtonState.Pressed));
            Then(() => At(Position)); Wait(.08);
        }
        internal void Capture(string name) { using var output = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height); }
        public void Frame(Texture2D surface)
        {
            if (!AllowError && view.Error.Length > 0) { Capture("failure"); throw new InvalidOperationException(view.Error); }
            if (view.Now > 45) { Capture("timeout"); throw new TimeoutException("Context guidance smoke timed out; remaining " + steps.Count + ", cue " + view.GuidanceCue?.Key); }
            if (mousePhase is 1 or 2) return;
            if (mousePhase == 3) {
                if (ExpectedClick != null && !ClickAccepted) { Capture("failure"); throw new InvalidOperationException("Context smoke rejected " + ExpectedClick); }
                mousePhase = 0; ExpectedClick = null;
            }
            if (!view.Loaded || view.ActivityOpen || view.Now < until) return;
            if (!steps.TryPeek(out var step)) { view.Exit(); return; }
            if (step.Ready != null && !step.Ready()) return;
            steps.Dequeue();
            try { step.Action?.Invoke(); }
            catch {
                Capture("failure");
                File.WriteAllText(Path.Combine(view.Profile, "failure.json"), JsonSerializer.Serialize(new { view.GuidanceCue, view.GuidanceBounds, view.LearnedControls,
                    view.Automatic, view.Held, view.SelectedCombat, view.Match, hovered = view.Hovered?.VisualId, pointer = Position,
                    hit = view.HitAt(Position)?.Id, replies = ((InteractionScenarioEngine)view.Engine).Replies, hits = view.Hits.Select(h => h.Id).ToArray() }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
                throw;
            }
        }
    }
}
