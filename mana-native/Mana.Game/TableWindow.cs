using Mana.Client;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal interface ITableAutomation
{
    MouseState ReadMouse();
    KeyboardState? ReadKeyboard() => null;
    string? ExpectedClick { get; }
    bool ClickAccepted { get; set; }
    void Frame(Texture2D surface);
}

/// <summary>Owns the window, input sampling and GPU resources. All application composition lives in TableApplication.</summary>
public sealed class TableWindow : Game
{
    private readonly TableApplication application;
    private readonly PresentationResources resources = new();
    private readonly CardArt art;
    private readonly GraphicsDeviceManager graphics;
    private readonly string? capture;
    private Canvas? painter;
    private TableAssets? assets;
    private RenderTarget2D? surface;
    private SpriteBatch? screen;
    private bool resizing;
    private KeyboardState previousKeys;
    private bool? handCursor;
    private string? windowCapture;
    private int frames;
    private ITableAutomation? automation;
    internal ITableAutomation? Automation {
        get => automation;
        set { automation = value; application.Automated = value != null; }
    }
    internal Probe TestPort => new(this);

    public TableWindow(TableServices services, string profile, string? capture, string? fixture, Point? size = null,
        GameSnapshot? initialState = null, ICardArtSource? artSource = null,
        IAppUpdates? appUpdates = null, Action<PreparedUpdate>? installUpdate = null, bool enableLiveTableStudy = false,
        bool restartedAfterUpdate = false)
    {
        this.capture = capture;
        art = new(artSource ?? new MagicArtSource(profile));
        resources.Card = art.Get;
        resources.Artwork = art.Readiness;
        application = new(services, profile, fixture, initialState, resources, appUpdates, installUpdate, allowAutomaticChecks: capture == null,
            restartedAfterUpdate: restartedAfterUpdate);
        if (enableLiveTableStudy) application.EnableLiveTableStudy();
        application.CaptureMode = capture != null;
        application.ExitRequested += Exit;
        graphics = new(this) { PreferredBackBufferWidth = size?.X ?? 1440, PreferredBackBufferHeight = size?.Y ?? 810, SynchronizeWithVerticalRetrace = true };
        Window.Title = "Mana Table — Commander"; Window.AllowUserResizing = true;
        IsMouseVisible = true; TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60);
        Window.ClientSizeChanged += (_, _) => {
            if (resizing || Window.ClientBounds.Width <= 0 || Window.ClientBounds.Height <= 0) return;
            resizing = true;
            try { graphics.PreferredBackBufferWidth = Window.ClientBounds.Width; graphics.PreferredBackBufferHeight = Window.ClientBounds.Height; graphics.ApplyChanges(); }
            finally { resizing = false; }
        };
        Deactivated += (_, _) => { application.DeactivateInput(); handCursor = null; };
        Window.TextInput += (_, e) => { if (IsActive) application.ReceiveText(e.Character); };
    }
    protected override void LoadContent()
    {
        painter = new(GraphicsDevice); screen = new(GraphicsDevice);
        assets = new(GraphicsDevice, Path.Combine(AppContext.BaseDirectory, "Assets"));
        resources.Arena = assets.Arena; resources.CardBack = assets.CardBack; resources.ManaSymbols = assets.ManaSymbols; resources.AudioOutput = assets.AudioOutput;
        application.Initialize();
    }
    protected override void Update(GameTime time)
    {
        art.Update(GraphicsDevice);
        var mouse = Automation?.ReadMouse() ?? Mouse.GetState();
        var keys = Automation?.ReadKeyboard() ?? Keyboard.GetState();
        var backbuffer = GraphicsDevice.PresentationParameters;
        application.Update(time, mouse, keys, new(backbuffer.BackBufferWidth, backbuffer.BackBufferHeight),
            inputActive: IsActive || Automation != null);
        var preload = application.ArtPreloadCards();
        art.Preload(preload.Visible, preload.Anticipated);
        bool cardPointer = application.InteractivePointer;
        if (!application.InputReady) handCursor = null;
        else if (handCursor != cardPointer) {
            Mouse.SetCursor(cardPointer ? MouseCursor.Hand : MouseCursor.Arrow);
            handCursor = cardPointer;
        }
        if (Automation is { } driver && (application.AcceptedDrag || application.AcceptedHit is { } id && id == driver.ExpectedClick)) driver.ClickAccepted = true;
        if (application.InputReady && keys.IsKeyDown(Keys.F11) && previousKeys.IsKeyUp(Keys.F11)) graphics.ToggleFullScreen();
        previousKeys = keys;
        base.Update(time);
    }
    protected override void Draw(GameTime time)
    {
        var frame = application.ReadPresentation();
        var backbuffer = GraphicsDevice.PresentationParameters;
        var renderView = RenderViewport.Fit(new(backbuffer.BackBufferWidth, backbuffer.BackBufferHeight), frame.DesignHeight);
        if (surface == null || surface.Width != renderView.Bounds.Width || surface.Height != renderView.Bounds.Height) {
            surface?.Dispose();
            surface = new(GraphicsDevice, renderView.Bounds.Width, renderView.Bounds.Height, false,
                SurfaceFormat.Color, DepthFormat.None, 4, RenderTargetUsage.DiscardContents);
        }
        GraphicsDevice.SetRenderTarget(surface);
        GraphicsDevice.Clear(new Color(17, 22, 29));
        painter!.Draw(frame.Drawing, renderView.Scale);
        GraphicsDevice.SetRenderTarget(null); GraphicsDevice.Clear(Color.Black);
        Automation?.Frame(surface);
        var view = GraphicsDevice.Viewport;
        screen!.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
        screen.Draw(surface, renderView.Bounds, Color.White); screen.End();
        if (windowCapture is { } windowPath) {
            windowCapture = null;
            var pixels = new Color[view.Width * view.Height]; GraphicsDevice.GetBackBufferData(pixels);
            using var texture = new Texture2D(GraphicsDevice, view.Width, view.Height); texture.SetData(pixels);
            using var output = File.Create(windowPath); texture.SaveAsPng(output, view.Width, view.Height);
        }
        if (capture != null && application.TestPort.Loaded && ++frames >= 90) {
            using (var output = File.Create(capture)) surface.SaveAsPng(output, surface.Width, surface.Height);
            var capturedPixels = new Color[view.Width * view.Height]; GraphicsDevice.GetBackBufferData(capturedPixels);
            using (var texture = new Texture2D(GraphicsDevice, view.Width, view.Height)) {
                texture.SetData(capturedPixels);
                using var output = File.Create(Path.Combine(Path.GetDirectoryName(capture)!, Path.GetFileNameWithoutExtension(capture) + "-window.png"));
                texture.SaveAsPng(output, view.Width, view.Height);
            }
            Exit();
        }
        base.Draw(time);
    }
    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        application.Dispose();
        base.OnExiting(sender, args);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) {
            application.Dispose(); art.Dispose(); assets?.Dispose(); painter?.Dispose(); screen?.Dispose();
            surface?.Dispose();
        }
        base.Dispose(disposing);
    }
    internal sealed class Probe(TableWindow window) : TableApplication.Probe(window.application)
    {
        internal GraphicsDevice GraphicsDevice => window.GraphicsDevice;
        internal Texture2D Surface => window.surface ?? throw new InvalidOperationException("The window has not loaded.");
        internal void CaptureWindow(string path) => window.windowCapture = path;
        internal PresentationFrame CaptureLobbySnapshot(Lobby state, string path)
        {
            var frame = ComposeLobbySnapshot(state);
            var backbuffer = window.GraphicsDevice.PresentationParameters;
            var view = RenderViewport.Fit(new(backbuffer.BackBufferWidth, backbuffer.BackBufferHeight), frame.DesignHeight);
            window.GraphicsDevice.SetRenderTarget(window.surface);
            try {
                window.GraphicsDevice.Clear(new Color(17, 22, 29));
                window.painter!.Draw(frame.Drawing, view.Scale);
            } finally { window.GraphicsDevice.SetRenderTarget(null); }
            using var file = File.Create(path); Surface.SaveAsPng(file, Surface.Width, Surface.Height);
            window.windowCapture = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-window.png");
            return frame;
        }
        internal void Exit() => window.Exit();
        internal void SetAutomation(ITableAutomation? value) => window.Automation = value;
    }
}
