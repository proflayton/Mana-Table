using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private Card? placementCard;
    private string placementScope = "";
    private int placementPage;

    private void OpenHandPlacement(Card card)
    {
        if (match == null || busy || HandPlacements.Options(match, card).Length == 0) return;
        placementCard = card; placementScope = Scope; placementPage = 0;
        zoneName = null; overlay = "hand-placement";
    }
    private void UseHandPlacement(Card card, string abilityId, string scope)
    {
        if (match == null || busy || Scope != scope || HandPlacements.Start(match, card, abilityId) is not { } reply) return;
        overlay = ""; zoneName = null; placementCard = null; Send(reply);
    }
    private bool PlacementDropChoice() => match != null && !OverlayOpen && dragSource is { Zone: "Hand", Card: { Selectable: false } card }
        && dragSource.Scope == Scope && TableWorld.CanPlayAt(pointer) && HandPlacements.Options(match, card).Length > 1;

    private void DrawHandPlacement()
    {
        if (match == null || placementCard == null || Scope != placementScope) return;
        var card = match.Viewer?.Zone("Hand").Cards.FirstOrDefault(c => CardViews.Same(c, placementCard));
        var options = card == null ? [] : HandPlacements.Options(match, card);
        if (card == null || options.Length == 0) return;
        ModalFrame("Place " + card.Name, new MatchIntent.CloseHandPlacement());
        canvas.Text("Choose a source. When it resolves, choose a card from your hand.", 205, 184, Muted, .79f, 1170, 2);
        PaintCard(card, new(new(410, 462), new(284, 398)), false, false);
        canvas.Text(card.Name, 266, 675, Ink, .82f, 300, 2, true);
        const int perPage = 3;
        int page = Math.Clamp(placementPage, 0, Math.Max(0, (options.Length - 1) / perPage));
        int row = 0;
        foreach (var option in options.Skip(page * perPage).Take(perPage)) {
            int y = 251 + row++ * 145;
            canvas.Panel(new(634, y, 735, 131), new(18, 28, 36), new(71, 97, 111), 8);
            canvas.Text(option.SourceName, 651, y + 12, Gold, .9f, 485, 1, true);
            canvas.Text(option.Text, 651, y + 44, Ink, .7f, 689, 3);
            var capturedScope = placementScope;
            Button("placement-use:" + option.Id, "Use this source", 1139, y + 8, 212,
                new MatchIntent.UseHandPlacement(card, option.Id), !busy, true, 33);
        }
        if (options.Length > perPage) {
            Button("placement-prev", "Previous sources", 634, 719, 235, new MatchIntent.SetPage(MatchPage.Placement, page - 1), page > 0);
            Button("placement-next", "More sources", 883, 719, 235, new MatchIntent.SetPage(MatchPage.Placement, page + 1), (page + 1) * perPage < options.Length);
        }
    }

    // These cues expose only the current actor's explicit engine-owned actions.
    // Printed rules text never creates a flashback, escape, or exile permission.
    private string? AvailableZone(Card card)
    {
        if (match is not { Status: "playing", Decision: { Task: DecisionTask.Priority, Id.Length: > 0 }, Viewer.Eliminated: false } state) return null;
        foreach (string zone in new[] { "Graveyard", "Exile", "Command" })
            if (state.Viewer?.Zone(zone).Cards.Any(c => CardViews.Same(c, card) && !c.FaceDown && c.Selectable && c.Key.Length > 0) == true) return zone;
        return null;
    }
    private int AvailableZoneCount(Player player, string zone) => match != null && player.Id == match.ViewerId
        ? player.Zone(zone).Cards.Count(c => AvailableZone(c) == zone) : 0;
    private void DrawZoneActionBadge(Player player, string zone, Pose pose)
    {
        int count = AvailableZoneCount(player, zone);
        if (count == 0) return;
        var badge = new Rectangle((int)pose.Center.X - 43, pose.Bounds.Top - 14, 86, 23);
        canvas.Panel(badge, new(15, 41, 54), ActionAccent, 4);
        canvas.CenterText(count + " available", badge, ActionAccent, .49f, true);
        hits.Add(new("zone-actions:" + player.Id + ":" + zone, Scope, badge, new MatchIntent.OpenZone(player.Id, zone)));
    }
}
