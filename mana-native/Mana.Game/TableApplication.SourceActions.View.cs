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
        else PaintCard(card, entry.Display, false, IsCardActionable(card), true);
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
        return string.Join(" · ", parts);
    }

    private void DrawSourceReaderControls(PresentedCard entry)
    {
        var card = VisibleCards().FirstOrDefault(current => SameVisibleCard(current, entry.Card));
        if (card == null) return;
        var options = SourceReaderOptions(card);
        string state = IsStudyBattlefieldReader(entry) ? "" : SourceReaderState(card, entry.Zone);
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
        int desiredWidth = Math.Clamp(options.SelectMany(option => option.Text.Split('\n'))
            .Append(state).Select(text => canvas.TextWidth(text, size, true) + padding * 2)
            .DefaultIfEmpty(120).Max(), 120, 316);
        int desiredHeight = (state.Length == 0 ? 0 : canvas.TextSize(state, .63f, desiredWidth - 8, 80, true).Y + 12)
            + options.Sum(option => canvas.TextSize(option.Text, size, desiredWidth - padding * 2, 80, true).Y + padding * 2 + gap);
        var area = AbilityActionArea(face, desiredWidth, Math.Min(face.Height, desiredHeight));
        if (area.IsEmpty) return;
        int width = area.Width;
        var rows = options.Select(option => {
            bool secondary = entry.Zone == "Hand" && option.Action is { Kind: "play" };
            float font = secondary ? .62f : size;
            int inset = secondary ? 6 : padding;
            int lineHeight = canvas.TextSize("Ag", font, width - inset * 2, 80, !secondary).Y;
            string text = option.Text.Replace('\r', ' ').Replace('\n', ' ');
            var lines = canvas.TextPages(text, font, width - inset * 2, lineHeight, 80, !secondary);
            return (Option: option, Lines: lines, Height: Math.Max(secondary ? 30 : 38, lines.Length * lineHeight + inset * 2),
                Secondary: secondary, Font: font, Inset: inset, LineHeight: lineHeight);
        }).ToArray();
        int stateHeight = state.Length == 0 ? 0 : canvas.TextSize(state, .63f, width - 8, 80, true).Y + 12;
        int contentHeight = stateHeight + rows.Sum(row => row.Height + gap) - (rows.Length > 0 ? gap : 0);
        int height = Math.Min(area.Height, contentHeight);
        int top = Math.Clamp(face.Center.Y - height / 2, area.Top, area.Bottom - height);
        sourceReaderActionsBounds = new(area.X, top, width, height);
        sourceReaderScrollMax = Math.Max(0, contentHeight - height);
        int scroll = Math.Clamp(sourceReaderScroll, 0, sourceReaderScrollMax);
        hits.Add(new("source-actions-surface", Scope, sourceReaderActionsBounds, UiIntent.None, ShowPointer: false));
        int y = top - scroll;
        if (stateHeight > 0) {
            if (y >= top && y + stateHeight <= top + height)
                canvas.Text(state, area.X + 4, y, new(161, 228, 207), .63f, width - 8, 80, true);
            y += stateHeight;
        }
        for (int index = 0; index < rows.Length; index++) {
            var row = rows[index]; var option = row.Option;
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
                for (int line = 0; line < row.Lines.Length; line++) {
                    int lineY = y + row.Inset + line * row.LineHeight;
                    if (lineY >= top && lineY + row.LineHeight <= top + height)
                        canvas.Text(row.Lines[line], area.X + row.Inset, lineY,
                            row.Secondary ? hover ? Blue : Muted : Ink, row.Font, width - row.Inset * 2, 1, !row.Secondary);
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

    private bool ScrollSourceReader(int direction)
    {
        if (ScrollBattlefieldReaderState(direction)) return true;
        if (!SourceActionsSupported || readingId == null || !sourceReaderActionsBounds.Contains(pointer)) return false;
        sourceReaderScroll = Math.Clamp(sourceReaderScroll - direction * 36, 0, sourceReaderScrollMax);
        return true;
    }
}
