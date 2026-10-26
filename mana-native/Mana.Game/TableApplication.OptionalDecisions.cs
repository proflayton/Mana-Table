using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    // Reading a card and answering an optional effect remain different gestures.
    // Both substantive answers share one size and one current decision scope.
    private bool DrawOptionalDecision(GameSnapshot state)
    {
        if (session.NeedsRecovery || state.Decision is not { } decision) return false;
        switch (decisions.Presentation?.Payload) {
            case OpeningHandPayload opening: DrawOpeningHand(state, decision, opening); return true;
            case ConfirmationPayload confirmation: DrawConfirmation(state, decision, confirmation); return true;
            default: return false;
        }
    }

    private void DrawOpeningHand(GameSnapshot state, Decision decision, OpeningHandPayload opening)
    {
        string detail = opening.Consequence is { } hand
            ? hand.FreeMulligan ? "Free mulligan" : $"Mulligan \u2192 keep {hand.NextHandSize}"
            : decision.Context.Message;
        var keep = opening.Consequence is { } current ? opening.Keep with { Label = "Keep " + current.HandSize } : opening.Keep;
        if (CardCollectionOpen) {
            Button("confirm", keep.Label, 964, 730, 248, new MatchIntent.ConfirmDecision(), !busy && keep.Enabled, true, 44);
            if (opening.Mulligan?.Enabled == true) Button("cancel", opening.Mulligan.Label, 1230, 730, 278,
                new MatchIntent.CancelDecision(), !busy, true, 44);
            return;
        }
        DrawDecisionAlternatives(state, decision, keep, opening.Mulligan, "Your opening hand", detail, true);
    }

    private void DrawConfirmation(GameSnapshot state, Decision decision, ConfirmationPayload confirmation)
    {
        var card = CardViews.Source(state, decision);
        string title = card is { FaceDown: false } ? card.Name : decision.Context.Title;
        string detail = decision.Context.Message;
        if (confirmation.Search is { } search) {
            string library = LibraryName(state, search.LibraryOwnerId);
            if (title.Length == 0) title = library;
            detail = (card is { FaceDown: false } ? library + "\n" : "") + SearchCriterion(search.CardName, search.Destination);
        }
        DrawDecisionAlternatives(state, decision, confirmation.Accept, confirmation.Decline, title, detail, false);
    }

    private void DrawDecisionAlternatives(GameSnapshot state, Decision decision, DecisionReply acceptReply,
        DecisionReply? declineReply, string title, string detail, bool opening)
    {
        var (anchor, source, _, _) = DecisionSourceAnchor(state, decision);
        int width = opening ? 490 : 410;
        const int gap = 10, actionHeight = 52;
        bool hasBoth = acceptReply.Enabled && declineReply?.Enabled == true;
        string accept = acceptReply.Label;
        (int TitleHeight, int TextHeight, bool FullDetails, bool Stacked, int Height) Measure(int availableWidth)
        {
            int body = detail.Length == 0 ? 0 : canvas.TextSize(detail, .77f, availableWidth - 24, 5).Y;
            int heading = title.Length == 0 ? 0 : canvas.TextSize(title, .76f, availableWidth - 24, 80, true).Y;
            bool full = detail.Length > 0 && canvas.TextSize(detail, .77f, availableWidth - 24, 80).Y > body;
            bool stack = hasBoth && Math.Max(canvas.TextWidth(accept, .76f, true), canvas.TextWidth((declineReply?.Label ?? ""), .76f, true)) + 24 > (availableWidth - gap) / 2;
            int actions = stack ? actionHeight * 2 + gap : actionHeight;
            return (heading, body, full, stack, 10 + heading + (body > 0 ? 6 + body : 0) + (full ? 36 : 0) + 12 + actions + 6);
        }
        var text = Measure(width);
        int requestedHeight = text.Height;
        Rectangle area;
        if (opening) {
            var hero = SeatHero(state.ViewerId);
            area = new(Math.Clamp((int)hero.X + 150, 20, 1580 - width),
                Math.Clamp((int)hero.Y - requestedHeight + 52, 64, Math.Max(64, 790 - requestedHeight)), width, requestedHeight);
        } else {
            var available = DecisionActionArea(anchor, width, Math.Max(130, requestedHeight), reserveBattlefield: true);
            width = available.Width;
            text = Measure(width); requestedHeight = text.Height;
            area = new(available.X, available.Y,
                width, requestedHeight);
        }
        decisionPanelBounds = area;
        canvas.Rounded(new(area.X - 8, area.Y - 8, area.Width + 16, area.Height + 16), new Color(8, 16, 21) * .96f, 6);
        int textY = area.Top + 10;
        if (title.Length > 0) { canvas.Text(title, area.X + 12, textY, Gold, .76f, width - 24, 80, true); textY += text.TitleHeight + 6; }
        if (detail.Length > 0) canvas.Text(detail, area.X + 12, textY, Ink, .77f, width - 24, 5);
        if (text.FullDetails)
            Button("decision-read-all", "Read full effect", area.X + 12, textY + text.TextHeight + 5, width - 24,
                new MatchIntent.OpenDecisionHelp(), height: 30);
        int actionsHeight = text.Stacked ? actionHeight * 2 + gap : actionHeight;
        int actionY = area.Bottom - actionsHeight - 6;
        int actionWidth = hasBoth && !text.Stacked ? (width - gap) / 2 : width;
        if (acceptReply.Enabled)
            Button("confirm", accept, area.X, actionY, actionWidth, new MatchIntent.ConfirmDecision(), !busy, true, actionHeight);
        if (declineReply?.Enabled == true)
            Button("cancel", (declineReply?.Label ?? ""), hasBoth && !text.Stacked ? area.X + actionWidth + gap : area.X,
                text.Stacked ? actionY + actionHeight + gap : actionY, actionWidth,
                new MatchIntent.CancelDecision(), !busy, true, actionHeight);
        if (!opening && source is { } pose) {
            canvas.CardFrame(pose with { Size = pose.Size + new Vector2(7) }, Gold * .65f, 2);
            var destination = new Pose(area.Center.ToVector2(), area.Size.ToVector2()).EdgeToward(pose.Center, 5);
            canvas.Line(pose.EdgeToward(destination, 5), destination, Gold * .5f, 1.5f);
        }
    }

    private static string LibraryName(GameSnapshot state, int player) => player == state.ViewerId ? "Your library"
        : (state.Players.FirstOrDefault(candidate => candidate.Id == player)?.Name ?? "Player") + "’s library";

    private static string SearchCriterion(string cardName, string destination)
    {
        string criterion = cardName.Length > 0 ? "Find " + cardName : "Search for a card";
        return destination.Length > 0 ? criterion + " → " + destination : criterion;
    }

    private void DrawLibrarySearchRecipients(GameSnapshot state, Decision decision, TargetSelectionPayload target, Player[] recipients)
    {
        var (anchor, source, _, _) = DecisionSourceAnchor(state, decision);
        if (source is { } sourcePose) canvas.CardFrame(sourcePose with { Size = sourcePose.Size + new Vector2(7) }, Blue * .75f, 2);
        var missingPiles = new List<Player>();
        foreach (var player in recipients) {
            var pile = tableScene?.Seats.FirstOrDefault(seat => seat.PlayerId == player.Id)?.Geometry.Piles.FirstOrDefault(candidate => candidate.Zone == "Library");
            if (pile == null) { missingPiles.Add(player); continue; }
            var pose = pile.Pose;
            int badgeHeight = LibraryRecipientHeight(state, player, 212);
            var badge = new Rectangle(Math.Clamp((int)pose.Center.X - 106, 12, 1376),
                Math.Clamp(pose.Bounds.Bottom + 7, 64, 745 - badgeHeight), 212, badgeHeight);
            string id = "recipient:" + player.Id;
            bool over = IsControlHovered(id);
            canvas.CardFrame(pose with { Size = pose.Size + new Vector2(over ? 12 : 7) }, Blue, over ? 3 : 2);
            DrawLibraryRecipient(state, player, badge);
            // The pile itself and its named gesture are the same legal target.
            // Add it after the pile browser region without disclosing any cards.
            if (!busy) hits.Add(new("recipient-pile:" + player.Id, Scope, pose.Bounds,
                new MatchIntent.ChooseRecipient(player.Id), Shape: pose.Contains));
            if (over && source is { } origin)
                canvas.Arrow(origin.EdgeToward(pose.Center, 5), pose.EdgeToward(origin.Center, 5), Blue, 10);
        }
        const int contextWidth = 410;
        string heading = "Choose whose library", criterion = SearchCriterion(target.Search!.CardName, target.Search.Destination);
        int headingHeight = canvas.TextSize(heading, .79f, contextWidth - 24, 80, true).Y;
        int criterionHeight = canvas.TextSize(criterion, .72f, contextWidth - 24, 80).Y;
        var additional = missingPiles.Select(player => (Player: player, Height: LibraryRecipientHeight(state, player, contextWidth - 12))).ToArray();
        int additionalHeight = additional.Length == 0 ? 0 : 12 + additional.Sum(row => row.Height + 6) - 6;
        int contextHeight = 24 + headingHeight + 9 + criterionHeight + additionalHeight;
        var area = DecisionActionArea(anchor, contextWidth, contextHeight, reserveBattlefield: true);
        canvas.Rounded(area, new Color(8, 16, 21) * .96f, 6);
        canvas.Text(heading, area.X + 12, area.Y + 12, Blue, .79f, area.Width - 24, 80, true);
        canvas.Text(criterion, area.X + 12, area.Y + 12 + headingHeight + 9, Ink, .72f, area.Width - 24, 80);
        int rowY = area.Y + 12 + headingHeight + 9 + criterionHeight + 12;
        foreach (var row in additional) {
            // Seats outside the visible table still retain every legal target.
            // Their named library uses the same explicit player-selection reply.
            var bounds = new Rectangle(area.X + 6, rowY, area.Width - 12, row.Height);
            DrawLibraryRecipient(state, row.Player, bounds);
            rowY += row.Height + 6;
        }
    }

    private int LibraryRecipientHeight(GameSnapshot state, Player player, int width)
        => Math.Max(44, canvas.TextSize(LibraryName(state, player.Id), .76f, width - 24, 80, true).Y + 18);

    private void DrawLibraryRecipient(GameSnapshot state, Player player, Rectangle bounds)
    {
        string id = "recipient:" + player.Id, label = LibraryName(state, player.Id);
        int textHeight = canvas.TextSize(label, .76f, bounds.Width - 24, 80, true).Y;
        PaintControlSurface(id, bounds, new(36, 67, 83), Gold * .75f, !busy);
        canvas.Text(label, bounds.X + 12, bounds.Y + (bounds.Height - textHeight) / 2, Ink, .76f, bounds.Width - 24, 80, true);
        if (!busy) hits.Add(new(id, Scope, bounds, new MatchIntent.ChooseRecipient(player.Id)));
    }
}
