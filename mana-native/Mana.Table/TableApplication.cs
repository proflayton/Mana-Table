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
    private readonly ICardEngine engine;
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
    private readonly List<int> selections = [];
    private readonly TablePresence presence = new();
    private readonly PresentationPacing playback = new();
    private TableScene? tableScene;
    private readonly Dictionary<string, Pose> cardPositions = [];
    private readonly Dictionary<int, Vector2> heroPositions = [];
    private readonly DrawList canvas = new();
    private PlayPreferences preferences;
    private IReadOnlyList<DeckSummary> decks = [];
    private IReadOnlyList<DeckPreset> presets = [];
    private DeckSummary? selectedDeck;
    private Lobby? lobby;
    private GameSnapshot? match => session.State;
    private SoloSetup? soloSetup;
    private readonly int[] soloChoices = [0, 1, 2];
    private bool soloTab = true, localMatch, loaded, closing, automatic, forward = true;
    private bool held, dragging, collapsedDecision, textFocus, filterInput, inspectBack;
    private string message = "Loading the card library…", error = "", decisionId = "", choiceFilter = "";
    private string overlay = "", inputText = "", previousTurn = "";
    private string notice = "";
    private string pendingActionLabel = "";
    private bool commandFailure;
    private double noticeUntil;
    private string? selectedCombat { get => interaction.SelectedCombat; set => interaction.SelectedCombat = value; }
    private string? zoneName;
    private int zonePlayer, zonePage, handPage, choicePage, deckPage, historyPage, inspectPage;

    private int[] allocations = [];
    private Card? hovered, inspected;
    private Point pointer, pressPoint;
    private MouseState previousMouse;
    private KeyboardState previousKeys;
    private Hit? pressed, dragSource;
    private double nextPoll, now, turnBannerUntil;
    private double lastDrawTime;
    private readonly AutoPriority priority = new();
    private readonly List<(Vector2 Position, string Text, Color Color, double Until)> feedback = [];
    private static readonly Color Ink = new(236, 236, 230), Muted = new(161, 173, 182), Gold = new(226, 190, 120), Teal = new(45, 94, 115), Panel = new(24, 30, 38), Blue = new(91, 184, 228), Red = new(235, 117, 105);
    private int DesignHeight => match == null ? 1000 : TableLayout.Height;
    private bool TableChoice => match != null && TableChoices.IsDirect(match);
    private bool AbilityActionPending => !session.NeedsRecovery && match?.Decision is { } decision && IsAbilityActionChoice(decision);
    private bool DecisionModal => !session.NeedsRecovery && !TableChoice && !AbilityActionPending && !collapsedDecision && match?.Decision?.Kind is "choice" or "reveal" or "number" or "text" or "allocate";
    private bool OverlayOpen => updatesOpen || ActivityOpen || ExperienceOpen || workshopDialog.Length > 0 || overlay.Length > 0 || zoneName != null || DecisionModal;
    private string Scope => match == null ? deckEditor ? DeckScope : LobbyScope : session.Scope;
    private string ControlScope(string id) => SourceActionsSupported && (id == "read-card-action" || id.StartsWith("source-action:", StringComparison.Ordinal)
        || id.StartsWith("source-placement:", StringComparison.Ordinal))
        ? Scope + ":source:" + readerActionsGeneration + ":" + readerActionsBinding
        : id.StartsWith("updates-") ? UpdateScope : id.StartsWith("activity-") ? ActivityScope : id.StartsWith("experience-") ? ExperienceScope : id.StartsWith("chat-") ? ChatScope
        : match != null && (id is "auto" or "hold" or "instructions" or "action-details" or "battlefields" or "quiet-priority-stop" or "ability-auto-pass-open" or "ability-prev" or "ability-next" or "ability-text-prev" or "ability-text-next"
            || id.StartsWith("phase-stop:") || id.StartsWith("ability-undo:")) ? match.Id + ":playback" : Scope;
    private bool CurrentHit(Hit hit) => hit.Scope == ControlScope(hit.Id);
    private Hit? HitAt(Point at) => TableHitAt(at);

    public TableApplication(ICardEngine engine, string profile, string? fixture = null, GameSnapshot? initialState = null,
        PresentationResources? resources = null, IAppUpdates? appUpdates = null, Action<PreparedUpdate>? installUpdate = null,
        bool allowAutomaticChecks = false, TimeProvider? clock = null, bool restartedAfterUpdate = false)
    {
        this.clock = clock ?? TimeProvider.System;
        this.restartedAfterUpdate = restartedAfterUpdate;
        session = new(engine, engine, this.clock); updates = new(session);
        session.Changed += AcceptEffects;
        session.Completed += () => { FinishActivity(); nextPoll = 0; if (error.Length == 0) priority.Reset(); };
        session.Failed += ex => { error = ex.Message; commandFailure = session.LastFailureWasCommand; FailActivity(); try { File.AppendAllText(Path.Combine(profile, "client.log"), ex + "\n"); } catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { } };
        // Fresh observations restore play, but an explicit action failure remains
        // available until acknowledged instead of disappearing between frames.
        session.Recovered += () => { if (!commandFailure) error = ""; priority.Reset(); pendingActionLabel = ""; };
        this.engine = engine; this.profile = profile; this.fixture = fixture; this.initialState = initialState; this.resources = resources ?? new(); this.allowAutomaticChecks = allowAutomaticChecks;
        this.appUpdates = appUpdates; this.installUpdate = installUpdate;
        LoadUpdatePreferences();
        preferences = PlayPreferences.Load(profile); automatic = preferences.Automatic;
        experience = ExperiencePreferences.Load(profile);
        welcomeOpen = initialState == null && fixture == null;
    }
    internal void ReceiveText(char character)
    {
        if (updatesOpen || ActivityOpen || ExperienceOpen) return;
        if (chatOpen && chatFocus) { ChatText(character); return; }
        if (match == null && deckEditor) { DeckText(character); return; }
        if (match == null) { SetupText(character); return; }
        if (!textFocus) return;
        string value = filterInput ? choiceFilter : inputText;
        if (character == '\b') { if (value.Length > 0) value = value[..^1]; }
        else if (!char.IsControl(character) && value.Length < 500) value += character;
        if (filterInput) { choiceFilter = value; choicePage = 0; } else inputText = value;
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
    private void AcceptEffects(GameSnapshot? previous, GameSnapshot? state)
    {
        if (state?.Id != previous?.Id) { ResetTableView(); tableGuideOpen = false; sceneRevealAt = now; }
        if (state != null) welcomeOpen = false;
        if (state?.Status == "finished" && previous?.Status != "finished") tableGuideOpen = false;
        if (state != null) { effects.Observe(previous, state, now); presence.Observe(previous, state, now); playback.Observe(previous, state, now); }
        if (state != null) ObserveActionFeedback(previous, state);
        if (state != null) publicChanges.Observe(previous, state, now);
        if (state != null) priorityTransitions.Observe(previous, state, now);
        ObserveMana(previous, state);
        if (state != null && previous != null && state.Id == previous.Id) {
            if (state.Notices.LastOrDefault() is { } latest && latest != previous.Notices.LastOrDefault()) { notice = latest; noticeUntil = now + 8; }
            foreach (var player in state.Players) {
                var old = previous.Players.FirstOrDefault(p => p.Id == player.Id);
                if (old != null && old.Life != player.Life) {
                    int difference = player.Life - old.Life;
                    feedback.Add((heroPositions.GetValueOrDefault(player.Id, new(800, 420)), difference > 0 ? "+" + difference : difference.ToString(), difference > 0 ? Blue : Red, now + 1.5));
                    if (difference < 0 && preferences.Sounds) resources.Play("life-loss", .45f);
                }
            }
            if (state.Status == "finished" && previous.Status != "finished") { overlay = "result"; zoneName = null; if (preferences.Sounds) resources.Play("game-end", .5f); }
            if (state.Combat?.Attackers.Length > 0 && previous.Combat?.Attackers.Length is null or 0 && preferences.Sounds) resources.Play("combat", .35f);
            if (state.Activity.LastOrDefault()?.Id != previous.Activity.LastOrDefault()?.Id && state.Activity.LastOrDefault()?.Kind is "land" or "cast" && preferences.Sounds) resources.Play("card-play", .35f);
        }
    }
    internal void Update(GameTime time, MouseState mouse, KeyboardState keys, Point viewport)
    {
        AcceptedHit = null; AcceptedDrag = false;
        now = time.TotalGameTime.TotalSeconds;
        session.Pump();
        UpdateActivity();
        UpdateSocial();
        UpdateDeckCatalog();
        UpdateWorkshopData();
        UpdateDeckAnalysis();
        UpdateAppUpdates();
        UpdateReaderActions();
        var prompt = match?.Decision;
        if (decisionId != (prompt?.Id ?? "")) {
            if (BattlefieldFocused && prompt != null && !ReadingAbilitySource(prompt) && (prompt.Kind is "choice" or "reveal" or "number" or "text" or "allocate"
                || prompt.Intent == DecisionIntent.Selection)) { zoneName = null; CollapseCardReading(); }
            decisionId = prompt?.Id ?? ""; selections.Clear(); choicePage = 0; choiceFilter = ""; selectedCombat = null; priority.Reset();
            dragging = false; dragSource = null; ClearCardDrag(); collapsedDecision = false; textFocus = false;
            if (!TableChoice && prompt is { Ordered: true } && prompt.Min == prompt.Choices.Length) selections.AddRange(prompt.Choices.Select(c => c.Index));
            allocations = prompt?.Choices.Select(_ => prompt.AtLeastOne ? 1 : 0).ToArray() ?? [];
            inputText = prompt?.Kind == "number" ? prompt.Min.ToString() : prompt?.Initial ?? "";
        }
        string turn = match == null ? "" : match.Id + ":" + match.Turn + ":" + match.ActivePlayerId;
        if (previousTurn != turn) { previousTurn = turn; held = false; turnBannerUntil = now + 1.5; if (match?.ActivePlayerId == match?.ViewerId && match?.Turn > 0 && preferences.Sounds) resources.Play("your-priority", .3f); }
        if (inspected != null) {
            inspected = RefreshInspectedCard();
            if (inspected == null && overlay == "inspect") overlay = "";
        }
        if (dragSource?.Card is { } heldCard && !VisibleCards().Any(c => SameVisibleCard(c, heldCard))) { dragging = false; dragSource = null; pressed = null; ClearCardDrag(); }
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
            pressed = HitAt(pointer); pressPoint = pointer;
            if (!blockedInput && chatOpen && !ChatBounds.Contains(pointer)) chatFocus = false;
            if (!blockedInput && match == null && deckEditor && pressed?.Id is not ("deck-search-library" or "deck-search-deck" or "deck-clear-library" or "deck-clear-deck")) { deckTextFocus = ""; textFocus = false; }
            dragSource = !OverlayOpen && !AbilityActionPending && pressed?.Card is { } source && IsCardActionable(source) ? pressed : null; dragging = false;
            BeginCardDrag(dragSource);
        }
        if (mouse.LeftButton == ButtonState.Pressed && dragSource != null && dragSource.Scope == Scope && Vector2.Distance(pressPoint.ToVector2(), pointer.ToVector2()) > CardDragThreshold) {
            if (!dragging) CollapseCardReading();
            dragging = true;
        }
        if (mouse.LeftButton == ButtonState.Released && previousMouse.LeftButton == ButtonState.Pressed) {
            var hit = HitAt(pointer);
            if (dragging) AcceptedDrag = DropCard(hit);
            else if (hit != null && pressed?.Id == hit.Id && pressed.Scope == hit.Scope && CurrentHit(hit)) { ActivateHit(hit); AcceptedHit = hit.Id; }
            else if (pressed == null && hit == null) CollapseCardReading();
            pressed = null; dragSource = null; dragging = false; ClearCardDrag();
        }
        if (experienceInput && keys.IsKeyDown(Keys.Escape) && previousKeys.IsKeyUp(Keys.Escape)) {
            if (tableGuideOpen) CloseTableGuide(); else DismissWelcome();
        }
        if (updatesOpen && !ActivityOpen && keys.IsKeyDown(Keys.Escape) && previousKeys.IsKeyUp(Keys.Escape)) CloseAppUpdates();
        if (escapePressed && !cancelledDrag) {
            bool closedReader = readingId != null;
            CollapseCardReading();
            dragging = false; dragSource = null; pressed = null; ClearCardDrag();
            if (!closedReader) {
                selectedCombat = null; textFocus = false;
                if (match == null && !busy) { if (workshopDialog == "printings") ClosePrintings(); else if (workshopDialog.Length > 0) workshopDialog = ""; else if (deckMenu.Length > 0) deckMenu = ""; else if (deckEditor) CloseDeckEditor(); else CloseSetupModal(); }
                if (overlay.Length > 0) overlay = ""; else if (zoneName != null) zoneName = null;
                else if (StudyFocusActive) SetStudyFocus(null);
                else if (DecisionModal && match?.Decision?.Kind == "allocate" && allocationDetail >= -1) CloseAllocationDetail();
                else if (DecisionModal) collapsedDecision = true;
            }
        }
        if (!chatKeys && !textFocus && keys.IsKeyDown(Keys.Space) && previousKeys.IsKeyUp(Keys.Space) && !OverlayOpen && !AbilityActionPending && !busy && match != null) {
            if (TableChoice && TableChoices.CanConfirm(match, selections)) Send(new(match.Id, prompt!.Id, ReplyAction.Choose, Choices: selections.ToArray()));
            else if (!TableChoice && CombatGuide.CanConfirm(match)) Send(new(match.Id, prompt!.Id, ReplyAction.Confirm));
        }
        if (!chatKeys && !textFocus && keys.IsKeyDown(Keys.C) && previousKeys.IsKeyUp(Keys.C) && HasCombat && (overlay.Length == 0 || overlay == "combat")) { if (overlay == "combat") overlay = ""; else OpenCombat(); }
        if (!chatKeys && !textFocus && keys.IsKeyDown(Keys.F) && previousKeys.IsKeyUp(Keys.F)) UseAutomaticControl(!automatic);
        if (!chatKeys && !textFocus && keys.IsKeyDown(Keys.LeftControl) && previousKeys.IsKeyUp(Keys.LeftControl)) ToggleHoldControl();
        if (!blockedInput && !chatKeys && !textFocus && keys.IsKeyDown(Keys.F1) && previousKeys.IsKeyUp(Keys.F1)) OpenTableGuide();
        int wheel = mouse.ScrollWheelValue - previousMouse.ScrollWheelValue;
        if (!blockedInput && wheel != 0) Scroll(Math.Sign(wheel));
        previousMouse = mouse; previousKeys = keys;
        // Give a ready pass precedence over starting another observation. Use this
        // update's pointer position so entering a card pauses Auto before Draw.
        bool autoBlocked = commandFailure && error.Length > 0 || OverlayOpen || AbilityActionPending || chatFocus || dragging || pressed != null || readingId != null || selectedCombat != null || HoveredPendingReference() != null;
        bool quietReserved = UpdateQuietPriority(autoBlocked);
        if (!quietReserved && (!automatic || held || autoBlocked)) session.CancelAdvance();
        if (!quietReserved && !ExperienceLabOpen && !session.NeedsRecovery && fixture == null && playback.Ready(now) && priority.Update(match, automatic, held, preferences.Stops,
            autoBlocked, busy || polling) != null) {
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
        feedback.RemoveAll(f => f.Until < now);
    }
    private void SetAutomatic(bool value) { automatic = value; preferences = preferences with { Automatic = value }; SavePreferences(); }
    private void SavePreferences() { StopQuietPriority(); session.CancelAdvance(); priority.Reset(); try { preferences.Save(profile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = "Could not save settings: " + ex.Message; } }
    private void Send(DecisionReply reply)
    {
        if (fixture != null || session.NeedsRecovery || match?.Id != reply.GameId || match.Decision?.Id != reply.DecisionId) return;
        if (AbilityActionPending) {
            var decision = match.Decision!;
            if (reply.Action != ReplyAction.Choose || reply.Choices is not { } choices
                || !(choices.Length == 0 && decision.Min == 0
                    || choices.Length == 1 && decision.Choices.Any(choice => choice.Index == choices[0]))) return;
        }
        if (reply.Action != ReplyAction.PassUntilPublicAction) StopQuietPriority();
        error = ""; commandFailure = false;
        if (session.Submit(reply)) pendingActionLabel = StateClarity.PendingActionLabel(match, reply);
    }
    internal PresentationFrame ComposePresentation(GameTime time)
    {
        float frameElapsed = (float)(time.TotalGameTime.TotalSeconds - lastDrawTime);
        lastDrawTime = time.TotalGameTime.TotalSeconds;
        canvas.TextScale = preferences.TextScale;
        ValidateStudyFocus();
        tableScene = match == null ? null : TableScene.Build(match, handPage, pointer, null,
            dragging && dragSource?.Card is { } held ? CardViews.Identity(held) : null, false, CardPresentation.IsResource,
            denseMeasure: MeasureDenseBattlefieldLabel, allowInlineOrdering: TableChoices.IsDirect(match), sharedTableStudy: TableStudyEnabled,
            studyFocusedOpponent: StudyFocusActive ? studyFocusOpponent : null);
        UpdateBoardView();
        BeginCardReading(frameElapsed);
        hits.Begin();
        publicCounterBadges.Clear();
        hovered = null; cardPositions.Clear(); heroPositions.Clear();
        canvas.BeginComposition();
        if (StartupVisible) DrawStartup();
        else if (match == null && welcomeOpen) DrawWelcome();
        else if (match == null) {
            canvas.Gradient(new(0, 0, 1600, 1000), new(24, 31, 42), new(12, 17, 24));
            canvas.Text("MANA TABLE", 28, 19, Gold, 1.2f, bold: true);
            int formatX = 48 + canvas.TextWidth("MANA TABLE", 1.2f, true);
            canvas.Text("COMMANDER", formatX, 25, Muted, .75f);
            int messageX = Math.Max(435, formatX + canvas.TextWidth("COMMANDER", .75f) + 24);
            canvas.Text(message, messageX, 25, Ink, .82f, Math.Max(0, 1175 - messageX), 1);
            DrawLobby();
        } else {
            DrawArena();
        }
        if (match != null && noticeUntil > now && !OverlayOpen) {
            canvas.Panel(new(427, 64, 746, 60), new Color(26, 36, 48), Gold, 7);
            canvas.Text(notice, 440, 76, Ink, .75f, 719, 2);
            hits.Add(new("notice", Scope, new(427, 64, 746, 60), () => { overlay = "history"; historyPage = 0; noticeUntil = 0; }));
        }
        if (!updatesOpen && !ExperienceOpen && !ActivityOpen) DrawSocial();
        if (!ActivityOpen && !string.IsNullOrWhiteSpace(error)) {
            int y = match == null ? 942 : 59;
            canvas.Panel(new(270, y, 1060, 46), new(71, 37, 38), Red, 7); canvas.Text(error, 284, y + 9, Ink, .76f, 980, 1);
            Button("dismiss", "×", 1280, y + 3, 42, () => error = "", height: 40);
        }
        if (tableGuideOpen && !StartupVisible) DrawTableGuide();
        if (ActivityOpen && !StartupVisible) DrawActivity();
        if (updatesOpen && !ActivityOpen && !StartupVisible) DrawAppUpdates();
        if (!ActivityOpen && !preferences.ReducedMotion && sceneRevealAt >= 0 && now - sceneRevealAt < .2)
            canvas.Fill(new(0, 0, 1600, DesignHeight), new Color(12, 18, 26) * (float)(.5 * (1 - (now - sceneRevealAt) / .2)));
        EndCardReading();
        var drawing = canvas.Complete();
        hits.Publish();
        return Frame = new(drawing, hits.Published, tableScene, cardPresentation.Cards, DesignHeight,
            new(match?.Id, match?.Revision ?? 0, match?.Decision?.Id, Scope, time.TotalGameTime.TotalSeconds), boardView, followTurnEnabled);
    }
    public void Dispose()
    {
        if (closing) return;
        closing = true; CloseUpdateRequests(); session.Close();
        engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        canvas.Dispose();
    }
}
