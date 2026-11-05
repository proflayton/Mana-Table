using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    internal const double QuickManaDoubleClickSeconds = .5;
    private const int QuickManaClickDistance = 8;
    private static readonly string[] QuickManaColorOrder = ["W", "U", "B", "R", "G", "C"];
    private sealed record ManaClick(SourceActionContext Context, Point Point, Rectangle Anchor,
        BoardView Camera, Point Viewport, double ReleasedAt, bool Retired = false);
    private sealed record ManaOption(string ActionId, ImmutableDictionary<string, int> Colors, string Text);
    private sealed record ManaGesture(SourceActionContext Context, Point Point, BoardView Camera,
        Point Viewport, long Generation, long OfferGeneration, ManaOption[]? Options = null);
    private ManaClick? manaClick, secondManaPress;
    private ManaGesture? manaGesture;
    // Accepted submission is not consumption. Require a fresh concrete ordinary
    // publication before this shortcut may submit another mana command.
    private SourceActionContext? submittedManaContext;
    private long manaGestureGeneration;
    private Rectangle quickManaBounds;
    private bool dismissManaPress;

    private Card? CurrentQuickManaSource(SourceActionContext context)
    {
        if (!InputReady || busy || session.NeedsRecovery || !SourceActionsSupported || fixture != null
            || submittedManaContext is { } submitted && submitted.GameId == context.GameId
                && submitted.ViewerId == context.ViewerId && submitted.DecisionId == context.DecisionId
                && submitted.Revision == context.Revision
            || match is not { Status: "playing", Decision: { Payload: PriorityPayload or PaymentPayload } decision } state
            || state.DecisionOwnerId != state.ViewerId
            || decision.Payload is PriorityPayload && state.PriorityPlayerId != state.ViewerId
            || decision.Payload is PaymentPayload payment && payment.PayerId != state.ViewerId
            || state.FiniteStackYield is { Status: FiniteStackYieldStatus.Active }) return null;
        var sources = state.Viewer!.Zone("Battlefield").Cards.Where(card => card.Key == context.SourceCardKey
            && CardViews.Identity(card) == context.SourceCardId).ToArray();
        return sources is [var source] && source is { FaceDown: false, Tapped: false, PhasedOut: false }
            && ReaderActionContext(source) == context && CardViews.CanReadActionSource(state, source.Key) ? source : null;
    }

    private bool QuickManaInputAvailable => InputReady && !ActivityOpen && !ExperienceOpen && !updatesOpen
        && !HistoryOpen && !historyInspector && !textFocus && !chatFocus
        && !boardPanning && !boardKeyboardPanning && !spreadPanning && !spreadKeyboardPanning
        && (!OverlayOpen || BattlefieldFocused);

    private void ClearQuickMana()
    {
        manaClick = secondManaPress = null;
        manaGesture = null;
        quickManaBounds = Rectangle.Empty;
    }

    private void ReconcileQuickMana()
    {
        if (submittedManaContext is { } submitted && match is { } current
            && (current.Id != submitted.GameId || current.ViewerId != submitted.ViewerId
                || current.Decision is { } decision && (current.Revision != submitted.Revision
                    || decision.Id != submitted.DecisionId))) submittedManaContext = null;
        if (manaClick is { } click) {
            if (!QuickManaInputAvailable || click.Camera != boardView || click.Viewport != boardViewport
                || now < click.ReleasedAt || now - click.ReleasedAt > QuickManaDoubleClickSeconds) manaClick = null;
            // The first click no longer grants authority, but its bounded second
            // release must not fall through into an unrelated ordinary action.
            else if (!click.Retired && CurrentQuickManaSource(click.Context) == null)
                manaClick = click with { Retired = true };
        }
        if (manaGesture is { } gesture && (CurrentQuickManaSource(gesture.Context) == null
            || gesture.Camera != boardView || gesture.Viewport != boardViewport || !QuickManaInputAvailable
            || dragging || boardPanning || spreadPanning || CardCollectionOpen
            || sourceActions.View.Context != gesture.Context || sourceActions.View.Generation != gesture.OfferGeneration)) ClearQuickMana();
    }

    private void BeginQuickManaPress()
    {
        secondManaPress = null;
        dismissManaPress = false;
        if (manaGesture?.Options is { Length: > 1 } && !quickManaBounds.Contains(pointer)) {
            ClearQuickMana(); dismissManaPress = true; return;
        }
        if (manaGesture != null && manaGesture.Options == null) ClearQuickMana();
        if (manaClick is not { } first || !QuickManaInputAvailable || CardCollectionOpen
            || boardPanning || spreadPanning
            || first.Camera != boardView || first.Viewport != boardViewport || !first.Anchor.Contains(pointer)
            || now < first.ReleasedAt || now - first.ReleasedAt > QuickManaDoubleClickSeconds
            || Vector2.Distance(first.Point.ToVector2(), pointer.ToVector2()) > QuickManaClickDistance) return;
        secondManaPress = CurrentQuickManaSource(first.Context) == null ? first with { Retired = true } : first;
    }

    private void RememberManaInspection(Hit hit)
    {
        if (!QuickManaInputAvailable || CardCollectionOpen || hit.Zone is not ("Battlefield" or "BattlefieldFocus")
            || hit.Intent is not (MatchIntent.InspectOccurrence or MatchIntent.InspectCard or MatchIntent.ReadFocusedCard)
            || hit.Card is not { } card || ReaderActionContext(card) is not { } context
            || CurrentQuickManaSource(context) == null
            || Vector2.Distance(pressPoint.ToVector2(), pointer.ToVector2()) > QuickManaClickDistance) {
            manaClick = null; return;
        }
        manaClick = new(context, pointer, hit.Bounds, boardView, boardViewport, now);
    }

    private bool FinishQuickManaPress(bool cancelled)
    {
        if (dismissManaPress) { dismissManaPress = false; return true; }
        var first = secondManaPress; secondManaPress = null;
        if (first == null) return false;
        manaClick = null;
        if (cancelled || first.Retired || dragging || !QuickManaInputAvailable || CardCollectionOpen
            || CurrentQuickManaSource(first.Context) == null || first.Camera != boardView
            || first.Viewport != boardViewport || !first.Anchor.Contains(pointer)
            || now < first.ReleasedAt || now - first.ReleasedAt > QuickManaDoubleClickSeconds
            || Vector2.Distance(first.Point.ToVector2(), pointer.ToVector2()) > QuickManaClickDistance) return true;
        sourceActions.Observe(first.Context);
        manaGesture = new(first.Context, pointer, boardView, boardViewport, ++manaGestureGeneration,
            sourceActions.View.Generation);
        return true;
    }

    private static ManaOption[] QuickManaOptions(SourceActionView view) => view.Actions
        .Where(action => action.Kind == "activate" && action.QuickMana is { } quick && quick.IsValid())
        .SelectMany(action => action.QuickMana!.Outputs.Select(output => new ManaOption(action.Id, output.Colors, action.Text)))
        .Take(33).ToArray();

    private void ContinueQuickMana()
    {
        ReconcileQuickMana();
        if (manaGesture is not { Options: null } gesture) return;
        var view = sourceActions.View;
        if (view.Status == SourceActionStatus.Loading) return;
        if (view.Status != SourceActionStatus.Ready) { ClearQuickMana(); return; }
        var options = QuickManaOptions(view);
        if (options.Length is 0 or > 32) { ClearQuickMana(); return; }
        // Explicit manual mana takes control before creating a floating pool.
        // A successful ACK still requires the ordinary command readback.
        TakePriorityControl();
        if (options.Length == 1) {
            var command = sourceActions.TryCreateCommand(gesture.Context, options[0].ActionId, options[0].Colors);
            ClearQuickMana();
            if (command != null && Send(command)) {
                submittedManaContext = gesture.Context;
                CollapseCardReading(); ClearReaderActions();
            }
        } else {
            manaGesture = gesture with { Options = options };
            CollapseCardReading();
        }
    }

    private void ChooseQuickMana(long generation, int index)
    {
        if (manaGesture is not { Options: { } options } gesture || gesture.Generation != generation
            || index < 0 || index >= options.Length || CurrentQuickManaSource(gesture.Context) == null
            || sourceActions.View.Context != gesture.Context || sourceActions.View.Generation != gesture.OfferGeneration
            || sourceActions.View.Status != SourceActionStatus.Ready) return;
        var option = options[index];
        var command = sourceActions.TryCreateCommand(gesture.Context, option.ActionId, option.Colors);
        ClearQuickMana();
        if (command != null && Send(command)) {
            submittedManaContext = gesture.Context;
            CollapseCardReading(); ClearReaderActions();
        }
    }

    private string QuickManaHint(Card card)
    {
        if (!ReaderActionsMatch(card) || ReaderActionContext(card) is not { } context
            || CurrentQuickManaSource(context) == null) return "";
        int count = QuickManaOptions(sourceActions.View).Length;
        return count == 1 ? "Double-click to tap for mana" : count is > 1 and <= 32 ? "Double-click to choose mana" : "";
    }

    private void DrawQuickMana()
    {
        quickManaBounds = Rectangle.Empty;
        if (manaGesture is not { Options: { Length: > 1 } options } gesture
            || CurrentQuickManaSource(gesture.Context) == null) return;
        bool descriptions = options.GroupBy(option => string.Join(";", QuickManaColorOrder
            .Select(color => option.Colors.GetValueOrDefault(color)))).Any(group => group.Count() > 1);
        int ManaWidth(ManaOption option) => option.Colors.Sum(part => 36
            + (part.Value == 1 ? 0 : canvas.TextWidth(part.Value.ToString(), .65f, true) + 3));
        int cellWidth = Math.Max(descriptions ? 240 : 76, options.Max(ManaWidth) + 16);
        int columns = Math.Max(1, Math.Min(options.Length, Math.Min(6, 1540 / cellWidth)));
        int textHeight = descriptions ? options.Max(option => canvas.TextSize(option.Text, .55f, cellWidth - 12, 80, true).Y) + 6 : 0;
        int rowHeight = 62 + textHeight, rows = (options.Length + columns - 1) / columns;
        int width = columns * cellWidth + 12, height = rows * rowHeight + 34;
        if (width > 1560 || height > DesignHeight - 84) {
            var source = CurrentQuickManaSource(gesture.Context);
            ClearQuickMana();
            if (source != null) Inspect(source);
            return;
        }
        var area = new Rectangle(Math.Clamp(gesture.Point.X - cellWidth / 2 - 6, 20, 1580 - width),
            Math.Clamp(gesture.Point.Y - 34 - rowHeight / 2, 64, DesignHeight - 20 - height), width, height);
        quickManaBounds = area;
        canvas.Rounded(area, new Color(10, 22, 29) * .98f, 7);
        canvas.Text("Choose mana", area.X + 10, area.Y + 7, Ink, .62f, area.Width - 20, 1, true);
        hits.Add(new("quick-mana-surface:" + gesture.Generation, Scope, area, new UiIntent.Block(InteractionScopeKind.Current)));
        for (int index = 0; index < options.Length; index++) {
            var option = options[index];
            var bounds = new Rectangle(area.X + 6 + index % columns * cellWidth,
                area.Y + 30 + index / columns * rowHeight, cellWidth, rowHeight);
            string id = "quick-mana-choice:" + gesture.Generation + ":" + index;
            bool over = IsControlHovered(id);
            if (over) canvas.Rounded(bounds, Blue * .18f, 4);
            int x = bounds.Center.X - ManaWidth(option) / 2;
            foreach (string color in QuickManaColorOrder) {
                if (!option.Colors.TryGetValue(color, out int amount)) continue;
                if (amount > 1) {
                    int countWidth = canvas.TextWidth(amount.ToString(), .65f, true);
                    canvas.Text(amount.ToString(), x, bounds.Y + 17, Ink, .65f, countWidth + 1, 1, true);
                    x += countWidth + 3;
                }
                PaintCostSymbol("{" + color + "}", new(x + 16, bounds.Y + 29), 32);
                x += 36;
            }
            if (descriptions) canvas.Text(option.Text, bounds.X + 6, bounds.Y + 58, Ink, .55f, bounds.Width - 12, 80, true);
            hits.Add(new(id, Scope, bounds, new MatchIntent.ChooseQuickMana(gesture.Generation, index)));
        }
    }
}
