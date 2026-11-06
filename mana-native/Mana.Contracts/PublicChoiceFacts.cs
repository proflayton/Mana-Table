using System.Globalization;

namespace Mana.Contracts;

public enum PublicChoiceViolation
{
    None, InvalidContext, BoardRevisionMismatch, UnknownPlayer, DecisionOwnerMismatch,
    NonChoiceAction, NotPlaying, MissingLocalChoice, InvalidSource
}

public static class PublicChoiceFacts
{
    public static PublicChoiceViolation Violation(GameSnapshot state)
    {
        if (state.PublicChoice is not { } context) return PublicChoiceViolation.None;
        if (!ValidId(context.Id) || !Enum.IsDefined(context.Kind)) return PublicChoiceViolation.InvalidContext;
        if (context.BoardRevision != state.BoardRevision) return PublicChoiceViolation.BoardRevisionMismatch;
        if (!state.Players.Any(player => player.Id == context.PlayerId && !player.Eliminated)) return PublicChoiceViolation.UnknownPlayer;
        if (state.DecisionOwnerId != context.PlayerId) return PublicChoiceViolation.DecisionOwnerMismatch;
        string actionClass = context.Kind switch { PublicChoiceKind.Attack => "attack", PublicChoiceKind.Block => "block", _ => "selection" };
        if (state.ActionClass != actionClass) return PublicChoiceViolation.NonChoiceAction;
        if (state.Status != "playing") return PublicChoiceViolation.NotPlaying;
        if (context.PlayerId == state.ViewerId && !(context.Kind switch {
            PublicChoiceKind.Attack => state.Decision?.Payload is AttackPayload,
            PublicChoiceKind.Block => state.Decision?.Payload is BlockPayload,
            PublicChoiceKind.Target => state.Decision?.Payload is TargetSelectionPayload { Search: null },
            _ => false
        })) return PublicChoiceViolation.MissingLocalChoice;
        if (context.Kind == PublicChoiceKind.Target ? !PublicSubject(state, context.Source) : context.Source != null)
            return PublicChoiceViolation.InvalidSource;
        return PublicChoiceViolation.None;
    }

    public static bool IsCurrent(GameSnapshot state) => state.PublicChoice != null && Violation(state) == PublicChoiceViolation.None;
    public static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Any(char.IsControl);
    public static bool PlayerId(string? id, out int player)
        => int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out player) && player >= 0
            && id == player.ToString(CultureInfo.InvariantCulture);

    public static bool PublicSubject(GameSnapshot current, TableInspectionSubject? subject)
        => subject != null && ValidId(subject.Id) && (subject.Kind switch {
            TableInspectionKind.BattlefieldCard => current.Players.SelectMany(player => player.Zone("Battlefield").Cards)
                .Count(card => card.VisualId == subject.Id && !card.FaceDown && !card.PhasedOut) == 1,
            TableInspectionKind.StackItem => current.Stack.Count(item => item.Id == subject.Id
                && item.Card is { FaceDown: false } && !string.IsNullOrWhiteSpace(item.Name)) == 1,
            _ => false
        });
    public static bool PublicTarget(GameSnapshot current, TableChoiceTarget? target)
        => target == null || ValidId(target.Id) && (target.Kind switch {
            TableChoiceTargetKind.BattlefieldCard => PublicSubject(current, new(TableInspectionKind.BattlefieldCard, target.Id)),
            TableChoiceTargetKind.StackItem => PublicSubject(current, new(TableInspectionKind.StackItem, target.Id)),
            TableChoiceTargetKind.Player => PlayerId(target.Id, out int id) && current.Players.Any(player => player.Id == id && !player.Eliminated),
            _ => false
        });

    // Recipients can validate disclosure and ownership without receiving the
    // actor's private option catalog. The authority admits the exact legal pair.
    public static bool AllowsPreview(GameSnapshot current, TableChoicePreview? preview)
    {
        if (!IsCurrent(current) || preview == null || !PublicSubject(current, preview.Source) || !PublicTarget(current, preview.Target)) return false;
        var context = current.PublicChoice!;
        if (context.Kind == PublicChoiceKind.Target) return preview.Source == context.Source;
        if (preview.Source.Kind != TableInspectionKind.BattlefieldCard
            || !current.Players.First(player => player.Id == context.PlayerId).Zone("Battlefield").Cards.Any(card => card.VisualId == preview.Source.Id)) return false;
        return context.Kind == PublicChoiceKind.Attack
            ? preview.Target?.Kind is null or TableChoiceTargetKind.Player or TableChoiceTargetKind.BattlefieldCard
            : preview.Target?.Kind is null or TableChoiceTargetKind.BattlefieldCard;
    }
}
