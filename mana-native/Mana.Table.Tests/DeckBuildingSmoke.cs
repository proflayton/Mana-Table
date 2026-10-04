using Mana.Contracts;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Text.Json;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

// Imported text follows the same application path as the file/clipboard dialog.
// Every deck selection and role change after that uses real pointer input and
// the bundled adapter, including persistence, network readiness and AI startup.
internal sealed class DeckBuildingSmoke(TableGame.Probe view) : ITableAutomation
{
    private readonly List<string> checks = [];
    private readonly DateTime deadline = DateTime.UtcNow.AddMinutes(4);
    private int stage, nextStage, mousePhase;
    private Point mouse;
    public string? ExpectedClick { get; private set; }
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse()
    {
        bool down = mousePhase == 1;
        if (mousePhase is 1 or 2) mousePhase++;
        return new(mouse.X, mouse.Y, 0, down ? ButtonState.Pressed : ButtonState.Released,
            ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    private void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException($"Deck-building stage {stage}: {message}");
        checks.Add(message);
        File.AppendAllText(Path.Combine(view.Profile, "deck-building-progress.log"), message + "\n");
    }
    private void Click(string id, int next)
    {
        var hit = view.Hits.LastOrDefault(h => h.Id == id) ?? throw new InvalidOperationException($"Missing deck control: {id} at stage {stage}");
        var point = hit.Bounds.Center;
        var viewport = view.GraphicsDevice.Viewport;
        float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        mouse = new((int)((viewport.Width - 1600 * scale) / 2 + point.X * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + point.Y * scale));
        ExpectedClick = id; ClickAccepted = false; nextStage = next; mousePhase = 1;
    }
    private DeckDetails Deck => view.DeckDetails ?? throw new InvalidOperationException("No imported deck");
    private DeckEntry Entry(string name) => Deck.Entries.First(e => e.Card.Name == name);
    private void Card(string name, int next) { var e = Entry(name); Click("deck-card:" + e.Section + ":" + e.Id, next); }
    private void Capture(string name)
    {
        using var file = File.Create(Path.Combine(view.Profile, name + ".png"));
        view.Surface.SaveAsPng(file, view.Surface.Width, view.Surface.Height);
    }
    public void Frame(Texture2D surface)
    {
        if (DateTime.UtcNow > deadline) throw new TimeoutException("Deck-building UI timed out at stage " + stage);
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (mousePhase is 1 or 2) return;
        if (mousePhase == 3) {
            if (!ClickAccepted) throw new InvalidOperationException("Deck click was rejected: " + ExpectedClick);
            stage = nextStage; mousePhase = 0; ExpectedClick = null;
        }
        if (!view.Loaded || view.Busy || view.Polling) return;
        switch (stage) {
            case 0:
                view.ImportDeck("Imported Commander regression", "Deck\n98 Forest\n1 Rhys the Redeemed\n1 Selvala, Explorer Returned"); stage++; break;
            case 1:
                Check(view.DeckEditor && Deck.CommanderIds.Length == 0, "Import opens the deck view and explicitly shows that no commander is set");
                Check(Deck.CommanderOptions.Length == 2 && !Deck.CommanderOptions.Any(c => c.Id == Entry("Forest").Id), "Engine exposes eligible commander choices, excluding basic lands");
                Capture("01-imported-deck"); Card("Rhys the Redeemed", 2); break;
            case 2: Click("deck-set-commander", 3); break;
            case 3:
                Check(Deck.CommanderIds.SequenceEqual([Entry("Rhys the Redeemed").Id]) && Deck.Entries.Where(e => e.Section == "Main").Sum(e => e.Quantity) == 99 && Deck.Problem.Length == 0, "Set commander moves exactly one card, producing a legal 99 + 1 deck");
                Capture("02-commander-set"); Click("deck-done", 4); break;
            case 4: Click("setup-close", 104); break;
            case 104: Click("change-deck", 105); break;
            case 105: Click("deck:" + Deck.Id, 5); break;
            case 5:
                Check(Deck.CommanderIds.SequenceEqual([Entry("Rhys the Redeemed").Id]) && Deck.SaveError.Length == 0, "Reopening the saved deck preserves its commander");
                Click("confirm-edit", 6); break;
            case 6: Card("Selvala, Explorer Returned", 7); break;
            case 7: Click("deck-set-commander", 8); break;
            case 8:
                Check(Entry("Rhys the Redeemed").Section == "Main" && Entry("Selvala, Explorer Returned").Section == "Commander" && Deck.Entries.Sum(e => e.Quantity) == 100, "Replacing the commander returns the previous card without losing or duplicating cards");
                Capture("03-commander-replaced"); Click("deck-all", 9); break;
            case 9: Card("Forest", 10); break;
            case 10:
                Check(!view.Hits.Any(h => h.Id == "deck-set-commander"), "An ineligible card remains inspectable but cannot be set as commander");
                Click("deck-done", 11); break;
            case 11:
                Click("setup-close", 111); break;
            case 111:
                view.ImportDeck("Partner Commander regression", "Deck\n98 Mountain\n1 Rograkh, Son of Rohgahh\n1 Kediss, Emberclaw Familiar"); stage = 12; break;
            case 12: Card("Rograkh, Son of Rohgahh", 13); break;
            case 13: Click("deck-set-commander", 14); break;
            case 14: Card("Kediss, Emberclaw Familiar", 15); break;
            case 15: Click("deck-add-partner", 16); break;
            case 16:
                Check(Deck.CommanderIds.Length == 2 && Deck.Entries.Where(e => e.Section == "Main").Sum(e => e.Quantity) == 98 && Deck.Problem.Length == 0, "Add partner creates a legal 98 + 2 deck");
                Capture("04-partner-commanders"); Click("deck-done", 17); break;
            case 17: Click("setup-close", 117); break;
            case 117: Click("change-deck", 118); break;
            case 118: Click("deck:" + Deck.Id, 18); break;
            case 18:
                Check(Deck.CommanderIds.Length == 2 && Deck.SaveError.Length == 0, "Both partner commanders survive reopening from disk");
                Click("setup-close", 119); break;
            case 119: Click("friends-tab", 19); break;
            case 19: Click("connection-options", 132); break;
            case 132: Click("forward", 32); break;
            case 32: Click("setup-close", 133); break;
            case 133: Click("host", 20); break;
            case 20: Click("review-deck", 120); break;
            case 120: Click("confirm-deck", 121); break;
            case 121: Click("ready", 21); break;
            case 21:
                Check(view.Lobby?.Seats.Any(s => s.Local && s.Ready && s.Deck == Deck.Name) == true, "The saved commanders pass the shared multiplayer deck and readiness validation");
                Click("edit-deck", 22); break;
            case 22: Card("Rograkh, Son of Rohgahh", 23); break;
            case 23: Click("deck-set-commander", 24); break;
            case 24:
                Check(view.Lobby?.Seats.Any(s => s.Local && !s.Ready) == true && Deck.CommanderIds.Length == 1, "Editing a readied deck clears readiness before saving");
                Card("Kediss, Emberclaw Familiar", 25); break;
            case 25: Click("deck-add-partner", 26); break;
            case 26: Click("deck-done", 27); break;
            case 27: Click("setup-close", 127); break;
            case 127: Click("leave", 28); break;
            case 28: Click("solo-tab", 29); break;
            case 29: Click("review-deck", 129); break;
            case 129: Click("confirm-deck", 30); break;
            case 30:
                Check(view.SoloSetup != null, "The same saved deck prepares a three-opponent AI game");
                Click("start-solo", 31); break;
            case 31:
                if (view.Match?.Decision == null) break;
                Check(view.Match.Players.Length == 4 && view.Match.Players.All(p => p.Life == 40), "The deck starts a real four-player Commander game");
                Check(view.Match.Viewer!.Zone("Command").Cards.Select(c => c.Name).Order().SequenceEqual(new[] { "Kediss, Emberclaw Familiar", "Rograkh, Son of Rohgahh" }), "The exact selected partners appear in the command zone");
                Capture("05-command-zone");
                File.WriteAllText(Path.Combine(view.Profile, "deck-building-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                view.Exit(); stage++; break;
        }
    }
}
