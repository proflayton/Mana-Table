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
    // Commander names are public for the whole match. Retain only this public
    // cosmetic identity when the card changes zones, never a decision handle.
    private readonly Dictionary<int, string> seatPortraits = [];
    private readonly List<Rectangle> seatReadoutBounds = new(12);
    private sealed record SeatAuxiliary(Player Player, Vector2 Center, bool Own, int Index, bool HasCounters,
        ManaHaloPlan Mana, string? Caption, int Incoming, Rectangle StatusArea);
    private void DrawTableSurface()
    {
        manaPoolBadges.Clear(); manaPoolDetail = null; battlefieldStatsBounds.Clear(); seatReadoutBounds.Clear();
        // Neutral material studies, drawn in code. An artist can supply the
        // play-surface texture without baking cards, seating, or UI into it.
        canvas.Gradient(new(0, 0, 1600, 900), new(13, 18, 24), new(7, 11, 16));
        canvas.Glow(new(800, 472), new(1800, 1080), new Color(81, 105, 113) * .23f);
        if (TableStudyEnabled) {
            DrawPlaymatTable();
            DrawHandPlayArea();
            return;
        }
        Vector2[] Rim(float inset = 0, float drop = 0) => TableWorld.Rim(inset, drop).Select(point => boardView.Apply(point)).ToArray();
        canvas.Polygon(Rim(-9, 32), Color.Black * .5f);
        canvas.Polygon(Rim(0, 17), new(23, 25, 28));
        canvas.Polygon(Rim(), new(89, 80, 65));
        canvas.Polygon(Rim(5), new(48, 46, 43));
        canvas.Polygon(Rim(24), new(19, 28, 32));
        canvas.Polygon(Rim(28), new(35, 48, 51));
        if (resources.Arena != null) canvas.ProjectedEllipse(resources.Arena, boardView.Apply(TableWorld.Card(Vector2.Zero, new(1664, 972))), Color.White);
        else {
            canvas.Glow(boardView.Apply(new Vector2(800, 355)), new Vector2(1450, 710) * boardView.Scale, new Color(126, 149, 145) * .26f);
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
        foreach (var sceneSeat in TwoFieldTable ? Array.Empty<SceneSeat>() : tableScene!.Seats) {
            int seat = sceneSeat.Index;
            var p = boardView.Apply(TableWorld.Project(TableWorld.SeatPoint(seat, 0, seat is 0 or 2 ? 690 : 421)));
            bool active = sceneSeat.PlayerId == match!.ActivePlayerId;
            canvas.Glow(p, (seat is 0 or 2 ? new Vector2(245, 370) : new Vector2(620, 230)) * boardView.Scale, (active ? Gold : Blue) * (active ? .16f : .035f));
        }
        if (TwoFieldTable) {
            canvas.Glow(new(740, 330), new(1100, 400), Blue * .08f);
            canvas.Glow(new(740, 590), new(1100, 260), Gold * .04f);
        }
        DrawHandPlayArea();
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
        heroPositions[player.Id] = SeatHero(player.Id);
    }
    private void DrawWorldBattlefield(Player player)
    {
        canvas.BeginClip(BoardViewport);
        var plan = tableScene!.Seat(player.Id);
        var clusters = plan.Battlefield;
        if (TableStudyEnabled) foreach (var family in clusters.SelectMany(cluster => cluster.Cards)
            .Select(node => node.StudyFamily).OfType<StudyBattlefieldFamily>().DistinctBy(family => family.Id))
                DrawStudyFamily(family);
        if (!TableStudyEnabled) foreach (var cluster in clusters)
            foreach (var node in cluster.Cards) DrawSceneCard(node);
        canvas.EndClip();
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
        hits.Add(new("zone:" + player.Id + ":Command", Scope, new(cx - 56, cy - 3, 112, 30), new MatchIntent.OpenZone(player.Id, "Command")));
        if (AvailableZoneCount(player, "Command") is > 0 and var available) {
            var badge = new Rectangle(cx - 51, cy + 25, 102, 22);
            canvas.Panel(badge, new(15, 41, 54), ActionAccent, 4);
            canvas.CenterText(available + " available", badge, ActionAccent, .5f, true);
            hits.Add(new("zone-actions:" + player.Id + ":Command", Scope, badge, new MatchIntent.OpenZone(player.Id, "Command")));
        }
        foreach (var pile in geometry.Piles) DrawWorldPile(player, pile.Zone, pile.Pose, pile.Compact);
    }
    private void DrawWorldPile(Player player, string zone, Pose pose, bool compact = false)
    {
        var value = player.Zone(zone); var top = zone == "Library" ? value.TopCard : value.Cards.LastOrDefault();
        var geometry = PileGeometry.Create(pose, value.Count, compact);
        canvas.Surface(null, geometry.Shadow, Color.Black * .4f);
        for (int i = 0; i < geometry.Layers.Count; i++)
            canvas.Surface(null, geometry.Layers[i], i % 2 == 0 ? new Color(154, 149, 131) : new Color(54, 58, 56));
        pose = geometry.Top;
        if (compact) {
            canvas.Rounded(geometry.Label, new Color(17, 25, 28) * .85f, 3);
            canvas.CenterText((zone == "Graveyard" ? "Grave" : zone) + " " + value.Count, geometry.Label, Ink, .43f);
        } else {
            canvas.Circle(geometry.CountCenter, 12, new Color(17, 25, 28));
            canvas.CenterText(value.Count.ToString(), geometry.Count, Ink, .56f, true);
            canvas.CenterText(zone == "Graveyard" ? "Grave" : zone, geometry.Label, Muted, .45f);
        }
        hits.Add(new("zone:" + player.Id + ":" + zone, Scope, geometry.HitBounds, new MatchIntent.OpenZone(player.Id, zone), Shape: geometry.Contains));
        // Browser members open their pile. A separately disclosed top remains
        // directly readable even when the rest of its zone is concealed.
        bool publicPile = zone is "Graveyard" or "Exile";
        if (top != null && !top.FaceDown && !tableScene!.PlayAccess.Contains(player.Id, zone, top)
            && (!publicPile || !SpreadContains(CardIdentity(top)) && !PublicCollectionReturning(player.Id, zone, top)))
            DrawTableCard(publicPile ? CardIdentity(top) : "pile:" + player.Id + ":" + zone + ":" + CardIdentity(top), top, pose, zone, player.Id,
            "pile-card:" + player.Id + ":" + zone, new MatchIntent.OpenZone(player.Id, zone), (displayed, focus) => PaintCard(top, displayed, false, IsCardActionable(top), focus),
            opensZone: value.Cards.Any(card => CardViews.Same(card, top)));
        else PaintBack(pose, value.Count == 0 ? .3f : 1);
        DrawZoneActionBadge(player, zone, pose);
    }
    private void DrawHero(Player player, Vector2 center, bool own, int index)
    {
        heroPositions[player.Id] = center;
        bool target = IsPlayerTarget(player.Id), active = match!.ActivePlayerId == player.Id;
        bool timedOut = player.Eliminated && TimedOut(player.Id);
        bool hasCounters = StateClarity.PlayerStates(player).Length > 0;
        float radius = own ? 40 : 34;
        if (!own) {
            var handCenter = SeatHand(player.Id);
            foreach (var back in OpponentHandPoses(player, index)) {
                canvas.Surface(null, back with { Center = back.Center + new Vector2(3, 6) }, Color.Black * .45f);
                PaintBack(back);
            }
            var handBounds = new Rectangle((int)handCenter.X - 83, (int)handCenter.Y - 30, 166, 75);
            hits.Add(new("hand-zone:" + player.Id, Scope, handBounds, new MatchIntent.OpenZone(player.Id, "Hand")));
            if (index != 1 && !hasCounters) {
                var caption = new Rectangle((int)handCenter.X - 65, (int)handCenter.Y + 35, 130, 22);
                canvas.CenterText(player.Zone("Hand").Count + " in hand", caption, Muted, .48f);
            }
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
        if (timedOut) {
            canvas.CenterText("TIMED", new((int)(center.X - radius), (int)center.Y - 22, (int)radius * 2, 24), Red, .5f, true);
            canvas.CenterText("OUT", new((int)(center.X - radius), (int)center.Y + 1, (int)radius * 2, 24), Ink, .72f, true);
        } else {
            canvas.CenterText(player.Life.ToString(), new((int)(center.X - radius) + 1, (int)(center.Y - radius) + 2, (int)radius * 2, (int)radius * 2), Color.Black, own ? 1.65f : 1.35f, true);
            canvas.CenterText(player.Life.ToString(), new((int)(center.X - radius), (int)(center.Y - radius), (int)radius * 2, (int)radius * 2), player.Eliminated ? Muted : Ink, own ? 1.65f : 1.35f, true);
        }
        DrawDecisionClock(player, center, radius);
        var bounds = new Rectangle((int)center.X - (int)radius - 9, (int)center.Y - (int)radius - 9, (int)radius * 2 + 18, (int)radius * 2 + 18);
        UiIntent portraitIntent = LibraryRecipientTarget ? new MatchIntent.OpenPlayerDetails(player.Id) : new MatchIntent.UsePlayer(player.Id);
        hits.Add(new("player:" + player.Id, Scope, bounds, portraitIntent, PlayerId: LibraryRecipientTarget ? null : player.Id,
            Shape: p => Vector2.DistanceSquared(p.ToVector2(), center) <= (radius + 9) * (radius + 9)));
        string name = own ? "YOU" : localMatch && !ExperienceLabOpen
            ? "AI " + (TableLayout.SeatIndex(match, player.Id) + 1) + " · " + player.Name.Split('·')[0].Trim() : player.Name;
        var manaHalo = PlanManaHalo(player, own ? 3 : index);
        var nameplate = SeatNameplateArea(center, own, index, manaHalo);
        int x = nameplate.X, y = nameplate.Y;
        bool topMatSeat = TableStudyEnabled && !own && index == 1;
        if (!own) {
            seatReadoutBounds.Add(nameplate);
            string seatMarker = PublicSeatOrdinal(player.Id).ToString();
            int markerWidth = canvas.TextWidth(seatMarker, .6f, true) + 10;
            canvas.CenterText(seatMarker, new(nameplate.X, nameplate.Y, markerWidth, nameplate.Height), Ink, .6f, true);
            string actionName = seatReadoutWorkspaces.Contains(player.Id) ? "Read resources" : name + " ↗";
            canvas.CenterText(actionName, new(nameplate.X + markerWidth, nameplate.Y, nameplate.Width - markerWidth, nameplate.Height),
                nameplate.Contains(pointer) ? Blue : Ink, .6f, true);
        }
        int incoming = match.Combat?.Attackers.Count(a => a.HasDefender && a.DefendingPlayerId == player.Id) ?? 0;
        string status = player.Eliminated ? TimedOut(player.Id) ? "TIMED OUT" : "ELIMINATED" : incoming > 0 ? incoming + (own ? " ATTACKING YOU" : " INCOMING")
            : active && !own && match.Turn > 0 ? turnProgress?.PhaseLabel ?? match.Phase : active ? "ACTIVE TURN" : "";
        if (!own && index != 1 && hasCounters) status += (status.Length > 0 ? " · " : "") + player.Zone("Hand").Count + " in hand";
        string? auxiliaryCaption = null;
        if (!own && !timedOut && (!TableStudyEnabled || incoming > 0 || player.Eliminated)) {
            string caption = TableStudyEnabled ? status : active && incoming > 0 ? (turnProgress?.PhaseLabel ?? match.Phase) + " · " + status : status;
            if (TableStudyEnabled) auxiliaryCaption = caption;
            else canvas.Text(caption, x + 4, y + 24, incoming > 0 ? Red : Gold, .53f, 180, 1, true);
        }
        if (own && !timedOut && (incoming > 0 || player.Eliminated))
            canvas.CenterText(status, new((int)center.X - 94, (int)center.Y - 64, 188, 19), Red, .5f, true);
        // Portrait identity and unspent mana remain fixed seat controls. Public
        // opponent resources are composed after the fixed hand is prepared.
        DrawPlayerMana(player, manaHalo);
        var auxiliary = new SeatAuxiliary(player, center, own, index, hasCounters, manaHalo, auxiliaryCaption, incoming, new(x + 4, y + 24, 180, 80));
        if (!TableStudyEnabled || own) DrawSeatAuxiliary(auxiliary);
        if (!own) DrawOpponentPhase(player, center, index);
        if (!own) hits.Add(new("focus:" + player.Id, Scope, nameplate, seatReadoutWorkspaces.Contains(player.Id)
            ? new MatchIntent.OpenPlayerState(player.Id, "") : new MatchIntent.OpenZone(player.Id, "Battlefield"),
            Shape: topMatSeat ? point => nameplate.Contains(point)
                && Vector2.DistanceSquared(point.ToVector2(), center) > (radius + 9) * (radius + 9) : null));
    }

    private IEnumerable<Pose> OpponentHandPoses(Player player, int index)
    {
        int count = Math.Min(7, player.Zone("Hand").Count);
        int arriving = preferences.ReducedMotion ? 0 : matchPresentation.PendingDraws(player.Id, now);
        var handCenter = SeatHand(player.Id);
        for (int card = 0; card < Math.Max(0, count - arriving); card++) {
            var back = TableLayout.HiddenHand(index, card, count);
            yield return back with { Center = back.Center + handCenter - TableLayout.SeatHand(index) };
        }
    }

    private Rectangle SeatNameplateArea(Vector2 center, bool own, int index, ManaHaloPlan mana)
    {
        if (own) return new(Math.Max((int)center.X + 58, mana.Bounds.Right) + 8, (int)center.Y - 22, 100, 24);
        if (TableStudyEnabled && index == 1)
            return TwoFieldTable ? new(904, 54, 188, 24) : new(496, 148, 208, 24);
        return index == 1 ? new(Math.Max((int)center.X + 50, mana.Bounds.Right) + 8, 64, 188, 24)
            : new((int)center.X - 94, (int)center.Y + 41, 188, 24);
    }

    private void DrawSeatAuxiliary(SeatAuxiliary auxiliary)
    {
        var (player, center, own, index, hasCounters, manaHalo, caption, incoming, statusArea) = auxiliary;
        if (hasCounters) {
            int counterX = own ? (int)center.X - 390 : (int)center.X - 94;
            int counterY = own ? (int)center.Y - 22 : index == 1 ? (int)center.Y + 35 : (int)center.Y - 64;
            int counterWidth = index == 1 && !own ? 144 : 188;
            bool growUp = !own && index != 1;
            if (own && TableStudyEnabled && tableScene!.Seat(player.Id).Geometry.PublicCounterArea is { } counterArea) {
                counterX = counterArea.X; counterY = counterArea.Bottom - (player.States.Length > 0 ? 24 : 25);
                counterWidth = counterArea.Width; growUp = true;
            } else if (own && manaHalo.Parts.Length > 0) counterX = Math.Min(counterX, manaHalo.Bounds.Left - 196);
            else if (index == 1) counterY = Math.Max(counterY, manaHalo.Bounds.Bottom + 8);
            var counters = DrawPublicPlayerCounters(player, counterX, counterY, counterWidth, growUp);
            if (!counters.IsEmpty) seatReadoutBounds.Add(counters);
        }
        if (TableStudyEnabled && own) DrawLandState(player, new(8, 800, 220, 100));
        if (caption != null) {
            var measured = canvas.TextSize(caption, .53f, statusArea.Width, 80, true);
            seatReadoutBounds.Add(new(statusArea.X, statusArea.Y, measured.X, measured.Y));
            canvas.Text(caption, statusArea.X, statusArea.Y, incoming > 0 ? Red : Gold, .53f, statusArea.Width, 80, true);
        }
    }

    private void DrawOpponentSeatReadouts()
    {
        foreach (var plan in seatReadoutPlans.Values) DrawSeatReadout(plan);
    }

    private Rectangle DrawLandState(Player player, Rectangle area)
    {
        var lands = PublicResources.Lands(player);
        Rectangle painted = Rectangle.Empty;
        int titleHeight = Readout(lands.Total + (lands.Total == 1 ? " land" : " lands"), area.Y, .75f, true);
        int stateY = area.Y + Math.Max(23, titleHeight + 2);
        Readout(lands.Untapped + " untapped · " + lands.Tapped + " tapped", stateY, .55f);
        return painted;
        int Readout(string text, int y, float size, bool bold = false) {
            var measured = canvas.TextSize(text, size, area.Width, 80, bold);
            var bounds = new Rectangle(area.X, y, measured.X, measured.Y);
            seatReadoutBounds.Add(bounds);
            painted = painted.IsEmpty ? bounds : Rectangle.Union(painted, bounds);
            canvas.Text(text, area.X, y, Ink, size, area.Width, 80, bold);
            return measured.Y;
        }
    }
}
