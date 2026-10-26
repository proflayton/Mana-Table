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
    // This workspace displays a cost reported by the engine. Counter names,
    // quantities and the affected occurrence never come from prompt parsing.
    private bool DrawCounterPaymentDecision(GameSnapshot state)
    {
        if (session.NeedsRecovery || state.Decision is not { } decision) return false;
        if (decisions.Presentation?.Payload is OptionsPayload { CounterPayment: { } choice }) {
            DrawCounterTypePayment(state, decision, choice);
            return true;
        }
        if (decisions.Presentation?.Payload is not EntitySelectionPayload { CounterPayment: { } payment } entities) return false;
        var (anchor, source, card, _) = DecisionSourceAnchor(state, decision);
        const int width = 390;
        string heading = payment.Remaining == 0 ? "Counters chosen" : payment.Amount == 1 ? "Pay with a counter" : $"Pay with {payment.Amount} counters";
        string name = card is { FaceDown: false } ? card.Name : "Pay the ability cost";
        string progress = payment.Amount > 1 ? $"{payment.Amount - payment.Remaining} / {payment.Amount} chosen" : "";
        int nameHeight = canvas.TextSize(name, .72f, width - 24, 80, true).Y;
        int headingHeight = canvas.TextSize(heading, .85f, width - 24, 80, true).Y;
        int progressHeight = progress.Length == 0 ? 0 : canvas.TextSize(progress, .69f, width - 24, 80).Y + 8;
        int height = 32 + nameHeight + headingHeight + progressHeight + 12 + 48;
        var area = DecisionActionArea(anchor, width, height, reserveBattlefield: true);
        decisionPanelBounds = area;
        canvas.Rounded(area, new Color(8, 16, 21) * .96f, 6);
        canvas.Text(name, area.X + 12, area.Y + 10, Gold, .72f, area.Width - 24, 80, true);
        canvas.Text(heading, area.X + 12, area.Y + 17 + nameHeight, Ink, .85f, area.Width - 24, 80, true);
        if (progress.Length > 0)
            canvas.Text(progress, area.X + 12, area.Y + 24 + nameHeight + headingHeight, new(154, 224, 203), .69f, area.Width - 24, 80);
        if (!entities.Complete.Enabled || !entities.Cancel.Enabled)
            canvas.Text("Ability cost", area.Right - 114, area.Bottom - 35, Muted, .56f, 102, 1);
        if (entities.Complete.Enabled)
            Button("confirm", entities.Complete.Label, area.X + 10, area.Bottom - 54, 170,
                new MatchIntent.ConfirmDecision(), !busy, true, 44);
        else if (entities.Cancel.Enabled)
            Button("cancel", entities.Cancel.Label, area.X + 10, area.Bottom - 54, 170,
                new MatchIntent.CancelDecision(), !busy, height: 44);
        if (entities.Complete.Enabled && entities.Cancel.Enabled)
            Button("cancel", entities.Cancel.Label, area.X + 188, area.Bottom - 54, 170,
                new MatchIntent.CancelDecision(), !busy, height: 44);
        if (source is { } origin) {
            canvas.CardFrame(origin with { Size = origin.Size + new Vector2(7) }, Gold * .8f, 2);
            LinkCounterPayment(origin, area, Gold);
        }
        DrawHoveredCounterDonor(source, area);
        return true;
    }

    private void DrawCounterTypePayment(GameSnapshot state, Decision decision, CounterPaymentChoice payment)
    {
        var affected = decision with { Context = decision.Context with { Source = payment.Permanent } };
        var (anchor, pose, card, _) = DecisionSourceAnchor(state, affected);
        string heading = payment.Amount == 1 ? "Pay with a counter" : $"Pay with {payment.Amount} counters";
        string name = card is { FaceDown: false } ? card.Name : "Face-down permanent";
        int columns = Math.Max(1, (payment.Options.Length + 6) / 7);
        int rows = (payment.Options.Length + columns - 1) / columns;
        int width = Math.Min(1480, 420 * columns + 10 * (columns - 1));
        int nameHeight = canvas.TextSize(name, .76f, width - 24, 80, true).Y;
        int headingHeight = canvas.TextSize(heading, .86f, width - 24, 80, true).Y;
        int columnWidth = (width - 24 - 10 * (columns - 1)) / columns;
        int[] rowHeights = new int[rows];
        for (int i = 0; i < payment.Options.Length; i++)
            rowHeights[i % rows] = Math.Max(rowHeights[i % rows], Math.Max(58,
                canvas.TextSize(payment.Options[i].Label, .8f, columnWidth - 142, 80, true).Y + 24));
        int header = 28 + nameHeight + headingHeight;
        int height = header + rowHeights.Sum() + (rows - 1) * 8 + 12;
        var area = DecisionActionArea(anchor, width, height, reserveBattlefield: true);
        decisionPanelBounds = area;
        canvas.Rounded(area, new Color(8, 16, 21) * .96f, 6);
        canvas.Text(name, area.X + 12, area.Y + 10, Gold, .76f, area.Width - 24, 80, true);
        canvas.Text(heading, area.X + 12, area.Y + 17 + nameHeight, Ink, .86f, area.Width - 24, 80, true);
        for (int i = 0; i < payment.Options.Length; i++) {
            var option = payment.Options[i];
            int column = i / rows, row = i % rows;
            int y = area.Y + header + rowHeights.Take(row).Sum() + row * 8;
            var bounds = new Rectangle(area.X + 12 + column * (columnWidth + 10), y, columnWidth, rowHeights[row]);
            string id = "counter-payment:" + option.Index;
            PaintControlSurface(id, bounds, new(27, 44, 48), new(74, 112, 115), !busy);
            PaintCounterGlyph(new(bounds.X + 12, bounds.Center.Y - 6, 12, 12), new(154, 224, 203));
            canvas.Text(option.Label, bounds.X + 36, bounds.Y + 12, Ink, .8f, bounds.Width - 154, 80, true);
            string amount = option.Available + " \u2192 " + (option.Available - payment.Amount);
            canvas.Text("Available", bounds.Right - 112, bounds.Y + 5, Muted, .48f, 100, 1);
            canvas.Text(amount, bounds.Right - 112, bounds.Y + 24, Ink, .78f, 100, 1, true);
            if (!busy) hits.Add(new(id, Scope, bounds, new MatchIntent.ChooseCounterPayment(option.Index)));
        }
        if (pose is { } permanent) {
            canvas.CardFrame(permanent with { Size = permanent.Size + new Vector2(7) }, Gold * .8f, 2);
            LinkCounterPayment(permanent, area, Gold);
        }
    }

    private void DrawHoveredCounterDonor(Pose? source, Rectangle controls)
    {
        var hit = HitAt(pointer);
        if (hit?.Card is not { } card || !CurrentHit(hit) || !IsCardActionable(card)) return;
        string id = CardViews.Identity(card);
        var donor = matchPresentation.Cards.Cards.LastOrDefault(entry => entry.Id == id);
        var pose = BattlefieldFocused && battlefieldFocusPlan?.Tile(id) is { } tile ? tile.ArtPose : donor?.Display;
        if (pose is not { } recipient) return;
        canvas.CardFrame(recipient with { Size = recipient.Size + new Vector2(9) }, Blue, 2);
        if (source is { } origin && !origin.Bounds.Intersects(recipient.Bounds))
            canvas.Arrow(origin.EdgeToward(recipient.Center, 5), recipient.EdgeToward(origin.Center, 5), Gold, 10);
        var counters = CardPresentation.Counters(card);
        if (counters.Length == 0 || readingId == id) return;
        string text = string.Join("\n", counters.Select(counter => counter.Value + " " + counter.Key));
        int width = Math.Max(180, Math.Min(350, counters.Max(counter => canvas.TextWidth(counter.Value + " " + counter.Key, .72f, true)) + 28));
        int height = canvas.TextSize(text, .72f, width - 24, 80, true).Y + 20;
        var available = DecisionWorkspaceLayout.Place(recipient.Bounds, width, height,
            matchPresentation.Cards.Cards.Select(entry => entry.Display.Bounds).Append(controls).ToArray(), true);
        var area = new Rectangle(available.X, available.Y, width, height);
        canvas.Rounded(area, new Color(8, 16, 21) * .97f, 5);
        canvas.Text(text, area.X + 12, area.Y + 10, new(154, 224, 203), .72f, area.Width - 24, 80, true);
        LinkCounterPayment(recipient, area, Blue);
    }

    private void LinkCounterPayment(Pose source, Rectangle area, Color accent)
    {
        var end = new Pose(area.Center.ToVector2(), area.Size.ToVector2()).EdgeToward(source.Center, 5);
        canvas.Line(source.EdgeToward(end, 5), end, accent * .6f, 1.5f);
    }
}
