using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed class PriorityAbilitySmoke(TableWindow.Probe view, InteractionScenarioEngine scenario) : ITableAutomation
{
    private int stage;
    private double changed;
    private readonly List<string> checks = [];
    private MouseState mouse = new(0, 0, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    private string? click;
    private Action? staleUndo;
    private string manualGame = "", manualDecision = "", manualCard = "";
    public string? ExpectedClick => click;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;
    private void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
    private void Publish(GameSnapshot state) { scenario.Present(state); view.Accept(scenario.State); }
    private void Next() { stage++; changed = view.Now; }
    private void Capture(Texture2D surface, string name) { using var file = File.Create(Path.Combine(view.Profile, name + ".png")); surface.SaveAsPng(file, surface.Width, surface.Height); }
    private void MouseAt(Point point, ButtonState button = ButtonState.Released)
    {
        var viewport = view.GraphicsDevice.Viewport; float scale = Math.Min(viewport.Width / 1600f, viewport.Height / 900f);
        mouse = new((int)(point.X * scale + (viewport.Width - 1600 * scale) / 2), (int)(point.Y * scale + (viewport.Height - 900 * scale) / 2), 0,
            button, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    private void Press(string id) { click = id; ClickAccepted = false; MouseAt(view.Hits.Single(h => h.Id == id).Bounds.Center, ButtonState.Pressed); }
    private void Release() { var hit = view.Pressed ?? throw new InvalidOperationException("Expected a pressed control"); MouseAt(hit.Bounds.Center); }

    internal static GameSnapshot Example()
    {
        var state = PresentationSmoke.Example();
        var mesa = new Card { VisualId = "mesa", CombatId = "mesa", Name = "Arid Mesa", ArtName = "Arid Mesa", Type = "Land", Selectable = true,
            Text = "{T}, Pay 1 life, Sacrifice Arid Mesa: Search your library for a Mountain or Plains card, put it onto the battlefield, then shuffle." };
        return state with { ActivePlayerId = 1, Phase = "Main phase", PhaseKey = "MAIN1", Combat = null, Stack = [],
            Players = state.Players.Select(p => p.Id != state.ViewerId ? p : p with { Zones = p.Zones.Select(z => z.Name == "Battlefield" ? z with { Count = 1, Cards = [mesa] }
                : z.Name is "Hand" or "Command" ? z with { Count = 0, Cards = [] } : z).ToImmutableArray() }).ToImmutableArray(),
            Decision = new() { Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = true, CanAutoPass = false, Cancel = "End Turn", CancelEnabled = true,
                PriorityAbilities = [new("mesa-fetch", "mesa", "Arid Mesa", mesa.Text, false)] } };
    }

    public void Frame(Texture2D surface)
    {
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (view.Now > 35) throw new TimeoutException("Ability auto-pass stage " + stage);
        if (view.Busy || view.Now - changed < (stage == 0 ? 1.5 : .17)) return;
        var state = scenario.State;
        switch (stage) {
            case 0:
                view.SetAutomatic(true); view.SetHeld(true);
                Check(view.Hits.Any(h => h.Id == "ability-pass:mesa-fetch"), "A single activated ability offers pass-until-my-next-turn beside priority");
                Check(!state.Decision!.CanAutoPass, "Arid Mesa initially prevents automatic passing");
                Press("ability-pass:mesa-fetch"); Next(); break;
            case 1: Capture(surface, "01-ability-available"); Release(); Next(); break;
            case 2:
                Check(ClickAccepted && scenario.Replies is [IgnorePriorityAbility { AbilityId: "mesa-fetch" }], "The contextual control submits only the selected ability preference");
                Check(state.Decision!.CanAutoPass && state.IgnoredPriorityAbilities is [{ Id: "mesa-fetch", IgnoredUntilNextTurn: true }], "Engine-authorized Auto becomes eligible after ignoring the sole response");
                Check(view.Automatic && view.Held && view.Hits.Any(h => h.Id == "confirm"), "Hold continues to expose manual priority after an ability is ignored");
                Check(view.Hits.Any(h => h.Id == "ability-undo:mesa-fetch"), "The temporary preference has a visible Undo control");
                Capture(surface, "02-ability-ignored-held");
                Publish(state with { Decision = null, Status = "resolving" }); view.SetHeld(false); Next(); break;
            case 3:
                Check(view.Hits.Any(h => h.Id == "ability-undo:mesa-fetch"), "Undo remains available while the table resolves without a local decision");
                staleUndo = view.Hits.Single(h => h.Id == "ability-undo:mesa-fetch").Action;
                Capture(surface, "03-ability-ignored-following"); Press("ability-undo:mesa-fetch"); Next(); break;
            case 4: Release(); Next(); break;
            case 5:
                Check(ClickAccepted && state.IgnoredPriorityAbilities.Length == 0, "Undo clears the preference without waiting for local priority");
                Check(scenario.Replies[^1] is RestorePriorityAbility, "Undo uses the match preference command rather than a gameplay decision");
                Check(scenario.Replies.Count == 2 && state.Decision == null, "Changing the preference never activates the card or advances a decision");
                var example = Example();
                Publish(example with { Decision = example.Decision! with { PriorityAbilities = [example.Decision.PriorityAbilities[0], new("mesa-second", "mesa", "Arid Mesa", "A second independent ability on the same card.", false)] } });
                view.SetHeld(true); Next(); break;
            case 6:
                Check(view.Hits.Any(h => h.Id == "ability-auto-pass-open"), "Multiple abilities open individual choices instead of a card-wide ignore");
                Press("ability-auto-pass-open"); Next(); break;
            case 7: Release(); Next(); break;
            case 8:
                Check(view.Overlay == "ability-auto-pass" && view.Hits.Any(h => h.Id == "ability-pass:mesa-fetch"), "Ability details show the exact first ability and its preference");
                Capture(surface, "04-ability-details"); Press("ability-next"); Next(); break;
            case 9: Release(); Next(); break;
            case 10:
                Check(view.Hits.Any(h => h.Id == "ability-pass:mesa-second"), "The next ability on the same source has an independent scoped control");
                Press("ability-pass:mesa-second"); Next(); break;
            case 11: Release(); Next(); break;
            case 12:
                Check(state.IgnoredPriorityAbilities is [{ Id: "mesa-second" }] && !state.Decision!.CanAutoPass, "Ignoring one ability does not suppress another ability on the same card");
                Check(view.Hits.Any(h => h.Id == "confirm"), "Another available response still stops Auto");
                var mesaHit = view.Hits.First(h => h.Card?.VisualId == "mesa" && h.Zone == "Battlefield");
                Check(mesaHit.Card!.Selectable && CardActions.CanActivate(state, mesaHit.Card), "An ignored source remains available for manual activation");
                manualGame = state.Id; manualDecision = state.Decision!.Id; manualCard = mesaHit.Card.Key;
                Press(mesaHit.Id); Next(); break;
            case 13: Release(); Next(); break;
            case 14:
                if (view.DisplayedPose("mesa") is not { Size.X: >= 400 } || !view.Hits.Any(hit => hit.Id == "read-card-action")) return;
                Check(scenario.Replies.Count == 3 && state.Decision is { Intent: DecisionIntent.Priority }
                    && state.IgnoredPriorityAbilities is [{ Id: "mesa-second" }],
                    "Reading the ignored source sends no gameplay reply and preserves its independent ability preference");
                Press("read-card-action"); Next(); break;
            case 15: Release(); Next(); break;
            case 16:
                Check(scenario.Replies[^1] is ActivateCard reply && reply.GameId == manualGame
                    && reply.DecisionId == manualDecision && reply.Card == manualCard && state.Decision is { Purpose: DecisionPurpose.AbilitySelection },
                    "Explicit manual activation opens the normal ability chooser with the exact current game, decision and card scope");
                var nextTurn = Example();
                Publish(nextTurn with { Turn = nextTurn.Turn + 1, ActivePlayerId = nextTurn.ViewerId, IgnoredPriorityAbilities = [] }); Next(); break;
            case 17:
                staleUndo!();
                Check(!view.Busy && scenario.Replies.Count == 4, "An Undo captured before expiry quietly does nothing after that preference has cleared");
                Check(view.Hits.Any(h => h.Id == "ability-pass:mesa-fetch") && !view.Hits.Any(h => h.Id.StartsWith("ability-undo:")), "The viewer's next turn shows the ability available again after engine expiry");
                Check(!TurnGuide.CanAutoPass(state, true, false, new HashSet<string>(), false), "After expiry the available ability once again stops Auto");
                Check(scenario.Replies.Count == 4 && scenario.Replies.All(r => r is IgnorePriorityAbility or RestorePriorityAbility or ActivateCard), "Preference and inspection flows never submit an automatic activation or extra pass");
                Capture(surface, "05-ability-next-turn");
                File.WriteAllText(Path.Combine(view.Profile, "priority-ability-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                view.Exit(); break;
        }
    }
}
