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
    private sealed record PublicAnnouncementPlan(StackArrivalPresentation.Announcement Announcement, string Heading,
        int HeadingHeight, int NameHeight, int DetailHeight, int Height, Rectangle Area);
    private PublicAnnouncementPlan? publicAnnouncementPlan;
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

    private PublicAnnouncementPlan? MeasurePublicAnnouncement(int width)
    {
        if (OverlayOpen || readingId != null || expandedStackWorkload.Length > 0 || match?.Decision is { Payload: not PriorityPayload }
            || matchPresentation.Announcement(now) is not { } announcement) return null;
        // Current work has one reading surface. This applies to repeated and
        // interleaved triggers too; exact occurrence IDs, never names or prose,
        // establish that the announcement belongs to the live stack.
        if (match!.Stack.Any(item => announcement.StackId is { Length: > 0 } id && item.Id == id
                || announcement.ExecutionId.Length > 0 && item.ExecutionId == announcement.ExecutionId)) return null;
        // Identified earlier stack events stay in History while current work
        // needs this space. Their settled board changes still paint in place.
        // Unbound live events retain their separate conservative notice.
        if (match.Stack.Length > 0 && (announcement.StackId is { Length: > 0 }
                || announcement.EventKind is "resolved" or "cancelled" or "declined" or "countered" or "failed" or "completed")) return null;
        string actor = announcement.ControllerId is { } controller ? StateClarity.PlayerName(match!, controller) : "A player";
        string verb = announcement.EventKind switch { "resolved" => "Resolved", "cancelled" => "Cancelled", "declined" => "Declined",
            "countered" => "Countered", "failed" => "Interrupted", "completed" => "Completed", _ => announcement.Kind switch { StackActionKind.Spell => "Cast", StackActionKind.ActivatedAbility => "Activated",
            StackActionKind.TriggeredAbility => "Triggered", _ => announcement.EventKind switch {
                "cast" => "Cast", "ability" => "Activated", "triggered" => "Triggered", _ => "Action" } } };
        if (announcement.EventKind is not ("resolved" or "cancelled" or "declined" or "countered" or "failed" or "completed")
            && announcement.Kind == StackActionKind.TriggeredAbility && announcement.StackId != match!.Stack.FirstOrDefault()?.Id)
            verb = match.Stack.Any(item => item.Id == announcement.StackId) ? "Pending trigger" : "Earlier trigger";
        if (announcement.CapturedCount > 1) verb = announcement.CapturedCount + " captured \u00b7 " + verb;
        string heading = verb + " · " + actor;
        int textWidth = width - 24;
        int headingHeight = canvas.TextSize(heading, .52f, textWidth, 80, true).Y;
        int nameHeight = canvas.TextSize(announcement.Name, .67f, textWidth, 80, true).Y;
        int detailHeight = canvas.TextSize(announcement.Text, .62f, textWidth, 3, true).Y;
        int incompleteHeight = matchPresentation.IncompleteAnnouncements > 0
            ? canvas.TextSize("Earlier action history is incomplete", .46f, textWidth, 1).Y + 3 : 0;
        return new(announcement, heading, headingHeight, nameHeight, detailHeight,
            headingHeight + nameHeight + detailHeight + incompleteHeight + 30, Rectangle.Empty);
    }

    private void DrawPublicAnnouncement()
    {
        var plan = TableStudyEnabled ? publicAnnouncementPlan : MeasurePublicAnnouncement(PendingStackWidth);
        if (plan == null) return;
        var announcement = plan.Announcement;
        var area = plan.Area;
        if (!TableStudyEnabled) {
            var occupied = stackRows.Select(row => HudPaint(row.Bounds)).ToList();
            if (match!.Stack.Length > 0) occupied.Add(new(PendingStackLeft - 2, 281, PendingStackWidth + 6, 42));
            area = PlaceStudyHud(new(PendingStackLeft, 100, PendingStackWidth, plan.Height), occupied, new(12, 64, 1576, 696));
            publicAnnouncementPlan = plan with { Area = area };
        }
        canvas.Panel(area, new(17, 29, 36), Muted * .65f, 5);
        var heading = new Rectangle(area.X + 12, area.Y + 10, area.Width - 24, plan.HeadingHeight);
        canvas.Text(plan.Heading, heading.X, heading.Y, Gold, .52f, heading.Width, 80, true);
        int nameY = heading.Bottom + 3;
        canvas.Text(announcement.Name, heading.X, nameY, Ink, .67f, heading.Width, 80, true);
        var detail = new Rectangle(heading.X, nameY + plan.NameHeight + 5, heading.Width, plan.DetailHeight);
        canvas.Text(announcement.Text, detail.X, detail.Y, Ink, .62f, detail.Width, 3, true);
        // Historical notices are context only. The current stack and its exact
        // references retain all inspection and gameplay authority.
        if (announcement.SourceId.Length > 0 && PendingConnectionCard(announcement.SourceId) is { } physical)
            canvas.CardFocus(physical.Display, Gold);
        if (matchPresentation.IncompleteAnnouncements > 0)
            canvas.Text("Earlier action history is incomplete", heading.X, detail.Bottom + 3, Muted, .46f, heading.Width, 1);
    }
}
