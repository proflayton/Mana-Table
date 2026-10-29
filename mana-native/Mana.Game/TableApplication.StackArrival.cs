using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string StackHeading(StackItem item)
    {
        if (matchPresentation.StackArrivalStrength(item.Id, now) <= 0) return "Next to resolve";
        var actor = item.ControllerId is { } controller ? StateClarity.PlayerName(match!, controller) : "A player";
        bool own = item.ControllerId == match!.ViewerId;
        return item.Kind switch {
            StackActionKind.Spell => actor + (own ? " cast" : " casts"),
            StackActionKind.ActivatedAbility => actor + (own ? " activate" : " activates"),
            StackActionKind.TriggeredAbility => "Triggered · " + actor,
            _ => "Ability · " + actor
        };
    }

    private void PaintStackArrival(StackItem item, Rectangle bounds)
    {
        float strength = matchPresentation.StackArrivalStrength(item.Id, now);
        if (strength <= 0) return;
        float pulse = preferences.ReducedMotion ? 1 : .75f + .25f * MathF.Sin((float)now * 6);
        canvas.CardFrame(new(bounds.Center.ToVector2(), bounds.Size.ToVector2()),
            Gold * ((.35f + .65f * strength) * pulse), 3);
        if (item.Card is not { FaceDown: false } source) return;
        var physical = matchPresentation.Cards.Cards.LastOrDefault(card => card.Zone == "Battlefield"
            && CardViews.Same(card.Card, source));
        if (physical != null) canvas.CardFocus(physical.Display, Gold * ((.4f + .6f * strength) * pulse));
    }

    private void DrawPublicAnnouncement()
    {
        if (OverlayOpen || readingId != null || expandedStackWorkload.Length > 0 || match?.Decision is { Payload: not PriorityPayload }
            || matchPresentation.Announcement(now) is not { } announcement) return;
        // An ordinary single-entry surface already shows its source and effect.
        // Repeated piles and off-turn actions need a readable current announcement.
        if (!RepeatedStackWorkload && announcement.StackId is { } id && stackRows.Any(row => row.Id == id)) return;
        var area = TableStudyEnabled ? StudyActionArea : new Rectangle(545, 319, 435, 165);
        var heading = new Rectangle(area.X, Math.Max(64, area.Y - 88), area.Width, 28);
        string actor = announcement.ControllerId is { } controller ? StateClarity.PlayerName(match!, controller) : "A player";
        string verb = announcement.EventKind switch { "resolved" => "Resolved", "cancelled" => "Cancelled", "declined" => "Declined",
            "countered" => "Countered", "failed" => "Interrupted", "completed" => "Completed", _ => announcement.Kind switch { StackActionKind.Spell => "Cast", StackActionKind.ActivatedAbility => "Activated",
            StackActionKind.TriggeredAbility => "Triggered", _ => announcement.EventKind switch {
                "cast" => "Cast", "ability" => "Activated", "triggered" => "Triggered", _ => "Action" } } };
        if (announcement.EventKind is not ("resolved" or "cancelled" or "declined" or "countered" or "failed" or "completed")
            && announcement.Kind == StackActionKind.TriggeredAbility && announcement.StackId != match!.Stack.FirstOrDefault()?.Id)
            verb = match.Stack.Any(item => item.Id == announcement.StackId) ? "Pending trigger" : "Earlier trigger";
        if (announcement.CapturedCount > 1) verb = announcement.CapturedCount + " captured \u00b7 " + verb;
        canvas.Text(verb + " · " + actor + " · " + announcement.Name, heading.X, heading.Y, Gold, .65f, heading.Width, 2, true);
        var detail = new Rectangle(heading.X, heading.Bottom + 3, heading.Width, 48);
        canvas.Text(announcement.Text, detail.X, detail.Y, Ink, .62f, detail.Width, 3, true);
        if (announcement.StackId is { } stack && match!.Stack.Any(item => item.Id == stack))
            hits.Add(new("stack-announcement:" + stack, Scope, Rectangle.Union(heading, detail), new MatchIntent.InspectPendingEffect(stack)));
        if (announcement.SourceId.Length > 0 && PendingConnectionCard(announcement.SourceId) is { } physical)
            canvas.CardFocus(physical.Display, Gold);
        if (matchPresentation.IncompleteAnnouncements > 0)
            canvas.Text("Earlier action history is incomplete", heading.X, detail.Bottom + 3, Muted, .46f, heading.Width, 1);
    }
}
