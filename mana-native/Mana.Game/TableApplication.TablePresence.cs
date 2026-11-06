using Mana.Client;
using Mana.Contracts;
using System.Collections.Immutable;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly TablePresenceSession tablePresence;
    private ImmutableArray<TableInspectionCue> tableInspectionCues = [];
    private bool TablePresenceActive => InputReady && !closing && !session.NeedsRecovery;
    private bool TableInspectionInputAvailable => TablePresenceActive
        && !ActivityOpen && !ExperienceOpen && !updatesOpen && !HistoryOpen && !historyInspector
        && !chatOpen && !textFocus && !dragging && !boardPanning && !boardKeyboardPanning
        && !spreadPanning && !spreadKeyboardPanning && !CardCollectionOpen && manaGesture == null
        && !OverlayOpen;
    private bool TableInspectionPaintVisible => TablePresenceActive && !ActivityOpen && !ExperienceOpen
        && !updatesOpen && !HistoryOpen && !historyInspector && !OverlayOpen;

    private void UpdateTablePresence()
    {
        tablePresence.Observe(match);
        tablePresence.Update(now, CurrentPublicInspection(), TablePresenceActive,
            !busy && !polling && !session.IsAdvancing && fixture == null && !ExperienceLabOpen, CurrentPublicChoice());
    }

    private TableInspectionSubject? CurrentPublicInspection()
    {
        if (!TableInspectionInputAvailable || match is not { PublicInspection: { } context } state
            || context.PlayerId != state.ViewerId || context.BoardRevision != state.BoardRevision) return null;
        var hit = HitAt(pointer);
        if (hit != null && CurrentHit(hit)) {
            string? stack = hit.Intent switch {
                MatchIntent.InspectPendingEffect value => value.Id,
                MatchIntent.ReadPendingReference { DecisionId.Length: 0 } value => value.StackId,
                MatchIntent.OpenStackWorkload value => CurrentStackWorkloads.FirstOrDefault(group => group.Key == value.Key)?.First.Item.Id,
                _ => null
            };
            if (stack is { Length: > 0 } && TablePresenceSession.PublicSubject(state, new(TableInspectionKind.StackItem, stack))) return new(TableInspectionKind.StackItem, stack);
            if (hit.Zone == "Battlefield" && hit.Card is { FaceDown: false } card
                && TablePresenceSession.PublicSubject(state, new(TableInspectionKind.BattlefieldCard, card.VisualId))) return new(TableInspectionKind.BattlefieldCard, card.VisualId);
        }
        // A pinned reader still names its actual current public occurrence.
        // Never use a decision source, historical face or private revealed face.
        if (matchPresentation.Cards.Pinned is { Zone: "Battlefield", ChoiceSlot: < 0 } pinned
            && CardContext.CanRead(pinned) && TablePresenceSession.PublicSubject(state, new(TableInspectionKind.BattlefieldCard, pinned.Card.VisualId)))
            return new(TableInspectionKind.BattlefieldCard, pinned.Card.VisualId);
        return null;
    }

    private Color InspectionColor(int playerId) => Array.FindIndex(match!.Players.ToArray(), player => player.Id == playerId) switch {
        0 => new(119, 194, 231), 1 => new(187, 148, 224), 2 => new(233, 186, 111), _ => new(125, 212, 183)
    };

    private void DrawTableInspection(PresentedCard entry)
    {
        if (!TableInspectionPaintVisible || entry.Zone != "Battlefield" || entry.ChoiceSlot >= 0) return;
        var cue = tableInspectionCues.FirstOrDefault(cue => cue.Subject.Kind == TableInspectionKind.BattlefieldCard
            && cue.Subject.Id == entry.Card.VisualId);
        if (cue == null) return;
        // Reading on this viewer must not move somebody else's attention cue
        // onto the viewer's enlarged face. It stays at the physical table slot.
        var pose = entry.Focused ? entry.Rest : entry.Display;
        var color = InspectionColor(cue.PlayerId);
        canvas.CardFrame(pose with { Size = pose.Size + new Vector2(7) }, Color.Black * .8f, 4);
        canvas.CardFrame(pose with { Size = pose.Size + new Vector2(7) }, color * .85f, 2);
        DrawInspectionSeat(cue.PlayerId, pose.Point(1, 0) + new Vector2(1, -3), color);
    }

    private void DrawStackInspections()
    {
        if (!TableInspectionPaintVisible) return;
        foreach (var cue in tableInspectionCues.Where(cue => cue.Subject.Kind == TableInspectionKind.StackItem)) {
            var row = stackRows.LastOrDefault(row => row.Id == cue.Subject.Id);
            if (row == null) continue;
            var color = InspectionColor(cue.PlayerId);
            canvas.CardFrame(new(row.Bounds.Center.ToVector2(), row.Bounds.Size.ToVector2()), color * .85f, 2);
            DrawInspectionSeat(cue.PlayerId, new(row.Bounds.Right - 5, row.Bounds.Top + 4), color);
        }
    }

    private void DrawInspectionSeat(int playerId, Vector2 point, Color color)
    {
        canvas.Circle(point, 9, new Color(10, 20, 25));
        canvas.Ring(point, 9, color, 2);
        string seat = (Array.FindIndex(match!.Players.ToArray(), player => player.Id == playerId) + 1).ToString();
        canvas.CenterText(seat, new((int)point.X - 8, (int)point.Y - 8, 16, 16), color, .42f, true);
    }
}
