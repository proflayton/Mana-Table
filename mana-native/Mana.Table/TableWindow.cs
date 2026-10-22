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
internal sealed class TableWindow : Game
{
    private readonly TableApplication application;
    private readonly PresentationResources resources = new();
    private readonly CardArt art;
    private readonly GraphicsDeviceManager graphics;
    private readonly string? capture;
    private Canvas? painter;
    private TableAssets? assets;
    private RenderTarget2D? tableSurface, setupSurface, surface;
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

    public TableWindow(ICardEngine engine, string profile, string? capture, string? fixture, Point? size = null,
        GameSnapshot? initialState = null, ICardArtSource? artSource = null,
        IAppUpdates? appUpdates = null, Action<PreparedUpdate>? installUpdate = null, bool enableLiveTableStudy = false)
    {
        this.capture = capture;
        art = new(artSource ?? new MagicArtSource(profile));
        resources.Card = art.Get;
        application = new(engine, profile, fixture, initialState, resources, appUpdates, installUpdate, allowAutomaticChecks: capture == null,
            restartedAfterUpdate: ManagedInstallation.WasRestarted);
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
        Window.TextInput += (_, e) => application.ReceiveText(e.Character);
    }
    protected override void LoadContent()
    {
        painter = new(GraphicsDevice); screen = new(GraphicsDevice);
        assets = new(GraphicsDevice, Path.Combine(AppContext.BaseDirectory, "Assets"));
        resources.Arena = assets.Arena; resources.CardBack = assets.CardBack; resources.ManaSymbols = assets.ManaSymbols; resources.Play = assets.Play;
        tableSurface = new(GraphicsDevice, 1600, 900, false, SurfaceFormat.Color, DepthFormat.None, 4, RenderTargetUsage.DiscardContents);
        setupSurface = new(GraphicsDevice, 1600, 1000, false, SurfaceFormat.Color, DepthFormat.None, 4, RenderTargetUsage.DiscardContents);
        surface = setupSurface;
        application.Initialize();
    }
    protected override void Update(GameTime time)
    {
        art.Update(GraphicsDevice);
        var mouse = Automation?.ReadMouse() ?? Mouse.GetState();
        var keys = Automation?.ReadKeyboard() ?? Keyboard.GetState();
        var viewport = GraphicsDevice.Viewport;
        application.Update(time, mouse, keys, new(viewport.Width, viewport.Height));
        var preload = application.ArtPreloadCards();
        art.Preload(preload.Visible, preload.Anticipated);
        bool cardPointer = application.InteractivePointer;
        if (handCursor != cardPointer) {
            Mouse.SetCursor(cardPointer ? MouseCursor.Hand : MouseCursor.Arrow);
            handCursor = cardPointer;
        }
        if (Automation is { } driver && (application.AcceptedDrag || application.AcceptedHit is { } id && id == driver.ExpectedClick)) driver.ClickAccepted = true;
        if (keys.IsKeyDown(Keys.F11) && previousKeys.IsKeyUp(Keys.F11)) graphics.ToggleFullScreen();
        previousKeys = keys;
        base.Update(time);
    }
    protected override void Draw(GameTime time)
    {
        var frame = application.ComposePresentation(time);
        surface = frame.DesignHeight == TableLayout.Height ? tableSurface! : setupSurface!;
        GraphicsDevice.SetRenderTarget(surface);
        GraphicsDevice.Clear(new Color(17, 22, 29));
        painter!.Draw(frame.Drawing);
        GraphicsDevice.SetRenderTarget(null); GraphicsDevice.Clear(Color.Black);
        Automation?.Frame(surface);
        var view = GraphicsDevice.Viewport;
        float scale = Math.Min(view.Width / 1600f, view.Height / (float)frame.DesignHeight);
        screen!.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
        screen.Draw(surface, new Rectangle((int)((view.Width - 1600 * scale) / 2), (int)((view.Height - frame.DesignHeight * scale) / 2), (int)(1600 * scale), (int)(frame.DesignHeight * scale)), Color.White); screen.End();
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
            tableSurface?.Dispose(); setupSurface?.Dispose();
        }
        base.Dispose(disposing);
    }
    internal sealed class Probe(TableWindow window) : TableApplication.Probe(window.application)
    {
        internal GraphicsDevice GraphicsDevice => window.GraphicsDevice;
        internal Texture2D Surface => window.surface ?? throw new InvalidOperationException("The window has not loaded.");
        internal void CaptureWindow(string path) => window.windowCapture = path;
        internal void Exit() => window.Exit();
        internal void SetAutomation(ITableAutomation? value) => window.Automation = value;
    }
}
