using Mana.Contracts;
using Mana.Client;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record SourceReaderOption(string Text, SourceAction? Action = null,
        PriorityAbility? Placement = null, bool Retry = false);
    private sealed record SourceReaderControl(string Id, Rectangle Bounds, SourceAction? Action = null,
        bool Retry = false, PriorityAbility? Placement = null);
    private readonly List<SourceReaderControl> sourceReaderControls = [];
    private Rectangle sourceReaderActionsBounds;
    private string sourceReaderViewKey = "";
    private int sourceReaderScroll, sourceReaderScrollMax;

    private bool DrawSourceActionReader(PresentedCard entry)
    {
        if ((!SourceActionsSupported && !IsStudyBattlefieldReader(entry)) || !entry.Focused || entry.ChoiceSlot >= 0 || entry.Card.FaceDown
            || entry.Display.Size.X < 300 || !CardContext.CanRead(entry)) return false;
        var card = VisibleCards().FirstOrDefault(current => SameVisibleCard(current, entry.Card));
        if (card == null) return false;
        // Reading always paints the complete original face. Action discovery,
        // effective stats and controls never replace or cover its printed rules.
        if (IsStudyBattlefieldReader(entry)) PaintStudyBattlefieldReader(card, entry);
        else {
            PaintCard(card, entry.Display, false, IsCardActionable(card), true);
            PaintPreparedMarker(card, entry.Display, entry.Request.Owner ?? -1);
        }
        return true;
    }

    private List<SourceReaderOption> SourceReaderOptions(Card card)
    {
        var options = new List<SourceReaderOption>();
        if (ReaderActionsMatch(card)) {
            options.AddRange(sourceActions.View.Actions.Select(action => new SourceReaderOption(
                action.Kind == "cast" && !action.Text.StartsWith("Cast ", StringComparison.OrdinalIgnoreCase)
                    ? "Cast " + action.Text : action.Text, action)));
            if (match != null) options.AddRange(HandPlacements.Options(match, card)
                .Select(placement => new SourceReaderOption(placement.Text, Placement: placement)));
        } else if (sourceActions.View.Context is { } current && current == ReaderActionContext(card)
            && sourceActions.View.Status is SourceActionStatus.Failed or SourceActionStatus.Stale) {
            options.Add(new("Retry actions", Retry: true));
        }
        return options;
    }

    private static string SourceReaderState(Card card, string zone)
    {
        var parts = new List<string>();
        if (CardPresentation.HasCombatStats(card)) parts.Add(CurrentStats(card));
        if (card.Damage > 0) parts.Add(card.Damage + " damage");
        parts.AddRange(CardPresentation.Counters(card).Select(counter => counter.Key + " ×" + counter.Value));
        if (card.PhasedOut) parts.Add("Phased out");
        if (zone == "Command" && CardPresentation.CommanderTax(card) is > 0 and var tax)
            parts.Add("Commander tax +" + tax);
        if (card.States is { } states) parts.AddRange(states.Where(state => state.Kind == "status")
            .Select(PublicStatePresentation.Description));
        return string.Join(" · ", parts);
    }

    private void DrawSourceReaderControls(PresentedCard entry)
    {
        var card = VisibleCards().FirstOrDefault(current => SameVisibleCard(current, entry.Card));
        if (card == null) return;
        var options = SourceReaderOptions(card);
        string state = IsStudyBattlefieldReader(entry) ? "" : SourceReaderState(card, entry.Zone);
        state = string.Join("\n", new[] { PlayAccessState(card), state }.Where(part => part.Length > 0));
        if (sourceActions.View is { Context: { } current, Status: SourceActionStatus.Unsupported } unavailable
            && current == ReaderActionContext(card))
            state = string.Join(" · ", new[] { state, unavailable.Error }.Where(part => part.Length > 0));
        var face = entry.Display.Bounds;
        if (options.Count == 0 && state.Length == 0) {
            if (sourceActions.View.Status == SourceActionStatus.Loading) {
                float start = preferences.ReducedMotion ? 0 : (float)now * 3;
                canvas.Ring(new(face.Right + 10, face.Center.Y), 4, Blue * .7f, 1.5f, start, start + 4.5f);
            }
            return;
        }
        const float size = .70f;
        const int padding = 10, gap = 8;
        int desiredWidth = Math.Clamp(options.Select(option => SourceActionTextLayout.Measure(canvas, option.Text, size, 2000, true).Width + padding * 2)
            .Append(canvas.TextWidth(state, size, true) + padding * 2)
            .DefaultIfEmpty(120).Max(), 120, 316);
        if (options.Any(option => option.Action?.Card != null)) desiredWidth = 316;
        var stateColumn = battlefieldReaderStateBounds;
        if (IsStudyBattlefieldReader(entry) && !stateColumn.IsEmpty && 1580 - stateColumn.Left >= 120)
            desiredWidth = Math.Min(desiredWidth, 1580 - stateColumn.Left);
        int desiredHeight = (state.Length == 0 ? 0 : canvas.TextSize(state, .63f, desiredWidth - 16, 80, true).Y + 16)
            + options.Sum(option => SourceActionTextLayout.Measure(canvas, option.Text, size, desiredWidth - padding * 2, true).Height + padding * 2 + gap
                + (option.Action?.Card == null ? 0 : LinkedSpellHeaderHeight(option.Action.Card, desiredWidth)));
        // The live-state column already belongs to this inspected source. Put
        // its actions directly below those facts before searching the table for
        // free space; a detached row on the opposite side loses that connection.
        bool besideState = IsStudyBattlefieldReader(entry) && !stateColumn.IsEmpty
            && stateColumn.Bottom + 10 + desiredHeight <= face.Bottom
            && stateColumn.Left + desiredWidth <= 1580;
        var area = besideState ? new Rectangle(stateColumn.Left, stateColumn.Bottom + 10, desiredWidth, desiredHeight)
            : DecisionActionArea(face, desiredWidth, Math.Min(face.Height, desiredHeight));
        if (area.IsEmpty) return;
        int width = area.Width;
        var rows = options.Select(option => {
            // An obvious land has a quiet fallback to dragging. A spell face's
            // land route must remain discoverable, even if it is the only offer.
            bool secondary = options.Count == 1 && entry.Zone == "Hand" && option.Action is { Kind: "play" }
                && CardPresentation.IsResource(card);
            float font = secondary ? .62f : size;
            int inset = secondary ? 6 : padding;
            var layout = SourceActionTextLayout.Measure(canvas, option.Text, font, width - inset * 2, !secondary);
            int linkedHeight = option.Action?.Card == null ? 0 : LinkedSpellHeaderHeight(option.Action.Card, width);
            return (Option: option, Layout: layout, Height: Math.Max(secondary ? 30 : 38, layout.Height + inset * 2), LinkedHeight: linkedHeight,
                Secondary: secondary, Font: font, Inset: inset);
        }).ToArray();
        int stateHeight = state.Length == 0 ? 0 : canvas.TextSize(state, .63f, width - 16, 80, true).Y + 16;
        int contentHeight = stateHeight + rows.Sum(row => row.Height + row.LinkedHeight + gap) - (rows.Length > 0 ? gap : 0);
        int height = Math.Min(area.Height, contentHeight);
        int top = besideState ? area.Top : Math.Clamp(face.Center.Y - height / 2, area.Top, area.Bottom - height);
        sourceReaderActionsBounds = new(area.X, top, width, height);
        sourceReaderScrollMax = Math.Max(0, contentHeight - height);
        int scroll = Math.Clamp(sourceReaderScroll, 0, sourceReaderScrollMax);
        hits.Add(new("source-actions-surface", Scope, sourceReaderActionsBounds, UiIntent.None, ShowPointer: false));
        int y = top - scroll;
        if (stateHeight > 0) {
            if (y >= top && y + stateHeight <= top + height) {
                canvas.Rounded(new(area.X, y, width, stateHeight - 4), new(13, 24, 31), 4);
                canvas.Text(state, area.X + 8, y + 6, new(161, 228, 207), .63f, width - 16, 80, true);
            }
            y += stateHeight;
        }
        for (int index = 0; index < rows.Length; index++) {
            var row = rows[index]; var option = row.Option;
            if (option.Action is { Card: { } linked } action) {
                var header = new Rectangle(area.X, y, width, row.LinkedHeight);
                if (header.Top >= top && header.Bottom <= top + height)
                    PaintLinkedSpellHeader(action, header, face);
                y += row.LinkedHeight;
            }
            var whole = new Rectangle(area.X, y, width, row.Height);
            var visible = Rectangle.Intersect(whole, sourceReaderActionsBounds);
            if (!visible.IsEmpty) {
                bool firstPlacement = option.Placement != null && sourceActions.View.Actions.Count == 0
                    && options.FindIndex(item => item.Placement != null) == index;
                string id = option.Retry ? "source-actions-retry"
                    : (option.Action != null && sourceActions.View.Actions.FirstOrDefault()?.Id == option.Action.Id || firstPlacement) ? "read-card-action"
                    : option.Placement != null ? "source-placement:" + option.Placement.Id : "source-action:" + option.Action!.Id;
                bool hover = !busy && IsControlHovered(id);
                if (row.Secondary) {
                    if (hover) canvas.Rounded(visible, Blue * (pressed?.Id == id && CurrentHit(pressed) ? .22f : .10f), 4);
                } else PaintControlSurface(id, visible, new(19, 34, 41), Blue * .6f, !busy);
                for (int line = 0; line < row.Layout.Lines.Count; line++) {
                    int lineY = y + row.Inset + line * row.Layout.LineHeight;
                    if (lineY >= top && lineY + row.Layout.LineHeight <= top + height)
                        PaintSourceActionLine(row.Layout, line, area.X + row.Inset, lineY,
                            row.Secondary ? hover ? Blue : Muted : Ink, row.Font, !row.Secondary);
                }
                sourceReaderControls.Add(new(id, visible, option.Action, option.Retry, option.Placement));
            }
            y += row.Height + gap;
        }
        if (sourceReaderScrollMax > 0) {
            int trackHeight = Math.Max(8, height - 8);
            int thumbHeight = Math.Max(18, trackHeight * height / contentHeight);
            int thumbY = top + 4 + (trackHeight - thumbHeight) * scroll / sourceReaderScrollMax;
            canvas.Rounded(new(area.Right + 4, top + 4, 3, trackHeight), Muted * .18f, 1);
            canvas.Rounded(new(area.Right + 4, thumbY, 3, thumbHeight), Blue * .75f, 1);
        }
        foreach (var control in sourceReaderControls) {
            if (busy) continue;
            UiIntent intent = control.Retry ? new MatchIntent.RetrySourceActions()
                : control.Action != null ? new MatchIntent.UseSourceAction(control.Action.Id)
                : control.Placement != null ? new MatchIntent.UseSourcePlacement(control.Placement.Id) : UiIntent.None;
            hits.Add(new(control.Id, ScopeFor(intent), control.Bounds, intent));
        }
    }

    private void PaintSourceActionLine(SourceActionTextLayout layout, int line, int x, int y, Color color, float font, bool bold)
    {
        foreach (var run in layout.Lines[line]) {
            if (run.Symbol is not { } symbol) {
                canvas.Text(run.Text, x + run.X, y, color, font, run.Width, 1, bold);
                continue;
            }
            var center = new Vector2(x + run.X + layout.SymbolSide / 2f, y + layout.LineHeight / 2f - 1);
            if (symbol is "W" or "U" or "B" or "R" or "G" or "C") PaintManaSymbol(symbol, center, layout.SymbolSide);
            else if (resources.ManaSymbols is { } image && SourceActionTextLayout.SymbolCrop(symbol) is var crop
                && crop.Right <= image.Width && crop.Bottom <= image.Height)
                canvas.Sprite(image, center, new(layout.SymbolSide), 0, Color.White, crop);
            else if (symbol is "T" or "Q") {
                canvas.Circle(center, layout.SymbolSide / 2f, new(196, 195, 187));
                var direction = new Vector2(layout.SymbolSide * .23f) * (symbol == "T" ? 1 : -1);
                canvas.Arrow(center - direction, center + direction, new(23, 28, 34), 2);
            } else {
                canvas.Circle(center, layout.SymbolSide / 2f, new(196, 195, 187));
                canvas.CenterText(symbol, new((int)center.X - layout.SymbolSide / 2, (int)center.Y - layout.SymbolSide / 2,
                    layout.SymbolSide, layout.SymbolSide), new(23, 28, 34), font, true);
            }
        }
    }

    private bool ScrollSourceReader(int direction)
    {
        if (ScrollBattlefieldReaderState(direction)) return true;
        if (!SourceActionsSupported || readingId == null || !sourceReaderActionsBounds.Contains(pointer)) return false;
        sourceReaderScroll = Math.Clamp(sourceReaderScroll - direction * 36, 0, sourceReaderScrollMax);
        return true;
    }
}
