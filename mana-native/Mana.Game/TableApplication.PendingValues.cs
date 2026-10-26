using System.Globalization;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private Rectangle targetingEffectBounds;
    private readonly List<Rectangle> pendingCounterReceiptBounds = [];
    private static readonly Color CounterReceiptAccent = new(154, 224, 203);

    private bool ValuedTargetDecision => decisions.Payload is TargetSelectionPayload { Effect.Kind: "damage" };

    private void DrawTargetingEffect(GameSnapshot state, Decision decision, TargetSelectionPayload target, Card? source)
    {
        var area = TableStudyEnabled ? StudyActionArea : new Rectangle(545, 319, 435, 165);
        targetingEffectBounds = area;
        canvas.Panel(area, new(17, 29, 36), Gold * .75f, 4);
        var art = new Rectangle(area.X + 12, area.Y + 10, 45, 40);
        PaintPendingSourceArt(source, art);
        string name = source is { FaceDown: false } ? source.Name : "Pending effect";
        int nameHeight = canvas.TextSize(name, .67f, area.Width - 84, 80, true).Y;
        canvas.Text(name, art.Right + 12, area.Y + 9, Ink, .67f, area.Width - 84, 80, true);
        int headerHeight = Math.Max(54, nameHeight + 18);
        AddTargetingSourceReference(decision, new(art.X, area.Y + 7, area.Width - 24, headerHeight - 7));
        if (IsControlHovered("targeting-source:" + decision.Id)) canvas.CardFrame(new(art.Center.ToVector2(), art.Size.ToVector2()), HoverAccent, 2);
        PaintPendingDamageValue(target.Effect!, new(area.X + 12, area.Y + headerHeight,
            area.Width - 24, Math.Max(1, area.Height - headerHeight - 8)), decision: decision);
        if (source != null && PendingConnectionCard(CardViews.Identity(source)) is { } board) {
            var end = new Pose(area.Center.ToVector2(), area.Size.ToVector2()).EdgeToward(board.Display.Center, 4);
            var start = board.Display.EdgeToward(end, 4);
            canvas.Line(start, end, Gold * .65f, 2);
            canvas.Circle(end, 3, Gold);
        }
        DrawPendingCounterReceipt(target.Effect!);
    }

    private void PaintPendingSourceArt(Card? card, Rectangle art)
    {
        if (card is { FaceDown: false } && resources.Card(card) is { } image) {
            var crop = new Rectangle((int)(image.Width * .12f), (int)(image.Height * .14f),
                (int)(image.Width * .76f), (int)(image.Height * .38f));
            canvas.Sprite(image, art.Center.ToVector2(), art.Size.ToVector2(), 0, Color.White, crop);
        } else canvas.Gradient(art, new(43, 58, 64), new(28, 40, 48));
    }

    private int PendingDamageCounterHeight(PendingEffect effect, int width)
    {
        if (effect.CounterChange is not { Counters.Length: > 0 } addition) return 0;
        string label = CounterReceiptLabel(addition);
        if (label.Length == 0) return 0;
        string recipient = CounterRecipientName(addition);
        int labelWidth = Math.Min(width / 2, canvas.TextWidth(label, .61f, true) + 8);
        return Math.Max(canvas.TextSize(label, .61f, labelWidth, 80, true).Y,
            canvas.TextSize(recipient, .58f, Math.Max(1, width - labelWidth - 13), 80, true).Y) + 8;
    }

    private void PaintPendingDamageValue(PendingEffect effect, Rectangle bounds, string? instruction = null, Decision? decision = null)
    {
        bool counterContext = effect.CounterChange is { Counters.Length: > 0 } counters && CounterReceiptLabel(counters).Length > 0;
        int instructionHeight = instruction == null ? 0 : canvas.TextSize(instruction, .59f, bounds.Width, 80, true).Y + 3;
        var valueArea = new Rectangle(bounds.X, bounds.Y, counterContext ? bounds.Width / 2 - 10 : bounds.Width,
            Math.Max(1, bounds.Height - instructionHeight));
        string amount = effect.Amount?.ToString(CultureInfo.InvariantCulture) ?? "?";
        float numberSize = valueArea.Height < 62 ? 1.45f : 1.85f;
        int numberWidth = Math.Max(38, canvas.TextWidth(amount, numberSize, true) + 7);
        canvas.CenterText(amount, new(valueArea.X, valueArea.Y - 2, numberWidth, valueArea.Height), Red, numberSize, true);
        int textX = valueArea.X + numberWidth + 8;
        int textWidth = Math.Max(1, valueArea.Width - numberWidth - 8);
        string caption = effect.Amount == null ? "Damage varies" : "base damage"
            + (effect.Distribution == "each" ? " each" : effect.Distribution == "divided" ? " total" : "");
        int captionHeight = canvas.TextSize(caption, .70f, textWidth, 80, true).Y;
        canvas.Text(caption, textX, valueArea.Center.Y - captionHeight / 2, Ink, .70f, textWidth, 80, true);
        if (instruction != null) canvas.Text(instruction, bounds.X, bounds.Bottom - instructionHeight,
            Gold, .59f, bounds.Width, 80, true);
        if (!counterContext || effect.CounterChange is not { } addition) return;
        var row = new Rectangle(valueArea.Right + 20, valueArea.Y, bounds.Right - valueArea.Right - 20, valueArea.Height);
        string label = CounterReceiptLabel(addition);
        string recipient = CounterRecipientName(addition);
        int labelHeight = canvas.TextSize(label, .61f, row.Width, 80, true).Y;
        int recipientHeight = canvas.TextSize(recipient, .58f, row.Width, 80, true).Y;
        int rowTop = row.Center.Y - (labelHeight + recipientHeight + 3) / 2;
        canvas.Line(new(row.Left - 10, row.Top + 3), new(row.Left - 10, row.Bottom - 3), CounterReceiptAccent * .25f, 1);
        canvas.Text(label, row.X, rowTop, CounterReceiptAccent, .61f, row.Width, 80, true);
        canvas.Text(recipient, row.X, rowTop + labelHeight + 3, Ink, .58f, row.Width, 80, true);
        if (addition.Card is { FaceDown: false } card) {
            if (decision != null) AddTargetingSourceReference(decision, row, card, "targeting-counter:");
            else if (match?.Stack.FirstOrDefault(item => item.Effect?.Id == effect.Id) is { } item)
                AddPendingCounterReference(item, card, row);
        }
    }

    private static string CounterReceiptLabel(CounterAddition addition) => string.Join(" · ", addition.Counters
        .Where(counter => counter.Added > 0).Select(counter => "+" + counter.Added.ToString(CultureInfo.InvariantCulture)
            + " " + (counter.Label.Length > 0 ? counter.Label : counter.Type)));

    private string CounterRecipientName(CounterAddition addition) => addition.Card is { FaceDown: false } card
        ? CardViews.Current(match, card).Name
        : addition.PlayerId is { } player ? StateClarity.PlayerName(match!, player) : "Counter recipient";

    private void DrawPendingCounterReceipt(PendingEffect effect)
    {
        if (effect.CounterChange is not { Counters.Length: > 0 } addition) return;
        string label = CounterReceiptLabel(addition);
        if (label.Length == 0) return;
        Pose? recipient = null;
        Vector2? anchor = null;
        if (addition.Card is { FaceDown: false } card && PendingConnectionCard(CardViews.Identity(card)) is { } board) {
            recipient = board.Display;
            anchor = counterAnchors.TryGetValue(board.Id, out var counter) ? counter : board.Display.Point(.8f, .82f);
        } else if (addition.Card == null && addition.PlayerId is { } player && heroPositions.TryGetValue(player, out var hero)) {
            recipient = new Pose(hero, new(player == match!.ViewerId ? 104 : 80));
            anchor = hero + new Vector2(0, player == match!.ViewerId ? -52 : 40);
        }
        if (recipient is not { } origin || anchor is not { } landing) return;
        int width = canvas.TextWidth(label, .66f, true) + 14;
        int height = canvas.TextSize(label, .66f, width, 80, true).Y + 8;
        var face = origin.Bounds;
        var candidates = new List<Rectangle>();
        foreach (int distance in new[] { 7, 37, 67, 107, 157 }) {
            candidates.Add(new(face.Right + distance, face.Bottom - height, width, height));
            candidates.Add(new(face.Left - distance - width, face.Bottom - height, width, height));
            candidates.Add(new(face.Center.X - width / 2, face.Bottom + distance, width, height));
            candidates.Add(new(face.Center.X - width / 2, face.Top - height - distance, width, height));
        }
        var occupied = matchPresentation.Cards.Cards.Where(entry => entry.ChoiceSlot < 0).Select(entry => entry.Display.Bounds)
            .Append(decisionPanelBounds).Concat(stackRows.Select(row => row.Bounds)).Concat(pendingCounterReceiptBounds)
            .Where(bounds => !bounds.IsEmpty).ToList();
        if (!targetingEffectBounds.IsEmpty) occupied.Add(targetingEffectBounds);
        var chosen = candidates.FirstOrDefault(candidate => candidate.Left >= 10 && candidate.Right <= 1590
            && candidate.Top >= 55 && candidate.Bottom <= Math.Min(880, DesignHeight - 12)
            && occupied.All(bounds => !bounds.Intersects(candidate)));
        if (chosen.IsEmpty) return; // The complete receipt remains in the pending effect face.
        pendingCounterReceiptBounds.Add(chosen);
        var end = new Pose(chosen.Center.ToVector2(), chosen.Size.ToVector2()).EdgeToward(landing);
        canvas.Line(landing, end, CounterReceiptAccent * .8f, 2);
        canvas.Circle(landing, 3, CounterReceiptAccent);
        canvas.Rounded(chosen, new(15, 32, 34), 4);
        canvas.Text(label, chosen.X + 7, chosen.Y + 3, CounterReceiptAccent, .66f, chosen.Width - 14, 80, true);
    }

    private void PaintPendingDamageMarker(int amount, Vector2 start, Vector2 target, Rectangle? recipient)
    {
        string text = amount.ToString(CultureInfo.InvariantCulture) + " base";
        int width = Math.Max(28, canvas.TextWidth(text, .66f, true) + 12);
        var occupied = matchPresentation.Cards.Cards.Select(card => card.Display.Bounds)
            .Append(decisionPanelBounds).Append(targetingEffectBounds).Concat(stackRows.Select(row => row.Bounds))
            .Concat(pendingCounterReceiptBounds).Where(bounds => !bounds.IsEmpty).ToList();
        foreach (var hero in heroPositions) {
            int radius = hero.Key == match?.ViewerId ? 56 : 44;
            occupied.Add(new((int)hero.Value.X - radius, (int)hero.Value.Y - radius, radius * 2, radius * 2));
        }
        var viewport = new Rectangle(10, 60, 1580, 720);
        var bounds = recipient is { } face ? EffectTransitionPresentation.ReceiptBounds(face, target, new(width, 30), viewport, occupied)
            : Rectangle.Empty;
        bool atRecipient = !bounds.IsEmpty;
        if (bounds.IsEmpty) bounds = EffectTransitionPresentation.PendingMarkerBounds(start, target, new(width, 30), viewport, occupied);
        if (bounds.IsEmpty) return; // The primary effect face still reports the exact pending base amount.
        if (atRecipient) {
            var edge = new Pose(bounds.Center.ToVector2(), bounds.Size.ToVector2()).EdgeToward(target);
            canvas.Line(edge, target, Red * .65f, 1.5f);
        }
        canvas.Rounded(bounds, new(38, 20, 24), 8);
        canvas.CenterText(text, bounds, Red, .66f, true);
    }

    private void AddTargetingSourceReference(Decision decision, Rectangle bounds, Card? reported = null, string prefix = "targeting-source:")
    {
        var card = reported ?? CardViews.Source(match, decision);
        if (card is not { FaceDown: false }) return;
        var reference = new PendingCardReference("", CardViews.Identity(card), decision.Id);
        if (ResolvePendingReference(reference) is not { } current) return;
        // Hover uses the existing noncommitting full-card preview. A click on
        // this reference never chooses a target or activates the source.
        hits.Add(new(prefix + decision.Id, Scope, bounds, new MatchIntent.PreviewPendingCard(reference.StackId, reference.CardId, reference.DecisionId), current, "StackReference"));
    }
}
