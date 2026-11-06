using System.Collections.Immutable;
using System.Globalization;
using Mana.Client;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private ImmutableArray<TableChoiceCue> tableChoiceCues = [];
    private bool TableChoiceInputAvailable => TablePresenceActive
        && !ActivityOpen && !ExperienceOpen && !updatesOpen && !HistoryOpen && !historyInspector
        && !chatOpen && !textFocus && !boardPanning && !boardKeyboardPanning
        && !spreadPanning && !spreadKeyboardPanning && !CardCollectionOpen && manaGesture == null
        && !OverlayOpen && !AbilityActionPending && !GroupBlocksOpen;

    private TableChoicePreview? CurrentPublicChoice()
    {
        if (!TableChoiceInputAvailable || match is not { PublicChoice: { } context } state
            || context.PlayerId != state.ViewerId || !PublicChoiceFacts.IsCurrent(state)) return null;
        TableChoicePreview? preview = null;
        if (context.Kind is PublicChoiceKind.Attack or PublicChoiceKind.Block) {
            if (CombatSource is not { Length: > 0 } sourceId || CombatGuide.Find(state, sourceId) is not { } source
                || !TablePresenceSession.PublicSubject(state, new(TableInspectionKind.BattlefieldCard, source.VisualId))) return null;
            TableChoiceTarget? target = null;
            var hit = HitAt(pointer);
            if (hit != null && CurrentHit(hit)) {
                if (hit.Zone == "Battlefield" && hit.Card is { } card
                    && (context.Kind == PublicChoiceKind.Attack ? LegalTargets.CanAttackCard(state, sourceId, card.CombatId)
                        : LegalTargets.CanBlock(state, sourceId, card.CombatId))) target = PublicChoiceTarget(hit);
                else if (context.Kind == PublicChoiceKind.Attack && hit.PlayerId is { } player && LegalTargets.CanAttack(state, sourceId, player))
                    target = new(TableChoiceTargetKind.Player, player.ToString(CultureInfo.InvariantCulture));
            }
            preview = new(new(TableInspectionKind.BattlefieldCard, source.VisualId), target);
        } else if (context.Kind == PublicChoiceKind.Target && context.Source is { } source
            && decisions.Presentation?.Payload is TargetSelectionPayload { Search: null } selection) {
            // The authority supplies the actual public source. Never substitute
            // a readable hand face or the actor's floating effect-panel location.
            var origin = TryPublicChoicePose(source, out var pose) ? pose : new Pose(Vector2.Zero, Vector2.One);
            preview = new(source, CurrentSourceTargetAim(state, selection, origin, true, null)?.PublicTarget);
        }
        return PublicChoiceFacts.AllowsPreview(state, preview) ? preview : null;
    }

    private TableChoiceTarget? PublicChoiceTarget(Hit hit)
    {
        if (match is not { } state) return null;
        TableChoiceTarget? result = null;
        if (hit.Zone == "Battlefield" && hit.Card is { } card)
            result = new(TableChoiceTargetKind.BattlefieldCard, card.VisualId);
        else if (hit.Zone == "Stack" && hit.Intent is MatchIntent.UseStackCard { PayingSource: false, Index: >= 0 } stack
            && state.Stack.ElementAtOrDefault(stack.Index) is { Card: { } face } item && hit.Card is { } actual && SameVisibleCard(face, actual))
            result = new(TableChoiceTargetKind.StackItem, item.Id);
        return result != null && PublicChoiceFacts.PublicTarget(state, result) ? result : null;
    }

    private bool TryPublicChoicePose(TableInspectionSubject subject, out Pose pose)
    {
        pose = default;
        if (subject.Kind == TableInspectionKind.BattlefieldCard) {
            var entry = matchPresentation.Cards.Cards.LastOrDefault(card => card.Zone == "Battlefield" && card.ChoiceSlot < 0
                && card.Card.VisualId == subject.Id);
            if (entry == null) return false;
            // Enlarging a face locally never drags another person's gesture off
            // the table. Keep it on this occurrence's physical resting slot.
            pose = entry.Focused ? entry.Rest : entry.Display;
            return true;
        }
        var row = stackRows.LastOrDefault(row => row.Id == subject.Id);
        if (row == null || row.Bounds.IsEmpty) return false;
        pose = new(row.Bounds.Center.ToVector2(), row.Bounds.Size.ToVector2());
        return true;
    }

    private bool TryPublicChoiceTargetPose(TableChoiceTarget target, out Pose pose)
    {
        pose = default;
        if (target.Kind == TableChoiceTargetKind.Player) {
            if (!PublicChoiceFacts.PlayerId(target.Id, out int player) || !heroPositions.TryGetValue(player, out var hero)) return false;
            // The local phase wheel surrounds the portrait. End outside that
            // whole seat control so its buttons cannot hide the open arrowhead.
            int radius = player == match!.ViewerId ? 88 : 46;
            pose = new(hero, new(radius * 2));
            return true;
        }
        return TryPublicChoicePose(new(target.Kind == TableChoiceTargetKind.BattlefieldCard
            ? TableInspectionKind.BattlefieldCard : TableInspectionKind.StackItem, target.Id), out pose);
    }

    private void DrawTableChoices()
    {
        if (!TableInspectionPaintVisible) return;
        if (TableStudyEnabled) canvas.BeginClip(BoardViewport);
        foreach (var cue in tableChoiceCues) {
            if (!TryPublicChoicePose(cue.Preview.Source, out var source)) continue;
            var color = InspectionColor(cue.PlayerId);
            canvas.CardFrame(source with { Size = source.Size + new Vector2(9) }, Color.Black * .8f, 4);
            canvas.CardFrame(source with { Size = source.Size + new Vector2(9) }, color, 2);
            DrawInspectionSeat(cue.PlayerId, source.Point(1, 0) + new Vector2(2, -4), color);
            if (cue.Preview.Target is not { } target || !TryPublicChoiceTargetPose(target, out var destination)) continue;
            var end = destination.EdgeToward(source.Center, 6);
            if (target.Kind == TableChoiceTargetKind.Player) {
                var direction = source.Center - destination.Center;
                if (direction.LengthSquared() <= 0) continue;
                float radius = destination.Size.X / 2;
                end = destination.Center + Vector2.Normalize(direction) * (radius + 4);
                canvas.Ring(destination.Center, radius, color * .65f, 1.5f);
            }
            TableChoiceArrow.Draw(canvas, source.EdgeToward(end, 6), end, color * .9f);
        }
        if (TableStudyEnabled) canvas.EndClip();
    }
}
