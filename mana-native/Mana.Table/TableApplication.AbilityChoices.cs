using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record AbilityChoiceRow(Choice Choice, int LabelHeight, int ManaHeight, int DetailHeight, int Height);

    private bool TryDrawAbilityChoices(GameSnapshot state, Decision decision)
    {
        if (decision.Kind != "choice" || decision.Context != "playAbility" || decision.Ordered
            || decision.Max != 1 || decision.Min is not (0 or 1) || decision.Choices.Length is < 1 or > 3
            || decision.LibraryCards.Length > 0 || decision.PlayerChoices.Length > 0
            || decision.Choices.Any(choice => choice.Card != null || choice.CardId.Length > 0 || choice.PlayerId != null)) return false;
        var source = InteractionCards.Source(state, decision);
        if (source == null) return false;

        const int paneX = 350, paneWidth = 900, padding = 24, leftWidth = 216, gap = 24;
        const int artWidth = 190, artHeight = 266, rightWidth = paneWidth - padding * 2 - leftWidth - gap;
        int closeWidth = Math.Max(200, canvas.TextWidth("View battlefield", .76f) + 24);
        const int confirmWidth = 236, footerHeight = 46;
        const float titleSize = .95f, messageSize = .72f, labelSize = .8f, detailSize = .65f;
        string sourceName = source.FaceDown ? "Face-down card" : source.Name;
        string title = string.IsNullOrWhiteSpace(decision.Title) ? sourceName : decision.Title;
        string confirm = string.IsNullOrWhiteSpace(decision.Ok) ? "Confirm" : decision.Ok;
        bool Fits(string text, float size, int width, int limit, bool bold, out int height)
        {
            height = canvas.TextSize(text, size, width, 80, bold).Y;
            return height <= limit && canvas.TextPages(text, size, width, limit, 80, bold).Length == 1;
        }
        // Finish all measurement before replacing any drawing or input. A
        // long authoritative description stays on the existing gallery path.
        if (!Fits(title, titleSize, paneWidth - padding * 2 - closeWidth - 16, 70, true, out int titleHeight)
            || !Fits(decision.Message, messageSize, paneWidth - padding * 2, 80, false, out int messageHeight)
            || canvas.TextWidth(confirm, .76f, true) > confirmWidth - 18
            || decision.Min == 0 && canvas.TextWidth("Cancel", .76f) > 170 - 18) return false;
        var counters = CardPresentation.Counters(source);
        if (counters.Length > 4 || counters.Any(counter =>
                canvas.TextWidth(counter.Key + " ×" + counter.Value, .86f * .8f, true) > artWidth - 16)) return false;
        int statsWidth = CardPresentation.HasCombatStats(source) ? canvas.TextWidth(CurrentStats(source), .86f, true) + 12 : 0;
        if (source.Damage > 0) statsWidth += canvas.TextWidth("DMG " + source.Damage, .86f, true) + 10;
        if (statsWidth > artWidth - 6) return false;

        var rows = new List<AbilityChoiceRow>();
        foreach (var choice in decision.Choices) {
            const int textWidth = rightWidth - 54;
            if (!Fits(choice.Label, labelSize, textWidth, 100, true, out int labelHeight)
                || !Fits(choice.Mana, detailSize, textWidth, 50, true, out int manaHeight)
                || !Fits(choice.Detail, detailSize, textWidth, 80, false, out int detailHeight)) return false;
            int height = Math.Max(64, 24 + labelHeight + (manaHeight > 0 ? manaHeight + 6 : 0)
                + (detailHeight > 0 ? detailHeight + 6 : 0));
            rows.Add(new(choice, labelHeight, manaHeight, detailHeight, height));
        }
        const int sourceHeight = artHeight;
        int bodyHeight = Math.Max(sourceHeight, rows.Sum(row => row.Height) + (rows.Count - 1) * 10);
        int headerHeight = Math.Max(36, titleHeight) + (messageHeight > 0 ? messageHeight + 8 : 0);
        int paneHeight = padding + headerHeight + 18 + bodyHeight + 18 + footerHeight + padding;
        if (paneHeight > 620) return false;

        int paneY = (900 - paneHeight) / 2, left = paneX + padding, right = left + leftWidth + gap;
        int bodyY = paneY + padding + headerHeight + 18;
        hits.Clear(); hovered = null; ClearCardComposition();
        canvas.Fill(new(0, 54, 1600, 846), Color.Black * .4f);
        canvas.Panel(new(paneX, paneY, paneWidth, paneHeight), new(23, 30, 40), new(98, 113, 130), 12);
        canvas.Text(title, left, paneY + padding, Ink, titleSize, paneWidth - padding * 2 - closeWidth - 16, 80, true);
        Button("close-overlay", "View battlefield", paneX + paneWidth - padding - closeWidth, paneY + padding, closeWidth,
            () => { collapsedDecision = true; textFocus = false; }, height: 36);
        if (messageHeight > 0)
            canvas.Text(decision.Message, left, paneY + padding + Math.Max(36, titleHeight) + 8,
                Ink, messageSize, paneWidth - padding * 2, 80);

        var sourcePose = new Pose(new(left + leftWidth / 2f, bodyY + artHeight / 2f), new(artWidth, artHeight));
        PaintInteractionCard(source, sourcePose, false, false);
        if (!source.FaceDown && VisibleCards().Any(card => SameVisibleCard(card, source))) {
            var sourceBounds = new Rectangle(left, bodyY, leftWidth, sourceHeight);
            if (IsControlHovered("choice-source-inspect")) canvas.CardFocus(sourcePose, HoverAccent);
            hits.Add(new("choice-source-inspect", Scope, sourceBounds, () => Inspect(source), source));
        }
        int rowY = bodyY;
        foreach (var row in rows) {
            var choice = row.Choice;
            string id = "choice:" + choice.Index;
            bool selected = selections.Contains(choice.Index);
            var bounds = new Rectangle(right, rowY, rightWidth, row.Height);
            PaintControlSurface(id, bounds, selected ? new Color(36, 63, 79) : Panel,
                selected ? Gold : new Color(77, 90, 105), !busy);
            if (selected) canvas.Text("✓", right + 12, rowY + 12, Gold, labelSize, 24, 1, true);
            int textX = right + 38, textY = rowY + 12;
            canvas.Text(choice.Label, textX, textY, Ink, labelSize, rightWidth - 54, 80, true);
            textY += row.LabelHeight;
            if (row.ManaHeight > 0) {
                textY += 6;
                canvas.Text(choice.Mana, textX, textY, Gold, detailSize, rightWidth - 54, 80, true);
                textY += row.ManaHeight;
            }
            if (row.DetailHeight > 0)
                canvas.Text(choice.Detail, textX, textY + 6, Muted, detailSize, rightWidth - 54, 80);
            if (!busy) hits.Add(new(id, Scope, bounds, () => ToggleChoice(decision, choice.Index)));
            rowY += row.Height + 10;
        }
        int footerY = paneY + paneHeight - padding - footerHeight;
        if (decision.Min == 0)
            Button("choice-cancel", "Cancel", right, footerY, 170,
                () => Send(new(state.Id, decision.Id, ReplyAction.Choose, Choices: [])), !busy, height: footerHeight);
        Button("choicesdone", confirm, paneX + paneWidth - padding - confirmWidth, footerY, confirmWidth, () => {
            textFocus = false;
            Send(new(state.Id, decision.Id, ReplyAction.Choose, Choices: selections.ToArray()));
        }, !busy && selections.Count == 1, true, footerHeight);
        return true;
    }
}
