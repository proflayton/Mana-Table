using Mana.Client;
using Mana.Magic;
using System.Collections.Concurrent;
using System.Text.Json;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal sealed partial class TableGame : Game
{
    private readonly ICardEngine engine;
    private readonly string profile;
    private readonly string? capture, fixture;
    private readonly GameSnapshot? initialState;
    private readonly ICardArtSource artSource;
    private readonly GraphicsDeviceManager graphics;
    private readonly ClientSession session;
    private sealed class CompletionQueue(ClientSession session) { public void Enqueue(Action action) => session.Post(action); }
    private readonly CompletionQueue updates;
    private bool busy => session.Busy;
    private bool polling => session.Polling;
    private readonly InteractionController interaction = new();
    private readonly List<Hit> hits = [];
    private readonly List<int> selections = [];
    private readonly Dictionary<string, int> pages = [];
    private readonly SceneMotion motion = new();
    private readonly TablePresence presence = new();
    private readonly PresentationPacing playback = new();
    private TableScene? tableScene;
    private readonly Dictionary<string, Pose> cardPositions = [];
    private readonly Dictionary<int, Vector2> heroPositions = [];
    private Canvas canvas = null!;
    private CardArt art = null!;
    private TableAssets assets = null!;
    private RenderTarget2D tableSurface = null!, setupSurface = null!, surface = null!;
    private SpriteBatch screen = null!;
    private PlayPreferences preferences;
    private IReadOnlyList<DeckSummary> decks = [];
    private IReadOnlyList<DeckPreset> presets = [];
    private DeckSummary? selectedDeck;
    private Lobby? lobby;
    private GameSnapshot? match => session.State;
    private SoloSetup? soloSetup;
    private readonly int[] soloChoices = [0, 1, 2];
    private bool soloTab = true, localMatch, loaded, closing, automatic, forward = true;
    private bool held, dragging, collapsedDecision, textFocus, filterInput, inspectBack, resizing;
    private string message = "Loading the card library…", error = "", decisionId = "", choiceFilter = "";
    private string overlay = "", inputText = "", previousTurn = "", lastHover = "";
    private string notice = "";
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
    private double nextPoll, now, hoverSince, turnBannerUntil;
    private double lastDrawTime;
    private int frames;
    private readonly AutoPriority priority = new();
    private readonly List<(Vector2 Position, string Text, Color Color, double Until)> feedback = [];
    private static readonly Color Ink = new(236, 236, 230), Muted = new(161, 173, 182), Gold = new(226, 190, 120), Teal = new(45, 94, 115), Panel = new(24, 30, 38), Blue = new(91, 184, 228), Red = new(235, 117, 105);
    private int DesignHeight => match == null ? 1000 : TableLayout.Height;
    private bool TableChoice => match != null && TableChoices.IsDirect(match);
    private bool DecisionModal => !TableChoice && !collapsedDecision && match?.Decision?.Kind is "choice" or "reveal" or "number" or "text" or "allocate";
    private bool OverlayOpen => overlay.Length > 0 || zoneName != null || DecisionModal;
    private string Scope => match == null ? deckEditor ? DeckScope : LobbyScope : session.Scope;
    private string ControlScope(string id) => id.StartsWith("chat-") ? ChatScope
        : match != null && (id is "auto" or "hold" or "instructions" or "action-details" || id.StartsWith("phase-stop:")) ? match.Id + ":playback" : Scope;
    private bool CurrentHit(Hit hit) => hit.Scope == ControlScope(hit.Id);
    internal sealed record Hit(string Id, string Scope, Rectangle Bounds, Action Action, Card? Card = null, string? Zone = null, int? PlayerId = null, Func<Point, bool>? Shape = null)
    {
        public bool Contains(Point point) => Bounds.Contains(point) && (Shape?.Invoke(point) ?? true);
    }
    private Hit? HitAt(Point at) => TableHitAt(at);

    public TableGame(ICardEngine engine, string profile, string? capture, string? fixture, Point? size = null, GameSnapshot? initialState = null, ICardArtSource? artSource = null)
    {
        session = new(engine); updates = new(session);
        session.Changed += AcceptEffects;
        session.Completed += () => { nextPoll = 0; if (error.Length == 0) priority.Reset(); };
        session.Failed += ex => { error = ex.Message; try { File.AppendAllText(Path.Combine(profile, "client.log"), ex + "\n"); } catch (IOException) { } };
        this.engine = engine; this.profile = profile; this.capture = capture; this.fixture = fixture; this.initialState = initialState;
        this.artSource = artSource ?? new MagicArtSource(profile);
        preferences = PlayPreferences.Load(profile); automatic = preferences.Automatic;
        graphics = new(this) { PreferredBackBufferWidth = size?.X ?? 1440, PreferredBackBufferHeight = size?.Y ?? 810, SynchronizeWithVerticalRetrace = true };
        Window.Title = "Mana Table — Commander"; Window.AllowUserResizing = true;
        IsMouseVisible = true; TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60);
        Window.ClientSizeChanged += (_, _) => {
            if (resizing || Window.ClientBounds.Width <= 0 || Window.ClientBounds.Height <= 0) return;
            resizing = true;
            try { graphics.PreferredBackBufferWidth = Window.ClientBounds.Width; graphics.PreferredBackBufferHeight = Window.ClientBounds.Height; graphics.ApplyChanges(); }
            finally { resizing = false; }
        };
        Window.TextInput += (_, e) => ReceiveText(e.Character);
    }
    private void ReceiveText(char character)
    {
        if (chatOpen && chatFocus) { ChatText(character); return; }
        if (match == null && deckEditor) { DeckText(character); return; }
        if (match == null) { SetupText(character); return; }
        if (!textFocus) return;
        string value = filterInput ? choiceFilter : inputText;
        if (character == '\b') { if (value.Length > 0) value = value[..^1]; }
        else if (!char.IsControl(character) && value.Length < 500) value += character;
        if (filterInput) { choiceFilter = value; choicePage = 0; } else inputText = value;
    }
    protected override void LoadContent()
    {
        canvas = new(GraphicsDevice); art = new(artSource); screen = new(GraphicsDevice);
        assets = new(GraphicsDevice, Path.Combine(AppContext.BaseDirectory, "Assets"));
        tableSurface = new(GraphicsDevice, 1600, 900, false, SurfaceFormat.Color, DepthFormat.None, 4, RenderTargetUsage.DiscardContents);
        setupSurface = new(GraphicsDevice, 1600, 1000, false, SurfaceFormat.Color, DepthFormat.None, 4, RenderTargetUsage.DiscardContents); surface = setupSurface;
        if (Automation != null) automatic = false;
        if (initialState != null) { localMatch = true; loaded = true; Accept(initialState); return; }
        if (fixture != null) {
            Accept(JsonSerializer.Deserialize<GameSnapshot>(File.ReadAllText(fixture), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
            loaded = true; message = "Visual review"; automatic = false; return;
        }
        Work(async () => { await engine.InitializeAsync(); decks = await engine.DecksAsync(); presets = await engine.PresetsAsync(); if (decks.FirstOrDefault() is { } deck) await ReadSelectedDeck(deck); loaded = true; message = "Choose your next Commander game."; });
    }
    private void Work(Func<Task> operation) { if (busy || closing) return; error = ""; session.Run(operation); }
    private void Accept(GameSnapshot? state) => session.Accept(state);
    private void AcceptEffects(GameSnapshot? previous, GameSnapshot? state)
    {
        if (state?.Id != previous?.Id) ResetTableView();
        if (state != null) { effects.Observe(previous, state, now); presence.Observe(previous, state, now); playback.Observe(previous, state, now); }
        if (state != null) ObserveActionFeedback(previous, state);
        if (state != null && previous != null && state.Id == previous.Id) {
            if (state.Notices.LastOrDefault() is { } latest && latest != previous.Notices.LastOrDefault()) { notice = latest; noticeUntil = now + 8; }
            foreach (var player in state.Players) {
                var old = previous.Players.FirstOrDefault(p => p.Id == player.Id);
                if (old != null && old.Life != player.Life) {
                    int difference = player.Life - old.Life;
                    feedback.Add((heroPositions.GetValueOrDefault(player.Id, new(800, 420)), difference > 0 ? "+" + difference : difference.ToString(), difference > 0 ? Blue : Red, now + 1.5));
                    if (difference < 0 && preferences.Sounds) assets.Play("life-loss", .45f);
                }
            }
            if (state.Status == "finished" && previous.Status != "finished") { overlay = "result"; zoneName = null; if (preferences.Sounds) assets.Play("game-end", .5f); }
            if (state.Combat?.Attackers.Length > 0 && previous.Combat?.Attackers.Length is null or 0 && preferences.Sounds) assets.Play("combat", .35f);
            if (state.Activity.LastOrDefault()?.Id != previous.Activity.LastOrDefault()?.Id && state.Activity.LastOrDefault()?.Kind is "land" or "cast" && preferences.Sounds) assets.Play("card-play", .35f);
        }
    }
    protected override void Update(GameTime time)
    {
        now = time.TotalGameTime.TotalSeconds;
        session.Pump();
        UpdateSocial();
        UpdateDeckCatalog();
        art.Update(GraphicsDevice);
        var prompt = match?.Decision;
        if (decisionId != (prompt?.Id ?? "")) {
            decisionId = prompt?.Id ?? ""; selections.Clear(); choicePage = 0; choiceFilter = ""; selectedCombat = null; priority.Reset();
            dragging = false; dragSource = null; pendingPlay = null; collapsedDecision = false; textFocus = false;
            if (!TableChoice && prompt is { Ordered: true } && prompt.Min == prompt.Choices.Length) selections.AddRange(prompt.Choices.Select(c => c.Index));
            allocations = prompt?.Choices.Select(_ => prompt.AtLeastOne ? 1 : 0).ToArray() ?? [];
            inputText = prompt?.Kind == "number" ? prompt.Min.ToString() : prompt?.Initial ?? "";
        }
        string turn = match == null ? "" : match.Id + ":" + match.Turn + ":" + match.ActivePlayerId;
        if (previousTurn != turn) { previousTurn = turn; held = false; turnBannerUntil = now + 1.5; if (match?.ActivePlayerId == match?.ViewerId && match?.Turn > 0 && preferences.Sounds) assets.Play("your-priority", .3f); }
        if (inspected != null) {
            inspected = VisibleCards().FirstOrDefault(c => SameVisibleCard(c, inspected));
            if (inspected == null && overlay == "inspect") overlay = "";
        }
        if (dragSource?.Card is { } heldCard && !VisibleCards().Any(c => SameVisibleCard(c, heldCard))) { dragging = false; dragSource = null; pressed = null; }
        var mouse = Automation?.ReadMouse() ?? Mouse.GetState(); var keys = Automation?.ReadKeyboard() ?? Keyboard.GetState();
        bool chatKeys = ChatKeys(keys);
        if (!chatKeys) { DeckKeys(keys); SetupKeys(keys); }
        var view = GraphicsDevice.Viewport; float scale = Math.Min(view.Width / 1600f, view.Height / (float)DesignHeight);
        pointer = new((int)((mouse.X - (view.Width - 1600 * scale) / 2) / scale), (int)((mouse.Y - (view.Height - DesignHeight * scale) / 2) / scale));
        if (mouse.RightButton == ButtonState.Pressed && previousMouse.RightButton == ButtonState.Released && hovered is { } card) { pendingPlay = null; Inspect(card); }
        if (mouse.LeftButton == ButtonState.Pressed && previousMouse.LeftButton == ButtonState.Released) {
            pressed = HitAt(pointer); pressPoint = pointer;
            if (chatOpen && !ChatBounds.Contains(pointer)) chatFocus = false;
            if (match == null && deckEditor && pressed?.Id is not ("deck-search-library" or "deck-search-deck" or "deck-clear-library" or "deck-clear-deck")) { deckTextFocus = ""; textFocus = false; }
            if (pendingPlay is { } pending && (pressed?.Id != pending.Id || pressed.Scope != pending.Scope)) pendingPlay = null;
            dragSource = !OverlayOpen && pressed?.Card is { } source && IsCardActionable(source) ? pressed : null; dragging = false;
        }
        if (mouse.LeftButton == ButtonState.Pressed && dragSource != null && dragSource.Scope == Scope && Vector2.Distance(pressPoint.ToVector2(), pointer.ToVector2()) > 9) { dragging = true; pendingPlay = null; }
        if (mouse.LeftButton == ButtonState.Released && previousMouse.LeftButton == ButtonState.Pressed) {
            var hit = HitAt(pointer);
            if (dragging) { bool accepted = DropCard(hit); if (Automation != null) Automation.ClickAccepted = accepted; }
            else if (hit != null && pressed?.Id == hit.Id && pressed.Scope == hit.Scope && CurrentHit(hit)) { ActivateHit(hit); if (Automation != null && hit.Id == Automation.ExpectedClick) Automation.ClickAccepted = true; }
            pressed = null; dragSource = null; dragging = false;
        }
        if (!chatKeys && keys.IsKeyDown(Keys.Escape) && previousKeys.IsKeyUp(Keys.Escape)) {
            dragging = false; dragSource = null; pressed = null; pendingPlay = null; selectedCombat = null; textFocus = false;
            if (match == null && !busy) { if (deckMenu.Length > 0) deckMenu = ""; else if (deckReview) deckReview = false; else if (deckEditor) CloseDeckEditor(); else CloseSetupModal(); }
            if (overlay.Length > 0) overlay = ""; else if (zoneName != null) zoneName = null; else if (DecisionModal) collapsedDecision = true;
        }
        if (!chatKeys && !textFocus && keys.IsKeyDown(Keys.Space) && previousKeys.IsKeyUp(Keys.Space) && !OverlayOpen && !busy && match != null) {
            if (TableChoice && TableChoices.CanConfirm(match, selections)) Send(new(match.Id, prompt!.Id, ReplyAction.Choose, Choices: selections.ToArray()));
            else if (!TableChoice && CombatGuide.CanConfirm(match)) Send(new(match.Id, prompt!.Id, ReplyAction.Confirm));
        }
        if (!chatKeys && !textFocus && keys.IsKeyDown(Keys.C) && previousKeys.IsKeyUp(Keys.C) && HasCombat && (overlay.Length == 0 || overlay == "combat")) { if (overlay == "combat") overlay = ""; else OpenCombat(); }
        if (!chatKeys && !textFocus && keys.IsKeyDown(Keys.F) && previousKeys.IsKeyUp(Keys.F)) SetAutomatic(!automatic);
        if (!chatKeys && !textFocus && keys.IsKeyDown(Keys.LeftControl) && previousKeys.IsKeyUp(Keys.LeftControl)) { held = !held; priority.Reset(); }
        if (keys.IsKeyDown(Keys.F11) && previousKeys.IsKeyUp(Keys.F11)) graphics.ToggleFullScreen();
        int wheel = mouse.ScrollWheelValue - previousMouse.ScrollWheelValue;
        if (wheel != 0) Scroll(Math.Sign(wheel));
        previousMouse = mouse; previousKeys = keys;
        // Give a ready pass precedence over starting another observation. Use this
        // update's pointer position so entering a card pauses Auto before Draw.
        bool autoBlocked = OverlayOpen || chatFocus || dragging || pressed != null || HitAt(pointer)?.Card is { FaceDown: false } || selectedCombat != null;
        if (!automatic || held || autoBlocked) session.CancelAdvance();
        if (fixture == null && playback.Ready(now) && priority.Update(match, automatic, held, preferences.Stops,
            autoBlocked, busy || polling) != null) {
            // Snapshot the policy: the worker never reads mutable UI preferences.
            var stops = preferences.Stops.ToHashSet();
            error = "";
            session.Advance(state => TurnGuide.CanAutoPass(state, true, false, stops, false), PresentationPacing.ShouldPresent);
        }
        if (loaded && fixture == null && !busy && !polling && now >= nextPoll && (localMatch || lobby != null)) {
            nextPoll = now + (match == null ? .6 : match.Decision == null ? .05 : .12);
            session.Poll(!localMatch, nextLobby => lobby = nextLobby);
        }
        feedback.RemoveAll(f => f.Until < now);
        base.Update(time);
    }
    private void SetAutomatic(bool value) { automatic = value; preferences = preferences with { Automatic = value }; SavePreferences(); }
    private void SavePreferences() { session.CancelAdvance(); priority.Reset(); try { preferences.Save(profile); } catch (IOException ex) { error = "Could not save settings: " + ex.Message; } }
    private void Send(DecisionReply reply)
    {
        if (fixture != null || match?.Id != reply.GameId || match.Decision?.Id != reply.DecisionId) return;
        error = ""; session.Submit(reply);
    }
    private void Button(string id, string label, int x, int y, int width, Action action, bool enabled = true, bool accent = false, int height = 40)
    {
        var bounds = new Rectangle(x, y, width, height); bool over = bounds.Contains(pointer);
        Color fill = enabled ? accent ? new(45, 91, 116) : over ? new(53, 64, 76) : new(32, 40, 51) : new(26, 31, 38);
        if (match != null && id is "confirm" or "open-decision") {
            // A single substantial action control, with a lit edge when it can advance play.
            canvas.Glow(bounds.Center.ToVector2(), new(width + 45, height * 2.7f), (enabled ? Gold : Color.Black) * (over ? .25f : .13f));
            canvas.Panel(bounds, enabled ? new Color(47, 72, 82) : fill, enabled ? Gold : Muted * .3f, 4);
            canvas.Gradient(new(x + 3, y + 3, width - 6, height - 6), enabled ? new Color(74, 114, 128) * (over ? .7f : .42f) : Color.Transparent, Color.Transparent);
            canvas.Line(new(x + 12, y + 2), new(x + width - 12, y + 2), (enabled ? Ink : Muted) * .4f);
        } else canvas.Panel(bounds, fill, enabled ? accent ? Gold : new Color(72, 85, 98) : new Color(45, 51, 59), 6);
        canvas.CenterText(label, new(x + 9, y + 1, width - 18, height - 2), enabled ? Ink : Muted * .6f, .76f, accent);
        if (enabled) hits.Add(new(id, ControlScope(id), bounds, action));
    }
    protected override void Draw(GameTime time)
    {
        BeginCardReading();
        hits.Clear(); hovered = null; hoverPreview = null; cardPositions.Clear(); heroPositions.Clear();
        surface = match == null ? setupSurface : tableSurface;
        GraphicsDevice.SetRenderTarget(surface); GraphicsDevice.Clear(new Color(17, 22, 29));
        float frameElapsed = (float)(time.TotalGameTime.TotalSeconds - lastDrawTime);
        lastDrawTime = time.TotalGameTime.TotalSeconds;
        canvas.Begin(); motion.Begin(frameElapsed, preferences.ReducedMotion);
        if (match == null) {
            canvas.Gradient(new(0, 0, 1600, 1000), new(24, 31, 42), new(12, 17, 24));
            canvas.Text("MANA TABLE", 28, 19, Gold, 1.2f, bold: true); canvas.Text("COMMANDER", 218, 25, Muted, .75f);
            canvas.Text(busy ? "Working…" : message, 435, 25, Ink, .82f, 740, 1); DrawLobby();
        } else {
            tableScene = TableScene.Build(match, pages, handPage, pointer, null, dragging ? dragSource?.Card is { } held ? CardViews.Identity(held) : null : null, false, CardPresentation.IsResource);
            DrawArena();
        }
        if (match != null && noticeUntil > now && !OverlayOpen) {
            canvas.Panel(new(427, 64, 746, 60), new Color(26, 36, 48), Gold, 7);
            canvas.Text(notice, 440, 76, Ink, .75f, 719, 2);
            hits.Add(new("notice", Scope, new(427, 64, 746, 60), () => { overlay = "history"; historyPage = 0; noticeUntil = 0; }));
        }
        DrawSocial();
        if (!string.IsNullOrWhiteSpace(error)) {
            int y = match == null ? 942 : 59;
            canvas.Panel(new(270, y, 1060, 46), new(71, 37, 38), Red, 7); canvas.Text(error, 284, y + 9, Ink, .76f, 980, 1);
            Button("dismiss", "×", 1280, y + 3, 42, () => error = "", height: 40);
        }
        motion.End(); canvas.End(); GraphicsDevice.SetRenderTarget(null); GraphicsDevice.Clear(Color.Black);
        Automation?.Frame(surface);
        var view = GraphicsDevice.Viewport; float scale = Math.Min(view.Width / 1600f, view.Height / (float)DesignHeight);
        screen.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
        screen.Draw(surface, new Rectangle((int)((view.Width - 1600 * scale) / 2), (int)((view.Height - DesignHeight * scale) / 2), (int)(1600 * scale), (int)(DesignHeight * scale)), Color.White); screen.End();
        if (capture != null && loaded && ++frames >= 90) { using var output = File.Create(capture); surface.SaveAsPng(output, surface.Width, surface.Height); Exit(); }
        base.Draw(time);
    }
    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        closing = true; session.Close(); engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        art.Dispose(); assets.Dispose(); canvas.Dispose(); screen.Dispose(); tableSurface.Dispose(); setupSurface.Dispose();
        base.OnExiting(sender, args);
    }
}
