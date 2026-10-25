using System.Text.Json;
using Mana.Contracts;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed partial class SmokeAutomation
{
    // Opt-in UI integration test: real engine, real rendered controls, and the
    // same press/release hit-testing path as a mouse. No player's profile is seeded.
    private readonly DateTime smokeDeadline = DateTime.UtcNow.AddMinutes(4);
    private string? smokeScreenshot, smokeFirstGame;
    private int smokeStage;
    private Point? smokeRelease, smokeDrag;
    private readonly List<string> smokeChecks = [];
    private string? smokeDecision;
    private string? smokePendingClick;
    private bool smokeClickAccepted;
    private int smokeRetryStage;
    private string? smokeLand;
    private readonly HashSet<string> artworkOpenings = [];

    private void RecordOpeningArtwork(string name)
    {
        var state = view.Match!;
        File.WriteAllText(Path.Combine(view.Profile, name + ".json"), JsonSerializer.Serialize(new {
            view.Now, state.Turn, state.PhaseKey, Progress = view.TableArtworkProgress,
            Cards = CardViews.Visible(state).Where(card => Mana.Magic.CardPresentation.Artwork(card) != null)
                .Select(card => new { card.Name, Artwork = Mana.Magic.CardPresentation.Artwork(card), Ready = view.Art(card) != null }).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void SmokeCheck(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("UI smoke: " + description);
        if (smokeChecks.Contains(description)) return;
        smokeChecks.Add(description);
        File.AppendAllText(Path.Combine(view.Profile, "ui-progress.log"), description + "\n");
    }
    private MouseState SmokeMouse()
    {
        MouseState At(Point point, ButtonState left) => new(point.X, point.Y, 0, left, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
        if (smokeDrag is { } destination) { smokeDrag = null; smokeRelease = destination; return At(destination, ButtonState.Pressed); }
        if (smokeRelease is { } release) { smokeRelease = null; return At(release, ButtonState.Released); }
        if (smokePendingClick != null) {
            // A decision can change between press and release. The client correctly
            // ignores that stale click; retry with the newly rendered control.
            if (!smokeClickAccepted) smokeStage = smokeRetryStage;
            smokePendingClick = null;
        }
        if (DateTime.UtcNow > smokeDeadline) throw new TimeoutException($"UI smoke timed out at stage {smokeStage}; decision {view.Match?.Decision?.Message}");
        if (view.Error.Length > 0) throw new InvalidOperationException("UI smoke: " + view.Error);
        if (!view.Loaded || view.Busy || view.ActivityOpen || view.Polling) return At(new(0, 0), ButtonState.Released);
        if (view.Match is { } opening && opening.Viewer!.Zone("Hand").Cards.Length > 0 && artworkOpenings.Add(opening.Id)) {
            string name = artworkOpenings.Count == 1 ? "solo-first-hand" : "solo-next-hand";
            RecordOpeningArtwork(name); smokeScreenshot = name + ".png";
        }
        if (smokeDecision != view.Match?.Decision?.Id) {
            smokeDecision = view.Match?.Decision?.Id;
            File.WriteAllText(Path.Combine(view.Profile, "ui-latest.json"), JsonSerializer.Serialize(view.Match));
        }
        int currentStage = smokeStage;
        string? click = null; bool drag = false;
        switch (smokeStage) {
            case 0:
                click = "change-deck"; smokeStage = 90; break;
            case 90:
                click = "picker-presets"; smokeStage = 91; break;
            case 91:
                click = "preset:" + view.Presets[0].Id; smokeStage = 1; break;
            case 1:
                if (view.SelectedDeck == null) break;
                SmokeCheck(view.SetupModal.Length == 0 && !view.DeckConfirmed && view.Match == null,
                    "Selecting a precon returns to its deck home without a forced review or implicit game");
                smokeScreenshot = "solo-deck-home.png";
                click = "start-solo"; smokeStage = 5; break;
            case 5:
                if (view.Match?.Decision == null) break;
                SmokeCheck(view.LocalMatch && view.Match.Players.Length == 4 && view.Match.Players.All(p => p.Life == 40)
                    && view.SoloSetup?.DeckId == view.SelectedDeck?.Id, "One Play action prepares and opens the selected deck's four-player AI table");
                smokeFirstGame = view.Match.Id;
                RecordOpeningArtwork("solo-opening-artwork");
                smokeScreenshot = "solo-opening.png"; smokeStage++; break;
            case 6:
            case 11:
                if (view.Match?.Decision is not { } decision) break;
                if (view.Match.ActivePlayerId == view.Match.ViewerId && decision.Task == DecisionTask.Priority && view.Match.PhaseKey == "MAIN1") {
                    var land = view.Match.Viewer!.Zone("Hand").Cards.FirstOrDefault(c => c.Selectable && c.Type.Contains("Land"));
                    if (land == null) throw new InvalidOperationException("UI smoke opening has no land to play");
                    click = "card:" + land.Key + ":" + land.VisualId; drag = smokeStage == 11;
                    smokeLand = land.VisualId; smokeStage = drag ? 12 : 70;
                } else if (decision.Task is DecisionTask.Options or DecisionTask.AbilityChoice or DecisionTask.Ordering or DecisionTask.Reveal) {
                    click = decision.Task is DecisionTask.Reveal || view.Selections.Count >= Math.Max(decision.Min, Math.Min(1, decision.Max)) ? "choicesdone" : "choice:" + decision.Choices[0].Index;
                } else if (decision.PlayerChoices.Length > 0) click = "player:" + (decision.PlayerChoices.Contains(view.Match.ViewerId) ? view.Match.ViewerId : decision.PlayerChoices[0]);
                else if (decision.Cancel == "Mulligan" && decision.CancelEnabled && !view.Match.Viewer!.Zone("Hand").Cards.Any(c => c.Type.Contains("Land"))) click = "cancel";
                else if (decision.OkEnabled) click = "confirm";
                else if (decision.Task is DecisionTask.TargetSelection or DecisionTask.EntitySelection && view.Match.Viewer!.Zone("Hand").Cards.FirstOrDefault(c => c.Selectable && !c.Highlighted) is { } selected) click = "card:" + selected.Key + ":" + selected.VisualId;
                break;
            case 70:
                if (smokeLand == null || view.DisplayedPose(smokeLand) is not { Size.X: >= 400 }
                    || !view.Hits.Any(hit => hit.Id == "read-card-action")) break;
                SmokeCheck(view.Match?.Decision?.Task == DecisionTask.Priority
                    && view.Match.Viewer!.Zone("Hand").Cards.Any(card => card.VisualId == smokeLand)
                    && !view.Match.Viewer.Zone("Battlefield").Cards.Any(card => card.VisualId == smokeLand),
                    "Reading a playable hand land preserves the exact card in hand until its explicit Play action");
                click = "read-card-action"; smokeStage = 7; break;
            case 7:
                if (view.Match?.Viewer?.Zone("Battlefield").Cards.Any(c => c.VisualId == smokeLand && c.Type.Contains("Land")) != true) { click = SmokeLandEntry(); break; }
                SmokeCheck(true, "The explicit Play action moves the exact read hand land onto the battlefield through its scoped decision");
                smokeScreenshot = "solo-table.png";
                click = "concede"; smokeStage++; break;
            case 8:
                if (view.Overlay == "concede") { click = "confirm-concede"; break; }
                if (view.Match?.Status != "finished") break;
                SmokeCheck(view.Match.Result == "Defeat", "Concede button ends the local game");
                click = "return"; smokeStage++; break;
            case 9:
                SmokeCheck(view.Match == null && !view.LocalMatch && view.SoloSetup != null, "Returning home clears the game and retains the selected deck and AI choices");
                click = "start-solo"; smokeStage++; break;
            case 10:
                if (view.Match?.Decision == null) break;
                SmokeCheck(view.LocalMatch && view.Match.Id != smokeFirstGame && view.Match.Players.Length == 4, "Start button opens a new game after returning to setup");
                smokeStage++; break;
            case 12:
                if (view.Match?.Viewer?.Zone("Battlefield").Cards.Any(c => c.VisualId == smokeLand && c.Type.Contains("Land")) != true) { click = SmokeLandEntry(); break; }
                SmokeCheck(true, "Dragging a hand card onto the battlefield plays a land through the normal mouse path");
                click = "settings"; smokeStage++; break;
            case 13:
                SmokeCheck(view.Overlay == "settings", "Settings open inside the table and pause automatic priority");
                click = "close-overlay"; smokeStage++; break;
            case 14:
                click = "concede"; smokeStage++; break;
            case 15:
                if (view.Overlay == "concede") { click = "confirm-concede"; break; }
                if (view.Match?.Status != "finished") break;
                click = "return"; smokeStage++; break;
            case 16:
                click = "friends-tab"; smokeStage++; break;
            case 17:
                if (!view.Hits.Any(h => h.Id == "host")) break;
                SmokeCheck(!view.SoloTab && view.Match == null && view.Hits.Any(h => h.Id == "host"), "Friends play is available in the same home after solo games");
                smokeScreenshot = "friends-home.png";
                File.WriteAllText(Path.Combine(view.Profile, "ui-results.json"), JsonSerializer.Serialize(smokeChecks, new JsonSerializerOptions { WriteIndented = true }));
                smokeStage++; break;
            case 18:
                if (smokeScreenshot == null) view.Exit();
                break;
        }
        if (click == null) return At(new(0, 0), ButtonState.Released);
        var hit = view.Hits.LastOrDefault(h => h.Id == click);
        // Async results are applied in Update; their controls appear in the next Draw.
        if (hit == null || !view.CurrentHit(hit)) { smokeStage = currentStage; return At(new(0, 0), ButtonState.Released); }
        var viewport = view.GraphicsDevice.Viewport;
        float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        Point? clickable = view.HitAt(hit.Bounds.Center)?.Id == hit.Id ? hit.Bounds.Center : null;
        for (int y = hit.Bounds.Y + 5; clickable == null && y < Math.Min(view.DesignHeight - 5, hit.Bounds.Bottom); y += 7)
            for (int x = hit.Bounds.X + 5; x < hit.Bounds.Right; x += 7) if (view.HitAt(new(x, y))?.Id == hit.Id) { clickable = new(x, y); break; }
        if (clickable == null) { smokeStage = currentStage; return At(new(0, 0), ButtonState.Released); }
        var point = new Point((int)((viewport.Width - 1600 * scale) / 2 + clickable.Value.X * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + clickable.Value.Y * scale));
        if (drag) smokeDrag = new Point((int)((viewport.Width - 1600 * scale) / 2 + 790 * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + 540 * scale));
        else smokeRelease = point;
        smokePendingClick = click; smokeClickAccepted = false; smokeRetryStage = currentStage;
        return At(point, ButtonState.Pressed);
    }
    private string? SmokeLandEntry()
    {
        // Precons include lands such as Vineglimmer Snarl which ask for a reveal
        // before they enter. Resolve that real input before asserting the move.
        var d = view.Match?.Decision;
        if (d == null) return null;
        if (d.Task is DecisionTask.Options or DecisionTask.AbilityChoice or DecisionTask.Ordering or DecisionTask.Reveal) return d.Task is DecisionTask.Reveal || view.Selections.Count >= Math.Max(d.Min, Math.Min(1, d.Max))
            ? "choicesdone" : "choice:" + d.Choices[0].Index;
        if (d.Task is DecisionTask.TargetSelection or DecisionTask.EntitySelection && view.AllCards().FirstOrDefault(c => c.Selectable && !c.Highlighted) is { } card) return "card:" + card.Key + ":" + card.VisualId;
        if (d.Task == DecisionTask.Priority) return null;
        return d.OkEnabled ? "confirm" : d.CancelEnabled ? "cancel" : null;
    }
}
