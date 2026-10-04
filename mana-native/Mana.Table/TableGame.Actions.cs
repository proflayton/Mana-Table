using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private readonly ActionFeedback actionFeedback = new();
    private ResolutionCue? inspectedAction;
    private sealed record Departure(Card Card, Pose Pose, string Destination, double Started);
    private readonly List<Departure> departures = [];

    private void ObserveActionFeedback(GameSnapshot? before, GameSnapshot after)
    {
        actionFeedback.Observe(before, after, now);
        departures.RemoveAll(d => now - d.Started > 1.5 || !CardViews.Located(after).Any(c => c.Card.VisualId == d.Card.VisualId && !c.Card.FaceDown && c.Zone is "Graveyard" or "Exile"));
        if (before == null || before.Id != after.Id) { departures.Clear(); return; }
        foreach (var card in before.Players.SelectMany(p => p.Zone("Battlefield").Cards).Where(c => !c.FaceDown && c.VisualId.Length > 0)) {
            var destination = CardViews.Located(after).FirstOrDefault(c => c.Card.VisualId == card.VisualId && !c.Card.FaceDown);
            // Keep no ghost face when a card enters a hidden zone or loses its public identity.
            if (destination.Card == null || destination.Zone is not ("Graveyard" or "Exile") || !motion.TryGet(card.VisualId, out var pose)) continue;
            departures.RemoveAll(d => d.Card.VisualId == card.VisualId);
            departures.Add(new(card, pose, destination.Zone, now));
        }
    }
    private void DrawDepartures()
    {
        foreach (var departure in departures) {
            double age = now - departure.Started;
            if (age > 1.5) continue;
            float alpha = preferences.ReducedMotion ? .6f : (float)(1 - age / 1.5);
            var pose = departure.Pose;
            if (art.Get(departure.Card) is { } image) canvas.Surface(image, pose, Color.White * (.55f * alpha));
            canvas.CardFrame(pose, Red * alpha, 3);
            var label = new Rectangle((int)pose.Center.X - 82, (int)pose.Center.Y - 15, 164, 30);
            canvas.Panel(label, new Color(34, 19, 23) * alpha, Red * alpha, 4);
            canvas.CenterText(departure.Destination == "Exile" ? "EXILED" : "TO GRAVEYARD", label, Ink * alpha, .61f, true);
        }
    }
    private void DrawStackTargets(StackItem item, Pose source)
    {
        foreach (var target in item.Targets) {
            Vector2? end = null;
            if (target.Kind == "card" && motion.TryGet(target.Id, out var card)) {
                end = card.EdgeToward(source.Center, 8);
                canvas.CardFrame(card with { Size = card.Size + new Vector2(10) }, Red, 3);
                var badge = new Rectangle((int)card.Center.X - 48, card.Bounds.Top - 22, 96, 23);
                canvas.Panel(badge, new(43, 24, 27), Red, 4); canvas.CenterText("TARGETED", badge, Ink, .49f, true);
            } else if (target.PlayerId is { } player && heroPositions.TryGetValue(player, out var hero)) end = hero;
            if (end is { } point) canvas.Arrow(source.EdgeToward(point, 5), point, Red * .8f, 11);
        }
    }
    private void DrawActionCallout()
    {
        var recent = actionFeedback.Last;
        bool resolving = recent != null && now - recent.Started < 5 && (match!.Stack.Length == 0 || now - recent.Started < 2.6);
        var next = match!.Stack.FirstOrDefault();
        if (!resolving && next == null) return;
        string title = resolving ? recent!.Title : next!.Name;
        string detail = resolving ? recent!.Changes.FirstOrDefault() ?? recent.Detail
            : !next!.Ability && next.Card is { Text.Length: > 0 } spell ? spell.Text : next.Text;
        string targets = resolving ? (recent!.Changes.Length > 1 ? recent.Changes[1] : "")
            : next!.Targets.Length == 0 ? "" : "Target: " + string.Join(", ", next.Targets.Select(t => t.Name));
        canvas.Panel(new(20, 66, 379, 175), new Color(15, 24, 32) * .97f, resolving ? Gold : Red, 7);
        canvas.Text(resolving ? "JUST RESOLVED" : "NEXT TO RESOLVE", 34, 77, resolving ? Gold : Red, .55f, 342, 1, true);
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
