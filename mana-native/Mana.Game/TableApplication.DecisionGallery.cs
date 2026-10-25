using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    internal sealed record ChoiceOrderBadge(int Index, int Position, Rectangle Bounds, string Text, int MeasuredWidth, int AvailableWidth);
    private readonly Dictionary<int, ChoiceOrderBadge> choiceOrderBadges = [];
    private void PaintChoiceOrder(int index, Rectangle bounds, float fontSize = .56f)
    {
        int position = decisions.Position(index);
        if (position == 0) return;
        string caption = "ORDER " + position;
        float size = fontSize;
        int available = bounds.Width - 8;
        while (size > .45f && canvas.TextWidth(caption, size, true) > available) size = Math.Max(.45f, size - .025f);
        if (canvas.TextWidth(caption, size, true) > available) {
            caption = position.ToString();
            size = fontSize;
            while (size > .45f && canvas.TextWidth(caption, size, true) > available) size = Math.Max(.45f, size - .025f);
        }
        canvas.Panel(bounds, new(34, 48, 49), Gold, 4);
        canvas.CenterText(caption, new(bounds.X + 4, bounds.Y, available, bounds.Height), Gold, size, true);
        if (Automated) choiceOrderBadges[index] = new(index, position, bounds, caption, canvas.TextWidth(caption, size, true), available);
    }
    private void DrawDecisionOverlay()
    {
        if (match?.Decision is not { } decision) return;
        var context = decision.Context;
        switch (decisions.Presentation?.Payload) {
            case AllocationPayload allocation: DrawAllocation(match, allocation, context); return;
            case OptionsPayload options: DrawChoiceGallery(match, decision, options.Selection, false); return;
            case OrderingPayload ordering: DrawChoiceGallery(match, decision, ordering.Selection, true); return;
            case RevealPayload reveal:
                ModalFrame(context.Title.Length > 0 ? context.Title : "Revealed cards", new MatchIntent.ViewBattlefield(), "View battlefield", 790);
                canvas.Text(context.Message, 204, 181, Ink, .77f, 1185, 3);
                DrawChoices(match, decision, null, false, reveal.Cards, true); return;
            case NumberPayload number:
                DrawValueFrame(context);
                DrawNumberDecision(number); return;
            case TextPayload:
                DrawValueFrame(context);
                DrawValueInput("Enter your answer.", true, false); return;
            default: throw new InvalidOperationException("No gallery owns " + decisions.Payload?.GetType().Name);
        }
    }

    private void DrawChoiceGallery(GameSnapshot state, Decision decision, ChoiceSelection selection, bool ordered)
    {
        bool cards = !selection.Disclosures.IsEmpty || !selection.Choices.IsEmpty && selection.Choices.All(choice => choice.Card != null);
        string title = decision.Context.Title.Length > 0 ? decision.Context.Title : ordered ? "Choose an order"
            : cards ? selection.Range.Max == 1 ? "Choose a card" : "Choose cards" : "Make your choice";
        ModalFrame(title, new MatchIntent.ViewBattlefield(), "View battlefield", 790);
        canvas.Text(decision.Context.Message, 204, 181, Ink, .77f, 1185, 3);
        DrawChoices(state, decision, selection, ordered, selection.Disclosures, false);
    }

    private void ToggleChoice(Decision d, int index)
    {
        if (match?.Decision == d && decisions.Toggle(match, index)) CollapseCardReading();
    }
    private void DrawChoices(GameSnapshot state, Decision d, ChoiceSelection? selection, bool ordered, System.Collections.Immutable.ImmutableArray<LibraryChoice> disclosures, bool readOnly)
    {
        int min = selection?.Range.Min ?? 0, max = selection?.Range.Max ?? 0;
        var items = disclosures.Length > 0 || selection == null
            ? disclosures.Select(c => (c.Index, c.Label, Card: (Card?)c.Card, Detail: "", Mana: "")).ToArray()
            : selection!.Choices.Select(c => (Index: (int?)c.Index, c.Label, Card: CardViews.Choice(state, c), c.Detail, c.Mana)).ToArray();
        var filtered = items.Select((item, slot) => (Item: item, Slot: slot))
            .Where(c => c.Item.Label.Contains(decisions.Filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool portraits = items.Any(c => c.Card != null); int capacity = portraits ? 7 : 6;
        bool sparseSource = portraits && items.Length <= 4;
        var sourcePreview = !portraits || sparseSource ? CardViews.Source(state, d) : null;
        if (sourcePreview != null) {
            int sourceX = sparseSource ? 199 : 1185;
            var sourcePose = new Pose(new(sourceX + 83, 435), new(166, 232));
            if (sparseSource) canvas.Text("SOURCE", sourceX, 294, Gold, .55f, 166, 1, true);
            PaintInteractionCard(sourcePreview, sourcePose, false, false);
            canvas.Text(sourcePreview.FaceDown ? "Face-down card" : sourcePreview.Name, sourceX, 562, Ink, .68f, 166, 3, true);
            if (!sourcePreview.FaceDown && VisibleCards().Any(card => SameVisibleCard(card, sourcePreview))) {
                var sourceBounds = new Rectangle(sourceX, 319, 166, 298);
                if (sourceBounds.Contains(pointer)) canvas.CardFocus(sourcePose, HoverAccent);
                hits.Add(new("choice-source-inspect", Scope, sourceBounds, new MatchIntent.InspectCard(sourcePreview), sourcePreview));
            }
        }
        bool cardChoices = items.Length > 0 && items.All(c => c.Card != null);
        int page = Math.Clamp(decisions.Page, 0, Math.Max(0, (filtered.Length - 1) / capacity));
        string instruction = readOnly ? "" : ordered
            ? $"Choose {min}–{max} in order. Selected: {decisions.Selections.Count}."
            : min == 1 && max == 1 ? decisions.Selections.Count == 0
                ? cardChoices ? "Choose one card" : "Choose one option"
                : cardChoices ? "One card selected" : "One option selected"
            : $"Choose {min}–{max}. Selected: {decisions.Selections.Count}.";
        canvas.Text(instruction, 206, 251, Gold, .72f, 1160, 2);
        var pageItems = filtered.Skip(page * capacity).Take(capacity).ToArray();
        int expanded = Array.FindIndex(pageItems, e => readingId == "choice-face:" + Scope + ":" + e.Slot);
        bool sparseCards = portraits && items.Length <= capacity;
        int rowWidth = expanded < 0 ? 148 + Math.Max(0, pageItems.Length - 1) * 169 : 410 + Math.Max(0, pageItems.Length - 1) * 128;
        int rowLeft = sparseCards ? sparseSource && sourcePreview != null ? 403 + (991 - rowWidth) / 2 : (1600 - rowWidth) / 2
            : expanded < 0 ? 206 : 190;
        int row = 0;
        foreach (var entry in pageItems) {
            var item = entry.Item;
            int index = row++; bool selected = item.Index is { } id && decisions.Selections.Contains(id);
            bool available = !readOnly && item.Index != null && !busy;
            string buttonId = item.Index is { } cardIndex ? "choice:" + cardIndex : "reveal:" + entry.Slot;
            var captured = item;
            if (portraits) {
                // Make room for the original face as it grows, so it cannot
                // completely bury an adjacent revealed card or choice.
                int column = expanded < 0 ? 148 : index == expanded ? 410 : 112;
                int x = expanded < 0 ? rowLeft + index * 169 : rowLeft + index * 128 + (index > expanded ? 298 : 0);
                int faceWidth = expanded >= 0 && index != expanded ? 112 : 148;
                var pose = new Pose(new(x + column / 2, 408), new(faceWidth, faceWidth * 1.4f));
                if (item.Card != null) DrawTableCard("choice-face:" + Scope + ":" + entry.Slot, item.Card, pose, "Choice", null,
                    buttonId, available ? new MatchIntent.SelectChoice(captured.Index!.Value) : UiIntent.None,
                    (displayed, focus) => {
                        PaintInteractionCard(item.Card, displayed, selected, false, focus);
                        // Availability stays visible, while the chosen face owns
                        // the strong gold frame and glow instead of competing halos.
                        if (available && !selected) canvas.CardFrame(displayed, ActionAccent * .65f, 1);
                    },
                    choiceSlot: entry.Slot, activationBounds: new(x, 304, column, 329));
                else canvas.Panel(new(x, 304, 148, 207), Panel, Muted);
                canvas.Text(item.Label, x + 1, 526, Ink, .66f, column, 3, true);
                var badgeBounds = new Rectangle(x, 605, column, 28);
                if (selected && ordered) PaintChoiceOrder(item.Index!.Value, badgeBounds, .65f);
                else if (selected) {
                    var center = new Vector2(badgeBounds.Center.X, badgeBounds.Center.Y);
                    canvas.Line(center + new Vector2(-8, 0), center + new Vector2(-2, 6), Gold, 3);
                    canvas.Line(center + new Vector2(-2, 6), center + new Vector2(9, -7), Gold, 3);
                }
                if (available && item.Card == null) hits.Add(new(buttonId, Scope, new(x, 304, 150, 329), new MatchIntent.SelectChoice(captured.Index!.Value)));
            } else {
                int columnWidth = sourcePreview != null ? 465 : 585;
                int x = 206 + index % 2 * (columnWidth + 17), y = 302 + index / 2 * 116;
                var bounds = new Rectangle(x, y, columnWidth, 103);
                PaintControlSurface(buttonId, bounds, selected ? new Color(36, 63, 79) : Panel,
                    selected ? Gold : new Color(77, 90, 105), available);
                bool numbered = selected && ordered;
                int offset = (numbered ? 99 : 16) + (item.Mana.Length > 0 ? 47 : 0);
                if (numbered) PaintChoiceOrder(item.Index!.Value, new(x + 12, y + 12, 76, 33), .65f);
                if (item.Mana.Length > 0) ManaOrb(item.Mana.Trim('{', '}'), 1, new(x + offset - 30, y + 48));
                canvas.Text((selected && !ordered ? "✓  " : "") + item.Label, x + offset, y + 12, Ink, .83f, columnWidth - 22 - offset, 3, true);
                if (item.Detail.Length > 0) canvas.Text(item.Detail, x + offset, y + 69, Muted, .65f, columnWidth - 22 - offset, 1);
                if (available) hits.Add(new(buttonId, Scope, bounds, new MatchIntent.SelectChoice(captured.Index!.Value)));
            }
        }
        if (filtered.Length == 0) canvas.CenterText("No matching cards or choices.", new(209, 356, 1177, 90), Muted, 1);
        if (filtered.Length > capacity) {
            Button("choicesprev", "‹", 206, 754, 45, new MatchIntent.SetPage(MatchPage.Choice, page - 1), page > 0, height: 34);
            Button("choicesnext", "›", 260, 754, 45, new MatchIntent.SetPage(MatchPage.Choice, page + 1), (page + 1) * capacity < filtered.Length, height: 34);
            canvas.Text($"Page {page + 1} / {(filtered.Length + capacity - 1) / capacity}", 325, 761, Muted, .66f, 350, 1);
        }
        if (items.Length > capacity) {
            Button("choicesfilter", decisions.Filter.Length == 0 ? "Search choices…" : decisions.Filter, 808, 754, 587, new MatchIntent.FocusInput(true), accent: textFocus && decisions.Filtering, height: 34);
            if (textFocus && decisions.Filtering) canvas.Text("Type to filter. Backspace edits. Escape closes the gallery.", 810, 794, Muted, .56f, 580, 1);
        }
        if (decisions.Selections.Count > 0 && !readOnly)
            Button("choicesclear", ordered ? "Reset order" : "Clear", 206, 819, 183, new MatchIntent.ClearSelection(), !busy);
        if (min == 0 && !readOnly) Button("choice-cancel", "Choose none", 405, 819, 182, new MatchIntent.ChooseNone(), !busy);
        Button("choicesdone", readOnly ? "Continue" : ordered ? "Confirm order" : cardChoices && decisions.Selections.Count > 0
            ? decisions.Selections.Count == 1 ? "Confirm card" : "Confirm cards" : "Confirm selection", 1088, 814, 307, new MatchIntent.ConfirmSelection(), !busy && (readOnly || decisions.Selections.Count >= min && decisions.Selections.Count <= max), true, 47);
    }
    private void DrawValueFrame(DecisionContext context)
    {
        ModalFrame(context.Title.Length > 0 ? context.Title : "Make your choice", new MatchIntent.ViewBattlefield(), "View battlefield");
        canvas.Text(context.Message, 204, 181, Ink, .77f, 1185, 3);
    }

    private void DrawNumberDecision(NumberPayload number)
    {
        string instruction = number.Range is { } range ? $"Choose a number from {range.Min} to {range.Max}." : "Enter a whole number.";
        bool valid = int.TryParse(decisions.Input, out int value) && (number.Range == null || value >= number.Range.Value.Min && value <= number.Range.Value.Max);
        DrawValueInput(instruction, valid, true, number.Range == null ? "Enter a whole number." : "Enter a whole number within the permitted range.");
    }

    private void DrawValueInput(string instruction, bool valid, bool numeric, string invalidMessage = "")
    {
        canvas.Text(instruction, 228, 315, Gold, .94f, 1127, 2);
        Button("text-input", decisions.Input.Length == 0 ? "Click here and type..." : decisions.Input, 230, 399, 1100, new MatchIntent.FocusInput(false), accent: textFocus, height: 66);
        if (numeric) {
            Button("number-less", "\u2212", 232, 490, 65, new MatchIntent.AdjustNumber(-1), height: 44);
            Button("number-more", "+", 310, 490, 65, new MatchIntent.AdjustNumber(1), height: 44);
        }
        if (!valid) canvas.Text(invalidMessage, 231, 559, Red, .78f, 1090, 2);
        Button("value", "Confirm", 1090, 718, 266, new MatchIntent.ConfirmValue(), valid && !busy, true, 54);
    }
}
