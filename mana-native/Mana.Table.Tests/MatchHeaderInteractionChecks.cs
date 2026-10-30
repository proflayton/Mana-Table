using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static class MatchHeaderInteractionChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        CheckManaReadingLateLayers(profile, check);
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) })
        foreach (float scale in new[] { 1f, 1.3f }) {
            string path = Path.Combine(profile, $"match-header-{size.X}-{scale}");
            Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = scale }.Save(path);
            var stages = FourPlayerTableScenario.Create();
            var engine = new InteractionScenarioEngine(); engine.Present(stages[0].Snapshot);
            var services = TestTableServices.For(engine) with { Social = new HeaderConversation() };
            using var app = new TableApplication(services, path, initialState: engine.State) { Automated = true };
            app.Initialize(); app.EnableLiveTableStudy();
            var view = app.TestPort;
            var lobby = new Lobby("joined", "Playing", null, false, null, true, "", null, [], [], "header-table");
            view.SetConnectedTable(lobby); view.PresentLobby(lobby, true);
            double time = 0;
            PresentationFrame Tick(Point? point = null, ButtonState button = ButtonState.Released)
            {
                time += .016;
                var at = point ?? new(-100, -100);
                app.Update(new(TimeSpan.FromSeconds(time), TimeSpan.FromSeconds(.016)),
                    new(at.X, at.Y, 0, button, default, default, default, default), new(), size);
                return app.ReadPresentation();
            }
            var frame = Tick();
            var chat = frame.Hits.Single(hit => hit.Id == "chat-toggle").Bounds;
            engine.Present(stages[2].Snapshot with { States = [new("day-night", "Day", "status", null, null, "It is day.", "day-night")] });
            view.Accept(engine.State); frame = Tick();
            var controls = frame.Hits.Where(hit => hit.Bounds.Top >= 0 && hit.Bounds.Bottom <= 54).ToArray();
            check(controls.Any(hit => hit.Id == "recent-changes") && controls.Any(hit => hit.Id == "table-states")
                    && controls.Single(hit => hit.Id == "chat-toggle").Bounds == chat
                    && controls.All(first => controls.All(second => first.Id == second.Id || !first.Bounds.Intersects(second.Bounds))),
                "The real connected header keeps chat, recent changes, table states and navigation in separate stable slots at both window sizes and text scales");
            Point Physical(string id)
            {
                var hit = frame.Hits.Single(hit => hit.Id == id);
                var viewport = RenderViewport.Fit(size, frame.DesignHeight);
                var pixel = viewport.ToPhysical(hit.Bounds.Center.ToVector2());
                var physical = new Point((int)MathF.Round(pixel.X), (int)MathF.Round(pixel.Y));
                check(view.HitAt(viewport.ToLogical(physical))?.Id == id,
                    "The visible header control owns its actual physical pointer target: " + id);
                return physical;
            }
            foreach (var control in controls) Physical(control.Id);
            void Click(string id) { var at = Physical(id); Tick(at, ButtonState.Pressed); frame = Tick(at); }
            Click("chat-toggle");
            check(view.ChatOpen, "Table chat opens from its own visible header button");
            Click("chat-toggle"); Click("recent-changes");
            check(!view.ChatOpen && view.Overlay == "changes" && engine.Replies.Count == 0,
                "Recent changes opens independently of chat without submitting a game action");
        }
    }

    private static void CheckManaReadingLateLayers(string profile, Action<bool, string> check)
    {
        string path = Path.Combine(profile, "mana-reading-connected-late-layers"); Directory.CreateDirectory(path);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(path);
        var engine = new InteractionScenarioEngine();
        var initial = ManaPaymentScenario.Snapshot(0) with { DecisionOwnerId = 0, PriorityPlayerId = 0 };
        initial = initial with { Players = initial.Players.Select(player => player.Id is 0 or 1 ? player with {
            Mana = ImmutableDictionary<string, int>.Empty.Add("C", player.Id == 0 ? 4 : 9)
        } : player).ToImmutableArray() };
        engine.Present(initial);
        engine.SourceActionRead = (game, decision, key) => new(game, decision, key, ManaPaymentScenario.Spell,
            [new("current-cast", "cast", "Cast Faerie Vandal")]) { Revision = engine.State.Revision };
        var services = TestTableServices.For(engine) with { Social = new HeaderConversation() };
        var size = new Point(1000, 740);
        using var run = new PresentationRun(services, path, engine.State, size);
        var view = run.View;
        var lobby = new Lobby("joined", "Playing", null, false, null, true, "", null, [], [], "header-table");
        view.SetConnectedTable(lobby); view.PresentLobby(lobby, true);
        PresentationFrame Tick(Point? point = null, ButtonState button = ButtonState.Released,
            Microsoft.Xna.Framework.Input.KeyboardState? keys = null) => run.Tick(TimeSpan.FromSeconds(1d / 60),
                new((point ?? new(-100, -100)).X, (point ?? new(-100, -100)).Y, 0, button, default, default, default, default), keys ?? new());
        var frame = Tick(); Tick(); frame = Tick();
        void Click(string id) {
            var hit = frame.Hits.Single(hit => hit.Id == id);
            var pixel = RenderViewport.Fit(size, frame.DesignHeight).ToPhysical(hit.Bounds.Center.ToVector2());
            var point = new Point((int)MathF.Round(pixel.X), (int)MathF.Round(pixel.Y));
            Tick(point, ButtonState.Pressed); frame = Tick(point);
        }
        var chat = frame.Hits.Single(hit => hit.Id == "chat-toggle");
        Click(frame.Cards.Single(card => card.Id == ManaPaymentScenario.Spell).Hit.Id);
        for (int index = 0; index < 120 && !frame.Hits.Any(hit => hit.Id == "read-card-action"); index++) { frame = Tick(); Thread.Yield(); }
        check(frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Focused) && view.ManaJourney != null,
            "The real connected-social variant starts with a current pinned hand reader and bill");
        var bill = view.ManaJourney!;
        string pinnedKey = frame.Cards.Single(card => card.Id == ManaPaymentScenario.Spell && card.Focused).Card.Key;
        Click("chat-toggle"); view.TypeText("Unsent table draft"); frame = Tick();
        check(view.ChatOpen && view.ChatDraft == "Unsent table draft" && frame.Hits.Any(hit => hit.Id == "chat-background"),
            "The connected continuation opens the real chat drawer and enters an unsent draft before mana inspection");
        // The left seat's public pip remains exposed beside the open drawer.
        Click("mana-pool:1:C"); frame = Tick();
        var reading = frame.Observe();
        check(frame.Drawing.Commands.OfType<Mana.Renderer.DrawCommand.Text>().Any(text => text.Value == "Table chat")
            && !view.ChatOpen && view.ChatDraft == "Unsent table draft" && !frame.Hits.Any(hit => hit.Id == "chat-background")
            && view.ManaPoolDetail is { Total: 9 } && frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Card.Key == pinnedKey && card.Focused)
            && view.ManaJourney is { } current && current.SourceId == bill.SourceId && current.Symbols.SequenceEqual(bill.Symbols)
            && ManaPaymentSmoke.PoolReadingIsolated(view) && !view.Activate(chat)
            && view.HitAt(chat.Bounds.Center)?.Intent is Mana.Table.Interaction.UiIntent.NoInteraction && engine.Replies.Count == 0,
            "Opening the real exposed mana pip dismisses chat focus/drawer, preserves its unsent draft and the valid reader/bill, and isolates the late chat toggle");
        view.TypeText(" must not append"); frame = Tick(keys: new(Microsoft.Xna.Framework.Input.Keys.Enter)); frame = Tick();
        check(view.ChatDraft == "Unsent table draft" && !view.ChatOpen && !view.SocialSending && engine.Replies.Count == 0,
            "Passive mana reading cannot capture typing or send the retired chat draft through Enter");
        Click("close-overlay"); frame = Tick();
        view.TypeText(" still must not append");
        check(frame.Hits.Any(hit => hit.Id == "chat-toggle") && frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Focused)
            && !view.ChatOpen && view.ChatDraft == "Unsent table draft",
            "Closing mana reading returns the actual chat toggle and valid pin without restoring the drawer or its text focus");
        Click("chat-toggle"); check(view.ChatOpen && view.ChatDraft == "Unsent table draft",
            "A new deliberate chat click opens the preserved unsent draft after mana reading");
        Click("chat-toggle"); Click("mana-pool:0:C");
        frame = Tick(keys: new(Microsoft.Xna.Framework.Input.Keys.F1)); frame = Tick();
        var guide = frame.Observe();
        check(view.TableGuideOpen && frame.Hits.Any(hit => hit.Id == "experience-guide-close")
            && frame.Hits.Any(hit => hit.Id == "experience-guide-done") && !frame.Hits.Any(hit => hit.Id == "mana-reading-surface"),
            "A real F1 guide takeover retains its own late close/back controls instead of being erased by mana input isolation");
        Click("experience-guide-done"); frame = Tick();
        check(!view.TableGuideOpen && view.Overlay.Length == 0 && view.ManaPoolDetail == null
            && !frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Focused) && engine.Replies.Count == 0,
            "Back to it closes the higher guide without restoring the cleared mana reader, pinned card or old action");
        File.WriteAllText(Path.Combine(path, "mana-reading-late-layers.json"), System.Text.Json.JsonSerializer.Serialize(new {
            reading, guide, afterGuide = frame.Observe(), replies = engine.Replies.Count,
            scope = "Candidate-only actual connected Social and guide composition; authored current facts, not a rules or human UX claim."
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }

    private sealed class HeaderConversation : ITableSocial
    {
        public Task<TableConversation> ConversationAsync(string tableId) => Task.FromResult(new TableConversation(tableId, 0, 0, [], []));
        public Task<TableConversation> SendMessageAsync(string tableId, string kind, string text) => ConversationAsync(tableId);
        public Task<TableConversation> SetPlayerNameAsync(string tableId, string name) => ConversationAsync(tableId);
    }
}
