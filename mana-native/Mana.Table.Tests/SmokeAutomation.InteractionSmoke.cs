using Mana.Magic;
using System.Text.Json;
using Mana.Contracts;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed partial class SmokeAutomation
{
    private readonly Queue<MouseState> interactionInput = new();
    private int interactionStep, interactionFrame;
    private bool interactionWaiting, interactionRequireAcceptance;
    private readonly List<string> interactionChecks = [];
    private void InteractionCheck(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Interaction test: " + message);
        if (interactionChecks.Contains(message)) return;
        interactionChecks.Add(message); File.AppendAllText(Path.Combine(view.Profile, "interaction-progress.log"), message + "\n");
    }
    private MouseState InteractionMouse()
    {
        MouseState At(Point p, ButtonState left) => new(p.X, p.Y, 0, left, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
        var idle = At(new(0, 0), ButtonState.Released);
        if (interactionInput.TryDequeue(out var pending)) return pending;
        if (interactionWaiting) {
            interactionWaiting = false;
            if (interactionRequireAcceptance && !smokeClickAccepted) interactionStep--;
        }
        if (DateTime.UtcNow > smokeDeadline) throw new TimeoutException("Interaction test at step " + interactionStep);
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (!view.Loaded || view.Busy || view.Polling || ++interactionFrame % 5 != 0) return idle;
        var scenario = (InteractionScenarioEngine)view.Engine;
        string CardHit(string id) { var card = CombatGuide.Find(view.Match!, id)!; return "card:" + card.Key + ":" + card.VisualId; }
        void State(Action action) { action(); view.Accept(scenario.State); interactionStep++; }
        string? click = null, target = null; bool expect = true;
        switch (interactionStep) {
            case 0: click = CardHit("own-0"); target = "player:3"; expect = false; break;
            case 1:
                InteractionCheck(scenario.Replies.Count == 0, "Dropping an attacker on an illegal player sends no command");
                click = CardHit("own-0"); target = "player:1"; break;
            case 2:
                InteractionCheck(view.Match!.Combat!.Attackers.Single().DefendingPlayerId == 1, "A real pointer drag assigns the projected legal defender");
                click = CardHit("own-0"); break;
            case 3: click = "combat-remove"; break;
            case 4:
                InteractionCheck(view.Match!.Combat!.Attackers.Length == 0, "Remove toggles the current attack using fresh scoped handles");
                click = "combat-review"; break;
            case 5: click = "combat-creatures"; break;
            case 6: click = "combat-source:own-12"; break;
            case 7:
                InteractionCheck(view.SelectedCombat == "own-12" && view.Hits.Any(h => h.Id == CardHit("own-12")), "Combat browser selects a creature from a crowded rank and brings it into view");
                click = "player:2"; break;
            case 8:
                InteractionCheck(view.Match!.Combat!.Attackers.Single().DefendingPlayerId == 2, "Different attackers can choose different Commander opponents");
                click = "combat-review"; break;
            case 9: CaptureInteraction("combat-review.png"); click = "combat-recall:own-12"; break;
            case 10:
                InteractionCheck(view.Match!.Combat!.Attackers.Length == 0, "Combat review can recall an assigned attacker");
                click = "close-overlay"; break;
            case 11:
                if (!view.Automatic) { click = "auto"; interactionStep--; break; }
                State(scenario.BeginBlocks); return idle;
            case 12: click = CardHit("own-9"); target = CardHit("enemy-b"); expect = false; break;
            case 13:
                InteractionCheck(scenario.Replies.Count == 4 && view.Automatic, "With Auto enabled, blocking waits for input and an ineligible pair sends no command");
                click = CardHit("own-9"); target = CardHit("enemy-a"); break;
            case 14:
                InteractionCheck(view.Match!.Combat!.Attackers[0].BlockerIds.SequenceEqual(["own-9"]) && !view.Hits.Any(h => h.Id == "confirm"), "Assigned block appears and engine-reported block problems disable confirmation");
                click = CardHit("own-12"); break;
            case 15: click = "combat-review"; break;
            case 16: click = "combat-pair:enemy-a"; break;
            case 17:
                InteractionCheck(view.Match!.Combat!.Attackers[0].BlockerIds.Length == 2 && view.Hits.Any(h => h.Id == "confirm"), "Combat review assigns a second blocker and confirmation becomes available");
                CaptureInteraction("combat-blocks.png"); click = "combat-review"; break;
            case 18: click = "combat-blockers:enemy-a"; break;
            case 19:
                InteractionCheck(view.Hits.Any(h => h.Id == "blocker-remove:own-9") && view.Hits.Any(h => h.Id == "blocker-remove:own-12"), "Blocker gallery exposes each assigned pair for inspection and removal");
                CaptureInteraction("blockers.png"); click = "blocker-remove:own-9"; break;
            case 20:
                InteractionCheck(view.Match!.Combat!.Attackers[0].BlockerIds.SequenceEqual(["own-12"]), "Remove block preserves the other blocker's assignment");
                click = "close-overlay"; break;
            case 21: State(() => scenario.Stack("A")); return idle;
            case 22: click = "stack"; break;
            case 23: State(() => scenario.Stack("B", "A")); return idle;
            case 24:
                InteractionCheck(view.InspectedStack == "A" && view.ZonePage == 1, "Stack inspector follows the same ability when another item is pushed above it");
                State(() => scenario.Stack("B")); return idle;
            case 25:
                InteractionCheck(view.Hits.Any(h => h.Id == "stack-current"), "An inspected ability leaving the stack cannot silently become a different target");
                click = "stack-current"; break;
            case 26:
                InteractionCheck(view.InspectedStack == "B", "Inspector explicitly advances to the next stack item");
                CaptureInteraction("stack-inspector.png"); click = "close-overlay"; break;
            case 27:
                // Simulate an authoritative decision arriving between press and release.
                var oldHit = view.Hits.Single(h => h.Id == "confirm");
                view.Pressed = oldHit; view.PreviousMouse = At(new(0, 0), ButtonState.Pressed);
                scenario.Stack("B"); view.Accept(scenario.State);
                interactionStep++;
                return At(ScreenPoint(oldHit.Bounds.Center), ButtonState.Released);
            case 28:
                InteractionCheck(scenario.Replies.Count == 7, "A press from an old decision is discarded before dispatch");
                State(() => scenario.ChoosePlayer(0, 1, 2)); return idle;
            case 29:
                InteractionCheck(!view.Hits.Any(h => h.Id is "confirm" or "cancel"), "Mandatory player targeting cannot be confirmed or cancelled without a target");
                CaptureInteraction("player-targets.png"); click = "player:3"; break;
            case 30:
                InteractionCheck(scenario.Replies.Count == 7 && view.Overlay == "player", "A player absent from the engine choices remains inspectable without submitting a target");
                click = "close-overlay"; break;
            case 31: click = "player:1"; break;
            case 32:
                InteractionCheck(scenario.Replies.Count == 8 && scenario.Replies[^1] is { Action: ReplyAction.SelectPlayer, Player: 1 } && view.Overlay == "", "Clicking a legal opponent portrait submits the scoped player target");
                State(() => scenario.ChoosePlayer(0, 1, 2, 3)); return idle;
            case 33: click = "player:0"; break;
            case 34:
                InteractionCheck(scenario.Replies.Count == 9 && scenario.Replies[^1].Player == 0, "The local portrait can select yourself when the engine permits it");
                State(() => scenario.ChoosePlayer(0, 1, 2, 3)); return idle;
            case 35: click = "player:2"; break;
            case 36:
                InteractionCheck(scenario.Replies.Count == 10 && scenario.Replies[^1].Player == 2, "The second opponent portrait can submit a player target");
                State(() => scenario.ChoosePlayer(0, 1, 2, 3)); return idle;
            case 37: click = "player:3"; break;
            case 38:
                InteractionCheck(scenario.Replies.Count == 11 && scenario.Replies[^1].Player == 3, "The third opponent becomes selectable after a fresh legal-target projection");
                State(() => scenario.ChooseCards(2, 2)); return idle;
            case 39:
                InteractionCheck(!view.Hits.Any(h => h.Id is "close-overlay" or "open-decision" or "confirm"), "A required battlefield card choice leaves the table open and cannot confirm too few cards");
                click = CardHit("own-9"); break;
            case 40:
                InteractionCheck(view.Selections.SequenceEqual([0]) && scenario.Replies.Count == 11, "A single table click stages the exact occurrence without a popup or command");
                click = CardHit("own-9"); break;
            case 41:
                InteractionCheck(view.Selections.Count == 0, "Clicking a selected battlefield card removes it from the choice");
                click = CardHit("own-12"); break;
            case 42: click = CardHit("enemy-a"); break;
            case 43:
                InteractionCheck(view.Selections.SequenceEqual([1, 2]) && scenario.Replies.Count == 11, "Cards from two seats can be selected together on the table");
                CaptureInteraction("table-choice.png");
                File.WriteAllText(Path.Combine(view.Profile, "table-choice.json"), JsonSerializer.Serialize(view.Match, new JsonSerializerOptions { WriteIndented = true }));
                click = "confirm"; break;
            case 44:
                InteractionCheck(scenario.Replies.Count == 12 && scenario.Replies[^1].Choices!.SequenceEqual([1, 2]), "The ordinary action button submits the staged authoritative choice indices");
                State(() => scenario.ChooseCards(3, 3, true)); return idle;
            case 45:
                InteractionCheck(view.Selections.Count == 0 && !view.Hits.Any(h => h.Id == "confirm"), "An ordered table choice starts empty so click order is explicit");
                click = CardHit("enemy-a"); break;
            case 46: click = CardHit("own-12"); break;
            case 47: click = CardHit("own-9"); break;
            case 48: click = "confirm"; break;
            case 49:
                InteractionCheck(scenario.Replies.Count == 13 && scenario.Replies[^1].Choices!.SequenceEqual([2, 1, 0]), "Ordered choices preserve the sequence of clicks across seats");
                State(() => scenario.ChooseCards(0, 1)); return idle;
            case 50: click = "confirm"; break;
            case 51:
                InteractionCheck(scenario.Replies.Count == 14 && scenario.Replies[^1].Choices!.Length == 0, "An optional table choice can explicitly choose none");
                State(scenario.Finish); return idle;
            case 52:
                InteractionCheck(view.Overlay == "result" && view.Hits.Any(h => h.Id == "return"), "Terminal state opens a complete game result with return controls");
                CaptureInteraction("result.png"); click = "close-overlay"; break;
            case 53:
                InteractionCheck(view.Overlay.Length == 0 && view.Match?.Status == "finished", "Final battlefield remains inspectable after closing results");
                File.WriteAllText(Path.Combine(view.Profile, "interaction-results.json"), JsonSerializer.Serialize(interactionChecks, new JsonSerializerOptions { WriteIndented = true }));
                view.Exit(); return idle;
        }
        if (click == null) return idle;
        var hit = view.Hits.LastOrDefault(h => h.Id == click && view.CurrentHit(h));
        if (hit == null) return idle;
        Point? point = view.HitAt(hit.Bounds.Center)?.Id == hit.Id ? hit.Bounds.Center : null;
        for (int y = hit.Bounds.Y + 4; point == null && y < Math.Min(view.DesignHeight, hit.Bounds.Bottom); y += 5)
            for (int x = hit.Bounds.X + 4; x < hit.Bounds.Right; x += 5) if (view.HitAt(new(x, y))?.Id == hit.Id) { point = new(x, y); break; }
        if (point == null) return idle;
        var from = ScreenPoint(point.Value);
        if (target != null) {
            var destination = view.Hits.LastOrDefault(h => h.Id == target);
            if (destination == null) return idle;
            var to = ScreenPoint(destination.Bounds.Center);
            interactionInput.Enqueue(At(to, ButtonState.Pressed)); interactionInput.Enqueue(At(to, ButtonState.Released));
        } else interactionInput.Enqueue(At(from, ButtonState.Released));
        smokePendingClick = click; smokeClickAccepted = false;
        interactionWaiting = true; interactionRequireAcceptance = expect; interactionStep++;
        return At(from, ButtonState.Pressed);
    }
    private Point ScreenPoint(Point logical)
    {
        var viewport = view.GraphicsDevice.Viewport; float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        return new((int)((viewport.Width - 1600 * scale) / 2 + logical.X * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + logical.Y * scale));
    }
    private void CaptureInteraction(string filename)
    {
        using var output = File.Create(Path.Combine(view.Profile, filename));
        view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height);
    }
}
