using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    internal sealed record BattlefieldLabelPaint(string Kind, string Text, Rectangle Bounds, int MeasuredWidth, int AvailableWidth);
    private readonly Dictionary<string, List<BattlefieldLabelPaint>> battlefieldLabels = [];
    private const int CompactLabelWidth = 96, CompactLabelRow = 22;
    private const float CompactStatsSize = .56f;

    private bool HasOrderedBattlefieldChoice(Card card) => match?.Decision is { Ordered: true }
        && TableChoices.Index(match, card) != null;

    private Rectangle CompactStatsBounds(Card card, Pose pose, int seat)
    {
        var center = CompactStateCenter(pose, seat);
        int width = Math.Max(50, canvas.TextWidth($"{card.Power}/{card.Toughness}", CompactStatsSize, true) + 10);
        int top = HasOrderedBattlefieldChoice(card) ? pose.Bounds.Top + 40 : (int)center.Y - 10;
        if (seat == 1 && !HasOrderedBattlefieldChoice(card)) top = pose.Bounds.Bottom + CompactLabelRow + 6;
        // Side seats fan their copies behind the inward edge. Keep the state
        // strip beyond the face so it cannot cover those copies' exposed edges.
        int left = seat == 0 ? pose.Bounds.Right + 8
            : seat == 2 ? pose.Bounds.Left - 8 - Math.Max(CompactLabelWidth, width)
            : (int)center.X - width / 2;
        return new(left, top, width, 22);
    }

    private void RecordBattlefieldLabel(Card card, int owner, string kind, string text, Rectangle bounds, int measured, int available)
    {
        // Labels belong to the same occurrence as the face, including the
        // portion that extends beyond its art. Never click through to a neighbor.
        ExtendCardHit(card, bounds);
        if (!Automated) return;
        string id = CardViews.Identity(card);
        if (!battlefieldLabels.TryGetValue(id, out var labels)) battlefieldLabels[id] = labels = [];
        labels.Add(new(kind, text, bounds, measured, available));
    }

    private void PaintBattlefieldLabels(Card card, Pose pose, int owner, int copies)
    {
        var dense = tableScene?.BattlefieldCard(owner, CardViews.Identity(card));
        if (dense is { Dense: true }) { PaintDenseBattlefieldLabels(card, owner, dense, copies); return; }
        int seat = owner == match!.ViewerId ? 3 : TableLayout.SeatIndex(match, owner);
        var stats = CompactStatsBounds(card, pose, seat);
        int left = stats.Left, row = 0, used = 0;
        int RowY() => stats.Top + row * CompactLabelRow;
        string name = card.FaceDown ? "Face-down permanent" : string.IsNullOrWhiteSpace(card.Name) ? "Permanent" : card.Name;
        int nameTop = HasOrderedBattlefieldChoice(card) ? pose.Bounds.Top - CompactLabelRow : stats.Top - CompactLabelRow;
        var nameBounds = new Rectangle(left, nameTop, CompactLabelWidth, 19);
        canvas.Panel(nameBounds, new(14, 22, 29), new(67, 85, 98), 3);
        canvas.Text(name, left + 5, nameBounds.Y + 2, Ink, .44f, CompactLabelWidth - 10, 1, true);
        RecordBattlefieldLabel(card, owner, "name", name, nameBounds, canvas.TextWidth(name, .44f, true), CompactLabelWidth - 10);
        void Chip(string kind, string text, Color accent, bool ownRow = false, float size = .45f)
        {
            while (size > .375f && canvas.TextWidth(text, size, true) + 10 > CompactLabelWidth) size = Math.Max(.375f, size - .025f);
            int measured = canvas.TextWidth(text, size, true), width = measured + 10;
            if (ownRow && used > 0 || used > 0 && used + 4 + width > CompactLabelWidth) { row++; used = 0; }
            int x = left + (used > 0 ? used + 4 : 0);
            var bounds = new Rectangle(x, RowY(), width, kind == "pt" ? 22 : 19);
            canvas.Panel(bounds, kind == "damage" ? new Color(67, 28, 29) : new Color(16, 26, 33), accent, 3);
            canvas.Text(text, bounds.X + 5, bounds.Y + 2, kind == "damage" ? new Color(255, 199, 188) : Ink,
                size, measured, 1, true);
            RecordBattlefieldLabel(card, owner, kind, text, bounds, measured, measured);
            used = x - left + width;
        }
        if (CardPresentation.HasCombatStats(card)) {
            string value = $"{card.Power}/{card.Toughness}";
            bool attached = card.AttachedTo != null || card.Attachments.Length > 0;
            canvas.Panel(stats, attached ? new Color(48, 39, 22) : card.Attacking ? new Color(98, 39, 40) : new Color(17, 27, 35),
                attached ? Gold : new Color(140, 155, 162), 3);
            canvas.CenterText(value, stats, Ink, CompactStatsSize, true);
            RecordBattlefieldLabel(card, owner, "pt", value, stats, canvas.TextWidth(value, CompactStatsSize, true), stats.Width - 10);
            used = stats.Width;
        }
        if (card.Damage > 0) Chip("damage", "DMG " + card.Damage, Red);
        if (copies > 1) Chip("copies", "COPIES " + copies, Gold);
        var counters = CardPresentation.Counters(card);
        if (counters.Length > 0) {
            var counter = counters[0];
            Chip("counter", "CTR " + counter.Key + " ×" + counter.Value, new(168, 207, 200), true);
            if (counters.Length > 1) Chip("counter-types", "+" + (counters.Length - 1) + " counter type" + (counters.Length == 2 ? "" : "s"), new(168, 207, 200));
        }
        var states = CardPresentation.DescriptorLabels(card).ToList();
        if (card.Sick && CardPresentation.HasCombatStats(card)) states.Insert(0, "Summoning sick");
        if (card.PhasedOut) states.Insert(0, "Phased out");
        if (states.Count > 0) {
            string label = states[0] + (states.Count > 1 ? " +" + (states.Count - 1) : "");
            // The full qualified state stays in the reader. Keep the table chip
            // within its reserved footprint when an unfamiliar label is long.
            if (canvas.TextWidth(label, .375f, true) + 10 > CompactLabelWidth)
                label = states.Count + (states.Count == 1 ? " state" : " states");
            Chip("state", label, AbilityColor(CardPresentation.Abilities(card).FirstOrDefault() ?? ""), true);
        }
    }

    private void PaintBattlefieldCopies(Card card, Pose pose, int owner, int copies)
    {
        if (copies <= 1 || pose.Size.X >= 300) return;
        int seat = owner == match!.ViewerId ? 3 : TableLayout.SeatIndex(match, owner);
        var center = CompactStateCenter(pose, seat);
        string text = "COPIES " + copies;
        int measured = canvas.TextWidth(text, .45f, true);
        var bounds = new Rectangle((int)center.X - measured / 2 - 5, (int)center.Y + 17, measured + 10, 19);
        canvas.Panel(bounds, new(25, 37, 42), Gold, 3);
        canvas.Text(text, bounds.X + 5, bounds.Y + 2, Gold, .45f, measured, 1, true);
        RecordBattlefieldLabel(card, owner, "copies", text, bounds, measured, measured);
    }
}
