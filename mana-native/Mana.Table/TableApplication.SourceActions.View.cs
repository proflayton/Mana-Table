using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record SourceReaderText(string Text, float Size, Color Color, bool Bold = false, SourceAction? Action = null, bool Retry = false, PriorityAbility? Placement = null);
    private sealed record SourceReaderRow(SourceReaderText Content, string Text, int Height);
    private sealed record SourceReaderControl(string Id, Rectangle Bounds, SourceAction? Action = null, bool Retry = false, PriorityAbility? Placement = null);
    private readonly List<SourceReaderControl> sourceReaderControls = [];
    private string sourceReaderViewKey = "";
    private SourceActionSet? sourceReaderViewActions;
    private int sourceReaderPage, sourceReaderPages;
    private Rectangle sourceReaderFooter;

    private float? SourceReaderHeight {
        get {
            if (!SourceActionsSupported || cardPresentation.Pinned is not { Card.FaceDown: false, ChoiceSlot: < 0 } entry) return null;
            var card = VisibleCards().FirstOrDefault(current => !current.FaceDown && SameVisibleCard(current, entry.Card));
            if (card == null) return null;
            var content = SourceReaderContent(card);
            int rows = content.Sum(block => {
                int inset = block.Action != null || block.Placement != null || block.Retry ? 10 : 0;
                return canvas.TextSize(block.Text, block.Size, 378 - inset * 2, 80, block.Bold).Y + inset * 2;
            });
            return Math.Clamp(SourceReaderNameHeight(card, 410) + 246 + rows + Math.Max(0, content.Count - 1) * 6, 350, 574);
        }
    }

    private int SourceReaderNameHeight(Card card, int width) => Math.Clamp(canvas.TextSize(card.Name, .88f, width - 32, 2, true).Y, 32, 66);

    private List<SourceReaderText> SourceReaderContent(Card card)
    {
        var content = new List<SourceReaderText>();
        void Add(string text, float size, Color color, bool bold = false, SourceAction? action = null, bool retry = false, PriorityAbility? placement = null) {
            if (!string.IsNullOrWhiteSpace(text)) content.Add(new(text, size, color, bold, action, retry, placement));
        }
        Add(card.Type, .59f, Gold);
        if (card.ManaCost != "no cost") Add(card.ManaCost, .63f, Gold);
        foreach (var counter in CardPresentation.Counters(card)) Add(counter.Key + " ×" + counter.Value, .67f, new(161, 228, 207), true);
        bool matching = ReaderActionsMatch(card);
        if (matching) {
            foreach (var action in readerActions!.Actions) Add(action.Text, .76f, Ink, true, action);
            if (match != null) foreach (var placement in HandPlacements.Options(match, card))
                Add(placement.Text, .76f, Ink, true, placement: placement);
        }
        else if (readerActionsBinding.Length > 0 && readerActionsBinding == ReaderActionBinding(card)) {
            if (readerActionsLoading) Add("Finding available actions…", .64f, Muted);
            else if (readerActionsError.Length > 0) Add(readerActionsError + "\nRetry", .66f, Red, retry: true);
        }
        // A concrete action already displays that entire printed line. Keep
        // every other Oracle line unchanged and leave the original card view
        // intact for complete inspection; this does not interpret rules text.
        string oracle = card.Text;
        if (matching) {
            var actionLines = readerActions!.Actions.Select(action => action.Text.Trim()).ToHashSet(StringComparer.Ordinal);
            oracle = string.Join("\n", oracle.Replace("\r\n", "\n").Split('\n').Where(line => !actionLines.Contains(line.Trim())));
        }
        Add(oracle, .69f, Ink);
        foreach (string state in CardPresentation.StateLabels(card).Where(value => value != "Untapped")) Add(state, .62f, Muted);
        return content;
    }

    private bool DrawSourceActionReader(PresentedCard entry)
    {
        if (!SourceActionsSupported || !entry.Focused || entry.ChoiceSlot >= 0 || entry.Card.FaceDown
            || entry.Display.Size.X < 300 || !CardContext.CanRead(entry)) return false;
        var readerTarget = entry.ReaderTarget ?? CardPresentationController.ReaderPose(entry.Rest, entry.Zone);
        if (Vector2.Distance(entry.Display.Size, readerTarget.Size) > 2
            || Vector2.Distance(entry.Display.Center, readerTarget.Center) > 2) return false;
        var card = VisibleCards().FirstOrDefault(current => SameVisibleCard(current, entry.Card));
        if (card == null) return false;
        string key = string.Join("\n", match?.Id, match?.Decision?.Id, entry.Id, entry.Zone, card.Key);
        if (key != sourceReaderViewKey || !ReferenceEquals(sourceReaderViewActions, readerActions)) {
            sourceReaderPage = 0; sourceReaderViewKey = key; sourceReaderViewActions = readerActions;
        }
        var face = entry.Display.Bounds;
        canvas.Panel(face, new(22, 31, 37), new(123, 143, 151), 10);
        int nameHeight = SourceReaderNameHeight(card, face.Width);
        canvas.Text(card.Name, face.X + 16, face.Y + 12, Ink, .88f, face.Width - 32, 2, true);
        var art = new Rectangle(face.X + 14, face.Y + nameHeight + 20, face.Width - 28, 166);
        if (resources.Card(card) is { } image) {
            // The picture has its own uninterrupted area. Live rules and
            // controls are typeset below it, never placed over the picture.
            var crop = new Rectangle((int)(image.Width * .08f), (int)(image.Height * .12f),
                (int)(image.Width * .84f), (int)(image.Height * .40f));
            float aspect = art.Width / (float)art.Height;
            if (crop.Width / (float)crop.Height > aspect) {
                int width = (int)(crop.Height * aspect); crop.X += (crop.Width - width) / 2; crop.Width = width;
            } else {
                int height = (int)(crop.Width / aspect); crop.Y += (crop.Height - height) / 2; crop.Height = height;
            }
            canvas.Sprite(image, art.Center.ToVector2(), art.Size.ToVector2(), 0, Color.White, crop);
        } else canvas.Gradient(art, new(43, 58, 64), new(28, 40, 48));
        sourceReaderFooter = new(face.X + 14, face.Bottom - 40, face.Width - 28, 32);
        var body = new Rectangle(face.X + 16, art.Bottom + 10, face.Width - 32, sourceReaderFooter.Top - art.Bottom - 20);
        var content = SourceReaderContent(card);
        List<List<SourceReaderRow>> pages = [[]];
        int used = 0;
        foreach (var block in content) {
            int inset = block.Action != null || block.Placement != null || block.Retry ? 10 : 0;
            int textWidth = body.Width - inset * 2;
            foreach (string part in canvas.TextPages(block.Text, block.Size, textWidth, body.Height - inset * 2, 80, block.Bold)) {
                int height = canvas.TextSize(part, block.Size, textWidth, 80, block.Bold).Y + inset * 2;
                if (used > 0 && used + 6 + height > body.Height) { pages.Add([]); used = 0; }
                pages[^1].Add(new(block, part, height));
                used += (used > 0 ? 6 : 0) + height;
            }
        }
        sourceReaderPages = pages.Count;
        sourceReaderPage = Math.Clamp(sourceReaderPage, 0, pages.Count - 1);
        int y = body.Y;
        foreach (var row in pages[sourceReaderPage]) {
            var block = row.Content;
            var bounds = new Rectangle(body.X, y, body.Width, row.Height);
            bool actionable = block.Action != null || block.Placement != null || block.Retry;
            int inset = actionable ? 10 : 0;
            if (actionable) {
                int index = block.Action == null ? -1 : Array.IndexOf(readerActions!.Actions, block.Action);
                bool firstPlacement = block.Placement != null && readerActions!.Actions.Length == 0
                    && ReferenceEquals(block, content.FirstOrDefault(item => item.Placement != null));
                string id = block.Retry ? "source-actions-retry" : index == 0 || firstPlacement ? "read-card-action"
                    : block.Placement != null ? "source-placement:" + block.Placement.Id : "source-action:" + block.Action!.Id;
                PaintControlSurface(id, bounds, new(28, 48, 58), Blue * .7f, !busy);
                sourceReaderControls.Add(new(id, bounds, block.Action, block.Retry, block.Placement));
            }
            canvas.Text(row.Text, bounds.X + inset, bounds.Y + inset, block.Color, block.Size, bounds.Width - inset * 2, 80, block.Bold);
            y += row.Height + 6;
        }
        string stats = CardPresentation.HasCombatStats(card) ? CurrentStats(card) : "";
        if (card.Damage > 0) stats += (stats.Length > 0 ? " · " : "") + "DMG " + card.Damage;
        if (stats.Length > 0) {
            int width = Math.Min(sourceReaderFooter.Width, canvas.TextWidth(stats, .83f, true) + 12);
            canvas.CenterText(stats, new(sourceReaderFooter.Right - width, sourceReaderFooter.Y, width, 32), Ink, .83f, true);
        }
        return true;
    }

    private void DrawSourceReaderControls(PresentedCard entry)
    {
        foreach (var control in sourceReaderControls) {
            if (busy) continue;
            var captured = control;
            hits.Add(new(control.Id, ControlScope(control.Id), control.Bounds, () => {
                if (captured.Retry) RetryReaderActions();
                else if (captured.Action != null) UseReaderAction(captured.Action);
                else if (captured.Placement != null) UseReaderPlacement(captured.Placement);
            }));
        }
        if (sourceReaderPages <= 1) return;
        Button("source-reader-prev", "‹", sourceReaderFooter.X, sourceReaderFooter.Y, 32,
            () => sourceReaderPage--, sourceReaderPage > 0, height: 30);
        Button("source-reader-next", "›", sourceReaderFooter.X + 38, sourceReaderFooter.Y, 32,
            () => sourceReaderPage++, sourceReaderPage + 1 < sourceReaderPages, height: 30);
        canvas.Text($"{sourceReaderPage + 1} / {sourceReaderPages}", sourceReaderFooter.X + 80, sourceReaderFooter.Y + 7, Muted, .56f, 85, 1);
    }
}
