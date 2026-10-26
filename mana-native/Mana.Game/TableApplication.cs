using System.Collections.Immutable;
using Mana.Client;
using Mana.Magic;
using System.Collections.Concurrent;
using System.Text.Json;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal sealed partial class TableApplication : IDisposable
{
    private readonly TableServices services;
    private readonly IEngineCatalog catalog;
    private readonly ITableSetup tables;
    private readonly IMatchConnection matchConnection;
    private readonly string profile;
    private readonly string? fixture;
    private readonly bool allowAutomaticChecks;
    private readonly bool restartedAfterUpdate;
    internal bool CaptureMode { get; set; }
    private readonly PresentationResources resources;
    internal bool Automated { get; set; }
    internal string? AcceptedHit { get; private set; }
    internal bool AcceptedDrag { get; private set; }
    internal event Action? ExitRequested;
    internal PresentationFrame? Frame { get; private set; }
    private void RequestExit() => ExitRequested?.Invoke();
    private readonly GameSnapshot? initialState;
    private readonly ClientSession session;
    private readonly TimeProvider clock;
    private sealed class CompletionQueue(ClientSession session) { public void Enqueue(Action action) => session.Post(action); }
    private readonly CompletionQueue updates;
    private bool busy => session.Busy;
    private bool polling => session.Polling;
    private readonly InteractionController interaction = new();
    private readonly InteractionFrame hits = new();
    private readonly DecisionSession decisions = new();
    private readonly MatchPresentationLifecycle matchPresentation = new();
    private TableScene? tableScene;
    private readonly Dictionary<string, Pose> cardPositions = [];
    private readonly Dictionary<int, Vector2> heroPositions = [];
    private readonly DrawList canvas = new();
    private PlayPreferences preferences;
    private ImmutableArray<DeckSummary> decks = [];
    private ImmutableArray<DeckPreset> presets = [];
    private DeckSummary? selectedDeck;
    private Lobby? lobby;
    private GameSnapshot? match => session.State;
    private SoloSetup? soloSetup;
    private readonly int[] soloChoices = [0, 1, 2];
    private bool soloTab = true, localMatch, loaded, closing, automatic, forward = true;
    private bool held, dragging, textFocus, inspectBack;
    private string message = "Loading the card library…", error = "";
    private string overlay = "", previousTurn = "";
    private string notice = "";
    private string pendingActionLabel = "";
    private bool commandFailure;
    private double noticeUntil;
    private string? selectedCombat { get => interaction.SelectedCombat; set => interaction.SelectedCombat = value; }
    private string? zoneName;
    private int zonePlayer, zonePage, handPage, deckPage, historyPage, inspectPage;

    private Card? hovered, inspected;
    private Point pointer, pressPoint;
    private MouseState previousMouse;
    private KeyboardState previousKeys;
    private Hit? pressed, dragSource;
    private double nextPoll, now, turnBannerUntil;
    private double lastPresentationTime;
    private static readonly Color Ink = new(236, 236, 230), Muted = new(161, 173, 182), Gold = new(226, 190, 120), Teal = new(45, 94, 115), Panel = new(24, 30, 38), Blue = new(91, 184, 228), Red = new(235, 117, 105);
    private int DesignHeight => match == null ? 1000 : TableLayout.Height;
    private bool TableChoice => decisions.Presentation?.RequiredSurface == DecisionRequiredSurface.Table;
    private bool AbilityActionPending => !session.NeedsRecovery && decisions.Presentation?.Payload is AbilityChoicePayload;
    private bool DecisionModal => !session.NeedsRecovery && !decisions.Collapsed && decisions.Presentation?.RequiredSurface is DecisionRequiredSurface.Gallery or DecisionRequiredSurface.Value or DecisionRequiredSurface.Allocation;
    private bool OverlayOpen => updatesOpen || ActivityOpen || ExperienceOpen || workshopDialog.Length > 0 || overlay.Length > 0 || zoneName != null || DecisionModal;
    private string Scope => match == null ? deckEditor ? DeckScope : LobbyScope + ":tempo:" + lobby?.Tempo?.Revision : session.Scope + ":viewer:" + match.ViewerId;
    private string ScopeFor(UiIntent intent) => intent.ScopeKind switch {
        InteractionScopeKind.Home => Scope + ":home:" + homeDeckPage,
        InteractionScopeKind.Updates => UpdateScope,
        InteractionScopeKind.Activity => ActivityScope,
        InteractionScopeKind.Experience => ExperienceScope,
        InteractionScopeKind.Chat => ChatScope,
        InteractionScopeKind.Playback when match != null => match.Id + ":viewer:" + match.ViewerId + ":playback",
        InteractionScopeKind.PhaseStop when match != null => match.Id + ":viewer:" + match.ViewerId + ":playback:phase-stop:" + match.PhaseKey,
        InteractionScopeKind.Source when SourceActionsSupported => Scope + ":source:" + sourceActions.View.Generation + ":" + sourceActions.View.Context,
        _ => Scope
    };
    private bool CurrentHit(Hit hit) => hit.Scope == ScopeFor(hit.Intent);
    private Hit? HitAt(Point at) => TableHitAt(at);

    public TableApplication(TableServices services, string profile, string? fixture = null, GameSnapshot? initialState = null,
        PresentationResources? resources = null, IAppUpdates? appUpdates = null, Action<PreparedUpdate>? installUpdate = null,
        bool allowAutomaticChecks = false, TimeProvider? clock = null, bool restartedAfterUpdate = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(services.Catalog);
        ArgumentNullException.ThrowIfNull(services.Tables);
        ArgumentNullException.ThrowIfNull(services.Match);
        ArgumentNullException.ThrowIfNull(services.Initialize);
        this.services = services;
        catalog = services.Catalog; tables = services.Tables; matchConnection = services.Match;
        this.clock = clock ?? TimeProvider.System;
        this.restartedAfterUpdate = restartedAfterUpdate;
        session = new(matchConnection, tables, this.clock, services.SourceActions); updates = new(session);
        catalogRead = new(updates.Enqueue); printingRead = new(updates.Enqueue);
        deckTokensRead = new(updates.Enqueue); tokenPrintingsRead = new(updates.Enqueue);
        deckWorkspace = new(services.Workshop, services.Analysis, updates.Enqueue);
        sourceActions = session.SourceActions;
        session.Changed += AcceptPresentation;
        session.Completed += () => { FinishActivity(); nextPoll = 0; };
        session.Failed += ex => { error = ex.Message; commandFailure = session.LastFailureWasCommand; FailActivity(); try { File.AppendAllText(Path.Combine(profile, "client.log"), ex + "\n"); } catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { } };
        // Fresh observations restore play, but an explicit action failure remains
        // available until acknowledged instead of disappearing between frames.
        session.Recovered += () => { if (!commandFailure) error = ""; pendingActionLabel = ""; };
        this.profile = profile; this.fixture = fixture; this.initialState = initialState; this.resources = resources ?? new(); this.allowAutomaticChecks = allowAutomaticChecks;
        InitializeAppUpdates(appUpdates, installUpdate);
        preferences = PlayPreferences.Load(profile); automatic = preferences.Automatic;
    }
    internal void ReceiveText(char character)
    {
        if (updatesOpen || ActivityOpen || ExperienceOpen) return;
        if (chatOpen && chatFocus) { ChatText(character); return; }
        if (match == null && deckEditor) { DeckText(character); return; }
        if (match == null) { SetupText(character); return; }
        if (!textFocus) return;
        string value = decisions.Filtering ? decisions.Filter : decisions.Input;
        if (character == '\b') { if (value.Length > 0) value = value[..^1]; }
        else if (!char.IsControl(character) && value.Length < 500) value += character;
        if (decisions.Filtering) { decisions.Filter = value; decisions.Page = 0; } else decisions.Input = value;
    }
    internal void Initialize()
    {
        if (Automated) automatic = false;
        if (initialState != null) { localMatch = true; loaded = true; Accept(initialState); return; }
        if (fixture != null) {
            Accept(JsonSerializer.Deserialize<GameSnapshot>(File.ReadAllText(fixture), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
            loaded = true; message = "Visual review"; automatic = false; return;
        }
        RetryStartup();
    }
    private void Accept(GameSnapshot? state) => session.Accept(state);
    private void AcceptPresentation(GameSnapshot? previous, GameSnapshot? state)
    {
        var transition = matchPresentation.Observe(previous, state, now, boardView);
        if (transition.ScopeChanged) { ResetTableView(); tableGuideOpen = false; sceneRevealAt = now; }
        ReconcileReleasedPlay();
        if (state?.Status == "finished" && previous?.Status != "finished") tableGuideOpen = false;
        if (transition.Notice is { } latest) { notice = latest; noticeUntil = now + 8; }
        if (transition.Finished) { overlay = "result"; zoneName = null; }
        if (!preferences.Sounds) return;
        for (int i = 0; i < transition.LifeLosses; i++) resources.Play("life-loss", .45f);
        if (transition.Finished) resources.Play("game-end", .5f);
        if (transition.CombatStarted) resources.Play("combat", .35f);
        if (transition.CardPlayed) resources.Play("card-play", .35f);
    }
    private void PrepareDecisionSession()
    {
        var presentation = match == null ? null : DecisionDockPresentation.From(match, CurrentDecisionControls());
        bool changed = decisions.Observe(match, presentation?.RequiredSurface == DecisionRequiredSurface.Table);
        decisions.Presentation = presentation;
        if (!changed) return;
        if (matchPresentation.Cards.Pinned is { ChoiceSlot: >= 0 }) CollapseCardReading();
        if (inspectedChoice != null) {
            inspectedChoice = null; inspected = null;
            if (overlay == "inspect") overlay = "";
        }
        if (BattlefieldFocused && match?.Decision is { } prompt && !ReadingAbilitySource(prompt)
            && decisions.Presentation?.RequiredSurface is DecisionRequiredSurface.Gallery or DecisionRequiredSurface.Value
                or DecisionRequiredSurface.Allocation or DecisionRequiredSurface.Targets) {
            zoneName = null; CollapseCardReading();
        }
        selectedCombat = null; dragging = false; dragSource = null; ClearCardDrag(); textFocus = false;
    }
    internal void Update(GameTime time, MouseState mouse, KeyboardState keys, Point viewport)
    {
        AcceptedHit = null; AcceptedDrag = false;
        now = time.TotalGameTime.TotalSeconds;
        session.Pump();
        UpdateActivity();
        UpdateTempo();
        UpdateSocial();
        UpdateDeckCatalog();
        UpdateWorkshopData();
        UpdateAppUpdates();
        UpdateReaderActions();
        PrepareDecisionSession();
        string turn = match == null ? "" : match.Id + ":" + match.Turn + ":" + match.ActivePlayerId;
        if (previousTurn != turn) { previousTurn = turn; held = false; turnBannerUntil = now + 1.5; if (match?.ActivePlayerId == match?.ViewerId && match?.Turn > 0 && preferences.Sounds) resources.Play("your-priority", .3f); }
        if (inspected != null) {
            inspected = RefreshInspectedCard();
            if (inspected == null && overlay == "inspect") overlay = "";
        }
        if (dragSource != null && CurrentDragCard(dragSource) == null) { dragging = false; dragSource = null; pressed = null; ClearCardDrag(); }
        bool experienceInput = ExperienceOpen && !ActivityOpen && !updatesOpen;
        bool blockedInput = ActivityOpen || experienceInput || updatesOpen;
        bool chatKeys = blockedInput || ChatKeys(keys);
        if (!chatKeys) { DeckKeys(keys); SetupKeys(keys); }
        pointer = RenderViewport.Fit(viewport, DesignHeight).ToLogical(new(mouse.X, mouse.Y));
        boardPointerActivity = mouse.X != previousMouse.X || mouse.Y != previousMouse.Y
            || mouse.ScrollWheelValue != previousMouse.ScrollWheelValue || mouse.LeftButton != previousMouse.LeftButton;
        boardViewportChanged = boardViewport != viewport; boardViewport = viewport;
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
                && IsCardActionable(source) && CanStartSourceDrag(sourceHit) ? pressed : null; dragging = false;
            BeginCardDrag(dragSource);
        }
        if (mouse.LeftButton == ButtonState.Pressed && dragSource != null && dragSource.Scope == Scope && Vector2.Distance(pressPoint.ToVector2(), pointer.ToVector2()) > CardDragThreshold) {
            if (!dragging) {
                CollapseCardReading();
                if (ZoneReadingOpen) zoneName = null;
            }
            dragging = true;
        }
        if (mouse.LeftButton == ButtonState.Released && previousMouse.LeftButton == ButtonState.Pressed) {
            var hit = HitAt(pointer);
            if (dragging) AcceptedDrag = DropCard(hit);
            else if (hit != null && pressed?.Id == hit.Id && pressed.Scope == hit.Scope && ActivateHit(hit)) AcceptedHit = hit.Id;
            else if (pressed == null && hit == null) CollapseCardReading();
            pressed = null; dragSource = null; dragging = false; ClearCardDrag();
        }
        if (experienceInput && keys.IsKeyDown(Keys.Escape) && previousKeys.IsKeyUp(Keys.Escape)) {
            CloseTableGuide();
        }
        if (updatesOpen && !ActivityOpen && keys.IsKeyDown(Keys.Escape) && previousKeys.IsKeyUp(Keys.Escape)) CloseAppUpdates();
        if (escapePressed && !cancelledDrag) {
            CancelReleasedPlayIntent();
            bool closedReader = readingId != null;
            CollapseCardReading();
            dragging = false; dragSource = null; pressed = null; ClearCardDrag();
            if (!closedReader) {
                selectedCombat = null; textFocus = false;
                if (match == null && !busy) { if (DismissDeckWorkspaceReading()) { } else if (workshopDialog == "printings") ClosePrintings(); else if (workshopDialog.Length > 0) workshopDialog = ""; else if (deckMenu.Length > 0) deckMenu = ""; else if (deckEditor) CloseDeckEditor(); else CloseSetupModal(); }
                if (overlay.Length > 0) overlay = ""; else if (zoneName != null) zoneName = null;
                else if (StudyFocusActive) SetStudyFocus(null);
                else if (DecisionModal && decisions.Allocation?.Inspection != null) CloseAllocationDetail();
                else if (DecisionModal) decisions.Collapsed = true;
            }
        }
        if (!chatKeys && !textFocus && keys.IsKeyDown(Keys.Space) && previousKeys.IsKeyUp(Keys.Space) && !OverlayOpen && !AbilityActionPending && !busy && match != null) {
            if (TableChoice && TableChoices.CanConfirm(match, decisions.Selections)) Send(DecisionCommands.Choose(match, decisions.Selections));
            else if (!TableChoice && CombatGuide.CanConfirm(match)) Send(DecisionCommands.Confirm(match));
        }
        if (!chatKeys && !textFocus && keys.IsKeyDown(Keys.C) && previousKeys.IsKeyUp(Keys.C) && HasCombat && (overlay.Length == 0 || overlay == "combat")) { if (overlay == "combat") overlay = ""; else OpenCombat(); }
        if (match != null && !chatKeys && !textFocus && keys.IsKeyDown(Keys.F) && previousKeys.IsKeyUp(Keys.F)) UseAutomaticControl(!automatic);
        if (match != null && !chatKeys && !textFocus && keys.IsKeyDown(Keys.LeftControl) && previousKeys.IsKeyUp(Keys.LeftControl)) ToggleHoldControl();
        if (!blockedInput && !chatKeys && !textFocus && keys.IsKeyDown(Keys.F1) && previousKeys.IsKeyUp(Keys.F1)) OpenTableGuide();
        if (!blockedInput && !chatKeys && !textFocus && keys.IsKeyDown(Keys.Left) && previousKeys.IsKeyUp(Keys.Left)) ScrollPlayAccess(1);
        if (!blockedInput && !chatKeys && !textFocus && keys.IsKeyDown(Keys.Right) && previousKeys.IsKeyUp(Keys.Right)) ScrollPlayAccess(-1);
        int wheel = mouse.ScrollWheelValue - previousMouse.ScrollWheelValue;
        if (!blockedInput && wheel != 0) Scroll(Math.Sign(wheel));
        ContinueReleasedPlay();
        previousMouse = mouse; previousKeys = keys;
        // Give a ready pass precedence over starting another observation. Use this
        // update's pointer position so entering a card pauses Auto before Draw.
        bool autoBlocked = AutomaticInteractionPause() != DecisionDockPauseReason.None;
        bool quietReserved = UpdateQuietPriority(autoBlocked);
        if (!quietReserved && (!automatic || held || autoBlocked)) session.CancelAdvance();
        if (!quietReserved && !ExperienceLabOpen && !session.NeedsRecovery && fixture == null && matchPresentation.Ready(now) && !busy && !polling
            && match is { } currentState && TurnGuide.CanAutoPass(currentState, automatic, held, preferences.Stops, autoBlocked)) {
            // Snapshot the policy: the worker never reads mutable UI preferences.
            var stops = preferences.Stops.ToHashSet();
            error = "";
            if (session.Advance(state => TurnGuide.CanAutoPass(state, true, false, stops, false), PresentationPacing.ShouldPresent))
                pendingActionLabel = "Passing priority automatically.";
        }
        if (!ExperienceLabOpen && loaded && fixture == null && !busy && !polling && now >= nextPoll && (localMatch || lobby != null)) {
            nextPoll = now + (session.NeedsRecovery ? 2 : match == null ? .6 : match.Decision == null ? .05 : .12);
            session.Poll(!localMatch, nextLobby => lobby = nextLobby);
        }
        PreparePresentation(time);
    }
    private void SetAutomatic(bool value) { automatic = value; preferences = preferences with { Automatic = value }; SavePreferences(); }
    private void SavePreferences() { StopQuietPriority(); session.CancelAdvance(); try { preferences.Save(profile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = "Could not save settings: " + ex.Message; } }
    private bool Send(DecisionCommand? command)
    {
        if (command == null || fixture != null || session.NeedsRecovery || match?.Id != command.GameId || match.Decision?.Id != command.DecisionId) return false;
        if (AbilityActionPending) {
            var ability = (AbilityChoicePayload)match.Decision!.Payload;
            if (command is not ChooseOptions { Choices: var choices }
                || !(choices.Length == 0 && ability.Selection.Range.Min == 0
                    || choices.Length == 1 && ability.Selection.Choices.Any(choice => choice.Index == choices[0]))) return false;
        }
        if (command is not PassForNow) StopQuietPriority();
        CancelReleasedPlayIntent();
        error = ""; commandFailure = false;
        if (!session.Submit(command)) return false;
        pendingActionLabel = StateClarity.PendingActionLabel(match, command);
        return true;
    }
    internal PresentationFrame ReadPresentation() => Frame
        ?? throw new InvalidOperationException("Update the presentation before reading its frame.");

    private void PreparePresentation(GameTime time)
    {
        float frameElapsed = (float)(time.TotalGameTime.TotalSeconds - lastPresentationTime);
        lastPresentationTime = time.TotalGameTime.TotalSeconds;
        canvas.TextScale = preferences.TextScale;
        PrepareShellPresentation();
        PrepareDeckInteraction();
        ValidateStudyFocus();
        tableScene = match == null ? null : TableScene.Build(match, handPage, pointer, null,
            dragging && dragSource?.Card is { } held ? CardViews.Identity(held) : null, false, CardPresentation.IsResource,
            denseMeasure: MeasureDenseBattlefieldLabel, allowInlineOrdering: TableChoices.IsDirect(match), sharedTableStudy: TableStudyEnabled,
            playAccessScroll: playAccessScroll);
        if (tableScene != null) playAccessScroll = tableScene.PlayAccess.Scroll;
        UpdateBoardView();
        PrepareMatchPresentation();
        BeginCardReading(frameElapsed);
        hits.Begin();
        publicCounterBadges.Clear();
        hovered = null; cardPositions.Clear(); heroPositions.Clear();
        canvas.BeginComposition();
        if (StartupVisible) DrawStartup();
        else if (match == null) DrawLobby();
        else {
            DrawArena();
        }
        if (match != null && noticeUntil > now && !OverlayOpen) {
            canvas.Panel(new(427, 64, 746, 60), new Color(26, 36, 48), Gold, 7);
            canvas.Text(notice, 440, 76, Ink, .75f, 719, 2);
            hits.Add(new("notice", Scope, new(427, 64, 746, 60), new MatchIntent.OpenHistory()));
        }
        if (!updatesOpen && !ExperienceOpen && !ActivityOpen) DrawSocial();
        if (!ActivityOpen && !string.IsNullOrWhiteSpace(error)) {
            int y = match == null ? 942 : 59;
            canvas.Panel(new(270, y, 1060, 46), new(71, 37, 38), Red, 7); canvas.Text(error, 284, y + 9, Ink, .76f, 980, 1);
            Button("dismiss", "×", 1280, y + 3, 42, new ShellIntent.DismissError(), height: 40);
        }
        if (tableGuideOpen && !StartupVisible) DrawTableGuide();
        if (ActivityOpen && !StartupVisible) DrawActivity();
        if (updatesOpen && !ActivityOpen && !StartupVisible) DrawAppUpdates();
        if (!ActivityOpen && !preferences.ReducedMotion && sceneRevealAt >= 0 && now - sceneRevealAt < .2)
            canvas.Fill(new(0, 0, 1600, DesignHeight), new Color(12, 18, 26) * (float)(.5 * (1 - (now - sceneRevealAt) / .2)));
        EndCardReading();
        var drawing = canvas.Complete();
        hits.Publish();
        Frame = new(drawing, hits.Published, tableScene, matchPresentation.Cards.Cards, DesignHeight,
            new(match?.Id, match?.Revision ?? 0, match?.Decision?.Id, Scope, time.TotalGameTime.TotalSeconds), boardView, followTurnEnabled);
    }
    public void Dispose()
    {
        if (closing) return;
        closing = true; CloseUpdateRequests(); deckWorkspace.Close(); deckPersistence.Close(); catalogRead.Close(); printingRead.Close(); deckTokensRead.Close(); tokenPrintingsRead.Close(); session.Close(); matchPresentation.Reset();
        canvas.Dispose();
    }
}
