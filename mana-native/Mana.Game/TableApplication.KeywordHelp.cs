using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record KeywordHelpTarget(string Id, string Source, string Scope, string Audience, Rectangle Bounds,
        IReadOnlyList<PublicState> Descriptions, IReadOnlyList<string> Parents);
    private readonly List<KeywordHelpTarget> keywordHelpTargets = [];
    private KeywordHelpTarget? keywordHelpHover;
    private Rectangle keywordHelpBounds;
    private double keywordHelpSince, keywordHelpLastOver;
    private Point? keywordHelpDismissedAt;
    private const double KeywordHelpDwell = .35, KeywordHelpTransit = .16;
    private bool KeywordHelpReading => InputReady && !dragging && (
        keywordHelpHover is { } target && target.Audience == KeywordHelpAudience
            && (target.Bounds.Contains(pointer) || keywordHelpBounds.Contains(pointer))
        // Input precedes composition. The completed frame already owns these
        // read-only hits, so entering one blocks Auto on that very input tick.
        || HitAt(pointer) is { } hit && hit.Id.StartsWith("keyword-help:", StringComparison.Ordinal) && CurrentHit(hit));
    private string KeywordHelpAudience => (match?.Id ?? "") + ":" + match?.ViewerId;

    private void ClearKeywordHelp()
    {
        keywordHelpHover = null; keywordHelpBounds = Rectangle.Empty;
        keywordHelpSince = keywordHelpLastOver = double.NegativeInfinity;
    }

    private bool DismissKeywordHelp()
    {
        if (keywordHelpHover == null) return false;
        ClearKeywordHelp(); keywordHelpDismissedAt = pointer;
        return true;
    }

    private void RegisterKeywordHelp(string source, Rectangle bounds, IReadOnlyList<PublicState> descriptions,
        IReadOnlyList<string> parents, string fragment = "")
    {
        if (bounds.IsEmpty || descriptions.Count == 0) return;
        string id = "keyword-help:" + source + ":" + string.Join(":", descriptions.Select(state => state.Id)) + ":" + fragment;
        keywordHelpTargets.Add(new(id, source, Scope, KeywordHelpAudience, bounds, descriptions, parents));
    }

    private void RegisterKeywordIcon(Card card, string key, Rectangle bounds)
    {
        var descriptions = CardPresentation.KeywordDescriptions(card).Where(state => string.Equals(state.Key, key, StringComparison.OrdinalIgnoreCase)).ToArray();
        var entry = matchPresentation.Cards.Cards.LastOrDefault(entry => ReferenceEquals(entry.Card, card));
        if (entry != null) RegisterKeywordHelp(entry.Id + ":icon", bounds, descriptions, [entry.Hit.Id]);
    }

    private void RegisterRulesKeywordHelp(Card card, SourceActionTextLayout paragraph, int x, int y, int paragraphIndex)
    {
        var entry = matchPresentation.Cards.Pinned;
        if (entry == null || !ReferenceEquals(entry.Card, card) || !CardContext.CanRead(entry)) return;
        var descriptions = CardPresentation.KeywordDescriptions(card);
        foreach (var matches in KeywordTextHelp.Find(paragraph.Text, descriptions).GroupBy(match => (match.Start, match.Length))) {
            var range = matches.Key;
            for (int line = 0; line < paragraph.Lines.Count; line++) foreach (var run in paragraph.Lines[line]) {
                if (run.SourceStart < 0) continue;
                int start = Math.Max(range.Start, run.SourceStart), end = Math.Min(range.Start + range.Length, run.SourceStart + run.Text.Length);
                if (end <= start) continue;
                int Prefix(string value) => value.Length == 0 ? 0 : Math.Max(1, canvas.TextWidth(value, CardRulesLayout.RulesFont) - 6);
                int inset = run.Symbol == null ? Prefix(run.Text[..(start - run.SourceStart)]) : 0;
                int width = run.Symbol == null ? Prefix(run.Text[(start - run.SourceStart)..(end - run.SourceStart)]) : run.Width;
                var area = new Rectangle(x + run.X + inset, y + line * paragraph.LineHeight, Math.Max(1, width), paragraph.LineHeight);
                canvas.Line(new(area.Left, area.Bottom - 2), new(area.Right, area.Bottom - 2), new Color(57, 109, 133) * .65f, 1);
                RegisterKeywordHelp(entry.Id + ":rules", area, matches.Select(match => match.Description).ToArray(), [entry.Hit.Id], paragraphIndex + ":" + start);
            }
        }
    }

    private void DrawKeywordHelp()
    {
        if (keywordHelpDismissedAt is { } dismissed) {
            if (dismissed == pointer) { ClearKeywordHelp(); return; }
            keywordHelpDismissedAt = null;
        }
        if (!InputReady || dragging || boardPanning || boardKeyboardPanning || spreadPanning || spreadKeyboardPanning
            || match == null || StartupVisible || HistoryOpen || updatesOpen || ActivityOpen || ExperienceOpen
            || tableGuideOpen || OverlayOpen && !ReadingGalleryOpen && !ZoneReadingOpen && !BattlefieldFocused && !CardCollectionOpen) {
            ClearKeywordHelp(); return;
        }
        // Resolve against the completed scene before adding help surfaces. A
        // modal, current action or another card wins over a keyword beneath it.
        var underlying = hits.ToArray();
        bool Exposed(KeywordHelpTarget target, Point at) => underlying.LastOrDefault(hit => hit.Contains(at) && CurrentHit(hit)) is { } top
            && target.Parents.Contains(top.Id, StringComparer.Ordinal);
        foreach (var target in keywordHelpTargets) {
            hits.Add(new(target.Id, target.Scope, target.Bounds, UiIntent.None,
                Shape: at => Exposed(target, at), ShowPointer: false));
        }
        var over = keywordHelpTargets.LastOrDefault(target => target.Bounds.Contains(pointer) && Exposed(target, pointer));
        bool Same(KeywordHelpTarget left, KeywordHelpTarget right) => left.Source == right.Source && left.Audience == right.Audience
            && left.Descriptions.SequenceEqual(right.Descriptions);
        var retained = keywordHelpHover is { } old
            ? keywordHelpTargets.FirstOrDefault(target => target.Id == old.Id && Same(target, old) && Exposed(target, target.Bounds.Center))
                ?? keywordHelpTargets.FirstOrDefault(target => Same(target, old) && Exposed(target, target.Bounds.Center)) : null;
        if (over != null) {
            if (keywordHelpHover == null || !Same(over, keywordHelpHover)) keywordHelpSince = now;
            keywordHelpHover = over; keywordHelpLastOver = now;
        } else if (retained != null && (keywordHelpBounds.Contains(pointer) || now - keywordHelpLastOver <= KeywordHelpTransit)) {
            keywordHelpHover = retained;
            if (keywordHelpBounds.Contains(pointer)) keywordHelpLastOver = now;
        } else { ClearKeywordHelp(); return; }
        keywordHelpBounds = Rectangle.Empty;
        if (!preferences.ReducedMotion && now - keywordHelpSince < KeywordHelpDwell) return;
        var active = keywordHelpHover!;
        const float titleFont = .80f, detailFont = .72f;
        int width = 380, height;
        (SourceActionTextLayout Title, SourceActionTextLayout Detail)[] blocks;
        do {
            blocks = active.Descriptions.Select(state => (
                SourceActionTextLayout.Measure(canvas, PublicStatePresentation.Caption(state), titleFont, width - 32, true),
                SourceActionTextLayout.Measure(canvas, state.Detail, detailFont, width - 32, false))).ToArray();
            height = 24 + blocks.Sum(block => block.Item1.Height + block.Item2.Height + 16);
            if (height <= 560 || width >= 700) break;
            width += 80;
        } while (true);
        var reservations = new List<Rectangle> { decisionPanelBounds, sourceReaderActionsBounds, battlefieldReaderStateBounds };
        reservations.AddRange(underlying.Where(hit => hit.Id is "read-card-rules" or "read-card-action")
            .Select(hit => ReaderControlPaintBounds(hit.Bounds)));
        if (chatOpen) reservations.Add(ChatBounds);
        if (noticeUntil > now && !OverlayOpen) reservations.Add(new(427, 64, 746, 60));
        if (!string.IsNullOrWhiteSpace(error)) reservations.Add(new(270, 59, 1060, 46));
        if (hoverPreview is { } reader) reservations.Add(reader);
        if (manaJourneyPaint is { } bill) { reservations.Add(bill.Bounds); reservations.Add(bill.SourceBounds); }
        var room = DecisionActionArea(active.Bounds, width, height, additionalReservations: reservations,
            minimumWidth: width, boundedWorkspace: true);
        var area = new Rectangle(room.X, room.Y, width, height);
        var occupied = DecisionActionReservations(active.Bounds, false, reservations);
        if (room.Width < width || room.Height < height || occupied.Any(bounds => !bounds.IsEmpty && bounds.Intersects(area))) return;
        keywordHelpBounds = area;
        canvas.Panel(area, new Color(10, 22, 29) * .98f, Blue * .7f, 6);
        int y = area.Top + 12;
        foreach (var block in blocks) {
            for (int line = 0; line < block.Title.Lines.Count; line++)
                PaintSourceActionLine(block.Title, line, area.Left + 16, y + line * block.Title.LineHeight, Blue, titleFont, true);
            y += block.Title.Height + 5;
            for (int line = 0; line < block.Detail.Lines.Count; line++)
                PaintSourceActionLine(block.Detail, line, area.Left + 16, y + line * block.Detail.LineHeight, Ink, detailFont, false);
            y += block.Detail.Height + 11;
        }
        hits.Add(new("keyword-help-popup", Scope, area, UiIntent.None, ShowPointer: false));
    }
}
