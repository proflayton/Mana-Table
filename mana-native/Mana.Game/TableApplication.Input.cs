using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly ForegroundInput foregroundInput = new();
    internal bool InputReady => foregroundInput.Ready;

    internal void DeactivateInput()
    {
        foregroundInput.Deactivate();
        CancelLocalInput();
    }

    private void CancelLocalInput()
    {
        FinishAudioAdjustment(); audioVolumeFocused = false;
        CancelReleasedPlayIntent();
        pressed = null; dragSource = null; dragging = false; selectedCombat = null;
        ClearCardDrag();
        boardPanning = spreadPanning = boardKeyboardPanning = spreadKeyboardPanning = false;
        boardPointerActivity = false;
        pendingHoldModifier = false;
        pointer = new(-10000, -10000);
    }

    private void UpdateLocalInput(GameTime time, MouseState mouse, KeyboardState keys, Point viewport, bool inputActive)
    {
        boardKeyboardPanning = spreadKeyboardPanning = false;
        boardViewportChanged = boardViewport != viewport; boardViewport = viewport;
        if (!foregroundInput.Admit(inputActive, mouse, keys)) {
            CancelLocalInput();
            // Rebase buttons, keys and wheel without producing a release or
            // accumulating background scroll for the next foreground frame.
            previousMouse = mouse; previousKeys = keys;
            return;
        }
        bool experienceInput = ExperienceOpen && !ActivityOpen && !updatesOpen;
        bool blockedInput = ActivityOpen || experienceInput || updatesOpen;
        bool chatKeys = blockedInput || !CurrentManaReading && ChatKeys(keys);
        if (!chatKeys) { DeckKeys(keys); SetupKeys(keys); }
        pointer = RenderViewport.Fit(viewport, DesignHeight).ToLogical(new(mouse.X, mouse.Y));
        if (UpdateAudioInput(mouse, keys, blockedInput)) {
            previousMouse = mouse; previousKeys = keys;
            return;
        }
        ReconcileCardCollection();
        if (blockedInput) { boardPanning = spreadPanning = false; }
        if (!blockedInput) {
            UpdateCardCollectionPointer(mouse, keys, time.ElapsedGameTime, !chatKeys);
            UpdateBoardCamera(mouse, keys, time.ElapsedGameTime, !chatKeys);
        }
        boardPointerActivity = mouse.X != previousMouse.X || mouse.Y != previousMouse.Y
            || mouse.ScrollWheelValue != previousMouse.ScrollWheelValue || mouse.LeftButton != previousMouse.LeftButton;
        bool escapePressed = !chatKeys && keys.IsKeyDown(Keys.Escape) && previousKeys.IsKeyUp(Keys.Escape);
        bool cancelledDrag = escapePressed && dragSource != null;
        if (cancelledDrag) {
            // Cancellation wins even if the mouse is released in this update.
            CollapseCardReading();
            dragging = false; dragSource = null; pressed = null; ClearCardDrag();
        }
        if (!blockedInput && mouse.RightButton == ButtonState.Pressed && previousMouse.RightButton == ButtonState.Released) {
            if (dragging) { dragging = false; dragSource = null; pressed = null; ClearCardDrag(); }
            else if (HitAt(pointer)?.Card is { } card) Inspect(card);
        }
        if (mouse.LeftButton == ButtonState.Pressed && previousMouse.LeftButton == ButtonState.Released) {
            CancelReleasedPlayIntent();
            pressed = HitAt(pointer); pressPoint = pointer;
            if (!blockedInput && chatOpen && !ChatBounds.Contains(pointer)) chatFocus = false;
            if (!blockedInput && match == null && deckEditor && pressed?.Intent is not DeckIntent.FocusText) { deckTextFocus = ""; textFocus = false; }
            dragSource = (!OverlayOpen || ZoneReadingOpen) && !AbilityActionPending && pressed is { Card: { } source } sourceHit
                && (IsCardActionable(source) || CanQueryCertifiedPlay(source, sourceHit.Zone))
                && CanStartSourceDrag(sourceHit) ? pressed : null; dragging = false;
            BeginCardDrag(dragSource);
        }
        if (mouse.LeftButton == ButtonState.Pressed && dragSource != null && dragSource.Scope == Scope && LeftCollectionDragOrigin
            && Vector2.Distance(pressPoint.ToVector2(), pointer.ToVector2()) > CardDragThreshold) {
            if (!dragging) {
                CollapseCardReading();
                if (ZoneReadingOpen) zoneName = null;
                // The drag origin was captured from the expanded face. Folding
                // the view must not replace that deliberate-release boundary.
                if (CardCollectionOpen) FoldCardCollection();
            }
            dragging = true;
        }
        if (mouse.LeftButton == ButtonState.Released && previousMouse.LeftButton == ButtonState.Pressed) {
            var hit = HitAt(pointer);
            if (dragging) AcceptedDrag = DropCard(hit);
            else if (hit != null && pressed?.Id == hit.Id && pressed.Scope == hit.Scope && ActivateHit(hit)) AcceptedHit = hit.Id;
            else if (pressed == null && hit == null) { CollapseCardReading(); FoldCardCollection(); }
            pressed = null; dragSource = null; dragging = false; ClearCardDrag();
        }
        if (experienceInput && keys.IsKeyDown(Keys.Escape) && previousKeys.IsKeyUp(Keys.Escape)) {
            CloseTableGuide();
        }
        if (updatesOpen && !ActivityOpen && keys.IsKeyDown(Keys.Escape) && previousKeys.IsKeyUp(Keys.Escape)) CloseAppUpdates();
        if (escapePressed && CurrentManaReading) {
            overlay = ""; pressed = null;
        } else if (escapePressed && turnPassRequest != null) {
            turnPassRequest = null;
        } else if (escapePressed && !cancelledDrag) {
            CancelReleasedPlayIntent();
            bool closedReader = readingId != null;
            CollapseCardReading();
            dragging = false; dragSource = null; pressed = null; ClearCardDrag();
            if (!closedReader) {
                selectedCombat = null; textFocus = false;
                if (match == null && !busy) { if (DismissDeckWorkspaceReading()) { } else if (workshopDialog == "printings") ClosePrintings(); else if (workshopDialog.Length > 0) workshopDialog = ""; else if (deckMenu.Length > 0) deckMenu = ""; else if (deckEditor) CloseDeckEditor(); else CloseSetupModal(); }
                if (CardCollectionOpen) FoldCardCollection();
                else if (overlay.Length > 0) overlay = ""; else if (zoneName != null) zoneName = null;
                else if (StudyFocusActive) SetStudyFocus(null);
                else if (DecisionModal && decisions.Allocation?.Inspection != null) CloseAllocationDetail();
                else if (DecisionModal) decisions.Collapsed = true;
            }
        }
        if (!chatKeys && !textFocus && keys.IsKeyDown(Keys.Space) && previousKeys.IsKeyUp(Keys.Space) && !OverlayOpen && !CardCollectionOpen && !AbilityActionPending && turnPassRequest == null && !busy && match != null) {
            if (TableChoice && TableChoices.CanConfirm(match, decisions.Selections)) Send(DecisionCommands.Choose(match, decisions.Selections));
            else if (!TableChoice && CanConfirmCombatNow && CombatGuide.CanConfirm(match)) Send(DecisionCommands.Confirm(match));
        }
        if (!chatKeys && !textFocus && keys.IsKeyDown(Keys.C) && previousKeys.IsKeyUp(Keys.C) && HasCombat && (overlay.Length == 0 || overlay == "combat")) { if (overlay == "combat") overlay = ""; else OpenCombat(); }
        if (!chatKeys && !textFocus && !blockedInput && !OverlayOpen && keys.IsKeyDown(Keys.R) && previousKeys.IsKeyUp(Keys.R)
            && readingId is { } reader) FlipReaderText(reader);
        if (match != null && !chatKeys && !textFocus && keys.IsKeyDown(Keys.F) && previousKeys.IsKeyUp(Keys.F)) UseAutomaticControl(!automatic);
        UpdateMatchControlKeys(keys, !blockedInput && !chatKeys && !textFocus && !OverlayOpen && !dragging);
        if (!blockedInput && !chatKeys && !textFocus && keys.IsKeyDown(Keys.F1) && previousKeys.IsKeyUp(Keys.F1)) OpenTableGuide();
        int wheel = mouse.ScrollWheelValue - previousMouse.ScrollWheelValue;
        if (!blockedInput && wheel != 0) Scroll(Math.Sign(wheel));
        ContinueReleasedPlay();
        TraceLabInput(mouse, viewport);
        previousMouse = mouse; previousKeys = keys;
    }
}
