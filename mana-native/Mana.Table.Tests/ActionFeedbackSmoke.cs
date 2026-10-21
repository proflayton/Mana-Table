using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed class ActionFeedbackSmoke(TableGame.Probe view, InteractionScenarioEngine scenario) : ITableAutomation
{
    private int stage;
    private double changed;
    private readonly List<string> checks = [];
    private readonly GameSnapshot original = scenario.State;
    private MouseState mouse = new(0, 0, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    public string? ExpectedClick => stage is 9 or 10 ? "read-card-action" : stage is 5 or 6 ? "close-overlay" : "action-details";
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;
    private void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
    private void Publish(GameSnapshot state) { scenario.Present(state); view.Accept(scenario.State); }
    private void Next() { stage++; changed = view.Now; }
    private void Capture(Texture2D surface, string name) {
        using var file = File.Create(Path.Combine(view.Profile, name + ".png")); surface.SaveAsPng(file, surface.Width, surface.Height);
    }
    private void MouseAt(Point point, ButtonState left = ButtonState.Released, ButtonState right = ButtonState.Released) {
        var viewport = view.GraphicsDevice.Viewport; float scale = Math.Min(viewport.Width / 1600f, viewport.Height / 900f);
        mouse = new((int)(point.X * scale + (viewport.Width - 1600 * scale) / 2), (int)(point.Y * scale + (viewport.Height - 900 * scale) / 2), 0,
            left, ButtonState.Released, right, ButtonState.Released, ButtonState.Released);
    }
    private void MouseAtAbility(ButtonState left = ButtonState.Released) {
        var hit = view.Hits.Single(h => h.Card?.VisualId == "seat-0-1" && h.Zone == "Battlefield");
        var physical = SmokePointer.Hit(view, hit);
        mouse = new(physical.X, physical.Y, 0, left, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    public void Frame(Texture2D surface)
    {
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (view.Now > 30) throw new TimeoutException("Action feedback stage " + stage);
        if (view.Busy || view.Now - changed < (stage is 0 or 1 ? 1 : .15)) return;
        if (stage is 3 or 9 && (view.DisplayedPose("seat-0-1") is not { Size.X: >= 400 }
            || view.HoverPreview == null || !view.Hits.Any(hit => hit.Id == "read-card-action")) && view.Now - changed < 3) return;
        var state = scenario.State;
        switch (stage) {
            case 0:
                var creature = view.Hits.First(h => h.Card?.VisualId == "seat-0-1" && h.Zone == "Battlefield");
                Check(CardActions.CanActivate(state, creature.Card!), "The engine's available battlefield action is described as an activation");
                Check(!CardActions.CanActivate(state, creature.Card! with { Selectable = false }), "Unavailable abilities are never guessed from printed rules text");
                MouseAtAbility(); Next(); break;
            case 1:
                Check(view.HoverPreview == null && scenario.Replies.Count == 0, "Hover highlights the available ability without opening a reader or activating it");
                MouseAtAbility(ButtonState.Pressed); Next(); break;
            case 2:
                mouse = new(mouse.X, mouse.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released); Next(); break;
            case 3:
                Check(view.DisplayedPose("seat-0-1") is { Size.X: >= 400 } && view.HoverPreview != null
                    && view.Hits.Any(h => h.Id == "read-card-action") && scenario.Replies.Count == 0,
                    "An explicit click reads the ability card and exposes its separate activation control; pose="
                    + view.DisplayedPose("seat-0-1") + "; preview=" + view.HoverPreview + "; replies=" + scenario.Replies.Count);
                Capture(surface, "01-ability-hover");
                MouseAt(view.HoverPreview!.Value.Center, right: ButtonState.Pressed); Next(); break;
            case 4:
                MouseAt(new(0, 0)); Next(); break;
            case 5:
                Check(view.Overlay == "inspect" && view.Hits.Any(h => h.Id == "activate-ability"), "Inspector exposes an explicit Activate ability control");
                Capture(surface, "02-ability-inspector");
                MouseAt(view.Hits.Single(h => h.Id == "close-overlay").Bounds.Center, ButtonState.Pressed); Next(); break;
            case 6:
                MouseAt(view.Hits.Single(h => h.Id == "close-overlay").Bounds.Center); Next(); break;
            case 7:
                Check(view.Overlay.Length == 0 && scenario.Replies.Count == 0, "Reviewing the activation in the inspector sends no engine command");
                MouseAtAbility(ButtonState.Pressed); Next(); break;
            case 8:
                mouse = new(mouse.X, mouse.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released); Next(); break;
            case 9:
                Check(view.DisplayedPose("seat-0-1") is { Size.X: >= 400 } && view.Hits.Any(h => h.Id == "read-card-action"),
                    "Reading the same available card again restores the explicit action");
                MouseAt(view.Hits.Single(h => h.Id == "read-card-action").Bounds.Center, ButtonState.Pressed); Next(); break;
            case 10:
                MouseAt(view.Hits.Single(h => h.Id == "read-card-action").Bounds.Center); Next(); break;
            case 11:
                Check(scenario.Replies.Count == 1 && scenario.Replies[0].Action == ReplyAction.SelectCard && state.Decision?.Title.StartsWith("Activate ") == true,
                    "The reader's explicit activation routes through the scoped card command into the engine's ability chooser");
                Capture(surface, "03-ability-choice");
                var victim = original.Viewer!.Zone("Battlefield").Cards.Single(c => c.VisualId == "seat-0-1");
                var raven = new Card { VisualId = "raven", Name = "Ravenform", Type = "Sorcery", Text = "Exile target artifact or creature. Its controller creates a 1/1 blue Bird creature token with flying." };
                Publish(original with { Stack = [new("Ravenform", "Exile Llanowar Elves. Its controller creates a 1/1 blue Bird creature token with flying.", false, raven) {
                    Id = "raven-cast", Targets = [new("card", victim.VisualId, victim.Name, 0)] }] }); MouseAt(new(0, 0)); Next(); break;
            case 12:
                Check(view.Hits.Any(h => h.Id == "action-details") && view.Match!.Stack[0].Targets[0].Id == "seat-0-1", "Pending removal presents its source, explanation and exact targeted occurrence");
                Capture(surface, "04-removal-target");
                var removed = state.Viewer!.Zone("Battlefield").Cards.Single(c => c.VisualId == "seat-0-1");
                var bird = new Card { VisualId = "new-bird", CombatId = "new-bird", Name = "Bird", Type = "Token Creature - Bird", Power = 1, Toughness = 1, CombatKeywords = ["flying"], Text = "Flying" };
                Publish(state with { Stack = [], Players = state.Players.Select(p => p.Id == 0 ? p with { Zones = p.Zones.Select(z =>
                    z.Name == "Battlefield" ? z with { Cards = z.Cards.Where(c => c.VisualId != removed.VisualId).Append(bird).ToArray() }
                    : z.Name == "Exile" ? z with { Count = 1, Cards = [removed] } : z).ToArray() } : p).ToArray(),
                    Activity = [new(1, state.Turn, state.PhaseKey, "moved", 0, "Llanowar Elves moved from battlefield to exile.", removed.VisualId, removed.Name),
                        new(2, state.Turn, state.PhaseKey, "arrived", 0, "Bird token entered the battlefield under your control.", bird.VisualId, bird.Name),
                        new(3, state.Turn, state.PhaseKey, "resolved", 1, "Ravenform resolved.", "raven", "Ravenform") { Detail = state.Stack[0].Text }] });
                Next(); break;
            case 13:
                Check(view.LastResolution is { Title: "Ravenform resolved.", Changes.Length: 2 }, "Resolution feedback keeps both the removal and newly created token visible as recent changes");
                Check(view.LastResolution!.Detail.Contains("1/1 blue Bird"), "The resolved effect retains its public explanation for review");
                Capture(surface, "05-removal-result");
                MouseAt(view.Hits.Single(h => h.Id == "action-details").Bounds.Center, ButtonState.Pressed); Next(); break;
            case 14:
                MouseAt(view.Hits.Single(h => h.Id == "action-details").Bounds.Center); Next(); break;
            case 15:
                Check(view.Overlay == "action" && view.Hits.Any(h => h.Id == "action-history"), "Resolution details remain reviewable after the spell leaves the stack");
                Capture(surface, "06-resolution-details");
                Check(scenario.Replies.Count == 1, "Inspecting cause and result never submits another game action");
                File.WriteAllText(Path.Combine(view.Profile, "action-feedback-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                view.Exit(); break;
        }
    }
}
