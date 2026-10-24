using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record LinkedSpellFace(SourceAction Action, Rectangle ReadBounds, Rectangle SourceBounds);
    // Geometry for this composition only. It cannot survive a new query/context.
    private readonly List<LinkedSpellFace> linkedSpellFaces = [];

    private void PaintPreparedMarker(Card card, Pose pose, int owner)
    {
        if (card.FaceDown || card.States is not { } states
            || states.FirstOrDefault(state => state.Kind == "status" && state.Key == "prepared") is not { } prepared) return;
        // A separate spell sigil keeps this state visible when keyword slots
        // are occupied. Its own card supplies the meaning on inspection.
        float radius = Math.Clamp(pose.Size.X * .11f, 6, 14);
        var center = pose.Point(.91f, .38f);
        canvas.Circle(center, radius, new Color(17, 31, 39));
        canvas.Ring(center, radius - 1, Gold, 1.4f);
        var glyph = new Pose(center + new Vector2(-radius * .1f, 0), new(radius * .8f, radius * 1.05f), -.12f);
        canvas.CardFrame(glyph, Gold, 1);
        var spark = center + new Vector2(radius * .38f, -radius * .36f);
        canvas.Line(spark - new Vector2(radius * .25f, 0), spark + new Vector2(radius * .25f, 0), Ink, 1.5f);
        canvas.Line(spark - new Vector2(0, radius * .25f), spark + new Vector2(0, radius * .25f), Ink, 1.5f);
        RecordBattlefieldLabel(card, owner, "prepared", PublicStatePresentation.Caption(prepared),
            new((int)(center.X - radius), (int)(center.Y - radius), (int)(radius * 2), (int)(radius * 2)), 0, 0);
    }

    private int LinkedSpellHeaderHeight(Card card, int width)
        => Math.Max(104, canvas.TextSize(card.Name, .76f, Math.Max(20, width - 94), 80, true).Y + 54);

    private void PaintLinkedSpellHeader(SourceAction action, Rectangle bounds, Rectangle source)
    {
        var card = action.Card!;
        int textX = bounds.X + 86, textWidth = bounds.Width - 96;
        bool hover = bounds.Contains(pointer);
        canvas.Rounded(bounds, new(12, 25, 32), 4);
        var face = new Pose(new(bounds.X + 40, bounds.Y + 49), new(60, 84));
        PaintCard(card, face, false, false);
        if (hover) canvas.CardFrame(face, HoverAccent, 2);
        int nameHeight = canvas.TextSize(card.Name, .76f, textWidth, 80, true).Y;
        canvas.Text(card.Name, textX, bounds.Y + 10, hover ? HoverAccent : Ink, .76f, textWidth, 80, true);
        canvas.Text(card.ManaCost == "no cost" ? "" : card.ManaCost, textX, bounds.Y + 16 + nameHeight, Gold, .72f, textWidth, 2);
        canvas.Text(action.Zone, textX, bounds.Bottom - 24, Muted, .57f, textWidth, 1);
        var intent = new UiIntent.Block(InteractionScopeKind.Source);
        hits.Add(new("source-spell:" + action.Id, ScopeFor(intent), bounds, intent, ShowPointer: false));
        linkedSpellFaces.Add(new(action, bounds, source));
    }

    private void DrawLinkedSpellPreview()
    {
        if (OverlayOpen || dragging || matchPresentation.Cards.Pinned is not { } entry
            || ReaderActionSource() is not { } source || !ReaderActionsMatch(source)) return;
        var linked = linkedSpellFaces.LastOrDefault(face => face.ReadBounds.Contains(pointer));
        if (linked?.Action.Card is not { } card
            || !sourceActions.View.Actions.Any(action => action == linked.Action)) return;
        // The current decision and complete source take precedence. A hover
        // preview is omitted when no clear full-face position exists.
        var occupied = new[] { linked.SourceBounds, sourceReaderActionsBounds, battlefieldReaderStateBounds,
            decisionPanelBounds, targetingEffectBounds }
            .Where(bounds => !bounds.IsEmpty).ToArray();
        var candidates = new[] { sourceReaderActionsBounds.Left - 422, sourceReaderActionsBounds.Right + 12,
            linked.ReadBounds.Center.X - 205, linked.SourceBounds.Right + 12,
            linked.SourceBounds.Left - 422, 12, 1178 }
            .Select(x => new Rectangle(Math.Clamp(x, 12, 1178), 90, 410, 574));
        var bounds = candidates.Where(candidate => occupied.All(area => !area.Intersects(candidate))
            && stackRows.All(row => !row.Bounds.Intersects(candidate)))
            .OrderBy(candidate => Vector2.DistanceSquared(candidate.Center.ToVector2(), linked.ReadBounds.Center.ToVector2()))
            .FirstOrDefault();
        if (bounds.IsEmpty) return;
        PaintCard(card, new(bounds.Center.ToVector2(), bounds.Size.ToVector2()), false, false, true);
        var intent = new UiIntent.Block(InteractionScopeKind.Source);
        hits.Add(new("source-spell-preview:" + linked.Action.Id, ScopeFor(intent), bounds, intent, ShowPointer: false));
    }
}
