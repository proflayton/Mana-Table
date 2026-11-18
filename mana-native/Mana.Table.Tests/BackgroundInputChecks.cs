using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static class BackgroundInputChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        using (var run = new BackgroundRun(profile, "background-input-table")) {
            var before = run.View.Match;
            var camera = run.View.BoardView;
            var card = run.Frame.Cards.First(card => card.Zone == "Hand");
            var at = run.At(card.Hit.Id);
            run.Tick(active: false, point: at, allButtons: true, wheelDelta: 120);
            run.Tick(active: false, point: at + new Point(90, 40), allButtons: true, wheelDelta: 240);
            run.Tick(active: false, point: at);
            foreach (var key in new[] { Keys.F, Keys.LeftControl, Keys.C, Keys.H, Keys.F1, Keys.Enter, Keys.Space, Keys.W }) {
                run.Tick(new(key), active: false); run.Tick(active: false);
            }
            check(ReferenceEquals(run.View.Match, before) && run.View.BoardView == camera
                    && !run.Frame.Cards.Any(card => card.Focused) && run.View.Overlay.Length == 0
                    && !run.View.Automatic && !run.View.Held && !run.View.ChatOpen && !run.View.TableGuideOpen
                    && run.Frame.Scene?.Spread == null && run.Engine.Replies.Count == 0
                    && run.AcceptedHit == null && !run.AcceptedDrag,
                "Inactive left, right, middle, auxiliary buttons, wheel and game shortcuts cannot inspect, pan, open views, change play controls or submit a scoped command");

            var current = run.View.Match!;
            var target = current.Viewer!.Zone("Battlefield").Cards[0];
            var changed = current with { Players = current.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                    Cards = zone.Cards.Select(card => card.VisualId != target.VisualId ? card : card with { Power = card.Power + 1 }).ToImmutableArray()
                }).ToImmutableArray()
            }).ToImmutableArray() };
            run.Engine.Present(changed); run.QueueObservation(); run.Tick(active: false);
            check(run.Frame.Stamp.Revision == run.Engine.State.Revision
                    && run.Frame.Cards.Single(card => card.Id == target.VisualId).Card.Power == target.Power + 1,
                "The inactive input gate still admits and renders a fresh authoritative game observation");

            run.Tick(new(Keys.F, Keys.W), point: at, allButtons: true, wheelDelta: 120);
            run.Tick(new(Keys.F), point: at, left: ButtonState.Pressed);
            run.Tick(point: at); // Neutral regain frame is consumed and rebases the wheel.
            check(!run.View.Automatic && run.View.BoardView == camera && !run.Frame.Cards.Any(card => card.Focused)
                    && run.Engine.Replies.Count == 0,
                "Returning with held buttons and shortcut keys cannot replay the activation click or accumulated wheel movement");
            run.Click(run.Frame.Cards.Single(item => item.Id == card.Id && item.Zone == "Hand").Hit.Id);
            check(run.Frame.Cards.Any(item => item.Id == card.Id && item.Focused) && run.Engine.Replies.Count == 0,
                "After neutral input, a fresh foreground click reads the same card normally");
            run.Deactivate();
            run.Tick(new(Keys.R), active: false); run.Tick(active: false);
            check(run.Frame.Cards.Any(item => item.Id == card.Id && item.Focused) && run.PaintedText.Contains("Rules"),
                "Background R cannot turn over an already-open card reader");
            run.Tick(new(Keys.Escape), active: false); run.Tick(active: false);
            check(run.Frame.Cards.Any(item => item.Id == card.Id && item.Focused),
                "Background Escape cannot close an existing card reader");
            run.Tick(); run.Tick(new(Keys.Escape)); run.Tick();
            run.Tick(new(Keys.H)); run.Tick();
            var spread = run.View.CardSpreadView;
            if (run.Frame.Scene?.Spread == null) throw new InvalidOperationException("The foreground hand gesture did not open the collection fixture.");
            var collectionPoint = run.Frame.Scene.Spread.CameraArea.Center;
            run.Tick(active: false, point: collectionPoint, wheelDelta: 240);
            run.Tick(point: collectionPoint, wheelDelta: 120); // First foreground neutral input rebases the wheel.
            check(run.View.CardSpreadView == spread,
                "Background and focus-regain wheel deltas cannot zoom an expanded hand collection");
            run.Tick(point: collectionPoint, wheelDelta: 120);
            check(run.View.CardSpreadView != spread,
                "A fresh foreground wheel gesture still zooms the actual expanded collection");
        }
        LostCapture(profile, check);
        Text(profile, check);
        ScopedTarget(profile, check);
        BackgroundProgress(profile, check);
    }

    private static void BackgroundProgress(string profile, Action<bool, string> check)
    {
        foreach (string mode in new[] { "auto", "off", "hold", "phase-stop", "required", "response", "reading" }) {
            string path = Path.Combine(profile, "background-progress-" + mode);
            var initial = FourPlayerTableScenario.Create()[0].Snapshot with {
                Id = "background-progress-" + mode, Turn = 3, ActivePlayerId = mode == "phase-stop" ? 0 : 1,
                Phase = "Main phase", PhaseKey = "MAIN1", Stack = [], Combat = null, Activity = [], Executions = null,
                DecisionOwnerId = 0, PriorityPlayerId = 0, ActionClass = "priority",
                Decision = mode == "required"
                    ? new("", new("A required choice waits for the player."), new ConfirmationPayload(new("Continue", true)))
                    : new("", new(), new PriorityPayload(new("Pass once", true), CanAutoPass: mode != "response"))
            };
            using var run = new BackgroundRun(profile, "background-progress-" + mode, initial, stopMain: true);
            var commands = new ConcurrentQueue<GameCommand>();
            var attempts = new ConcurrentQueue<GameCommand>();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var offered = run.View.Match!;
            run.Engine.ReplyOverride = async command => {
                attempts.Enqueue(command);
                if (command is not AutoPassPriority || !GameCommandPermissions.Allows(offered, command))
                    throw new InvalidOperationException("Background Auto escaped its original scoped priority permission.");
                commands.Enqueue(command);
                await release.Task;
                run.Engine.Present(run.Engine.State with { ActionClass = "confirmation", Decision = new("",
                    new("A fresh required choice stops background Auto."), new ConfirmationPayload(new("Continue", true))) });
            };
            if (mode == "reading") {
                run.Click(run.Frame.Cards.First(card => card.Zone == "Hand").Hit.Id);
                if (!run.Frame.Cards.Any(card => card.Focused)) throw new InvalidOperationException("The reading control did not pin its actual card.");
            }
            run.View.SetHeld(mode == "hold");
            run.View.SetAutomatic(mode != "off");
            run.Deactivate();
            try {
                // Inactive device samples are hostile input, never permission.
                for (int frame = 0; frame < 80; frame++) {
                    run.Tick(new(Keys.F, Keys.Enter, Keys.Space), active: false, allButtons: true, wheelDelta: 120);
                    Thread.Yield();
                }
                if (mode == "auto") {
                    bool submitted = Pump(() => commands.Count == 1);
                    Save();
                    check(submitted && commands.Single() is AutoPassPriority pass && pass.GameId == offered.Id
                            && pass.DecisionId == offered.Decision!.Id && run.View.Automatic && !run.View.Held,
                        "Already enabled Auto submits its exact current pass while unfocused, without accepting inactive keyboard or pointer input");
                    release.TrySetResult();
                    bool current = Pump(() => !run.View.Busy && run.View.Match?.Decision?.Payload is ConfirmationPayload);
                    Save();
                    check(current && run.Frame.Stamp.DecisionId == run.Engine.State.Decision!.Id && commands.Count == 1,
                        "An inactive update admits the actual accepted continuation and exposes its fresh required choice instead of freezing at old priority");
                    for (int frame = 0; frame < 40; frame++) run.Tick(new(Keys.Enter), active: false);
                    check(commands.Count == 1 && run.View.Match?.Decision?.Payload is ConfirmationPayload,
                        "Background Auto waits at a required choice and ignores an inactive Enter key");
                } else {
                    Save();
                    check(commands.IsEmpty && run.View.Automatic == (mode != "off") && run.View.Held == (mode == "hold"),
                        "Losing focus preserves the existing automatic-play boundary: " + mode);
                }
                int beforeRegain = commands.Count;
                run.Tick(new(Keys.F, Keys.Enter), allButtons: true);
                run.Tick(); // Consume neutral foreground input before fresh gestures.
                check(commands.Count == beforeRegain && run.AcceptedHit == null && !run.AcceptedDrag,
                    "Held activation keys and buttons cannot become a manual game action on focus return: " + mode);
            } finally { release.TrySetResult(); Save(); }

            bool Pump(Func<bool> ready)
            {
                var time = Stopwatch.StartNew();
                while (!ready() && time.Elapsed < TimeSpan.FromSeconds(5)) {
                    run.Tick(active: false); Thread.Sleep(1);
                }
                return ready();
            }
            void Save() => File.WriteAllText(Path.Combine(path, "background-progress.json"), JsonSerializer.Serialize(new {
                mode, automatic = run.View.Automatic, held = run.View.Held, run.View.Busy,
                attempts = attempts.Select(command => new { kind = command.GetType().Name,
                    command = JsonSerializer.SerializeToElement(command, command.GetType()) }).ToArray(),
                commands = commands.Select(command => new { kind = command.GetType().Name,
                    command = JsonSerializer.SerializeToElement(command, command.GetType()) }).ToArray(),
                current = run.View.Match, frame = run.Frame.Observe(),
                scope = "Production application/session with authored permissions and controlled accepted replies; no Magic rules or live-game claim."
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        }
    }

    private static void LostCapture(string profile, Action<bool, string> check)
    {
        using var run = new BackgroundRun(profile, "background-input-capture");
        var card = run.Frame.Cards.First(card => card.Zone == "Hand");
        var at = run.At(card.Hit.Id);
        run.Tick(point: at, left: ButtonState.Pressed);
        run.Deactivate();
        run.Tick(active: false, point: at);
        run.Tick(point: at, left: ButtonState.Pressed);
        run.Tick(point: at);
        check(!run.Frame.Cards.Any(card => card.Focused) && run.AcceptedHit == null && !run.AcceptedDrag
                && run.Engine.Replies.Count == 0,
            "Losing foreground during a captured card press cancels it; an outside release and held activation click cannot complete the stale gesture");
        run.Click(card.Hit.Id);
        check(run.Frame.Cards.Any(item => item.Id == card.Id && item.Focused),
            "A cancelled card press leaves the actual card available for a new deliberate foreground gesture");
    }

    private static void Text(string profile, Action<bool, string> check)
    {
        using var run = new BackgroundRun(profile, "background-input-text");
        run.Tick(new(Keys.Enter)); run.Tick();
        if (!run.View.ChatOpen) throw new InvalidOperationException("The foreground Enter gesture did not focus actual table chat.");
        run.View.TypeText("inside");
        run.Deactivate();
        run.View.TypeText("outside\b"); // The window deactivation event precedes the next game update.
        run.Tick(new(Keys.LeftControl, Keys.A), active: false);
        run.Tick(active: false); run.Tick(new(Keys.Enter), active: false); run.Tick(active: false);
        check(run.View.ChatDraft == "inside" && run.View.ChatOpen && !run.View.SocialSending && run.Social.Sends == 0,
            "Immediate deactivation rejects text, Backspace, select-all and send shortcuts while preserving the actual chat draft");
        run.Tick(new(Keys.LeftControl, Keys.A)); run.Tick();
        run.View.TypeText("!"); run.Tick();
        check(run.View.ChatDraft == "inside!" && run.Social.Sends == 0,
            "After neutral foreground input, fresh typing resumes without a background select-all or held shortcut replacing the draft");
    }

    private static void ScopedTarget(string profile, Action<bool, string> check)
    {
        using var run = new BackgroundRun(profile, "background-input-command");
        run.Engine.ChoosePlayer(3); run.QueueObservation(); run.Tick();
        string prompt = run.View.Match!.Decision!.Id;
        var at = run.At("player:3");
        run.Tick(active: false, point: at, left: ButtonState.Pressed);
        run.Tick(active: false, point: at);
        run.Tick(point: at, left: ButtonState.Pressed);
        run.Tick(point: at);
        check(run.Engine.Replies.Count == 0 && run.View.Match?.Decision?.Id == prompt,
            "A current legal target receives no command from background clicks or the held click that reactivates the game");
        run.Click("player:3");
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (run.View.Busy && deadline.Elapsed < TimeSpan.FromSeconds(5)) { run.Tick(); Thread.Yield(); }
        check(run.Engine.Replies.SingleOrDefault() is SelectPlayer { Player: 3 } reply && reply.DecisionId == prompt,
            "The next fresh foreground target click submits exactly its current scoped player-selection command");
    }

    private sealed class BackgroundRun : IDisposable
    {
        private readonly PresentationRun presentation;
        private int wheel;
        internal InteractionScenarioEngine Engine { get; } = new();
        internal Conversation Social { get; } = new();
        internal TableApplication.Probe View => presentation.View;
        internal PresentationFrame Frame => presentation.Frame;
        internal string? AcceptedHit => presentation.AcceptedHit;
        internal bool AcceptedDrag => presentation.AcceptedDrag;
        internal IEnumerable<string> PaintedText => Frame.Drawing.Commands.Select(command => command switch {
            DrawCommand.Text text => text.Value, DrawCommand.CenterText text => text.Value, _ => null }).OfType<string>();
        internal BackgroundRun(string profile, string name, GameSnapshot? initial = null, bool stopMain = false)
        {
            var path = Path.Combine(profile, name); Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false,
                Stops = stopMain ? ["MAIN1"] : [] }.Save(path);
            Engine.Present(initial ?? FourPlayerTableScenario.Create()[0].Snapshot);
            presentation = new(TestTableServices.For(Engine) with { Social = Social }, path, Engine.State, new(1600, 900));
            presentation.EnableLiveTableStudy();
            var lobby = new Lobby("joined", "Background input fixture", null, false, null, true, "", null, [], [], "foreground-input-table");
            View.SetConnectedTable(lobby); View.PresentLobby(lobby, true);
            Tick(); Tick();
        }
        internal void QueueObservation() => presentation.QueueObservation(Engine.State);
        internal void Deactivate() => presentation.Deactivate();
        internal void Tick(KeyboardState keys = default, bool active = true, Point? point = null,
            ButtonState left = ButtonState.Released, bool allButtons = false, int wheelDelta = 0)
        {
            wheel += wheelDelta; var at = point ?? new Point(20, 30);
            var other = allButtons ? ButtonState.Pressed : ButtonState.Released;
            presentation.Tick(TimeSpan.FromSeconds(.05),
                new(at.X, at.Y, wheel, allButtons ? ButtonState.Pressed : left, other, other, other, other), keys, active: active);
        }
        internal Point At(string id)
        {
            var hit = Frame.Hits.Last(hit => hit.Id == id);
            for (int y = Math.Max(0, hit.Bounds.Top + 2); y < Math.Min(900, hit.Bounds.Bottom - 2); y += 3)
                for (int x = Math.Max(0, hit.Bounds.Left + 2); x < Math.Min(1600, hit.Bounds.Right - 2); x += 3)
                    if (View.HitAt(new(x, y))?.Id == id) return new(x, y);
            throw new InvalidOperationException("Covered background-input fixture control: " + id);
        }
        internal void Click(string id)
        {
            var at = At(id); Tick(point: at, left: ButtonState.Pressed); Tick(point: at);
            if (AcceptedHit != id) throw new InvalidOperationException("Foreground fixture click was not accepted: " + id);
        }
        public void Dispose() => presentation.Dispose();
    }

    private sealed class Conversation : ITableSocial
    {
        private int sends;
        internal int Sends => Volatile.Read(ref sends);
        public Task<TableConversation> ConversationAsync(string tableId) => Task.FromResult(new TableConversation(tableId, 0, 0, [], []));
        public Task<TableConversation> SendMessageAsync(string tableId, string kind, string text) { Interlocked.Increment(ref sends); return ConversationAsync(tableId); }
        public Task<TableConversation> SetPlayerNameAsync(string tableId, string name) => ConversationAsync(tableId);
    }
}
