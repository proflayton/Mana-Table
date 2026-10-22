using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static class CommanderInteractionChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            string directory = Path.Combine(profile, $"commander-drag-{size.X}x{size.Y}"); Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(directory);
            var example = PresentationSmoke.Example();
            var engine = new InteractionScenarioEngine();
            engine.Present(example with { Id = "commander-drag", PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority",
                Players = example.Players.Select(player => player with {
                    Zones = player.Zones.Select(zone => zone.Name is "Hand" or "Battlefield" ? zone with { Count = 0, Cards = [] }
                        : zone.Name == "Command" ? zone with { Count = player.Id == 0 ? 2 : 1, Cards = (new Card[] { new() {
                            VisualId = "command-" + player.Id, CombatId = "command-" + player.Id,
                            Name = player.Id == 0 ? "Toothy, Imaginary Friend" : "Commander " + player.Id,
                            Type = "Legendary Creature", Selectable = true, Power = 1, Toughness = 1, CommanderTax = player.Id == 0 ? 2 : 0
                        } }).Concat(player.Id == 0 ? new Card[] { new() { VisualId = "partner-0", CombatId = "partner-0",
                            Name = "Pir, Imaginative Rascal", Type = "Legendary Creature", Selectable = true, Power = 1, Toughness = 1 } } : []).ToArray() } : zone).ToArray()
                }).ToArray() });
            var sourceReads = new System.Collections.Concurrent.ConcurrentQueue<string>();
            engine.SourceActionRead = (game, decision, key) => {
                var source = engine.State.Players.SelectMany(player => player.Zones).SelectMany(zone => zone.Cards).Single(card => card.Key == key);
                sourceReads.Enqueue(key);
                return new(game, decision, key, source.VisualId,
                    engine.State.Viewer!.Zone("Command").Cards.Any(card => card.Key == key && card.Selectable)
                        ? [new("cast-" + source.VisualId, "cast", source.Name + " - Creature 1 / 1")] : []);
            };
            using var app = new TableApplication(engine, directory, initialState: engine.State) { Automated = true };
            app.Initialize(); var view = app.TestPort;
            double seconds = 0;
            PresentationFrame frame = Tick(new(20, 60));
            Point Physical(Point logical)
            {
                var mapped = RenderViewport.Fit(size, 900).ToPhysical(logical.ToVector2());
                return new((int)MathF.Ceiling(mapped.X), (int)MathF.Ceiling(mapped.Y));
            }
            PresentationFrame Tick(Point logical, ButtonState left = ButtonState.Released)
            {
                seconds += 1.0 / 60; var at = Physical(logical);
                var time = new GameTime(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60));
                app.Update(time, new(at.X, at.Y, 0, left, default, default, default, default), new(), size);
                return app.ComposePresentation(time);
            }
            Point Source(string id)
            {
                var card = frame.Cards.Single(entry => entry.Id == id && entry.Zone == "Command");
                var hit = frame.Hits.Last(entry => entry.Id == card.Hit.Id && entry.Scope == card.Hit.Scope);
                var map = RenderViewport.Fit(size, 900);
                for (int y = hit.Bounds.Top + 2; y < hit.Bounds.Bottom - 2; y += 3)
                    for (int x = hit.Bounds.Left + 2; x < hit.Bounds.Right - 2; x += 3) {
                        var logical = new Point(x, y);
                        if (view.HitAt(map.ToLogical(Physical(logical))) is { } actual && actual.Id == hit.Id && actual.Scope == hit.Scope) return logical;
                    }
                File.WriteAllText(Path.Combine(directory, "missing-command-source.json"), JsonSerializer.Serialize(new {
                    attemptedSource = id, viewport = size, snapshot = view.Match, frame = frame.Observe()
                }, new JsonSerializerOptions { WriteIndented = true }));
                throw new InvalidOperationException("No exposed command-zone card " + id + " at " + size);
            }
            void Publish(GameSnapshot state) { engine.Present(state); view.Accept(engine.State); frame = Tick(new(20, 60)); }
            void Drag(string id)
            {
                frame = Tick(Source(id), ButtonState.Pressed);
                frame = Tick(new(800, 420), ButtonState.Pressed);
                frame = Tick(new(800, 420));
            }
            string? previousActionScope = null;
            foreach (string id in new[] { "command-0", "partner-0" }) {
                var partner = engine.State.Viewer!.Zone("Command").Cards.Single(card => card.VisualId == id);
                var point = Source(id); frame = Tick(point, ButtonState.Pressed); frame = Tick(point);
                var readerDeadline = DateTime.UtcNow.AddSeconds(5);
                while (!frame.Hits.Any(hit => hit.Id == "read-card-action") && DateTime.UtcNow < readerDeadline) {
                    frame = Tick(new(20, 60)); Thread.Yield();
                }
                var action = frame.Hits.Single(hit => hit.Id == "read-card-action");
                string paintedAction = string.Join(" ", frame.Drawing.Commands.OfType<DrawCommand.Text>()
                    .Where(text => action.Bounds.Contains(new Point(text.X, text.Y))).Select(text => text.Value));
                var surface = frame.Hits.Single(hit => hit.Id == "source-actions-surface");
                string stateText = string.Join(" ", frame.Drawing.Commands.OfType<DrawCommand.Text>()
                    .Where(text => surface.Bounds.Contains(new Point(text.X, text.Y))).Select(text => text.Value));
                check(frame.Cards.Any(card => card.Id == id && card.Zone == "Command" && card.Focused)
                    && sourceReads.Contains(partner.Key) && action.Scope.Contains(partner.Key, StringComparison.Ordinal)
                    && action.Scope != previousActionScope && paintedAction.Contains("Cast " + partner.Name, StringComparison.Ordinal)
                    && stateText.Contains("Commander tax +2", StringComparison.Ordinal) == (id == "command-0")
                    && engine.Replies.Count == 0,
                    "Each partner opens its own current Cast action, full name and independent commander tax without sending a command at " + size + ": " + partner.Name);
                File.WriteAllText(Path.Combine(directory, "reader-" + id + ".json"), JsonSerializer.Serialize(new {
                    source = partner, viewport = size, snapshot = view.Match, frame = frame.Observe()
                }, new JsonSerializerOptions { WriteIndented = true }));
                previousActionScope = action.Scope;
                // An enlarged card legitimately covers its compact neighbour.
                // Close that reader through the same card before choosing Pir.
                point = Source(id); frame = Tick(point, ButtonState.Pressed); frame = Tick(point);
                check(!frame.Cards.Any(card => card.Focused) && engine.Replies.Count == 0,
                    "Clicking the read commander closes its face without casting before choosing its partner at " + size);
            }
            var own = engine.State.Viewer!.Zone("Command").Cards.Single(card => card.VisualId == "command-0");
            var opponent = engine.State.Players.Single(player => player.Id == 1).Zone("Command").Cards.Single();
            check(InteractionController.Play(engine.State, own, "Command") is { Action: ReplyAction.SelectCard } allowed && allowed.Card == own.Key
                && InteractionController.Play(engine.State, opponent, "Command") == null
                && InteractionController.Play(engine.State, own) == null
                && InteractionController.Play(engine.State, own, "Graveyard") == null
                && InteractionController.Play(engine.State, own with { Key = "expired-key" }, "Command") == null,
                "Command play uses only the viewer's exact current source handle without borrowing hand or other-zone permission at " + size);
            Drag("command-1");
            check(engine.Replies.Count == 0 && !app.AcceptedDrag,
                "Dragging an opponent's selectable commander cannot submit a local cast at " + size);
            Publish(engine.State with { Players = engine.State.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Command" ? zone : zone with {
                    Cards = zone.Cards.Select(card => card with { Selectable = false }).ToArray()
                }).ToArray()
            }).ToArray() });
            check(InteractionController.Play(engine.State, engine.State.Viewer!.Zone("Command").Cards.Single(card => card.VisualId == "command-0"), "Command") == null,
                "An unselectable commander has no drag command despite having a visible public face at " + size);
            Drag("command-0");
            check(engine.Replies.Count == 0 && !app.AcceptedDrag,
                "Dragging an unselectable local commander leaves its zone and current decision unchanged at " + size);
            Publish(engine.State with { Players = engine.State.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Command" ? zone : zone with {
                    Cards = zone.Cards.Select(card => card with { Selectable = true }).ToArray()
                }).ToArray()
            }).ToArray() });
            var staleSource = engine.State.Viewer!.Zone("Command").Cards.Single(card => card.VisualId == "command-0");
            string staleDecision = engine.State.Decision!.Id;
            frame = Tick(Source("command-0"), ButtonState.Pressed); frame = Tick(new(800, 420), ButtonState.Pressed);
            engine.Present(engine.State); view.Accept(engine.State);
            frame = Tick(new(800, 420));
            check(engine.Replies.Count == 0 && !app.AcceptedDrag && engine.State.Decision!.Id != staleDecision
                    && engine.State.Viewer!.Zone("Command").Cards.Single(card => card.VisualId == "command-0").Key != staleSource.Key,
                "A refreshed prompt revokes a held command-zone cast before pointer release at " + size);
            string currentGame = engine.State.Id, currentDecision = engine.State.Decision!.Id;
            string currentKey = engine.State.Viewer!.Zone("Command").Cards.Single(card => card.VisualId == "command-0").Key;
            Drag("command-0");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((view.Busy || engine.Replies.Count == 0) && DateTime.UtcNow < deadline) { frame = Tick(new(20, 60)); Thread.Yield(); }
            check(view.Error.Length == 0 && engine.Replies.Single() is { Action: ReplyAction.SelectCard } cast
                    && cast.GameId == currentGame && cast.DecisionId == currentDecision && cast.Card == currentKey
                    && view.Match!.Viewer!.Zone("Command").Cards.Single().VisualId == "partner-0"
                    && view.Match.Viewer.Zone("Battlefield").Cards.Single().VisualId == "command-0",
                "A fresh physical command-zone drag submits exactly its current scoped source and presents the authored move at " + size);
        }
    }
}
