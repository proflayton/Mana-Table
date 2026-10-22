using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private static readonly Color PriorityAccent = new(100, 230, 209);
    private readonly PriorityTransitionFeedback priorityTransitions = new();
    private PriorityView? priorityView;
    private int stackQueuePage;
    private string stackQueueTop = "";
    private readonly List<StackRowPaint> stackRows = [];
    private readonly Dictionary<int, Rectangle> prioritySeats = [];
    internal sealed record StackRowPaint(string Id, int Position, Rectangle Bounds);

    private void DrawPrioritySeat(Player player, Vector2 center, float radius)
    {
        if (priorityView?.ResponderId != player.Id || player.Eliminated) return;
        float pulse = preferences.ReducedMotion ? 1 : .85f + .15f * MathF.Sin((float)now * 3);
        canvas.Ring(center, radius + 9, PriorityAccent * pulse, 3);
        var token = center + new Vector2(-radius * .75f, -radius * .75f);
        PaintPriorityToken(token, 12, 1);
        prioritySeats[player.Id] = new((int)token.X - 13, (int)token.Y - 13, 26, 26);
    }

    private void DrawPriorityHandoff()
    {
        if (preferences.ReducedMotion || OverlayOpen || priorityTransitions.Latest is not {
                ResponderChanged: true, PreviousResponderId: { } previous, ResponderId: { } current } cue
            || now - cue.Started is < 0 or > .55 || priorityView?.ResponderId != current) return;
        var state = match!;
        if (!state.Players.Any(player => player.Id == previous) || !state.Players.Any(player => player.Id == current)) return;
        Vector2 Center(int id) => TableLayout.Hero(id == state.ViewerId ? 3 : TableLayout.SeatIndex(state, id));
        float t = (float)((now - cue.Started) / .55);
        t = t * t * (3 - 2 * t);
        var position = Vector2.Lerp(Center(previous), Center(current), t);
        PaintPriorityToken(position, 9, 1);
    }

    private void PaintPriorityToken(Vector2 center, float radius, float alpha)
    {
        canvas.Circle(center, radius + 2, new Color(10, 27, 31) * alpha);
        canvas.Circle(center, radius, PriorityAccent * alpha);
        canvas.Polygon([center + new Vector2(-3, -6), center + new Vector2(6, 0), center + new Vector2(-3, 6)], new Color(10, 27, 31) * alpha);
    }

    private void DrawStackFlow()
    {
        var state = match!;
        if (state.Stack.Length == 0) return;
        if (TableStudyEnabled) { DrawStudyStackFlow(); return; }
        var view = priorityView!;
        string topId = state.Stack[0].Id;
        if (stackQueueTop != topId) { stackQueueTop = topId; stackQueuePage = 0; }
        const int capacity = 3;
        stackQueuePage = Math.Clamp(stackQueuePage, 0, (state.Stack.Length - 1) / capacity);
        string status = view.ResponderId is { } responder ? responder == state.ViewerId ? "Your priority" : view.ResponderName + " has priority"
            : view.Phase == PriorityPhase.Payment ? "Paying a cost" : view.Phase == PriorityPhase.Decision ? "Choice in progress" : "Processing";
        int y = 319;
        for (int slot = 0; slot < capacity; slot++) {
            int index = stackQueuePage * capacity + slot;
            if (index >= state.Stack.Length) break;
            var entry = state.Stack[index];
            bool expanded = slot == 0;
            string owner = entry.ControllerId is { } ownerId ? StateClarity.PlayerName(state, ownerId) : "Owner not reported";
            string effect = !string.IsNullOrWhiteSpace(entry.Text) ? entry.Text
                : !entry.Ability && entry.Card is { Text.Length: > 0 } spell ? spell.Text : "Effect text not reported.";
            int height = expanded ? Math.Clamp(PendingEffectHeaderHeight(entry, owner)
                + canvas.TextSize(effect, .86f, 407, 80, true).Y + (entry.Targets.Length > 0 ? 27 : 0) + 12, 134, 220) : 46;
            var bounds = new Rectangle(545, y, 435, height);
            canvas.Panel(bounds, new(17, 29, 36), index == 0 ? Gold * .7f : Muted * .45f, 4);
            if (expanded) PaintPendingEffect(entry, index, bounds, owner, effect);
            else {
                canvas.Circle(new(bounds.X + 17, bounds.Y + 18), 10, Muted);
                canvas.CenterText((index + 1).ToString(), new(bounds.X + 7, bounds.Y + 8, 20, 20), new(12, 24, 29), .47f, true);
                canvas.Text(entry.Name + " · " + owner, bounds.X + 34, bounds.Y + 3, Ink, .54f, bounds.Width - 45, 1, true);
                canvas.Text(effect, bounds.X + 34, bounds.Y + 24, Muted, .48f, bounds.Width - 45, 1);
                AddPendingSourceReference(entry, new(bounds.X + 34, bounds.Y + 1,
                    Math.Min(bounds.Width - 45, canvas.TextWidth(entry.Name + " · " + owner, .54f, true) + 6), 22));
                string captured = entry.Id;
                hits.Add(new("stack-effect:" + entry.Id, Scope, new(bounds.X + 34, bounds.Y + 23, bounds.Width - 45, 22), () => InspectPendingEffect(captured)));
            }
            stackRows.Add(new(entry.Id, index + 1, bounds));
            y = bounds.Bottom + 5;
        }
        canvas.Rounded(new(545, 285, 435, 34), new(11, 22, 28), 5);
        PaintPriorityToken(new(562, 304), 8, 1);
        canvas.Text(status, 578, 293, PriorityAccent, .56f, 388, 1, true);
        if (state.Stack.Length > capacity) {
            Button("stack-queue-prev", "‹", 905, y + 1, 32, () => stackQueuePage--, stackQueuePage > 0, height: 28);
            Button("stack-queue-next", "›", 945, y + 1, 32, () => stackQueuePage++, (stackQueuePage + 1) * capacity < state.Stack.Length, height: 28);
            canvas.Text($"{stackQueuePage * capacity + 1}–{Math.Min(state.Stack.Length, (stackQueuePage + 1) * capacity)} / {state.Stack.Length} pending", 555, y + 7, Muted, .51f, 338, 1);
        }
        if (stackRows.FirstOrDefault() is { } expandedRow) {
            var occupied = new Rectangle(545, 285, 435, y - 285 + (state.Stack.Length > capacity ? 34 : 0));
            DrawPendingEffectConnections(state.Stack[expandedRow.Position - 1], expandedRow.Bounds, occupied);
        }
    }

    private void InspectPendingEffect(string id)
    {
        if (match == null) return;
        int index = Array.FindIndex(match.Stack, item => item.Id == id);
        if (index < 0) return;
        // A real counterspell target remains a game choice. Otherwise the one
        // combined surface reads this exact effect without activating its source.
        if (!match.Stack[index].Ability && match.Stack[index].Card is { } card && IsCardActionable(card)) CardClick(card);
        else OpenStack(index);
    }

    private int PendingEffectHeaderHeight(StackItem entry, string owner) => Math.Max(64,
        21 + canvas.TextSize(entry.Name, .67f, 347, 2, true).Y + canvas.TextSize("Pending · " + owner, .5f, 347, 1).Y);

    private void PaintPendingEffect(StackItem entry, int index, Rectangle bounds, string owner, string effect)
    {
        var art = new Rectangle(bounds.X + 12, bounds.Y + 12, 52, 44);
        if (entry.Card is { FaceDown: false } source && resources.Card(source) is { } image) {
            var crop = new Rectangle((int)(image.Width * .12f), (int)(image.Height * .14f),
                (int)(image.Width * .76f), (int)(image.Height * .38f));
            canvas.Sprite(image, art.Center.ToVector2(), art.Size.ToVector2(), 0, Color.White, crop);
        } else canvas.Gradient(art, new(43, 58, 64), new(28, 40, 48));
        int nameHeight = canvas.TextSize(entry.Name, .67f, bounds.Width - 88, 2, true).Y;
        canvas.Text(entry.Name, bounds.X + 76, bounds.Y + 10, Ink, .67f, bounds.Width - 88, 2, true);
        string order = match!.Stack.Length == 1 ? "Pending" : (index + 1) + (index == 0 ? " · Next" : " · Waiting");
        canvas.Text(order + " · " + owner, bounds.X + 76, bounds.Y + 13 + nameHeight,
            index == 0 ? Gold : Muted, .5f, bounds.Width - 88, 1);
        var sourceBounds = new Rectangle(art.X, bounds.Y + 8,
            Math.Min(bounds.Width - 24, 64 + Math.Max(canvas.TextWidth(entry.Name, .67f, true), canvas.TextWidth(order + " · " + owner, .5f))),
            PendingEffectHeaderHeight(entry, owner) - 8);
        AddPendingSourceReference(entry, sourceBounds);
        if (IsControlHovered("stack-source:" + entry.Id)) canvas.CardFrame(new(art.Center.ToVector2(), art.Size.ToVector2()), HoverAccent, 2);
        int bodyY = bounds.Y + PendingEffectHeaderHeight(entry, owner);
        int targetHeight = entry.Targets.Length > 0 ? 27 : 0;
        int bodyHeight = Math.Max(25, bounds.Bottom - bodyY - targetHeight - 12);
        string[] pages = canvas.TextPages(effect, .86f, bounds.Width - 28, bodyHeight, 80, true);
        canvas.Text(pages[0], bounds.X + 14, bodyY, Ink, .86f, bounds.Width - 28, 80, true);
        if (entry.Targets.Length > 0) {
            int targetY = bounds.Bottom - 27;
            canvas.Arrow(new(bounds.X + 14, targetY + 11), new(bounds.X + 31, targetY + 11), Red, 3);
            DrawPendingTargetReferences(entry, new(bounds.X + 39, targetY + 1, bounds.Width - 53, 24));
        }
        if (pages.Length > 1) {
            canvas.Text("…", bounds.Right - 24, bounds.Bottom - 26, Muted, .75f, 18, 1, true);
            string captured = entry.Id;
            hits.Add(new("stack-effect:" + entry.Id, Scope, new(bounds.Right - 30, bounds.Bottom - 29, 28, 28), () => InspectPendingEffect(captured)));
        }
    }
}
