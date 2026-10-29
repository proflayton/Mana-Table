using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private bool pendingHoldModifier;
    private UndoAction? CurrentUndoAction => fixture == null && !busy && !session.NeedsRecovery && match != null
        ? UndoFacts.Command(match) : null;
    private string UndoActionLabel => "Undo tap " + match!.Undo!.Offer!.SourceName;
    private const string UndoControlPrefix = "undo-certified-action:";

    private int UndoControlHeight(int width) => CurrentUndoAction == null ? 0
        : canvas.TextSize(UndoActionLabel, .65f, width - 44, 80, true).Y
            + canvas.TextSize("Ctrl+Z", .5f, width - 44, 1).Y + 15;

    private void DrawUndoControl(Rectangle bounds)
    {
        if (CurrentUndoAction is not { } command) return;
        // The offer identity is part of the physical press target. Replacing an
        // offer in the same decision cannot turn an old press into a new Undo.
        string id = UndoControlPrefix + command.OfferId;
        var intent = new MatchIntent.SubmitCommand(command);
        bool over = IsControlHovered(id), down = over && pressed?.Id == id;
        var color = over ? Ink : Muted;
        if (over) {
            canvas.Rounded(bounds, Blue * (down ? .17f : .07f), 4);
            canvas.Line(new(bounds.X + 28, bounds.Bottom - 2), new(bounds.Right - 8, bounds.Bottom - 2), Blue, 1);
        }
        var bend = new Vector2(bounds.X + 15, bounds.Y + 17);
        canvas.Line(bend + new Vector2(7, 8), bend + new Vector2(7, -4), color, 2);
        canvas.Arrow(bend + new Vector2(7, -4), bend + new Vector2(-7, -4), color, 2);
        int labelHeight = canvas.TextSize(UndoActionLabel, .65f, bounds.Width - 44, 80, true).Y;
        canvas.Text(UndoActionLabel, bounds.X + 32, bounds.Y + 4, color, .65f, bounds.Width - 44, 80, true);
        canvas.Text("Ctrl+Z", bounds.X + 32, bounds.Y + 8 + labelHeight, over ? Blue : Muted, .5f, bounds.Width - 44, 1);
        hits.Add(new(id, ScopeFor(intent), bounds, intent));
    }

    private bool UndoShownAction()
    {
        if (CurrentUndoAction is not { } current || OverlayOpen || textFocus || chatFocus
            || dragging || pressed != null || boardPanning || spreadPanning || AbilityActionPending) return false;
        // A key answers the offer that was actually displayed, not a different
        // offer accepted at the beginning of this update before recomposition.
        var shown = hits.Published.LastOrDefault(hit => hit.Id == UndoControlPrefix + current.OfferId
            && hit.Intent is MatchIntent.SubmitCommand { Command: UndoAction command } && command == current);
        return shown != null && CurrentHit(shown) && Send(current);
    }

    private void UpdateMatchControlKeys(KeyboardState keys, bool available)
    {
        bool control = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
        bool wasControl = previousKeys.IsKeyDown(Keys.LeftControl) || previousKeys.IsKeyDown(Keys.RightControl);
        if (!available || match == null) { pendingHoldModifier = false; return; }
        if (control && !wasControl) pendingHoldModifier = true;
        bool undo = control && keys.IsKeyDown(Keys.Z) && previousKeys.IsKeyUp(Keys.Z)
            && keys.GetPressedKeys().All(key => key is Keys.LeftControl or Keys.RightControl or Keys.Z);
        if (undo) { pendingHoldModifier = false; UndoShownAction(); }
        else if (keys.GetPressedKeys().Any(key => key is not (Keys.LeftControl or Keys.RightControl))) pendingHoldModifier = false;
        if (!control && wasControl && pendingHoldModifier) {
            pendingHoldModifier = false; ToggleHoldControl();
        }
    }

    private void ReconcileRestoredInteraction()
    {
        StopQuietPriority(); StopStackResolution(); StopGroupBlocks();
        finiteProgressProjection = null; lastFiniteProgress = null;
        CancelReleasedPlayIntent(); pendingHoldModifier = false;
        turnPassRequest = null; selectedCombat = null; dragging = false; dragSource = pressed = null; ClearCardDrag();
        ClearCardReading(); ClearReaderActions();
        inspected = null; inspectedChoice = null; inspectedStack = null; inspectedAction = null;
        pendingPreviewReference = null; pendingPreviewBounds = null; pendingPreviewCardId = null;
        expandedStackWorkload = ""; workloadSnapshot = stackEligibilitySnapshot = null;
        cachedWorkloads = []; cachedStackEligibility = false;
        publicCollectionReturns.Clear(); deferredCardComposition.Clear();
        if (overlay is "inspect" or "stack" or "action" or "ability-auto-pass") overlay = "";
        pointerSurface = null; hovered = null; notice = ""; noticeUntil = 0;
        hits.RevokePublished(); Frame = null;
        // Camera, chat, audio preferences and historical action records retain
        // their forward-moving owners; this is not a table/viewer reset.
    }
}
