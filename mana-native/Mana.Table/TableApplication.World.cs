using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    // Commander names are public for the whole match. Retain only this public
    // cosmetic identity when the card changes zones, never a decision handle.
    private readonly Dictionary<int, string> seatPortraits = [];
    private readonly Dictionary<(int Player, string Kind), Rectangle> battlefieldRoleLabels = [];
    private void DrawTableSurface()
    {
        manaPoolBadges.Clear(); manaPoolDetail = null; battlefieldRoleLabels.Clear(); denseStatsBounds.Clear();
        // Neutral material studies, drawn in code. An artist can supply the
        // play-surface texture without baking cards, seating, or UI into it.
        canvas.Gradient(new(0, 0, 1600, 900), new(13, 18, 24), new(7, 11, 16));
        canvas.Glow(new(800, 472), new(1800, 1080), new Color(81, 105, 113) * .23f);
        Vector2[] Rim(float inset = 0, float drop = 0) => TableWorld.Rim(inset, drop).Select(point => boardView.Apply(point)).ToArray();
        canvas.Polygon(Rim(-9, 32), Color.Black * .5f);
        canvas.Polygon(Rim(0, 17), new(23, 25, 28));
        canvas.Polygon(Rim(), new(89, 80, 65));
        canvas.Polygon(Rim(5), new(48, 46, 43));
        canvas.Polygon(Rim(24), new(19, 28, 32));
        canvas.Polygon(Rim(28), new(35, 48, 51));
        if (resources.Arena != null) canvas.ProjectedEllipse(resources.Arena, boardView.Apply(TableWorld.Card(Vector2.Zero, new(1664, 972))), Color.White);
        else {
            canvas.Glow(new(800, 355), new(1450, 710), new Color(126, 149, 145) * .26f);
            // Fine woven marks remain understated at play distance.
            for (int i = 0; i < 650; i++) {
                float x = (i * 173 % 1591) - 795, y = (i * 137 % 887) - 443;
                if (x * x / (810 * 810) + y * y / (464 * 464) > 1) continue;
                var p = boardView.Apply(TableWorld.Project(new(x, y)));
                canvas.Line(p, p + new Vector2(2, .45f), new Color(176, 188, 173) * .04f, 1);
            }
        }
        // One continuous inlay, rather than four independent player panels.
        var inner = Rim(53);
        for (int i = 0; i < inner.Length; i++) canvas.Line(inner[i], inner[(i + 1) % inner.Length], new Color(162, 150, 119) * .25f, 1);
        for (int i = 0; i < 64; i++) {
            float a = i * MathF.Tau / 64, b = (i + 1) * MathF.Tau / 64;
            canvas.Line(boardView.Apply(TableWorld.Project(new(MathF.Cos(a) * 136, MathF.Sin(a) * 136))), boardView.Apply(TableWorld.Project(new(MathF.Cos(b) * 136, MathF.Sin(b) * 136))), Gold * .055f, 1);
        }
        foreach (int seat in new[] { 0, 1, 2, 3 }) {
            var p = boardView.Apply(TableWorld.Project(TableWorld.SeatPoint(seat, 0, seat is 0 or 2 ? 690 : 421)));
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
        DrawWorldBattlefield(player);
        DrawSeatObjects(player);
    }
    private void DrawLocal(Player player)
    {
        DrawWorldBattlefield(player);
        DrawSeatObjects(player);
        heroPositions[player.Id] = TableLayout.Hero(3);
    }
    private void DrawWorldBattlefield(Player player)
    {
        var plan = tableScene!.Seat(player.Id);
        int seat = plan.Index;
        var clusters = plan.Battlefield;
        bool dense = plan.IsDense;
        if (dense) {
            var area = BattlefieldLayout.DenseTerritory(seat);
            int right = area.Right - 4;
            foreach (var cluster in clusters.Reverse()) {
                string text = cluster.Kind.ToUpperInvariant() + " " + cluster.Cards.Length;
                int width = canvas.TextWidth(text, .375f, true) + 10;
                var caption = new Rectangle(right - width, area.Top, width - 2, 19);
                canvas.Panel(caption, new Color(15, 24, 29) * .95f, new Color(88, 107, 112), 3);
                canvas.Text(text, caption.X + 4, caption.Y + 1, Muted, .375f, caption.Width - 8, 1, true);
                battlefieldRoleLabels[(player.Id, cluster.Kind)] = caption;
                right -= width;
            }
            int incoming = match!.Combat?.Attackers.Count(attack => attack.DefendingPlayerId == player.Id) ?? 0;
            string status = player.Eliminated ? "ELIMINATED" : incoming > 0 ? incoming + (seat == 3 ? " ATTACKING YOU" : " INCOMING")
                : player.Priority ? "PRIORITY" : player.Id == match.ActivePlayerId ? "ACTIVE TURN" : "";
            int statusWidth = status.Length > 0 ? canvas.TextWidth(status, .375f, true) + 10 : 0;
            if (statusWidth > 0) {
                var caption = new Rectangle(right - statusWidth, area.Top, statusWidth - 2, 19);
                canvas.Panel(caption, new Color(15, 24, 29) * .95f, incoming > 0 ? Red : Gold, 3);
                canvas.Text(status, caption.X + 4, caption.Y + 1, incoming > 0 ? Red : Gold, .375f, caption.Width - 8, 1, true);
                right -= statusWidth;
            }
            var identity = new Rectangle(area.Left + 4, area.Top, Math.Max(1, right - area.Left - 8), 19);
            string owner = seat == 3 ? "YOU" : player.Name;
            canvas.Panel(identity, new Color(15, 24, 29) * .95f, Blue, 3);
            canvas.Text(owner, identity.X + 4, identity.Y + 1, Ink, .375f, Math.Max(1, identity.Width - 8), 1, true);
            hits.Add(new("focus:" + player.Id, Scope, identity, () => OpenZone(player.Id, "Battlefield")));
        }
        foreach (var cluster in clusters) {
            if (!dense && cluster.Cards.Length > 0) DrawBattlefieldRole(player.Id, seat, cluster);
            foreach (var node in cluster.Cards) DrawSceneCard(node);
        }
    }
    private Rectangle BattlefieldRoleBounds(int seat, BattlefieldCluster cluster)
    {
        var bounds = cluster.Cards.Select(card => card.Pose.Bounds).Aggregate(Rectangle.Union);
        string label = cluster.Kind.ToUpperInvariant() + "  " + cluster.Cards.Length;
        int width = Math.Max(110, canvas.TextWidth(label, .44f, true) + 14);
        int x = seat == 3 ? Math.Clamp(bounds.Left, 170, 1238 - width)
            : seat == 1 ? Math.Clamp(bounds.Left, 170, 1400 - width)
            : Math.Clamp(bounds.Center.X - width / 2, 175, 1425 - width);
        int y = seat == 1 ? bounds.Bottom + 5 : bounds.Top - 48;
        y = Math.Clamp(y, 70, 690);
        return new(x, y, width, 19);
    }
    private void DrawBattlefieldRole(int player, int seat, BattlefieldCluster cluster)
    {
        string label = cluster.Kind.ToUpperInvariant() + "  " + cluster.Cards.Length;
        var caption = boardView.Apply(BattlefieldRoleBounds(seat, cluster));
        canvas.Panel(caption, new Color(15, 24, 29) * .9f, new Color(88, 107, 112) * .7f, 3);
        canvas.Text(label, caption.X + 7, caption.Y + 2, Muted, .44f, caption.Width - 14, 1, true);
        battlefieldRoleLabels[(player, cluster.Kind)] = caption;
    }
    private void DrawSeatObjects(Player player)
    {
        var geometry = tableScene!.Seat(player.Id).Geometry;
        foreach (var node in geometry.Commanders) {
            canvas.CardFrame(node.Pose with { Size = node.Pose.Size + new Vector2(13) }, Gold * .35f, 1);
            DrawSceneCard(node);
        }
        var commandLink = geometry.CommandLink;
        int cx = (int)commandLink.X, cy = (int)commandLink.Y;
        canvas.CenterText("Command " + player.Zone("Command").Count, new(cx - 52, cy, 104, 24), Gold * .85f, .48f);
        hits.Add(new("zone:" + player.Id + ":Command", Scope, new(cx - 56, cy - 3, 112, 30), () => OpenZone(player.Id, "Command")));
        if (AvailableZoneCount(player, "Command") is > 0 and var available) {
            var badge = new Rectangle(cx - 51, cy + 25, 102, 22);
            canvas.Panel(badge, new(15, 41, 54), ActionAccent, 4);
            canvas.CenterText(available + " available", badge, ActionAccent, .5f, true);
            hits.Add(new("zone-actions:" + player.Id + ":Command", Scope, badge, () => OpenZone(player.Id, "Command")));
        }
        foreach (var pile in geometry.Piles) DrawWorldPile(player, pile.Zone, pile.Pose, pile.Compact);
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
            hits.Add(new("zone:" + player.Id + ":" + zone, Scope, Rectangle.Union(bounds, label), () => OpenZone(player.Id, zone),
                Shape: point => pose.Contains(point) || label.Contains(point)));
        } else {
            canvas.Circle(new(pose.Center.X, bounds.Bottom + 2), 12, new Color(17, 25, 28));
            canvas.CenterText(value.Count.ToString(), new((int)pose.Center.X - 15, bounds.Bottom - 10, 30, 24), Ink, .56f, true);
            canvas.CenterText(zone == "Graveyard" ? "Grave" : zone, new((int)pose.Center.X - 38, bounds.Bottom + 17, 76, 18), Muted, .45f);
            var countBounds = new Rectangle((int)pose.Center.X - 15, bounds.Bottom - 10, 30, 24);
            var labelBounds = new Rectangle((int)pose.Center.X - 38, bounds.Bottom + 17, 76, 18);
            hits.Add(new("zone:" + player.Id + ":" + zone, Scope, Rectangle.Union(Rectangle.Union(bounds, countBounds), labelBounds), () => OpenZone(player.Id, zone),
                Shape: point => pose.Contains(point) || countBounds.Contains(point) || labelBounds.Contains(point)));
        }
        if (top != null) DrawTableCard("pile:" + player.Id + ":" + zone + ":" + CardIdentity(top), top, pose, zone, player.Id,
            "pile-card:" + player.Id + ":" + zone, () => OpenZone(player.Id, zone), (displayed, focus) => PaintCard(top, displayed, false, false, focus), opensZone: true);
        else PaintBack(pose, value.Count == 0 ? .3f : 1);
        DrawZoneActionBadge(player, zone, pose);
    }
    private void DrawHero(Player player, Vector2 center, bool own, int index)
    {
        heroPositions[player.Id] = center;
        bool target = IsPlayerTarget(player.Id), active = match!.ActivePlayerId == player.Id;
        bool hasCounters = StateClarity.PlayerStates(player).Length > 0;
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
            if (index != 1 && !hasCounters) canvas.CenterText(player.Zone("Hand").Count + " in hand", new((int)handCenter.X - 65, (int)handCenter.Y + 35, 130, 22), Muted, .48f);
        }
        canvas.Glow(center + new Vector2(0, 10), new(radius * 3.8f, radius * 2.5f), Color.Black * .9f);
        if (target || active) canvas.Glow(center, new(radius * 4.7f), (target ? Blue : Gold) * .4f);
        // Layered seat medallions sit on the table rim, like physical life counters.
        canvas.Circle(center + new Vector2(0, 6), radius + 7, new Color(11, 17, 20));
        canvas.Circle(center, radius + 5, target ? Blue : active ? Gold : new Color(119, 115, 100));
        canvas.Circle(center, radius + 3, new Color(36, 41, 42));
        canvas.Circle(center - new Vector2(0, 1), radius, new Color(55, 67, 66));
        DrawSeatImpact(player.Id, center, radius);
        if (player.Zone("Command").Cards.FirstOrDefault(c => !c.FaceDown && c.GamePieceType != "effect") is { } commander)
            seatPortraits.TryAdd(player.Id, commander.ArtName.Length > 0 ? commander.ArtName : commander.Name);
        if (seatPortraits.TryGetValue(player.Id, out var nameOfCommander) && resources.Card(new Card { Name = nameOfCommander, ArtName = nameOfCommander }) is { } portrait) {
            canvas.Portrait(portrait, center - new Vector2(0, 1), radius, Color.White * .8f);
            canvas.Circle(center, radius, new Color(11, 20, 24) * .38f);
        }
        canvas.Glow(center - new Vector2(4, 13), new(radius * 2), new Color(169, 178, 159) * .25f);
        DrawPrioritySeat(player, center, radius);
        canvas.CenterText(player.Life.ToString(), new((int)(center.X - radius) + 1, (int)(center.Y - radius) + 2, (int)radius * 2, (int)radius * 2), Color.Black, own ? 1.65f : 1.35f, true);
        canvas.CenterText(player.Life.ToString(), new((int)(center.X - radius), (int)(center.Y - radius), (int)radius * 2, (int)radius * 2), player.Eliminated ? Muted : Ink, own ? 1.65f : 1.35f, true);
        var bounds = new Rectangle((int)center.X - (int)radius - 9, (int)center.Y - (int)radius - 9, (int)radius * 2 + 18, (int)radius * 2 + 18);
        hits.Add(new("player:" + player.Id, Scope, bounds, () => PlayerClick(player.Id), PlayerId: player.Id, Shape: p => Vector2.DistanceSquared(p.ToVector2(), center) <= (radius + 9) * (radius + 9)));
        string name = own ? "YOU" : localMatch && !ExperienceLabOpen ? "AI " + (index + 1) + " · " + player.Name.Split('·')[0].Trim() : player.Name;
        int x = (int)center.X - 94, y = (int)center.Y + (own ? 44 : 41);
        if (own) { x = (int)center.X + 58; y = (int)center.Y - 22; }
        if (!own && index == 1) { x = (int)center.X + 50; y = (int)center.Y - 32; }
        var nameplate = new Rectangle(x, y, 188, 24);
        var manaHalo = PlanManaHalo(player, own ? 3 : index);
        if (own) { x = Math.Max((int)center.X + 58, manaHalo.Bounds.Right) + 8; nameplate = new(x, y, 100, 24); }
        else if (index == 1) { x = Math.Max((int)center.X + 50, manaHalo.Bounds.Right) + 8; y = 64; nameplate = new(x, y, 188, 24); }
        bool dense = tableScene!.Seat(player.Id).IsDense;
        if (!dense) canvas.CenterText(own ? name : name + " ↗", nameplate, !own && nameplate.Contains(pointer) ? Blue : Ink, .6f, true);
        int incoming = match.Combat?.Attackers.Count(a => a.DefendingPlayerId == player.Id) ?? 0;
        string status = player.Eliminated ? "ELIMINATED" : incoming > 0 ? incoming + (own ? " ATTACKING YOU" : " INCOMING") : player.Priority ? "PRIORITY" : active ? "ACTIVE TURN" : "";
        if (!own && index != 1 && hasCounters) status += (status.Length > 0 ? " · " : "") + player.Zone("Hand").Count + " in hand";
        if (!dense) {
            if (own) canvas.Text(status, x, y + 23, incoming > 0 ? Red : Gold, .43f, 100, (int)Math.Ceiling(2 * preferences.TextScale), true);
            else canvas.CenterText(status, new(x, y + 23, 188, 19), incoming > 0 ? Red : Gold, .43f, true);
        }
        var permanents = player.Zone("Battlefield").Cards;
        int creatures = permanents.Count(c => CardPresentation.HasCombatStats(c) && !c.PhasedOut);
        int phased = permanents.Count(c => CardPresentation.HasCombatStats(c) && c.PhasedOut);
        int unknown = permanents.Count(c => c.FaceDown && c.Type.Length == 0);
        int countX = own ? x + 110 : x, countY = own ? y : y + 43, countWidth = own ? 140 : 188;
        if (!dense) canvas.CenterText($"{creatures} " + (creatures == 1 ? "CREATURE" : "CREATURES"), new(countX, countY, countWidth, 22), Ink, .72f, true);
        string secondary = phased > 0 || unknown > 0
            ? string.Join(" · ", new[] { phased > 0 ? $"{phased} phased out" : "", unknown > 0 ? $"{unknown} unknown" : "" }.Where(v => v.Length > 0))
            : $"{permanents.Length} permanents";
        if (!dense) canvas.CenterText(secondary, new(countX, countY + 22, countWidth, 18), Muted, .51f);
        if (hasCounters) {
            int counterX = own ? (int)center.X - 240 : (int)center.X - 94;
            int counterY = own ? (int)center.Y - 22 : index == 1 ? (int)center.Y + 35 : (int)center.Y - 64;
            if (own) counterX = Math.Min(counterX, manaHalo.Bounds.Left - 196);
            else if (index == 1) counterY = Math.Max(counterY, manaHalo.Bounds.Bottom + 8);
            DrawPublicPlayerCounters(player, counterX, counterY, index == 1 && !own ? 144 : 188, growUp: !own && index != 1);
        }
        DrawPlayerMana(player, manaHalo);
        if (!own && !dense) hits.Add(new("focus:" + player.Id, Scope, nameplate, () => OpenZone(player.Id, "Battlefield")));
    }
}
