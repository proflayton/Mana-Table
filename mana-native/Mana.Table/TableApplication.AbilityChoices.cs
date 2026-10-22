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

    private static bool IsAbilityActionChoice(Decision decision) => decision.Kind == "choice" && decision.Purpose == DecisionPurpose.AbilitySelection
        && !decision.Ordered && decision.Max == 1 && decision.Min is 0 or 1 && decision.Choices.Length > 0
        && decision.LibraryCards.Length == 0 && decision.PlayerChoices.Length == 0
        && decision.Choices.All(choice => choice.Card == null && choice.CardId.Length == 0 && choice.PlayerId == null);

    private void SubmitAbilityChoice(GameSnapshot state, Decision decision, int index)
    {
        textFocus = false;
        CollapseCardReading();
        Send(new(state.Id, decision.Id, ReplyAction.Choose, Choices: [index]));
    }

    private void DrawAbilityActions()
    {
        if (match is not { Decision: { } decision } state || !IsAbilityActionChoice(decision) || session.NeedsRecovery
            || ActivityOpen || updatesOpen || ExperienceOpen || overlay.Length > 0 || zoneName != null && !BattlefieldFocused) return;
        var reported = InteractionCards.Source(state, decision);
        var source = reported == null ? null : VisibleCards().FirstOrDefault(card => SameVisibleCard(card, reported));
        var location = source == null ? null : state.Players.SelectMany(player => player.Zones.Select(zone => new { Player = player, Zone = zone }))
            .FirstOrDefault(item => item.Zone.Cards.Any(card => SameVisibleCard(card, source))
                || item.Zone.TopCard is { } top && SameVisibleCard(top, source));
        string identity = source == null ? "" : CardViews.Identity(source);
        var entry = identity.Length == 0 || location == null ? null : cardPresentation.Cards.LastOrDefault(card =>
            card.ChoiceSlot < 0 && card.Zone == location.Zone.Name && CardViews.Identity(card.Card) == identity);
        var owner = location?.Player ?? state.Players.FirstOrDefault(player => player.Id == decision.SourceControllerId) ?? state.Viewer;
        var geometry = owner == null ? null : tableScene?.Seat(owner.Id).Geometry;
        string zone = location?.Zone.Name ?? decision.SourceZone;
        var pile = geometry?.Piles.FirstOrDefault(candidate => candidate.Zone == zone);
        var anchor = (entry?.ReaderTarget ?? entry?.Rest)?.Bounds ?? pile?.Pose.Bounds
            ?? new Rectangle((int)(geometry?.Hero.X ?? 800) - 55, (int)(geometry?.Hero.Y ?? 744) - 55, 110, 110);
        const float fontSize = .78f;
        const int padding = 12, rowGap = 8, controlsHeight = 40;
        string context = entry != null ? "" : reported is { FaceDown: false } ? reported.Name
            : !string.IsNullOrWhiteSpace(decision.Title) ? decision.Title : reported == null ? "Card ability" : "Face-down card";
        if (context.Length > 0 && zone.Length > 0) context += " · " + zone;
        string Content(Choice choice) => string.Join("\n", new[] { context, choice.Mana, choice.Label, choice.Detail }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        int requestedWidth = Math.Clamp(decision.Choices.SelectMany(choice => Content(choice).Split('\n'))
            .Select(line => canvas.TextWidth(line, fontSize, true) + padding * 2).DefaultIfEmpty(120).Max(), 120, 460);
        int requestedHeight = controlsHeight + decision.Choices.Sum(choice => Math.Max(48,
            canvas.TextSize(Content(choice), fontSize, requestedWidth - padding * 2, 80, true).Y + padding * 2))
            + (decision.Choices.Length - 1) * rowGap;
        var area = AbilityActionArea(anchor, requestedWidth, Math.Min(400, requestedHeight));
        if (area.Width < 100 || area.Height < 110) return;
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
        choicePage = Math.Clamp(choicePage, 0, pages.Count - 1);
        var visible = pages[choicePage];
        int heightNeeded = visible.Sum(row => row.Height) + (visible.Count - 1) * rowGap + controlsHeight;
        int y = Math.Clamp(anchor.Top, area.Top, area.Bottom - heightNeeded);
        bool rightSide = area.Left >= anchor.Right;
        var target = new Vector2(rightSide ? area.Left : area.Right, y + visible[0].Height / 2);
        var connection = entry?.Display.EdgeToward(target, 4)
            ?? new Vector2(rightSide ? anchor.Right : anchor.Left, Math.Clamp((int)target.Y, anchor.Top, anchor.Bottom));
        canvas.Line(connection, target, Blue * .45f, 1.5f);
        if (entry != null) canvas.CardFrame(entry.Display with { Size = entry.Display.Size + new Vector2(5) }, Blue * .55f, 1);
        foreach (var row in visible) {
            string id = "choice:" + row.Choice.Index;
            var bounds = new Rectangle(area.X, y, area.Width, row.Height);
            PaintControlSurface(id, bounds, Panel, new(77, 110, 125), !busy);
            canvas.Text(row.Text, bounds.X + padding, bounds.Y + padding, Ink, fontSize, textWidth, 80, true);
            int index = row.Choice.Index;
            if (!busy) hits.Add(new(id, Scope, bounds, () => SubmitAbilityChoice(state, decision, index)));
            y += row.Height + rowGap;
        }
        if (pages.Count > 1) {
            Button("choicesprev", "‹", area.X, y, 40, () => choicePage--, !busy && choicePage > 0, height: 32);
            Button("choicesnext", "›", area.X + 46, y, 40, () => choicePage++, !busy && choicePage + 1 < pages.Count, height: 32);
            canvas.Text($"{choicePage + 1} / {pages.Count}", area.X + 98, y + 6, Muted, .6f,
                Math.Min(110, Math.Max(0, area.Width - 98 - (decision.Min == 0 ? 117 : 0))), 1);
        }
        if (decision.Min == 0)
            Button("choice-cancel", "Cancel", area.Right - 105, y, 105, () => {
                CollapseCardReading();
                Send(new(state.Id, decision.Id, ReplyAction.Choose, Choices: []));
            }, !busy, height: 32);
    }

    private Rectangle AbilityActionArea(Rectangle anchor, int requestedWidth, int requestedHeight)
    {
        var free = new List<Rectangle> { new(20, 64, 1560, 696) };
        var source = anchor; source.Inflate(12, 12);
        var occupied = new List<Rectangle> { source, new(1240, 550, 350, 245) };
        if (!battlefieldReaderStateBounds.IsEmpty) occupied.Add(battlefieldReaderStateBounds);
        occupied.AddRange(match!.Players.Select(player => {
            var hero = SeatHero(player.Id);
            return new Rectangle((int)hero.X - 55, (int)hero.Y - 55, 110, 110);
        }));
        occupied.AddRange(manaPoolBadges.Values.Select(badge => badge.Bounds).Where(bounds => !bounds.IsEmpty));
        occupied.AddRange(publicCounterBadges.Values.Select(badge => badge.Bounds).Where(bounds => !bounds.IsEmpty));
        occupied.AddRange(hits.Where(hit => hit.Id.StartsWith("focus:", StringComparison.Ordinal))
            .Select(hit => { var bounds = hit.Bounds; bounds.Inflate(8, 8); return bounds; }));
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
}
