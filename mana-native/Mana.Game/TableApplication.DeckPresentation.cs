using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record WorkspaceCardReading(string Scope, string Identity, Card Card, bool OtherFace = false);
    private WorkspaceCardReading? workspaceCardReading;
    private string WorkspaceReadingScope => $"{selectedDeckDetails?.Id}:{selectedDeckDetails?.Revision}:{workshopTab}:{workshopHand?.TestId}:{workshopHand?.Draws}:{workshopHand?.Redraws}:{analysisTab}:{analysisPage}:{analysisDetailTitle}";
    private void ReadWorkspaceCard(Card card, string identity)
    {
        workspaceCardReading = workspaceCardReading is { } current && current.Scope == WorkspaceReadingScope && current.Identity == identity
            ? null : new(WorkspaceReadingScope, identity, card);
    }
    private bool DismissDeckWorkspaceReading()
    {
        bool visible = workspaceCardReading?.Scope == WorkspaceReadingScope;
        workspaceCardReading = null;
        return visible;
    }
    // Reading owns only its local space. The hand, selected finding, deck version
    // and test setup stay in place; no deck mutation or game command is offered.
    private void DrawWorkspaceCardReading(Rectangle area)
    {
        if (workspaceCardReading is not { } reading) return;
        if (reading.Scope != WorkspaceReadingScope) { workspaceCardReading = null; return; }
        canvas.Rounded(area, new(17, 26, 33), 8);
        hits.RemoveAll(hit => hit.Bounds.Intersects(area));
        bool otherFace = reading.Card.OtherFace != null;
        int height = Math.Min(area.Height - (otherFace ? 58 : 24), (int)((area.Width - 28) * 1.4f));
        int width = height * 5 / 7;
        var pose = new Pose(new(area.Center.X, area.Top + 12 + height / 2), new(width, height));
        PaintCard(DeckCardFace(reading.Card, reading.OtherFace), pose, false, false);
        hits.Add(new("workspace-card-reading", Scope, area, () => workspaceCardReading = null));
        if (otherFace) Button("workspace-card-flip", reading.OtherFace ? "Front face" : "Other face",
            area.Center.X - 90, area.Bottom - 40, 180,
            () => { if (workspaceCardReading == reading) workspaceCardReading = reading with { OtherFace = !reading.OtherFace }; }, !busy, height: 30);
    }

    private static Card DeckCardFace(Card card, bool otherFace) => otherFace && card.OtherFace is { } back ? card with {
        Name = back.Name, Type = back.Type, Text = back.OracleText, ManaCost = back.ManaCost,
        Power = back.Power, Toughness = back.Toughness, ArtName = back.ArtName, ArtFace = back.ArtFace
    } : card;

    // One presentation of main-deck composition for both the builder and lobby.
    private void DrawDeckComposition(DeckDetails deck, int x, int y, int width)
    {
        var main = deck.Entries.Where(e => e.Section == "Main").ToArray();
        var spells = main.Where(e => !e.Card.Type.Contains("Land")).ToArray();
        int lands = main.Where(e => e.Card.Type.Contains("Land")).Sum(e => e.Quantity);
        int creatures = main.Where(e => e.Card.Type.Contains("Creature")).Sum(e => e.Quantity);
        double average = spells.Sum(e => e.ManaValue * e.Quantity) / (double)Math.Max(1, spells.Sum(e => e.Quantity));
        canvas.Text($"{lands} lands · {creatures} creatures · Avg. MV {average:0.0}", x, y, Ink, .65f, width, 1);
        var curve = Enumerable.Range(0, 7).Select(i => spells.Where(e => Math.Min(6, e.ManaValue) == i).Sum(e => e.Quantity)).ToArray();
        int step = width / 7;
        for (int i = 0; i < curve.Length; i++) {
            int left = x + 4 + i * step, height = (int)(36f * curve[i] / Math.Max(1, curve.Max()));
            canvas.Fill(new(left, y + 65 - height, step - 18, height), new(95, 160, 185));
            canvas.Text(curve[i].ToString(), left + (step - 36) / 2, y + 48 - height, Muted, .47f, 35, 1);
            canvas.Text(i == 6 ? "6+" : i.ToString(), left + (step - 30) / 2, y + 69, Muted, .51f, 36, 1);
            if (deckEditor && WorkshopEngine != null && workshopDialog.Length == 0) {
                int value = i;
                hits.Add(new("workshop-curve:" + i, Scope, new(left - 3, y + 22, step - 4, 68), () => {
                    workshopManaFilter = value; workshopRoleFilter = ""; analysisRelatedCards.Clear(); workshopTab = "build"; workshopDialog = "";
                    commanderCandidates = false; deckSection = "Main"; deckFilter = ""; deckCardsPage = 0; deckIssuesOnly = false;
                }));
            }
        }
    }
}
