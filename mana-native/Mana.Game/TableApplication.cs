using System.Collections.Immutable;
using Mana.Client;
using Mana.Audio;
using Mana.Magic;
using Mana.Scenarios;
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
    private AudioMixer? audioMixer;
    internal bool Automated { get; set; }
    internal string? AcceptedHit { get; private set; }
    internal bool AcceptedDrag { get; private set; }
    internal event Action? ExitRequested;
    internal PresentationFrame? Frame { get; private set; }
    private void RequestExit() => ExitRequested?.Invoke();
    private readonly GameSnapshot? initialState;
    private readonly ClientSession session;
    private readonly TimeProvider clock;
    private TimeSpan updateTime;
    private Point updateViewport;
    private bool pumpingObservations;
    private ProjectionCapture? projectionCapture;
    internal event Action<GameSnapshot, TimeSpan, ProjectionPresentationSettings>? ProjectionAccepted;
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
    private Lobby? lobby => session.Lobby;
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
    private int zonePlayer, zonePage, deckPage, historyPage, inspectPage;

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
        InteractionScopeKind.Settings => "settings:" + profile,
        InteractionScopeKind.Playback when match != null => match.Id + ":viewer:" + match.ViewerId + ":playback",
        InteractionScopeKind.PhaseStop when match != null => match.Id + ":viewer:" + match.ViewerId + ":playback:phase-stop:" + match.PhaseKey,
        InteractionScopeKind.Source when SourceActionsSupported => Scope + ":source:" + sourceActions.View.Generation + ":" + sourceActions.View.Context,
        _ => Scope
    };
    private bool CurrentHit(Hit hit) => hit.Scope == ScopeFor(hit.Intent);
    private Hit? HitAt(Point at) => TableHitAt(at);

    public TableApplication(TableServices services, string profile, string? fixture = null, GameSnapshot? initialState = null,
        PresentationResources? resources = null, IAppUpdates? appUpdates = null, Action<PreparedUpdate>? installUpdate = null,
        bool allowAutomaticChecks = false, TimeProvider? clock = null, bool restartedAfterUpdate = false,
        ObservationDeliveryMode observationDelivery = ObservationDeliveryMode.Polled)
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
        session = new(matchConnection, tables, this.clock, services.SourceActions, observationDelivery); updates = new(session);
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
        if (!InputReady) return;
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
        audioMixer = new(resources.AudioOutput);
        audioMixer.SetMix(preferences.Audio);
        if (Automated) automatic = false;
        if (initialState != null) {
            localMatch = true; loaded = true;
            if (session.ObservationDelivery == ObservationDeliveryMode.Polled) Accept(initialState);
            return;
        }
        if (fixture != null) {
            Accept(JsonSerializer.Deserialize<GameSnapshot>(File.ReadAllText(fixture), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
            loaded = true; message = "Visual review"; automatic = false; return;
        }
        RetryStartup();
    }
    private void Accept(GameSnapshot? state) => session.Accept(state);
    internal ClientSession.ObservationIngress OpenObservationIngress(string gameId, int viewerId)
        => session.OpenObservationIngress(gameId, viewerId);
    private ProjectionPresentationSettings? ProjectionSettings(Point viewport)
    {
        try { return new(preferences.ReducedMotion, preferences.TextScale, tableStudy, viewport.X, viewport.Y); }
        catch (ArgumentException) { return null; } // Unsupported diagnostics must not interrupt play.
    }
    internal void CaptureProjection(ProjectionCapture? capture) => projectionCapture = capture;
    private void ObserveProjectionEnvironment()
        => projectionCapture?.ObserveEnvironment(updateTime, ProjectionSettings(updateViewport), match);
    private void AcceptPresentation(GameSnapshot? previous, GameSnapshot? state)
    {
        bool restored = session.RestorationChanged;
        if (restored) ReconcileRestoredInteraction();
        if (state != null) finiteProgressProjection?.Observe(state);
        var transition = matchPresentation.Observe(previous, state, now, boardView, session.ExecutionChanges,
            finiteResolutionIntent is { } finite && FiniteProgress(finite, repeatedPresentationExecutions, previous, state), restored);
        if (transition.ScopeChanged) { ResetTableView(); tableGuideOpen = false; sceneRevealAt = now; }
        if (transition.ScopeChanged) { manualBoardView = null; boardPanning = false; }
        ReconcileReleasedPlay();
        ReconcileTurnPass();
        if (state?.Status == "finished" && previous?.Status != "finished") tableGuideOpen = false;
        if (transition.Notice is { } latest) { notice = latest; noticeUntil = now + 8; }
        if (transition.Finished) { overlay = "result"; zoneName = null; }
        for (int i = 0; i < transition.LifeLosses; i++) audioMixer?.Play(new("life-loss", AudioBus.Effects, .45f));
        if (transition.Finished) audioMixer?.Play(new("game-end", AudioBus.Effects, .5f));
        if (transition.CombatStarted) audioMixer?.Play(new("combat", AudioBus.Effects, .35f));
        if (transition.CardPlayed) audioMixer?.Play(new("card-play", AudioBus.Effects, .35f));
        // Capture accepted publications on the same clock as their presentation.
        // This optional private diagnostic does not receive raw poll responses.
        if (pumpingObservations && (projectionCapture != null || ProjectionAccepted != null)) {
            var settings = ProjectionSettings(updateViewport);
            projectionCapture?.ObserveEnvironment(updateTime, settings, state);
            if (settings != null && state != null) {
                projectionCapture?.TryAppend(state, updateTime, settings);
                ProjectionAccepted?.Invoke(state, updateTime, settings);
            }
        }
    }
    private void PrepareDecisionSession()
    {
        ReconcileTurnPass();
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
    internal void Update(GameTime time, MouseState mouse, KeyboardState keys, Point viewport, bool inputActive = true)
    {
        AcceptedHit = null; AcceptedDrag = false;
        updateTime = time.TotalGameTime; updateViewport = viewport;
        now = time.TotalGameTime.TotalSeconds;
        audioMixer?.Update();
        ObserveProjectionEnvironment();
        pumpingObservations = true;
        try { session.Pump(); }
        finally { pumpingObservations = false; }
        UpdateLobbyDeck();
        UpdateActivity();
        UpdateTempo();
        UpdateSocial();
        UpdateDeckCatalog();
        UpdateWorkshopData();
        UpdateAppUpdates();
        UpdateReaderActions();
        PrepareDecisionSession();
        priorityAttention.Observe(match);
        string turn = match == null ? "" : match.Id + ":" + match.Turn + ":" + match.ActivePlayerId;
        if (previousTurn != turn) { previousTurn = turn; held = false; turnBannerUntil = now + 1.5; }
        if (inspected != null) {
            inspected = RefreshInspectedCard();
            if (inspected == null && overlay == "inspect") overlay = "";
        }
        if (dragSource != null && CurrentDragCard(dragSource) == null) { dragging = false; dragSource = null; pressed = null; ClearCardDrag(); }
        UpdateLocalInput(time, mouse, keys, viewport, inputActive);
        // Give a ready pass precedence over starting another observation. Use this
        // update's pointer position so entering a card pauses Auto before Draw.
        bool autoBlocked = AutomaticInteractionPause() != DecisionDockPauseReason.None;
        ArmAutomaticQuietPriority(autoBlocked);
        bool paymentReserved = UpdateOptionalPayments(autoBlocked);
        bool combatReserved = paymentReserved || UpdateGroupBlocks(autoBlocked);
        bool stackReserved = combatReserved || UpdateStackResolution(autoBlocked);
        bool quietReserved = stackReserved || UpdateQuietPriority(autoBlocked);
        if (!quietReserved && (!automatic || held || autoBlocked)) session.CancelAdvance();
        if (!quietReserved && !ExperienceLabOpen && !session.NeedsRecovery && fixture == null && matchPresentation.Ready(now) && !busy && !polling
            && match is { } currentState && TurnGuide.CanAutoPass(currentState, automatic, held, preferences.Stops, autoBlocked)) {
            // Snapshot the policy: the worker never reads mutable UI preferences.
            var stops = preferences.Stops.ToHashSet();
            error = "";
            if (session.Advance(state => TurnGuide.CanAutoPass(state, true, false, stops, false), PresentationPacing.ShouldPresent))
                pendingActionLabel = "Passing priority automatically.";
        }
        UpdatePriorityNotification();
        if (!ExperienceLabOpen && loaded && fixture == null && !busy && !polling && now >= nextPoll && (localMatch || lobby != null)) {
            nextPoll = now + (session.NeedsRecovery ? 2 : match == null ? .6 : match.Decision == null ? .05 : .12);
            session.Poll(!localMatch, _ => { });
        }
        PreparePresentation(time);
        ObserveProjectionEnvironment();
    }
    private void SetAutomatic(bool value) { automatic = value; preferences = preferences with { Automatic = value }; SavePreferences(); }
    private void SavePreferences() { StopQuietPriority(); StopStackResolution(); StopGroupBlocks(); session.CancelAdvance(); try { preferences.Save(profile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = "Could not save settings: " + ex.Message; } }
    private bool Send(DecisionCommand? command, bool confirmedTurnPass = false)
    {
        if (command == null || fixture != null || session.NeedsRecovery || match?.Id != command.GameId || match.Decision?.Id != command.DecisionId) return false;
        if (AwaitingFinitePublication) return false;
        if (CurrentOptionalPayment?.Availability == OptionalManaPaymentAvailability.Unpayable && command is not CancelPayment) return false;
        if (command is UndoLastAction) return false;
        if (command is UndoAction undo && (!InputReady || UndoFacts.Command(match) != undo)) return false;
        if (match.FiniteStackYield is { Status: FiniteStackYieldStatus.Active }) {
            TakePriorityControl(); ClearReaderActions();
            notice = "Taking control \u2014 choose your action once priority returns.";
            noticeUntil = now + 4;
            return false;
        }
        // Legacy providers may still offer Forge's uninterruptible end-turn
        // latch. The native player always uses current, bounded passes instead.
        if (command is PassTurn) { ArmQuietPriority(confirmedTurnPass); return false; }
        if (AbilityActionPending) {
            var ability = (AbilityChoicePayload)match.Decision!.Payload;
            if (command is not ChooseOptions { Choices: var choices }
                || !(choices.Length == 0 && ability.Selection.Range.Min == 0
                    || choices.Length == 1 && ability.Selection.Choices.Any(choice => choice.Index == choices[0]))) return false;
        }
        if (!confirmedTurnPass && NeedsFirstLandConfirmation(match, command)) {
            turnPassRequest = new(command, match.ViewerId, match.Turn, match.PhaseKey);
            StopQuietPriority(); session.CancelAdvance();
            return false;
        }
        turnPassRequest = null;
        StopStackResolution();
        if (command is not PassForNow) StopQuietPriority();
        CancelReleasedPlayIntent();
        error = ""; commandFailure = false;
        if (!session.Submit(command)) return false;
        priorityAttention.Submitted(match, command);
        if (CardCollectionOpen) FoldCardCollection();
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
        matchPresentation.HoldAnnouncements(now, expandedStackWorkload.Length > 0 || match?.Decision is { Payload: not PriorityPayload } && !AutomatedCapturedPayment
            || AutomaticInteractionPause() is DecisionDockPauseReason.Inspection or DecisionDockPauseReason.Overlay);
        PrepareShellPresentation();
        PrepareDeckInteraction();
        ValidateStudyFocus();
        ReconcileCardCollection();
        PrepareCardCollectionHeader();
        tableScene = match == null ? null : TableScene.Build(match, pointer, null,
            dragging && dragSource?.Card is { } held ? CardViews.Identity(held) : null, false, CardPresentation.IsResource,
            allowInlineOrdering: TableChoices.IsDirect(match), sharedTableStudy: TableStudyEnabled,
            spread: spreadCollection == null ? null : new(spreadCollection, spreadTarget, spreadAreaTop), spreadMeasure: MeasureSpreadPacketHeader,
            groupCombatCandidates: GroupedCombatReview);
        PrepareCardCollection();
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
        projectionCapture = null;
        FinishAudioAdjustment();
        audioMixer?.Dispose();
        canvas.Dispose();
    }
}
