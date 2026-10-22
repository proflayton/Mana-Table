using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly ActionFeedback actionFeedback = new();
    private readonly ImpactFeedback impactFeedback = new();
    private ResolutionCue? inspectedAction;
    private sealed record Departure(Card Card, Pose Pose, string Destination, double Started);
    private readonly List<Departure> departures = [];

    private void ObserveActionFeedback(GameSnapshot? before, GameSnapshot after)
    {
        actionFeedback.Observe(before, after, now);
        impactFeedback.Observe(before, after, now);
        departures.RemoveAll(d => now - d.Started > 1.5 || !CardViews.Located(after).Any(c => c.Card.VisualId == d.Card.VisualId && !c.Card.FaceDown && c.Zone is "Graveyard" or "Exile"));
        if (before == null || before.Id != after.Id) { departures.Clear(); return; }
        foreach (var card in before.Players.SelectMany(p => p.Zone("Battlefield").Cards).Where(c => !c.FaceDown && c.VisualId.Length > 0)) {
            var destination = CardViews.Located(after).FirstOrDefault(c => c.Card.VisualId == card.VisualId && !c.Card.FaceDown);
            // Keep no ghost face when a card enters a hidden zone or loses its public identity.
            if (destination.Card == null || destination.Zone is not ("Graveyard" or "Exile") || !cardPresentation.TryGetPose(card.VisualId, out var pose)) continue;
            departures.RemoveAll(d => d.Card.VisualId == card.VisualId);
            var entry = cardPresentation.Cards.LastOrDefault(entry => entry.Id == card.VisualId);
            var origin = entry?.Focused == true ? entry.Rest : pose;
            departures.Add(new(card, (entry?.View ?? boardView).Inverse(origin), destination.Zone, now));
        }
    }
    private void DrawDepartures()
    {
        foreach (var departure in departures) {
            double age = now - departure.Started;
            if (age > 1.5) continue;
            float alpha = preferences.ReducedMotion ? .6f : (float)(1 - age / 1.5);
            var pose = DeparturePose(departure);
            if (resources.Card(departure.Card) is { } image) canvas.Surface(image, pose, Color.White * (.8f * alpha));
            else {
                canvas.Surface(null, pose, new Color(22, 34, 40) * alpha);
                int width = Math.Max(1, pose.Bounds.Width - 6);
                if (departure.Card.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(word => canvas.TextWidth(word, .5f, true) <= width)
                    && canvas.TextSize(departure.Card.Name, .5f, width, 80, true).Y <= pose.Bounds.Height - 6)
                    canvas.Text(departure.Card.Name, pose.Bounds.X + 3, pose.Bounds.Y + 3, Ink * alpha, .5f, width, 80, true);
            }
            canvas.CardFrame(pose, Red * alpha, 3);
        }
    }
    private Pose DeparturePose(Departure departure)
    {
        var origin = boardView.Apply(departure.Pose);
        var owner = match?.Players.FirstOrDefault(player => player.Zone(departure.Destination).Cards.Any(card => card.VisualId == departure.Card.VisualId));
        var pile = tableScene?.Seats.FirstOrDefault(seat => seat.PlayerId == owner?.Id)?.Geometry.Piles.FirstOrDefault(pile => pile.Zone == departure.Destination);
        if (pile == null) return origin;
        var destination = pile.Pose;
        float progress = preferences.ReducedMotion ? 1 : Math.Clamp((float)((now - departure.Started) / .65), 0, 1);
        progress = progress * progress * (3 - 2 * progress);
        return origin with { Center = Vector2.Lerp(origin.Center, destination.Center, progress),
            Size = Vector2.Lerp(origin.Size, destination.Size, progress), Angle = MathHelper.Lerp(origin.Angle, destination.Angle, progress) };
    }
    private void DrawActionCallout()
    {
        if (AbilityActionPending || SelectingActionTarget) return;
        if (DrawDecisionClarity(match!)) return;
        // The pending-effect surface already owns the reported effect, target
        // connections and responder. Do not repeat it in a second callout.
        if (match!.Stack.Length > 0) return;
        if (match!.Stack.Length == 0 && publicChanges.Latest is { } change && now - change.Updated < 8) return;
        var recent = actionFeedback.Last;
        // Ordinary success is visible at the recipients and in their settled
        // state. Keep prose in History instead of covering the table with a
        // second, detached result panel. A failed resolution still needs notice.
        if (recent?.Outcome != "notResolved") return;
        bool resolving = recent != null && now - recent.Started < 5 && (match!.Stack.Length == 0 || now - recent.Started < 2.6);
        var next = match!.Stack.FirstOrDefault();
        if (!resolving && next == null) return;
        string title = resolving ? recent!.Title : next!.Name;
        string detail = resolving ? recent!.Changes.FirstOrDefault() ?? recent.Detail
            : !next!.Ability && next.Card is { Text.Length: > 0 } spell ? spell.Text : next.Text;
        if (!resolving && next is { ControllerId: not null }) detail = StateClarity.StackController(match, next) + "\n" + detail;
        string targets = resolving ? (recent!.Changes.Length > 1 ? recent.Changes[1] : "")
            : next!.Targets.Length == 0 ? "" : "Target: " + string.Join(", ", next.Targets.Select(t => t.Name));
        canvas.Panel(new(20, 66, 379, 175), new Color(15, 24, 32) * .97f, resolving ? Gold : Red, 7);
        canvas.Text(resolving ? recent!.Heading : "NEXT TO RESOLVE", 34, 77, resolving ? Gold : Red, .55f, 342, 1, true);
        canvas.Text(title, 34, 101, Ink, .78f, 350, 2, true);
        canvas.Text(detail, 34, 143, Ink, .63f, 350, 3);
        canvas.Text(targets, 34, 201, resolving ? Gold : Red, .6f, 275, 2);
        Button("action-details", "Details", 304, 208, 82, () => {
            if (resolving) { inspectedAction = recent; inspectPage = 0; overlay = "action"; }
            else OpenStack();
        }, height: 25);
    }
    private void DrawActionDetails()
    {
        if (inspectedAction == null) { overlay = ""; return; }
        ModalFrame(inspectedAction.Title, () => overlay = "");
        var pages = TextPages(inspectedAction.Detail + "\n\nRecent table changes\n" + string.Join("\n", inspectedAction.Changes), 1800);
        inspectPage = Math.Clamp(inspectPage, 0, pages.Length - 1);
        canvas.Text(pages[inspectPage], 215, 198, Ink, .9f, 1150, 19);
        Button("action-prev", "Previous", 215, 734, 145, () => inspectPage--, inspectPage > 0);
        Button("action-next", "More", 372, 734, 145, () => inspectPage++, inspectPage + 1 < pages.Length);
        Button("action-history", "Match history", 1090, 734, 275, () => { overlay = "history"; historyPage = 0; });
    }
}
