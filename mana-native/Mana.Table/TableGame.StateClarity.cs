using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private string playerCounterContext = "";
    private int playerCounterPage;
    internal sealed record PublicCounterBadge(string Text, float Size, int MeasuredWidth, int AvailableWidth, int HiddenTypes);
    private readonly Dictionary<int, PublicCounterBadge> publicCounterBadges = [];
    // This complements the portraits. Both surfaces submit the same current,
    // engine-projected player handle; no recipient is selected automatically.
    private bool DrawDecisionClarity(GameSnapshot state)
    {
        var recipients = StateClarity.Recipients(state);
        if (session.NeedsRecovery || state.Status != "playing" || OverlayOpen || recipients.Length == 0 || state.Decision is not { Intent: DecisionIntent.Selection } decision)
            return false;
        const int x = 20, y = 66, width = 510;
        int line = (int)Math.Ceiling(24 * preferences.TextScale);
        int rows = (recipients.Length + 1) / 2, purposeY = y + 45 + 3 * line, controlsY = y + 55 + 7 * line;
        var bounds = new Rectangle(x, y, width, 65 + 7 * line + rows * 42);
        canvas.Panel(bounds, new Color(15, 24, 32) * .98f, ActionAccent, 7);
        hits.Add(new("decision-context-panel", Scope, bounds, () => { }));
        canvas.Text("CHOOSE A RECIPIENT", x + 14, y + 11, ActionAccent, .58f, width - 28, 1, true);
        canvas.Text(StateClarity.Source(state), x + 14, y + 35, Ink, .85f, width - 28, (int)Math.Ceiling(3 * preferences.TextScale), true);
        canvas.Text(StateClarity.Purpose(state), x + 14, purposeY, Ink, .85f, width - 28, (int)Math.Ceiling(4 * preferences.TextScale));
        Button("decision-explanation", "Full decision", x + width - 151, y + 11, 135,
            () => { overlay = "help"; inspectPage = 0; }, height: 25);
        for (int i = 0; i < recipients.Length; i++) {
            var player = recipients[i]; string scope = Scope;
            Button("recipient:" + player.Id, StateClarity.SeatName(state, player.Id), x + 14 + i % 2 * 246, controlsY + i / 2 * 42, 236,
                () => {
                    if (Scope == scope && match is { } current && StateClarity.ChooseRecipient(current, player.Id) is { } reply) Send(reply);
                }, !busy, true, 35);
        }
        return true;
    }

    private void DrawPublicPlayerCounters(Player player, int x, int y, int width)
    {
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
        hits.Add(new("player-counters:" + player.Id, Scope, bounds, () => { zonePlayer = player.Id; overlay = "player"; }));
    }

    // Returns the height consumed so the caller can place commander damage
    // below it. Full public totals remain available even with many types.
    private int DrawPlayerCounterDetails(Player player, int x, int y, int width)
    {
        var counters = StateClarity.PlayerCounters(player);
        canvas.Text("PUBLIC COUNTERS", x, y, Gold, .65f, width, 1, true);
        string context = match!.Id + ":" + player.Id;
        if (playerCounterContext != context) { playerCounterContext = context; playerCounterPage = 0; }
        const int perPage = 4;
        int pages = Math.Max(1, (counters.Length + perPage - 1) / perPage);
        playerCounterPage = Math.Clamp(playerCounterPage, 0, pages - 1);
        var visible = counters.Skip(playerCounterPage * perPage).Take(perPage).ToArray();
        if (visible.Length == 0) canvas.Text("None", x, y + 25, Ink, .83f, width, 1);
        for (int i = 0; i < visible.Length; i++) {
            var counter = visible[i];
            canvas.Text(counter.Key + " " + counter.Value, x + i % 2 * (width / 2), y + 25 + i / 2 * 27, Ink, .83f, width / 2 - 12, 1);
        }
        if (pages > 1) {
            Button("counter-details-prev", "Previous", x + width - 272, y - 5, 125, () => playerCounterPage--, playerCounterPage > 0, height: 26);
            Button("counter-details-next", "More counters", x + width - 137, y - 5, 137, () => playerCounterPage++, playerCounterPage + 1 < pages, height: 26);
        }
        return 80;
    }

    private void DrawPublicOutcome()
    {
        if (actionFeedback.LastPublicOutcome is not { } outcome || now - outcome.Started > 5) return;
        canvas.Panel(new(20, 66, 379, 130), new Color(15, 24, 32) * .97f, Gold, 7);
        hits.Add(new("public-outcome-panel", Scope, new(20, 66, 379, 130), () => { }));
        canvas.Text("PUBLIC OUTCOME", 34, 78, Gold, .55f, 345, 1, true);
        canvas.Text(string.Join("\n", outcome.Messages), 34, 105, Ink, .77f, 345, 4);
        Button("public-outcome-history", "History", 292, 157, 92, () => { overlay = "history"; historyPage = 0; }, height: 26);
    }
}
