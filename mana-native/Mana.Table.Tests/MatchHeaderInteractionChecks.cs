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

    private sealed class HeaderConversation : ITableSocial
    {
        public Task<TableConversation> ConversationAsync(string tableId) => Task.FromResult(new TableConversation(tableId, 0, 0, [], []));
        public Task<TableConversation> SendMessageAsync(string tableId, string kind, string text) => ConversationAsync(tableId);
        public Task<TableConversation> SetPlayerNameAsync(string tableId, string name) => ConversationAsync(tableId);
    }
}
