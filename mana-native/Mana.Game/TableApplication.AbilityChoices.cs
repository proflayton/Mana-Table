using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record AbilityActionText(Choice Choice, string Text, int Height);

    private static bool IsAbilityActionChoice(Decision decision) => decision.Task == DecisionTask.AbilityChoice;

    private void SubmitAbilityChoice(GameSnapshot state, Decision decision, int index)
    {
        textFocus = false;
        CollapseCardReading();
        Send(new ChooseOptions(state.Id, decision.Id, [index]));
    }

    private sealed record AbilityActionPlan(Rectangle Anchor, Pose? Source, Rectangle Area, List<List<AbilityActionText>> Pages, int TextWidth);
    private AbilityActionPlan? abilityActionPlan;

    private AbilityActionPlan? PrepareAbilityActionLayout()
    {
        if (match is not { Decision: { } decision } state || !IsAbilityActionChoice(decision) || session.NeedsRecovery
            || ActivityOpen || updatesOpen || ExperienceOpen || overlay.Length > 0 || zoneName != null && !BattlefieldFocused) return null;
        var (anchor, sourcePose, reported, zone) = DecisionSourceAnchor(state, decision);
        const float fontSize = .78f;
        const int padding = 12, rowGap = 8, controlsHeight = 40;
        string context = sourcePose != null ? "" : reported is { FaceDown: false } ? reported.Name
            : !string.IsNullOrWhiteSpace(decision.Title) ? decision.Title : reported == null ? "Card ability" : "Face-down card";
        if (context.Length > 0 && zone.Length > 0) context += " · " + zone;
        string Content(Choice choice) => string.Join("\n", new[] { context, choice.Mana, choice.Label, choice.Detail }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        int requestedWidth = Math.Clamp(decision.Choices.SelectMany(choice => Content(choice).Split('\n'))
            .Select(line => canvas.TextWidth(line, fontSize, true) + padding * 2).DefaultIfEmpty(120).Max(), 120, 460);
        int requestedHeight = controlsHeight + decision.Choices.Sum(choice => Math.Max(48,
            canvas.TextSize(Content(choice), fontSize, requestedWidth - padding * 2, 80, true).Y + padding * 2))
            + (decision.Choices.Length - 1) * rowGap;
        var area = DecisionActionArea(anchor, requestedWidth, Math.Min(400, requestedHeight));
        if (area.Width < 100 || area.Height < 110) return null;
        int textWidth = area.Width - padding * 2, bodyHeight = area.Height - controlsHeight;
        List<List<AbilityActionText>> pages = [[]];
        int used = 0;
        foreach (var choice in decision.Choices) {
            string content = Content(choice);
            if (content.Length == 0) content = "Use this option";
            foreach (string fragment in canvas.TextPages(content, fontSize, textWidth, bodyHeight - padding * 2, 80, true)) {
                int height = Math.Max(48, canvas.TextSize(fragment, fontSize, textWidth, 80, true).Y + padding * 2);
                if (used > 0 && used + rowGap + height > bodyHeight) { pages.Add([]); used = 0; }
                pages[^1].Add(new(choice, fragment, height));
                used += (used > 0 ? rowGap : 0) + height;
            }
        }
        return new(anchor, sourcePose, area, pages, textWidth);
    }

    private void DrawAbilityActions()
    {
        if (abilityActionPlan is not { } layout || match?.Decision is not { } decision) return;
        const float fontSize = .78f;
        const int padding = 12, rowGap = 8, controlsHeight = 40;
        var anchor = layout.Anchor; var entry = layout.Source; var area = layout.Area;
        var pages = layout.Pages; int textWidth = layout.TextWidth;
        int page = Math.Clamp(choicePage, 0, pages.Count - 1);
        var visible = pages[page];
        int heightNeeded = visible.Sum(row => row.Height) + (visible.Count - 1) * rowGap + controlsHeight;
        int y = Math.Clamp(anchor.Top, area.Top, area.Bottom - heightNeeded);
        bool rightSide = area.Left >= anchor.Right;
        var target = new Vector2(rightSide ? area.Left : area.Right, y + visible[0].Height / 2);
        var connection = entry?.EdgeToward(target, 4)
            ?? new Vector2(rightSide ? anchor.Right : anchor.Left, Math.Clamp((int)target.Y, anchor.Top, anchor.Bottom));
        canvas.Line(connection, target, Blue * .45f, 1.5f);
        if (entry is { } source) canvas.CardFrame(source with { Size = source.Size + new Vector2(5) }, Blue * .55f, 1);
        foreach (var row in visible) {
            string id = "choice:" + row.Choice.Index;
            var bounds = new Rectangle(area.X, y, area.Width, row.Height);
            PaintControlSurface(id, bounds, Panel, new(77, 110, 125), !busy);
            canvas.Text(row.Text, bounds.X + padding, bounds.Y + padding, Ink, fontSize, textWidth, 80, true);
            int index = row.Choice.Index;
            if (!busy) hits.Add(new(id, Scope, bounds, new MatchIntent.ChooseAbility(index)));
            y += row.Height + rowGap;
        }
        if (pages.Count > 1) {
            Button("choicesprev", "‹", area.X, y, 40, new MatchIntent.SetPage(MatchPage.Choice, page - 1), !busy && page > 0, height: 32);
            Button("choicesnext", "›", area.X + 46, y, 40, new MatchIntent.SetPage(MatchPage.Choice, page + 1), !busy && page + 1 < pages.Count, height: 32);
            canvas.Text($"{page + 1} / {pages.Count}", area.X + 98, y + 6, Muted, .6f,
                Math.Min(110, Math.Max(0, area.Width - 98 - (decision.Min == 0 ? 117 : 0))), 1);
        }
        if (decision.Min == 0)
            Button("choice-cancel", "Cancel", area.Right - 105, y, 105, new MatchIntent.ChooseNone(true), !busy, height: 32);
    }

    private (Rectangle Anchor, Pose? Pose, Card? Card, string Zone) DecisionSourceAnchor(GameSnapshot state, Decision decision)
    {
        var reported = CardViews.Source(state, decision);
        var location = reported == null ? null : CardViews.InZones(state)
            .Where(occurrence => SameVisibleCard(occurrence.Card, reported)).Select(occurrence => (ZoneCardOccurrence?)occurrence).FirstOrDefault();
        var source = location?.Card;
        string identity = source == null ? "" : CardViews.Identity(source);
        var sourcePose = location == null ? null : DecisionSourcePose(identity, location.Value.Zone, location.Value.PlayerId);
        var owner = state.Players.FirstOrDefault(player => player.Id == location?.PlayerId)
            ?? state.Players.FirstOrDefault(player => player.Id == decision.SourceControllerId) ?? state.Viewer;
        var geometry = tableScene?.Seats.FirstOrDefault(seat => seat.PlayerId == owner?.Id)?.Geometry;
        string zone = location?.Zone ?? decision.SourceZone;
        var pile = geometry?.Piles.FirstOrDefault(candidate => candidate.Zone == zone);
        var anchor = sourcePose?.Bounds ?? pile?.Pose.Bounds
            ?? new Rectangle((int)(geometry?.Hero.X ?? 800) - 55, (int)(geometry?.Hero.Y ?? 744) - 55, 110, 110);
        return (anchor, sourcePose, reported, zone);
    }

    private Pose? DecisionSourcePose(string identity, string zone, int owner)
    {
        if (tableScene == null || identity.Length == 0) return null;
        SceneCard? node = zone == "Battlefield" ? tableScene.Cohort(identity)?.Front
            : zone == "Hand" ? tableScene.Hand.Cards.FirstOrDefault(card => card.Id == identity)
            : zone == "Command" ? tableScene.Seats.FirstOrDefault(seat => seat.PlayerId == owner)?.Geometry.Commanders.FirstOrDefault(card => card.Id == identity)
            : null;
        Pose? pose = node == null ? null : BoardView.AffectsZone(zone) ? boardView.Apply(node.Pose) : node.Pose;
        if (BattlefieldFocused && zone == "Battlefield" && battlefieldFocusPlan?.Tile(identity) is { } tile) pose = tile.ArtPose;
        if (pose is not { } rest || matchPresentation.Cards.PinnedId != identity) return pose;
        return TableStudyEnabled && zone == "Battlefield"
            ? Mana.Table.Presentation.CardPresentationController.StudyReaderPose(rest, StudyActionArea)
            : Mana.Table.Presentation.CardPresentationController.ReaderPose(rest, zone);
    }

    private Rectangle DecisionActionArea(Rectangle anchor, int requestedWidth, int requestedHeight, bool reserveBattlefield = false)
    {
        var free = new List<Rectangle> { new(20, 64, 1560, 696) };
        var source = anchor; source.Inflate(12, 12);
        var occupied = new List<Rectangle> { source, new(1240, 550, 350, 245) };
        if (match is { } state && tableScene != null) {
            occupied.AddRange(TableViewGeometry.FixedReservations(state, tableScene));
            if (reserveBattlefield) occupied.AddRange(tableScene.BattlefieldDrawOrder.Select(card => {
                var protectedBounds = boardView.Apply(card.Pose).Bounds; protectedBounds.Inflate(8, 8); return protectedBounds;
            }));
            occupied.AddRange(state.Players.Select(player => PlanManaHalo(player, SeatDisplayIndex(player.Id)).Bounds).Where(bounds => !bounds.IsEmpty));
            if (TableStudyEnabled && matchPresentation.Cards.Pinned is { Zone: "Battlefield" } && anchor.Width >= 300)
                occupied.Add(new(anchor.Right + Mana.Table.Presentation.CardPresentationController.StudyDetailsGap, anchor.Top,
                    Mana.Table.Presentation.CardPresentationController.StudyDetailsWidth, anchor.Height));
        }
        if (reserveBattlefield) return PlaceDecisionWorkspace(anchor, requestedWidth, requestedHeight, occupied);
        foreach (var obstacle in occupied) {
            for (int index = free.Count - 1; index >= 0; index--) {
                var area = free[index]; var overlap = Rectangle.Intersect(area, obstacle);
                if (overlap.IsEmpty) continue;
                free.RemoveAt(index);
                if (overlap.Left > area.Left) free.Add(new(area.Left, area.Top, overlap.Left - area.Left, area.Height));
                if (overlap.Right < area.Right) free.Add(new(overlap.Right, area.Top, area.Right - overlap.Right, area.Height));
                if (overlap.Top > area.Top) free.Add(new(area.Left, area.Top, area.Width, overlap.Top - area.Top));
                if (overlap.Bottom < area.Bottom) free.Add(new(area.Left, overlap.Bottom, area.Width, area.Bottom - overlap.Bottom));
            }
        }
        var candidates = free.Where(area => area.Width >= Math.Min(260, requestedWidth) && area.Height >= 120).ToArray();
        if (candidates.Length == 0) candidates = free.Where(area => area.Width >= 100 && area.Height >= 110).ToArray();
        return candidates.Select(area => {
            int width = Math.Min(requestedWidth, area.Width), height = Math.Min(400, area.Height);
            int x = Math.Clamp(anchor.Right + 12, area.Left, area.Right - width);
            return new Rectangle(x, area.Top, width, height);
        }).OrderBy(area => area.Height >= requestedHeight ? 0 : 1)
            .ThenBy(area => area.Width >= requestedWidth ? 0 : 1)
            .ThenBy(area => area.Left >= anchor.Right ? 0 : area.Right <= anchor.Left ? 1 : 2)
            .ThenBy(area => {
                // Score where the content will actually sit beside the card,
                // rather than the unused top of its available vertical strip.
                int height = Math.Min(requestedHeight, area.Height);
                int top = Math.Clamp(anchor.Center.Y - height / 2, area.Top, area.Bottom - height);
                return Vector2.DistanceSquared(new Vector2(area.Center.X, top + height / 2), anchor.Center.ToVector2());
            })
            .ThenByDescending(area => area.Width * area.Height).FirstOrDefault();
    }

    private static Rectangle PlaceDecisionWorkspace(Rectangle anchor, int width, int height, List<Rectangle> occupied)
    {
        var viewport = new Rectangle(20, 64, 1560, 696);
        width = Math.Clamp(width, 320, viewport.Width); height = Math.Clamp(height, 100, viewport.Height);
        Rectangle At(int x, int y) => new(Math.Clamp(x, viewport.Left, viewport.Right - width),
            Math.Clamp(y, viewport.Top, viewport.Bottom - height), width, height);
        long Overlap(Rectangle candidate, long ceiling)
        {
            candidate.Inflate(8, 8);
            long total = 0;
            foreach (var obstacle in occupied) {
                var overlap = Rectangle.Intersect(candidate, obstacle);
                total += (long)overlap.Width * overlap.Height;
                if (total >= ceiling) break;
            }
            return total;
        }
        var preferred = new[] {
            At(anchor.Center.X - width / 2, anchor.Top - height - 16),
            At(anchor.Left - width - 16, anchor.Center.Y - height / 2),
            At(anchor.Right + 16, anchor.Center.Y - height / 2),
            At(800 - width / 2, 470 - height / 2),
            At(anchor.Center.X - width / 2, anchor.Bottom + 16)
        };
        var best = preferred[0]; long bestOverlap = long.MaxValue;
        foreach (var candidate in preferred) {
            long overlap = Overlap(candidate, bestOverlap);
            if (overlap == 0) return candidate;
            if (overlap < bestOverlap) { best = candidate; bestOverlap = overlap; }
        }
        // Nearby obstacle edges and viewport edges supplement the source/center
        // candidates. Bound both axes so a crowded table does not multiply all
        // permanent positions every frame. The fallback stays fully reachable
        // when physical space requires some overlap; names never get squeezed.
        var xs = occupied.SelectMany(obstacle => new[] { obstacle.Right + 8, obstacle.Left - width - 8 })
            .Append(viewport.Left).Append(viewport.Right - width).Select(x => At(x, 64).X).Distinct()
            .OrderBy(x => Math.Abs(x + width / 2 - anchor.Center.X)).Take(24)
            .Append(viewport.Left).Append(viewport.Right - width).Distinct().ToArray();
        var ys = occupied.SelectMany(obstacle => new[] { obstacle.Bottom + 8, obstacle.Top - height - 8 })
            .Append(viewport.Top).Append(viewport.Bottom - height).Select(y => At(20, y).Y).Distinct()
            .OrderBy(y => Math.Abs(y + height / 2 - anchor.Center.Y)).Take(24)
            .Append(viewport.Top).Append(viewport.Bottom - height).Distinct().ToArray();
        foreach (int x in xs) foreach (int y in ys) {
            var candidate = At(x, y); long overlap = Overlap(candidate, bestOverlap);
            if (overlap == 0) return candidate;
            if (overlap < bestOverlap) { best = candidate; bestOverlap = overlap; }
        }
        return best;
    }
}
