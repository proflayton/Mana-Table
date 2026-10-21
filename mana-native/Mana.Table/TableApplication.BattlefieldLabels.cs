using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    internal sealed record BattlefieldLabelPaint(string Kind, string Text, Rectangle Bounds, int MeasuredWidth, int AvailableWidth);
    private readonly Dictionary<string, List<BattlefieldLabelPaint>> battlefieldLabels = [];
    private void RecordBattlefieldLabel(Card card, int owner, string kind, string text, Rectangle bounds, int measured, int available)
    {
        ExtendCardHit(card, bounds);
        if (!Automated) return;
        string id = CardViews.Identity(card);
        if (!battlefieldLabels.TryGetValue(id, out var labels)) battlefieldLabels[id] = labels = [];
        labels.Add(new(kind, text, bounds, measured, available));
    }

    private void PaintBattlefieldLabels(Card card, Pose pose, int owner, int copies)
    {
        var node = tableScene?.BattlefieldCard(owner, CardViews.Identity(card));
        if (node is { Dense: true }) { PaintDenseBattlefieldLabels(card, owner, node, copies); return; }
        if (pose.Bounds.Height >= 50) {
            var states = CardPresentation.Abilities(card).ToList();
            if (card.Sick && CardPresentation.HasCombatStats(card)) states.Insert(0, "summoning sick");
            if (card.PhasedOut) states.Insert(0, "phased out");
            for (int index = 0; index < Math.Min(2, states.Count); index++)
                PaintAbilityIcon(states[index], new(pose.Bounds.Left + 12 + index * 23, pose.Center.Y - 2));
        }
        PaintAttachedCardState(card, pose, owner);
        PaintBattlefieldCopies(card, pose, owner, copies);
    }

    private void PaintBattlefieldCopies(Card card, Pose pose, int owner, int copies)
    {
        if (copies <= 1 || pose.Size.X >= 300) return;
        string text = "×" + copies;
        int measured = canvas.TextWidth(text, .44f, true);
        var bounds = new Rectangle(pose.Bounds.Left + 2, pose.Bounds.Top + 2, measured + 8, 18);
        canvas.Panel(bounds, new(25, 37, 42), Gold, 3);
        canvas.CenterText(text, bounds, Gold, .44f, true);
        RecordBattlefieldLabel(card, owner, "copies", text, bounds, measured, measured);
    }
}
