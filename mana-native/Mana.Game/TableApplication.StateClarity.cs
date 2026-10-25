using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string playerCounterContext = "";
    private int playerCounterPage;
    private string playerStateFocus = "";
    internal sealed record PublicStateChipPaint(string Id, string Text, Rectangle Bounds, int MeasuredWidth, int AvailableWidth);
    internal sealed record PublicCounterBadge(string Text, float Size, int MeasuredWidth, int AvailableWidth, int HiddenTypes)
    {
        public PublicStateChipPaint[] Chips { get; init; } = [];
        public Rectangle Bounds { get; init; }
        public string DetailStateId { get; init; } = "";
        public string DetailCaption { get; init; } = "";
        public string DetailText { get; init; } = "";
    }
    private readonly Dictionary<int, PublicCounterBadge> publicCounterBadges = [];
    // This complements the portraits. Both surfaces submit the same current,
    // engine-projected player handle; no recipient is selected automatically.
    private bool DrawDecisionClarity(GameSnapshot state)
    {
        if (state.Decision is { Task: DecisionTask.TargetSelection, LibrarySearch: not null } libraryDecision
            && !session.NeedsRecovery && !OverlayOpen && StateClarity.Recipients(state) is { Length: > 0 } libraries) {
            DrawLibrarySearchRecipients(state, libraryDecision, libraries);
            return true;
        }
        if (SelectingActionTarget) return false;
        var recipients = StateClarity.Recipients(state);
        if (session.NeedsRecovery || state.Status != "playing" || OverlayOpen || recipients.Length == 0 || state.Decision is not { Task: DecisionTask.TargetSelection or DecisionTask.EntitySelection } decision)
            return false;
        const int x = 545, width = 206, bottom = 548;
        const float consequenceSize = .56f;
        int consequenceLines = (int)Math.Ceiling(4 * preferences.TextScale);
        string consequence = string.IsNullOrWhiteSpace(decision.Message) ? "" :
            canvas.TextPages(decision.Message, consequenceSize, width - 20, 72, consequenceLines)[0];
        int consequenceHeight = consequence.Length == 0 ? 0 : canvas.TextSize(consequence, consequenceSize, width - 20, consequenceLines).Y;
        int headingHeight = Math.Max(22, canvas.TextSize("CHOOSE A PLAYER", .57f, width - 20, 1, true).Y);
        int controlsOffset = 11 + headingHeight + (consequenceHeight > 0 ? 8 + consequenceHeight : 0) + 12;
        int controlHeight = recipients.Length > 4 ? 27 : 34;
        int controlsHeight = recipients.Length * controlHeight + (recipients.Length - 1) * 4;
        // Grow upward from the same lower edge so the engine's consequence and
        // every named recipient retain space without touching the decision dock.
        int height = Math.Max(263, controlsOffset + controlsHeight + 46);
        int y = bottom - height;
        var bounds = new Rectangle(x, y, width, height);
        canvas.Panel(bounds, new Color(15, 24, 32) * .98f, ActionAccent, 7);
        hits.Add(new("decision-context-panel", Scope, bounds, UiIntent.None, ShowPointer: false));
        canvas.Text("CHOOSE A PLAYER", x + 10, y + 11, ActionAccent, .57f, width - 20, 1, true);
        if (consequence.Length > 0)
            canvas.Text(consequence, x + 10, y + 11 + headingHeight + 8, Ink, consequenceSize, width - 20, consequenceLines);
        canvas.Text(state.Decision?.SourceCard?.Name ?? "Current effect", 760, 292, Ink, .65f, 212,
            (int)Math.Ceiling(2 * preferences.TextScale), true);
        for (int i = 0; i < recipients.Length; i++) {
            var player = recipients[i]; string scope = Scope;
            string label = player.Id == state.ViewerId ? "You" : player.Name;
            if (state.Players.Count(other => other.Name == player.Name) > 1 && player.Id != state.ViewerId) label += " · Seat " + (state.Players.IndexOf(player) + 1);
            Button("recipient:" + player.Id, label, x + 6, y + controlsOffset + i * (controlHeight + 4), width - 12,
                new MatchIntent.ChooseRecipient(player.Id), !busy, true, controlHeight);
        }
        Button("decision-explanation", "Effect details", x + 6, bottom - 34, width - 12,
            new MatchIntent.OpenView(MatchView.Help), height: 28);
        return true;
    }

    private void DrawPublicPlayerCounters(Player player, int x, int y, int width, bool growUp = false)
    {
        if (player.States.Length > 0) { DrawPublicPlayerStates(player, x, y, width, growUp); return; }
        var counters = StateClarity.PlayerCounters(player);
        if (counters.Length == 0) { publicCounterBadges.Remove(player.Id); return; }
        // Keep the caption footprint stable at every text size. The most urgent
        // resource and its overflow affordance use the exact cached font metrics
        // used to paint them, including bold and the current accessibility size.
        int available = width - 14, hidden = 0;
        string summary = StateClarity.CounterSummary(player);
        float size = .7f;
        bool Fits(float candidate) => canvas.TextWidth(summary, candidate, true) <= available;
        if (!Fits(size)) size = .65f;
        if (!Fits(size) && counters.Length > 1) {
            hidden = counters.Length - 1;
            summary = counters[0].Key + " " + counters[0].Value + " +" + hidden + (hidden == 1 ? " type" : " types");
            size = .7f;
            if (!Fits(size)) size = .65f;
        }
        // Public resource names and integer totals normally fit at .65. Keep
        // unusually large totals whole rather than letting ellipsis eat +N.
        while (!Fits(size) && size > .4f) size -= .05f;
        int measured = canvas.TextWidth(summary, size, true);
        publicCounterBadges[player.Id] = new(summary, size, measured, available, hidden);
        var bounds = new Rectangle(x, y, width, 25);
        bool poison = counters.Any(c => c.Key.Equals("poison", StringComparison.OrdinalIgnoreCase));
        canvas.Panel(bounds, new(14, 26, 31), poison ? new Color(157, 198, 118) : Blue, 4);
        canvas.Text(summary, x + 7, y + 2, Ink, size, available, 1, true);
        hits.Add(new("player-counters:" + player.Id, Scope, bounds, new MatchIntent.OpenPlayerDetails(player.Id)));
    }

    private void DrawPublicPlayerStates(Player player, int x, int y, int width, bool growUp)
    {
        var states = StateClarity.ProminentPlayerStates(player);
        if (states.Length == 0) { publicCounterBadges.Remove(player.Id); return; }
        const int rowHeight = 24, gap = 3;
        var chips = new List<(PlayerState State, string Text, float Size, int Width, int X, int Row)>();
        int cursor = 0, row = 0;
        foreach (var state in states) {
            string text = StateClarity.StateCaption(state);
            float size = .6f;
            while (canvas.TextWidth(text, size, true) > width - 12 && size > .4f) size -= .025f;
            int chipWidth = Math.Min(width, canvas.TextWidth(text, size, true) + 12);
            if (cursor > 0 && cursor + chipWidth > width) { row++; cursor = 0; }
            if (row >= 2) break;
            chips.Add((state, text, size, chipWidth, cursor, row)); cursor += chipWidth + gap;
        }
        int hidden = states.Length - chips.Count;
        string MoreText() => "+" + hidden + " states";
        int MoreWidth() => canvas.TextWidth(MoreText(), .53f, true) + 12;
        if (hidden > 0) {
            // Reserve a real, readable control instead of truncating a total.
            while (chips.Count > 1) {
                var last = chips[^1]; cursor = last.X + last.Width + gap; row = last.Row;
                if (cursor + MoreWidth() <= width || row == 0) break;
                chips.RemoveAt(chips.Count - 1); hidden++;
            }
            var final = chips[^1]; cursor = final.X + final.Width + gap; row = final.Row;
            if (cursor + MoreWidth() > width && row == 0) { row = 1; cursor = 0; }
        }
        int rows = Math.Max(chips.Max(c => c.Row) + 1, hidden > 0 ? row + 1 : 1);
        int top = growUp ? y - (rows - 1) * (rowHeight + gap) : y;
        var bounds = new Rectangle(x, top, width, rows * rowHeight + (rows - 1) * gap);
        hits.Add(new("player-counters:" + player.Id, Scope, bounds, new MatchIntent.OpenPlayerState(player.Id, "")));
        var painted = new List<PublicStateChipPaint>();
        foreach (var chip in chips) {
            var rectangle = new Rectangle(x + chip.X, top + chip.Row * (rowHeight + gap), chip.Width, rowHeight);
            Color accent = chip.State.Kind == "progress" ? Gold : Blue;
            string hitId = "player-state:" + player.Id + ":" + chip.State.Id;
            PaintControlSurface(hitId, rectangle, new(14, 26, 31), accent);
            canvas.Text(chip.Text, rectangle.X + 6, rectangle.Y + 2, Ink, chip.Size, rectangle.Width - 12, 1, true);
            string stateId = chip.State.Id;
            hits.Add(new(hitId, Scope, rectangle, new MatchIntent.OpenPlayerState(player.Id, stateId)));
            painted.Add(new(stateId, chip.Text, rectangle, canvas.TextWidth(chip.Text, chip.Size, true), rectangle.Width - 12));
        }
        if (hidden > 0) {
            var rectangle = new Rectangle(x + cursor, top + row * (rowHeight + gap), Math.Min(MoreWidth(), width - cursor), rowHeight);
            string hitId = "player-state-more:" + player.Id;
            PaintControlSurface(hitId, rectangle, new(14, 26, 31), Muted);
            canvas.Text(MoreText(), rectangle.X + 6, rectangle.Y + 2, Ink, .53f, rectangle.Width - 12, 1, true);
            hits.Add(new(hitId, Scope, rectangle, new MatchIntent.OpenPlayerState(player.Id, "")));
        }
        publicCounterBadges[player.Id] = new(string.Join(" · ", painted.Select(c => c.Text)) + (hidden > 0 ? " " + MoreText() : ""),
            .6f, painted.Max(c => c.MeasuredWidth), width - 12, hidden) { Chips = painted.ToArray(), Bounds = bounds };
    }

    private void OpenPlayerState(int player, string state)
    {
        zonePlayer = player; playerStateFocus = state; overlay = "player";
    }

    // Returns the height consumed so the caller can place commander damage
    // below it. Full public totals remain available even with many types.
    private int DrawPlayerCounterDetails(Player player, int x, int y, int width)
    {
        if (player.States.Length > 0) return DrawPlayerStateDetails(player, x, y, width);
        var counters = StateClarity.PlayerCounters(player);
        canvas.Text("PUBLIC COUNTERS", x, y, Gold, .65f, width, 1, true);
        const int perPage = 4;
        int pages = Math.Max(1, (counters.Length + perPage - 1) / perPage);
        int page = Math.Clamp(playerCounterPage, 0, pages - 1);
        var visible = counters.Skip(page * perPage).Take(perPage).ToArray();
        if (visible.Length == 0) canvas.Text("None", x, y + 25, Ink, .83f, width, 1);
        for (int i = 0; i < visible.Length; i++) {
            var counter = visible[i];
            canvas.Text(counter.Key + " " + counter.Value, x + i % 2 * (width / 2), y + 25 + i / 2 * 27, Ink, .83f, width / 2 - 12, 1);
        }
        if (pages > 1) {
            Button("counter-details-prev", "Previous", x + width - 272, y - 5, 125, new MatchIntent.SetPage(MatchPage.PlayerCounter, page - 1), page > 0, height: 26);
            Button("counter-details-next", "More counters", x + width - 137, y - 5, 137, new MatchIntent.SetPage(MatchPage.PlayerCounter, page + 1), page + 1 < pages, height: 26);
        }
        return 80;
    }

    private int DrawPlayerStateDetails(Player player, int x, int y, int width)
    {
        var states = StateClarity.ProminentPlayerStates(player);
        int page = Math.Clamp(playerCounterPage, 0, Math.Max(0, states.Length - 1));
        string heading = "PLAYER STATE" + (states.Length > 1 ? "  " + (page + 1) + " / " + states.Length : "");
        canvas.Text(heading, x, y, Gold, .5f, 190, 1, true);
        if (states.Length == 0) { canvas.Text("None", x, y + 24, Ink, .75f, width, 1); return 80; }
        var state = states[page];
        string caption = StateClarity.StateCaption(state);
        canvas.Text(caption, x + 200, y - 3, Ink, .7f, width - 490, 1, true);
        canvas.Text(state.Detail, x, y + 30, Muted, .55f, width, (int)Math.Ceiling(2 * preferences.TextScale));
        if (publicCounterBadges.TryGetValue(player.Id, out var badge))
            publicCounterBadges[player.Id] = badge with { DetailStateId = state.Id, DetailCaption = caption, DetailText = state.Detail };
        if (states.Length > 1) {
            Button("counter-details-prev", "Previous state", x + width - 284, y - 5, 136, new MatchIntent.SetPage(MatchPage.PlayerCounter, page - 1), page > 0, height: 26);
            Button("counter-details-next", "Next state", x + width - 138, y - 5, 138, new MatchIntent.SetPage(MatchPage.PlayerCounter, page + 1), page + 1 < states.Length, height: 26);
        }
        return 80;
    }

}
