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
    private readonly PriorityNotification priorityNotification = new();

    private void UpdatePriorityNotification()
    {
        bool waiting = false;
        if (!busy && !session.IsAdvancing && !session.NeedsRecovery && !QuietPriorityArmed && !ResolvingCapturedStack
            && !ExperienceLabOpen && fixture == null && match is { Status: "playing", Turn: > 0, Viewer.Eliminated: false,
                Decision: { Payload: PriorityPayload { Pass.Enabled: true } } decision } state) {
            var view = DecisionDockPresentation.From(state, CurrentDecisionControls());
            waiting = view is { Status: DecisionDockStatus.Priority, Surface: DecisionDockSurface.Portrait }
                && PriorityPresentation.From(state).CanPass
                && GameCommandPermissions.Allows(state, new PassPriority(state.Id, decision.Id));
        }
        if (priorityNotification.Update(match, waiting, preferences.Sounds, now)) resources.Play("your-priority", .24f);
    }

    private void DrawPriorityAlertControl(TurnControlLayout layout)
    {
        var center = layout.AlertCenter;
        bool muted = priorityNotification.IsMuted(now), hover = IsControlHovered("priority-alerts");
        var color = !preferences.Sounds ? Muted * .45f : hover ? Ink : muted ? Gold : Muted;
        canvas.Circle(center + new Vector2(0, 2), 13, Color.Black * .65f);
        canvas.Circle(center, 12, hover ? new(36, 57, 65) : new(13, 24, 30));
        Vector2[] bell = [new(-7, 5), new(-5, 2), new(-5, -3), new(-3, -6), new(3, -6), new(5, -3), new(5, 2), new(7, 5), new(-7, 5)];
        for (int i = 1; i < bell.Length; i++) canvas.Line(center + bell[i - 1], center + bell[i], color, 1.5f);
        canvas.Line(center + new Vector2(0, -9), center + new Vector2(0, -7), color, 1.5f);
        canvas.Circle(center + new Vector2(0, 8), 1.5f, color);
        if (muted || !preferences.Sounds) canvas.Line(center + new Vector2(-9, -9), center + new Vector2(9, 9), color, 2);
        var intent = new MatchIntent.TogglePriorityAlerts();
        if (preferences.Sounds) hits.Add(new("priority-alerts", ScopeFor(intent), layout.AlertBounds, intent,
            Shape: point => Vector2.DistanceSquared(point.ToVector2(), center) <= 14 * 14));
        if (hover) DrawTurnHint(muted ? "Unmute priority alerts · " + TimeSpan.FromSeconds(Math.Max(0, priorityNotification.MutedUntil - now)).ToString(@"m\:ss") + " left"
            : "Mute priority alerts for 5 minutes", center);
    }
}
