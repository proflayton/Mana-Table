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
        var view = priorityView!;
        string topId = state.Stack[0].Id;
        if (stackQueueTop != topId) { stackQueueTop = topId; stackQueuePage = 0; }
        const int capacity = 3;
        stackQueuePage = Math.Clamp(stackQueuePage, 0, (state.Stack.Length - 1) / capacity);
        // This entire footprint is reserved by the same layout used for cards.
        canvas.Panel(new(545, 285, 435, 263), new Color(11, 22, 28) * .94f, PriorityAccent * .5f, 7);
        string status = view.ResponderId is { } responder ? responder == state.ViewerId ? "YOU CAN RESPOND" : view.ResponderName + " CAN RESPOND"
            : view.Phase == PriorityPhase.Payment ? "PAYING A COST" : view.Phase == PriorityPhase.Decision ? "CHOICE IN PROGRESS" : "PROCESSING";
        PaintPriorityToken(new(562, 304), 8, 1);
        canvas.Text(status, 578, 293, PriorityAccent, .56f, 388, 1, true);
        canvas.Text("1  NEXT TO RESOLVE", 553, 319, Gold, .44f, 156, 1, true);
        for (int slot = 0; slot < capacity; slot++) {
            int index = stackQueuePage * capacity + slot;
            if (index >= state.Stack.Length) break;
            var entry = state.Stack[index];
            var bounds = new Rectangle(551, 337 + slot * 58, 198, 56);
            canvas.Panel(bounds, index == 0 ? new(50, 45, 30) : new(22, 34, 41), index == 0 ? Gold : Muted * .4f, 4);
            canvas.Circle(new(bounds.X + 14, bounds.Y + 15), 10, index == 0 ? Gold : Muted);
            canvas.CenterText((index + 1).ToString(), new(bounds.X + 4, bounds.Y + 5, 20, 20), new(12, 24, 29), .47f, true);
            canvas.Text(entry.Name, bounds.X + 29, bounds.Y + 2, Ink, .52f, bounds.Width - 34, 2, true);
            string owner = entry.ControllerId is { } ownerId ? StateClarity.PlayerName(state, ownerId) : "Owner not reported";
            canvas.Text((entry.Ability ? "Ability" : "Spell") + " · " + owner, bounds.X + 5, bounds.Y + 40, Muted, .37f, bounds.Width - 10, 1);
            int captured = index;
            hits.Add(new("stack-row:" + entry.Id, Scope, bounds, () => OpenStack(captured)));
            stackRows.Add(new(entry.Id, index + 1, bounds));
        }
        // The face remains the production card presentation with its own hit.
        // A compact queue makes the order and controllers readable beside it.
        Button("stack", "Stack · " + state.Stack.Length, 551, 514, 158, () => OpenStack(), height: 28);
        if (state.Stack.Length > capacity) {
            Button("stack-queue-prev", "‹", 918, 504, 24, () => stackQueuePage--, stackQueuePage > 0, height: 28);
            Button("stack-queue-next", "›", 948, 504, 24, () => stackQueuePage++, (stackQueuePage + 1) * capacity < state.Stack.Length, height: 28);
            canvas.Text($"{stackQueuePage * capacity + 1}–{Math.Min(state.Stack.Length, (stackQueuePage + 1) * capacity)} / {state.Stack.Length}", 837, 512, Muted, .44f, 78, 1);
        }
    }
}
