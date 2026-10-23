using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private static readonly Rectangle BattlefieldFocusBounds = new(166, 88, 1068, 690);
    private static readonly Rectangle BattlefieldFocusViewport = new(180, 220, 1040, 510);
    private int battlefieldFocusScroll, battlefieldFocusMaximum, battlefieldFocusOwner = -1;
    private string battlefieldFocusGame = "";
    private FocusedBattlefieldPlan? battlefieldFocusPlan;
    private bool BattlefieldFocused => overlay.Length == 0 && zoneName == "Battlefield";
    private sealed record BattlefieldFocusLine(string Text, int Y, int Height, float Size, Color Color,
        bool Bold = false, bool Chip = false);
    private sealed record BattlefieldFocusDetails(IReadOnlyList<BattlefieldFocusLine> Lines, int Height);

    private IReadOnlyList<FocusedBattlefieldGroup> FocusedBattlefieldGroups(Player player) =>
        FocusedBattlefieldLayout.Group(player.Zone("Battlefield").Cards, CardPresentation.IsResource, matchPresentation.Changes);
    private static string BattlefieldFocusRoleLabel(string role) => role == "Lands" ? "Other lands" : role;

    // This workspace paints aliases of public table objects. It never prepares
    // a second canonical occurrence or submits an engine action when inspecting.
    private void PrepareFocusedBattlefield()
    {
        battlefieldFocusPlan = null;
        if (!BattlefieldFocused || match is not { } state) return;
        var player = state.Players.FirstOrDefault(candidate => candidate.Id == zonePlayer);
        if (player == null) { zoneName = null; return; }
        if (battlefieldFocusGame != state.Id || battlefieldFocusOwner != player.Id) {
            battlefieldFocusGame = state.Id; battlefieldFocusOwner = player.Id;
            battlefieldFocusScroll = battlefieldFocusMaximum = 0;
        }
        var groups = FocusedBattlefieldGroups(player);
        var details = new Dictionary<string, BattlefieldFocusDetails>(StringComparer.Ordinal);
        var plan = FocusedBattlefieldLayout.Arrange(groups, BattlefieldFocusViewport, battlefieldFocusScroll, (group, width) => {
            var content = MeasureBattlefieldFocusDetails(group.Front, width);
            details[group.Id] = content;
            return content.Height;
        });
        battlefieldFocusScroll = plan.Scroll; battlefieldFocusMaximum = plan.MaximumScroll;
        battlefieldFocusPlan = plan;

        battlefieldFocusDetails = details;
    }

    private IReadOnlyDictionary<string, BattlefieldFocusDetails> battlefieldFocusDetails = new Dictionary<string, BattlefieldFocusDetails>();
    private void DrawFocusedBattlefield()
    {
        if (match is not { } state || battlefieldFocusPlan is not { } plan) return;
        var player = state.Players.FirstOrDefault(candidate => candidate.Id == zonePlayer);
        if (player == null) return;
        var details = battlefieldFocusDetails;
        canvas.Fill(new(0, 54, 1600, 724), Color.Black * .36f);
        canvas.Panel(BattlefieldFocusBounds, new(19, 29, 36), new(104, 125, 135), 12);
        // Gaps and partially clipped rows must not click the obscured table.
        hits.Add(new("battlefield-focus-surface", Scope, BattlefieldFocusBounds, new MatchIntent.FocusSurface(), ShowPointer: false));
        canvas.Text(player.Id == state.ViewerId ? "Your battlefield" : player.Name + "'s battlefield", 190, 103, Ink, 1.06f, 820, 2, true);
        Button("battlefield-focus-close", "Whole table", 1050, 101, 160, new MatchIntent.CloseBattlefieldFocus(), height: 36);

        int tabGap = 8, tabWidth = (1020 - tabGap * Math.Max(0, state.Players.Length - 1)) / Math.Max(1, state.Players.Length);
        for (int index = 0; index < state.Players.Length; index++) {
            var candidate = state.Players[index];
            Button("battlefield-focus-owner:" + candidate.Id, candidate.Name, 190 + index * (tabWidth + tabGap), 151,
                tabWidth, new MatchIntent.SetBattlefieldOwner(candidate.Id), accent: candidate.Id == player.Id, height: 34);
        }
        canvas.Text(player.Zone("Battlefield").Count + " permanents", 191, 192, Muted, .67f, 620, 1);
        int roleX = 400;
        foreach (var heading in plan.Headings) {
            int offset = heading.Bounds.Top + plan.Scroll - BattlefieldFocusViewport.Top;
            int capturedOffset = offset;
            string caption = BattlefieldFocusRoleLabel(heading.Role) + " " + heading.Permanents;
            int roleWidth = Math.Max(170, canvas.TextWidth(caption, .76f) + 22);
            Button("battlefield-focus-role:" + heading.Role, caption, roleX, 190, roleWidth,
                new MatchIntent.ScrollBattlefieldTo(capturedOffset), height: 27);
            roleX += roleWidth + 10;
        }
        if (state.Stack.Length > 0)
            Button("battlefield-focus-stack", "Stack · " + state.Stack.Length, 1038, 189, 172, new MatchIntent.OpenStack(), height: 27);

        foreach (var heading in plan.Headings) {
            if (!BattlefieldFocusViewport.Contains(heading.Bounds)) continue;
            string caption = BattlefieldFocusRoleLabel(heading.Role);
            canvas.Text(caption, heading.Bounds.X + 2, heading.Bounds.Y + 2, Gold, .82f, 420, 1, true);
            canvas.Text(heading.Permanents.ToString(), heading.Bounds.X + Math.Max(185, canvas.TextWidth(caption, .82f, true) + 14),
                heading.Bounds.Y + 6, Muted, .66f, 160, 1);
        }
        foreach (var tile in plan.Tiles) {
            Rectangle visible = Rectangle.Intersect(tile.Bounds, BattlefieldFocusViewport);
            if (visible.Width <= 0 || visible.Height <= 0) continue;
            var card = tile.Group.Front;
            bool over = visible.Contains(pointer), actionable = IsCardActionable(card);
            canvas.Panel(visible, over ? new Color(39, 54, 63) : new Color(29, 41, 50),
                over ? Gold : new Color(65, 84, 94), 6);
            PaintBattlefieldFocusArt(card, tile.Art, BattlefieldFocusViewport);
            foreach (var line in details[tile.Group.Id].Lines) {
                var bounds = new Rectangle(tile.Details.X, tile.Details.Y + line.Y, tile.Details.Width, line.Height);
                if (!BattlefieldFocusViewport.Contains(bounds)) continue;
                if (line.Chip) {
                    canvas.Panel(bounds, new(41, 66, 70), new(76, 137, 132), 4);
                    canvas.Text(line.Text, bounds.X + 7, bounds.Y + 4, line.Color, line.Size, bounds.Width - 14, 80, line.Bold);
                } else canvas.Text(line.Text, bounds.X, bounds.Y, line.Color, line.Size, bounds.Width, 80, line.Bold);
            }
            if (tile.Group.Count > 1) {
                var badge = new Rectangle(tile.Art.X - 3, tile.Art.Y + 5, tile.Art.Width + 6, 36);
                if (BattlefieldFocusViewport.Contains(badge)) {
                    canvas.Panel(badge, new(19, 38, 45), Gold, 4);
                    canvas.CenterText(tile.Group.Count + "×", badge, Ink, .98f, true);
                }
            }
            if (actionable) {
                var mark = new Rectangle(tile.Art.X + 3, tile.Art.Bottom - 8, tile.Art.Width - 6, 4);
                if (BattlefieldFocusViewport.Contains(mark)) canvas.Fill(mark, ActionAccent);
            }
            if (over) hovered = card;
            string game = state.Id, identity = CardViews.Identity(card); int owner = player.Id;
            hits.Add(new("battlefield-focus-card:" + tile.Group.Id, Scope, visible,
                new MatchIntent.ReadFocusedCard(game, owner, identity), card, "BattlefieldFocus", owner));
        }
        if (plan.Tiles.Count == 0)
            canvas.CenterText("No permanents on this battlefield.", BattlefieldFocusViewport, Muted, 1);
        if (plan.MaximumScroll > 0) {
            var rail = new Rectangle(BattlefieldFocusViewport.Right - 7, BattlefieldFocusViewport.Top, 5, BattlefieldFocusViewport.Height);
            canvas.Rounded(rail, new(45, 61, 70), 2);
            int height = Math.Max(32, rail.Height * rail.Height / Math.Max(1, plan.ContentHeight));
            int y = rail.Top + (rail.Height - height) * plan.Scroll / plan.MaximumScroll;
            canvas.Rounded(new(rail.X, y, rail.Width, height), Muted, 2);
        }
        if (plan.MaximumScroll > 0) canvas.Text("Scroll to browse", 987, 746, Muted, .65f, 223, 1);
    }

    private BattlefieldFocusDetails MeasureBattlefieldFocusDetails(Card card, int width)
    {
        var lines = new List<BattlefieldFocusLine>(); int y = 0;
        void Line(string text, float size, Color color, bool bold = false, bool chip = false) {
            if (string.IsNullOrWhiteSpace(text)) return;
            int height = canvas.TextSize(text, size, width - (chip ? 14 : 0), 80, bold).Y + (chip ? 8 : 0);
            lines.Add(new(text, y, height, size, color, bold, chip)); y += height + (chip ? 7 : 8);
        }
        Line(card.FaceDown ? "Face-down card" : card.Name, .88f, Ink, true);
        Line(card.Type.Split('—')[0].Trim(), .64f, Muted);
        if (CardPresentation.HasCombatStats(card))
            Line((card.Power?.ToString() ?? "?") + " / " + (card.Toughness?.ToString() ?? "?"), 1.25f, Ink, true);
        foreach (var counter in CardPresentation.Counters(card))
            Line(counter.Key + " ×" + counter.Value, .72f, new(167, 237, 216), true, true);
        if (card.Damage > 0) Line(card.Damage + " damage marked", .70f, Red, true);
        foreach (string caption in CardPresentation.StateLabels(card).Where(label => label != "Untapped").Distinct(StringComparer.OrdinalIgnoreCase))
            Line(caption, .68f, caption == "Tapped" ? Gold : Muted, caption is "Tapped" or "Phased out" or "Attacking" or "Blocking");
        return new(Array.AsReadOnly(lines.ToArray()), Math.Max(0, y - 8));
    }

    private void PaintBattlefieldFocusArt(Card card, Rectangle art, Rectangle viewport)
    {
        var clip = Rectangle.Intersect(art, viewport);
        if (clip.Width <= 0 || clip.Height <= 0) return;
        if (clip == art) {
            PaintCard(card, new(art.Center.ToVector2(), art.Size.ToVector2()), false, false);
            return;
        }
        var image = card.FaceDown ? resources.CardBack : resources.Card(card);
        if (image == null) { canvas.Fill(clip, new(47, 61, 69)); return; }
        int left = Math.Clamp((clip.Left - art.Left) * image.Width / art.Width, 0, image.Width - 1);
        int top = Math.Clamp((clip.Top - art.Top) * image.Height / art.Height, 0, image.Height - 1);
        int right = Math.Clamp((clip.Right - art.Left) * image.Width / art.Width, left + 1, image.Width);
        int bottom = Math.Clamp((clip.Bottom - art.Top) * image.Height / art.Height, top + 1, image.Height);
        canvas.Sprite(image, clip.Center.ToVector2(), clip.Size.ToVector2(), 0, Color.White,
            new(left, top, right - left, bottom - top));
    }

    private bool ScrollFocusedBattlefield(int direction)
    {
        if (!BattlefieldFocused) return false;
        if (BattlefieldFocusBounds.Contains(pointer)) {
            CollapseCardReading();
            battlefieldFocusScroll = Math.Clamp(battlefieldFocusScroll - direction * 110, 0, battlefieldFocusMaximum);
        } else if (pointer.Y > 778) handPage = Math.Max(0, handPage - direction);
        return true;
    }

    private void ReadFocusedBattlefieldCard(string game, int owner, string identity)
    {
        if (!BattlefieldFocused || match?.Id != game || identity.Length == 0) return;
        var current = match.Players.FirstOrDefault(player => player.Id == owner)?.Zone("Battlefield").Cards
            .FirstOrDefault(card => CardViews.Identity(card) == identity);
        if (current == null) return;
        var canonical = matchPresentation.Cards.Cards.LastOrDefault(entry => entry.Zone == "Battlefield"
            && entry.Request.Owner == owner && CardViews.Same(entry.Card, current));
        if (canonical == null || !ToggleCardReader(canonical.Hit)) Inspect(current);
    }
}
