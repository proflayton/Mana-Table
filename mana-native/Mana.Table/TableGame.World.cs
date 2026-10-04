using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    // Commander names are public for the whole match. Retain only this public
    // cosmetic identity when the card changes zones, never a decision handle.
    private readonly Dictionary<int, string> seatPortraits = [];
    private void DrawTableSurface()
    {
        // Neutral material studies, drawn in code. An artist can supply the
        // play-surface texture without baking cards, seating, or UI into it.
        canvas.Gradient(new(0, 0, 1600, 900), new(13, 18, 24), new(7, 11, 16));
        canvas.Glow(new(800, 472), new(1800, 1080), new Color(81, 105, 113) * .23f);
        canvas.Polygon(TableWorld.Rim(-9, 32), Color.Black * .5f);
        canvas.Polygon(TableWorld.Rim(0, 17), new(23, 25, 28));
        canvas.Polygon(TableWorld.Rim(), new(89, 80, 65));
        canvas.Polygon(TableWorld.Rim(5), new(48, 46, 43));
        canvas.Polygon(TableWorld.Rim(24), new(19, 28, 32));
        canvas.Polygon(TableWorld.Rim(28), new(35, 48, 51));
        if (assets.Arena != null) canvas.ProjectedEllipse(assets.Arena, TableWorld.Card(Vector2.Zero, new(1664, 972)), Color.White);
        else {
            canvas.Glow(new(800, 355), new(1450, 710), new Color(126, 149, 145) * .26f);
            // Fine woven marks remain understated at play distance.
            for (int i = 0; i < 650; i++) {
                float x = (i * 173 % 1591) - 795, y = (i * 137 % 887) - 443;
                if (x * x / (810 * 810) + y * y / (464 * 464) > 1) continue;
                var p = TableWorld.Project(new(x, y));
                canvas.Line(p, p + new Vector2(2, .45f), new Color(176, 188, 173) * .04f, 1);
            }
        }
        // One continuous inlay, rather than four independent player panels.
        var inner = TableWorld.Rim(53);
        for (int i = 0; i < inner.Length; i++) canvas.Line(inner[i], inner[(i + 1) % inner.Length], new Color(162, 150, 119) * .25f, 1);
        for (int i = 0; i < 64; i++) {
            float a = i * MathF.Tau / 64, b = (i + 1) * MathF.Tau / 64;
            canvas.Line(TableWorld.Project(new(MathF.Cos(a) * 136, MathF.Sin(a) * 136)), TableWorld.Project(new(MathF.Cos(b) * 136, MathF.Sin(b) * 136)), Gold * .055f, 1);
        }
        foreach (int seat in new[] { 0, 1, 2, 3 }) {
            var p = TableWorld.Project(TableWorld.SeatPoint(seat, 0, seat is 0 or 2 ? 690 : 421));
            var player = seat == 3 ? match!.Viewer : TableLayout.Opponents(match!).ElementAtOrDefault(seat);
            bool active = player?.Id == match!.ActivePlayerId;
            canvas.Glow(p, seat is 0 or 2 ? new(245, 370) : new(620, 230), (active ? Gold : Blue) * (active ? .16f : .035f));
        }
        if (dragging && dragSource?.Zone == "Hand" && match!.Decision?.Intent == DecisionIntent.Priority) {
            var drop = TableWorld.Card(new(0, 230), new(835, 278));
            canvas.Surface(null, drop, Blue * .045f); canvas.CardFrame(drop, Blue * .4f, 2);
        }
    }
    private void DrawOpponent(Player player, int seat)
    {
        DrawWorldRank(player, seat, true); DrawWorldRank(player, seat, false);
        DrawSeatObjects(player, seat);
    }
    private void DrawLocal(Player player)
    {
        DrawWorldRank(player, 3, false); DrawWorldRank(player, 3, true);
        DrawSeatObjects(player, 3);
        heroPositions[player.Id] = TableLayout.Hero(3);
        if (player.Mana.Values.Sum() > 0) {
            int x = 110; foreach (var mana in player.Mana.Where(p => p.Value > 0)) { ManaOrb(mana.Key, mana.Value, new(x, 801)); x += 43; }
        }
    }
    private void DrawWorldRank(Player player, int seat, bool lands)
    {
        var rank = tableScene!.Rank(player.Id, lands);
        foreach (var node in rank.Cards) DrawSceneCard(node);
        if (rank.Total <= rank.Capacity) return;
        string key = rank.Key; int first = rank.First, capacity = rank.Capacity;
        int x = (int)rank.Pager.X, y = (int)rank.Pager.Y;
        Button("rank-prev:" + key, "‹", x - 35, y, 30, () => pages[key] = Math.Max(0, first - capacity), first > 0, height: 26);
        Button("rank-next:" + key, "›", x + 5, y, 30, () => pages[key] = Math.Min(rank.Total - capacity, first + capacity), first + capacity < rank.Total, height: 26);
        canvas.CenterText($"{first + 1}–{Math.Min(first + capacity, rank.Total)} / {rank.Total}", new(x - 50, y + 27, 100, 18), Muted, .43f);
    }
    private void DrawSeatObjects(Player player, int seat)
    {
        bool own = seat == 3, side = seat is 0 or 2;
        foreach (var node in tableScene!.Commanders(player.Id)) {
            canvas.CardFrame(node.Pose with { Size = node.Pose.Size + new Vector2(13) }, Gold * .35f, 1);
            DrawSceneCard(node);
        }
        var commandLink = own ? new Vector2(191, 647) : seat == 2 ? new Vector2(1380, 520) : side ? TableWorld.Project(TableWorld.SeatPoint(seat, -270, 645)) : new Vector2(1100, 156);
        int cx = (int)commandLink.X, cy = (int)commandLink.Y;
        canvas.CenterText("Command " + player.Zone("Command").Count, new(cx - 52, cy, 104, 24), Gold * .85f, .48f);
        hits.Add(new("zone:" + player.Id + ":Command", Scope, new(cx - 56, cy - 3, 112, 30), () => OpenZone(player.Id, "Command")));
        foreach (var pile in tableScene.Piles(player.Id)) DrawWorldPile(player, pile.Zone, pile.Pose, pile.Compact);
    }
    private void DrawWorldPile(Player player, string zone, Pose pose, bool compact = false)
    {
        var value = player.Zone(zone); var top = zone == "Library" ? value.TopCard : value.Cards.LastOrDefault();
        int thickness = value.Count == 0 ? 0 : Math.Clamp(value.Count / 12, 1, 7);
        canvas.Surface(null, pose with { Center = pose.Center + new Vector2(4, 6), Size = pose.Size + new Vector2(4) }, Color.Black * .4f);
        for (int i = 0; i < thickness; i++) {
            var layer = pose with { Center = pose.Center - new Vector2(0, i * 1.5f) };
            canvas.Surface(null, layer, i % 2 == 0 ? new Color(154, 149, 131) : new Color(54, 58, 56));
        }
        pose = pose with { Center = pose.Center - new Vector2(0, thickness * 1.5f) };
        var bounds = pose.Bounds;
        if (compact) {
            var label = new Rectangle((int)pose.Center.X - 42, bounds.Bottom, 84, 19);
            canvas.Rounded(label, new Color(17, 25, 28) * .85f, 3);
            canvas.CenterText((zone == "Graveyard" ? "Grave" : zone) + " " + value.Count, label, Ink, .43f);
            hits.Add(new("zone:" + player.Id + ":" + zone, Scope, new(bounds.X - 4, bounds.Y - 4, bounds.Width + 8, bounds.Height + 24), () => OpenZone(player.Id, zone)));
        } else {
            canvas.Circle(new(pose.Center.X, bounds.Bottom + 2), 12, new Color(17, 25, 28));
            canvas.CenterText(value.Count.ToString(), new((int)pose.Center.X - 15, bounds.Bottom - 10, 30, 24), Ink, .56f, true);
            canvas.CenterText(zone == "Graveyard" ? "Grave" : zone, new((int)pose.Center.X - 38, bounds.Bottom + 17, 76, 18), Muted, .45f);
            hits.Add(new("zone:" + player.Id + ":" + zone, Scope, new(bounds.X - 4, bounds.Y - 4, bounds.Width + 8, bounds.Height + 40), () => OpenZone(player.Id, zone)));
        }
        if (top != null) DrawTableCard("pile:" + player.Id + ":" + zone + ":" + CardIdentity(top), top, pose, zone, player.Id,
            "pile-card:" + player.Id + ":" + zone, () => OpenZone(player.Id, zone), (displayed, focus) => PaintCard(top, displayed, false, false, focus), opensZone: true);
        else PaintBack(pose, value.Count == 0 ? .3f : 1);
    }
    private void DrawHero(Player player, Vector2 center, bool own, int index)
    {
        heroPositions[player.Id] = center;
        bool target = IsPlayerTarget(player.Id), active = match!.ActivePlayerId == player.Id;
        float radius = own ? 40 : 34;
        if (!own) {
            int count = Math.Min(7, player.Zone("Hand").Count);
            int arriving = preferences.ReducedMotion ? 0 : presence.PendingDraws(player.Id, now);
            var handCenter = TableLayout.SeatHand(index);
            for (int i = 0; i < Math.Max(0, count - arriving); i++) {
                var back = TableLayout.HiddenHand(index, i, count);
                canvas.Surface(null, back with { Center = back.Center + new Vector2(3, 6) }, Color.Black * .45f);
                PaintBack(back);
            }
            var handBounds = new Rectangle((int)handCenter.X - 83, (int)handCenter.Y - 30, 166, 75);
            hits.Add(new("hand-zone:" + player.Id, Scope, handBounds, () => OpenZone(player.Id, "Hand")));
            if (index != 1) canvas.CenterText(player.Zone("Hand").Count + " in hand", new((int)handCenter.X - 65, (int)handCenter.Y + 35, 130, 22), Muted, .48f);
        }
        canvas.Glow(center + new Vector2(0, 10), new(radius * 3.8f, radius * 2.5f), Color.Black * .9f);
        if (target || player.Priority || active) canvas.Glow(center, new(radius * 4.7f), (target ? Blue : Gold) * .4f);
        // Layered seat medallions sit on the table rim, like physical life counters.
        canvas.Circle(center + new Vector2(0, 6), radius + 7, new Color(11, 17, 20));
        canvas.Circle(center, radius + 5, target ? Blue : active ? Gold : new Color(119, 115, 100));
        canvas.Circle(center, radius + 3, new Color(36, 41, 42));
        canvas.Circle(center - new Vector2(0, 1), radius, new Color(55, 67, 66));
        DrawSeatImpact(player.Id, center, radius);
        if (player.Zone("Command").Cards.FirstOrDefault(c => !c.FaceDown) is { } commander) seatPortraits.TryAdd(player.Id, commander.ArtName.Length > 0 ? commander.ArtName : commander.Name);
        if (seatPortraits.TryGetValue(player.Id, out var nameOfCommander) && art.Get(new Card { Name = nameOfCommander, ArtName = nameOfCommander }) is { } portrait) {
            canvas.Portrait(portrait, center - new Vector2(0, 1), radius, Color.White * .8f);
            canvas.Circle(center, radius, new Color(11, 20, 24) * .38f);
        }
        canvas.Glow(center - new Vector2(4, 13), new(radius * 2), new Color(169, 178, 159) * .25f);
        if (player.Priority) canvas.Ring(center, radius + 10, target ? Blue : Gold * .8f, 2, -.6f, MathF.PI * 1.35f);
        canvas.CenterText(player.Life.ToString(), new((int)(center.X - radius) + 1, (int)(center.Y - radius) + 2, (int)radius * 2, (int)radius * 2), Color.Black, own ? 1.65f : 1.35f, true);
        canvas.CenterText(player.Life.ToString(), new((int)(center.X - radius), (int)(center.Y - radius), (int)radius * 2, (int)radius * 2), player.Eliminated ? Muted : Ink, own ? 1.65f : 1.35f, true);
        var bounds = new Rectangle((int)center.X - (int)radius - 9, (int)center.Y - (int)radius - 9, (int)radius * 2 + 18, (int)radius * 2 + 18);
        hits.Add(new("player:" + player.Id, Scope, bounds, () => PlayerClick(player.Id), PlayerId: player.Id, Shape: p => Vector2.DistanceSquared(p.ToVector2(), center) <= (radius + 9) * (radius + 9)));
        string name = own ? "YOU" : localMatch ? "AI " + (index + 1) + " · " + player.Name.Split('·')[0].Trim() : player.Name;
        int x = (int)center.X - 94, y = (int)center.Y + (own ? 44 : 41);
        if (own) { x = (int)center.X + 58; y = (int)center.Y - 22; }
        if (!own && index == 1) { x = (int)center.X + 50; y = (int)center.Y - 32; }
        var nameplate = new Rectangle(x, y, 188, 24);
        canvas.CenterText(own ? name : name + " ↗", nameplate, !own && nameplate.Contains(pointer) ? Blue : Ink, .6f, true);
        int incoming = match.Combat?.Attackers.Count(a => a.DefendingPlayerId == player.Id) ?? 0;
        string status = player.Eliminated ? "ELIMINATED" : incoming > 0 ? incoming + (own ? " ATTACKING YOU" : " INCOMING") : player.Priority ? "PRIORITY" : active ? "ACTIVE TURN" : "";
        canvas.CenterText(status, new(x, y + 23, 188, 19), incoming > 0 ? Red : Gold, .43f, true);
        if (!own) hits.Add(new("focus:" + player.Id, Scope, nameplate, () => OpenZone(player.Id, "Battlefield")));
    }
}
