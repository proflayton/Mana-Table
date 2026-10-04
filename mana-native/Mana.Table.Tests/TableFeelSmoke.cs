using System.Text.Json;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

// A visual sequence, not a rules simulation. The real engine/UI tests validate play.
internal sealed class TableFeelSmoke(TableGame.Probe view, InteractionScenarioEngine scenario) : ITableAutomation
{
    private int stage;
    private double started;
    private readonly List<string> checks = [];
    private Card? spell;
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => new(0, 0, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    private void Publish(GameSnapshot state) { scenario.Present(state); view.Accept(scenario.State); started = view.Now; stage++; }
    private void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); checks.Add(description); }
    private void Capture(Texture2D surface, string name)
    {
        using var output = File.Create(Path.Combine(view.Profile, name + ".png"));
        surface.SaveAsPng(output, surface.Width, surface.Height);
    }
    public void Frame(Texture2D surface)
    {
        double age = view.Now - started;
        var state = scenario.State;
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (view.Now > 30) throw new TimeoutException("Table feel review at stage " + stage);
        if (stage == 0 && age > 2) {
            Capture(surface, "01-table");
            Publish(state with { Players = state.Players.Select(p => p.Id != 1 ? p : p with { Zones = p.Zones.Select(z => z.Name == "Hand" ? z with { Count = z.Count + 1 } : z.Name == "Library" ? z with { Count = z.Count - 1 } : z).ToArray() }).ToArray() });
        } else if (stage == 1 && age > .23) {
            Capture(surface, "02-opponent-draw");
            Check(state.Players[1].Zone("Hand").Cards.Length == 0, "Opponent draw animation needs only public counts; the hand remains empty of card identities");
            stage++; started = view.Now;
        } else if (stage == 2 && age > .8) {
            spell = state.Players[1].Zone("Command").Cards[0];
            Publish(state with { Players = state.Players.Select(p => p.Id != 1 ? p : p with { Zones = p.Zones.Select(z => z.Name == "Command" ? z with { Count = 0, Cards = [] } : z).ToArray() }).ToArray(),
                Stack = [new(spell.Name, "A commander spell is waiting to resolve.", false, spell) { Id = "cast-1" }],
                Activity = [new(1, state.Turn, state.PhaseKey, "cast", 1, "Opponent 1 cast their commander.", spell.VisualId, spell.Name)] });
        } else if (stage == 3 && age > .18) {
            Check(view.DisplayedPose(spell!.VisualId) is { Elevation: > 0 }, "A cast commander keeps its occurrence and rises into the response area");
            Capture(surface, "03-cast-flight"); stage++; started = view.Now;
        } else if (stage == 4 && age > 1) {
            Check(view.Hits.Any(h => h.Id == "stack") && view.Hits.Any(h => h.Id == "confirm"), "The enlarged stack and the response button remain independently accessible");
            Capture(surface, "04-response-window");
            Publish(state with { Stack = [], Players = state.Players.Select(p => p.Id != 1 ? p : p with { Zones = p.Zones.Select(z => z.Name == "Battlefield" ? z with { Count = z.Count + 1, Cards = [spell!, .. z.Cards] } : z).ToArray() }).ToArray() });
        } else if (stage == 5 && age > .19) {
            Check(view.DisplayedPose(spell!.VisualId) is { Elevation: > 0 }, "Resolving a commander continues from the stack toward its owner's battlefield");
            Capture(surface, "05-resolution"); stage++; started = view.Now;
        } else if (stage == 6 && age > 1) {
            Capture(surface, "06-settled");
            Publish(state with { Turn = state.Turn + 1, ActivePlayerId = 0, Players = state.Players.Select(p => p.Id != 0 ? p : p with { Life = p.Life - 4 }).ToArray() });
        } else if (stage == 7 && age > .2) {
            Capture(surface, "07-turn-and-life");
            Check(view.Hits.Any(h => h.Id == "confirm") && scenario.Replies.Count == 0, "Turn and life feedback never consumes input or submits a game action");
            File.WriteAllText(Path.Combine(view.Profile, "feel-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            view.Exit();
        }
    }
}
