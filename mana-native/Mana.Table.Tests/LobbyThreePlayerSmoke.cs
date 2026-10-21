using System.Text.Json;
using Mana.Contracts;
using Mana.Forge;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Real native gestures choose three seats, safely resize a connected lobby and
// start an actual three-human Commander game with independent private views.
internal sealed class LobbyThreePlayerSmoke : ITableAutomation, IDisposable
{
    private readonly TableWindow.Probe view;
    private readonly ForgePaths paths;
    private readonly List<ForgeEngine> guests = [];
    private readonly Queue<(double Delay, Func<bool>? Ready, Action Action)> steps = [];
    private readonly List<string> checks = [];
    private MouseState mouse;
    private Task? background;
    private double lastStep;
    private int step;
    private string tableId = "", deckId = "";
    private GameSnapshot[] privateViews = [];
    private readonly DateTime deadline = DateTime.UtcNow.AddMinutes(5);
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;

    internal static void Run(ForgePaths paths, Point? size)
    {
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true, TextScale = 1.3f }.Save(paths.Profile);
        File.WriteAllText(Path.Combine(paths.Profile, "experience.json"), JsonSerializer.Serialize(new { WelcomeSeen = true, TableGuideSeen = true }));
        var engine = new ForgeEngine(paths);
        try {
            engine.InitializeAsync().GetAwaiter().GetResult();
            engine.ImportAsync("Three-player host", "Commander\n1 Rograkh, Son of Rohgahh\nDeck\n99 Mountain").GetAwaiter().GetResult();
            using var game = new TableWindow(engine, paths.Profile, null, null, size, artSource: new CardReadingArtFixture());
            using var smoke = new LobbyThreePlayerSmoke(game.TestPort, paths);
            game.Automation = smoke; game.TestPort.DismissWelcome(); game.Run();
        } finally { engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    private LobbyThreePlayerSmoke(TableWindow.Probe view, ForgePaths paths)
    {
        this.view = view; this.paths = paths;
        Wait(() => view.Loaded && !view.Busy && !view.ActivityOpen && view.DeckDetails != null, 40, () => {
            Check(view.TextScale == 1.3f, "Three-player setup uses130% text at the requested physical window size");
            deckId = view.DeckDetails!.Id;
        });
        Click("friends-tab");
        Step(.1, () => {
            Check(new[] { "friends-count:2", "friends-count:3", "friends-count:4" }.All(id => view.Hits.Any(h => h.Id == id)),
                "Friend setup offers explicit two-, three- and four-player choices before hosting");
            Capture("01-choose-group-size");
        });
        Click("friends-count:3"); Click("connection-options"); Click("forward"); Click("setup-close"); Click("host");
        Wait(() => view.Lobby?.Mode == "hosting" && !view.Busy && !view.ActivityOpen, 20, () => {
            Check(view.Lobby is { Seats.Length: 3, PortMapping: "disabled" } && view.Lobby.Seats.All(s => s.Type != "AI"),
                "Choosing three creates exactly three human seats and no implicit AI or fourth seat");
            tableId = view.Lobby!.TableId;
            Capture("02-three-seat-lobby");
        });
        Click("review-deck"); Click("confirm-deck"); Click("ready");
        Step(.1, () => background = JoinGuests());
        Wait(() => view.Lobby?.CanStart == true, 20, () => {
            Check(view.Lobby is { Seats.Length: 3 } && view.Lobby.Seats.All(s => s.Ready && s.Type != "OPEN" && s.Type != "AI"),
                "Three real friends fill all three chosen seats without waiting for a fourth");
            Check(view.Hits.Any(h => h.Id == "start") && view.Hits.All(h => h.Id != "lobby-count:2"),
                "Start is available for three players and shrinking past a connected friend is unavailable");
            Capture("03-three-friends-ready");
        });
        Click("lobby-count:4");
        Wait(() => view.Lobby?.Seats.Length == 4, 20, () => {
            Check(view.Lobby!.TableId == tableId && view.Lobby.Seats[3].Type == "OPEN" && view.Lobby.Seats.Take(3).All(s => !s.Ready && s.Deck != null),
                "Expanding adds an open fourth seat, preserves connected decks and asks humans to ready again");
            Check(view.DeckConfirmed && view.DeckDetails!.Id == deckId && !view.Lobby.CanStart,
                "An open extra seat disables Start while preserving the host's confirmed list");
            Capture("04-open-fourth-seat");
        });
        Click("lobby-count:3");
        Wait(() => view.Lobby?.Seats.Length == 3, 20, () => {
            Check(view.Lobby!.TableId == tableId && view.Lobby.Seats.All(s => s.Type != "OPEN" && s.Type != "AI" && s.Deck != null),
                "Choosing three removes only the unused open seat and retains every friend at the same table");
            background = ReadyGuests();
        });
        Click("ready");
        Wait(() => view.Lobby?.CanStart == true, 20, () => {
            Check(view.Lobby!.Seats.All(s => s.Ready) && view.Hits.Any(h => h.Id == "start"),
                "After everyone readies again the resized three-human table can start");
            Capture("05-resized-three-ready");
        });
        Click("start");
        Wait(() => view.Match?.Players.Length == 3 && view.Lobby?.MatchActive == true && !view.Busy && !view.ActivityOpen, 30, () => {
            Check(view.Match is { Players.Length: 3 } && view.Lobby?.MatchActive == true,
                "The native Start control launches a real three-player Commander game");
            background = FinishOpening();
        });
        Step(.1, () => {
            Check(privateViews.Length == 3 && privateViews.Select(s => s.ViewerId).Distinct().Count() == 3,
                "Each of the three real human clients owns a distinct viewer in the same game");
            Check(privateViews.All(s => s.Players.Length == 3 && s.Players.All(p => p.Life == 40)
                && s.Players.All(p => p.Id == s.ViewerId ? p.Zone("Hand").Cards.Length == p.Zone("Hand").Count : p.Zone("Hand").Cards.Length == 0)),
                "All three clients retain their own complete hand while every opponent hand remains private");
            Check(privateViews.Any(s => s.PhaseKey == "MAIN1") && privateViews.All(s => s.Status == "playing"),
                "Three-human opening decisions finish and reach a usable first main phase");
            Capture("06-three-player-game");
            File.WriteAllText(Path.Combine(view.Profile, "lobby-three-player-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        });
        Step(.05, () => view.Exit());
    }
    private async Task JoinGuests()
    {
        string address = view.Lobby!.Addresses.First(a => a.Url.StartsWith("127.0.0.1:", StringComparison.Ordinal)).Url;
        for (int i = 0; i < 2; i++) {
            var guest = new ForgeEngine(paths with { Profile = Path.Combine(paths.Profile, "friend-" + i) }); guests.Add(guest);
            await guest.InitializeAsync();
            var deck = await guest.ImportAsync("Friend " + (i + 1), "Commander\n1 Rograkh, Son of Rohgahh\nDeck\n99 Mountain");
            var joined = await guest.JoinAsync(address);
            if (joined.Seats.Single(s => s.Local).Index != i + 1) throw new InvalidOperationException("A friend joined the wrong three-player seat");
            await guest.SelectDeckAsync(deck.Id); await guest.SetReadyAsync(true);
        }
    }
    private async Task ReadyGuests()
    {
        for (int i = 0; i < guests.Count; i++) {
            var lobby = await guests[i].LobbyAsync();
            if (lobby.TableId != tableId || lobby.Seats.Length != 3 || lobby.Seats.Single(s => s.Local).Index != i + 1)
                throw new InvalidOperationException("Resizing displaced a connected friend");
            await guests[i].SetReadyAsync(true);
        }
    }
    private async Task FinishOpening()
    {
        var until = DateTime.UtcNow.AddSeconds(45);
        ICardEngine[] seats = [view.Engine, .. guests];
        while (DateTime.UtcNow < until) {
            foreach (var seat in seats) {
                var state = await seat.ObserveAsync();
                if (state?.Status == "error") throw new InvalidOperationException(state.Error);
                if (state?.PhaseKey == "MAIN1") {
                    privateViews = (await Task.WhenAll(seats.Select(s => s.ObserveAsync()))).Select(s => s ?? throw new InvalidOperationException("Missing human view")).ToArray();
                    return;
                }
                if (state?.Decision is not { } d) continue;
                DecisionReply? reply = d.Kind switch {
                    "choice" => new(state.Id, d.Id, ReplyAction.Choose, Choices: d.Choices.Take(Math.Max(d.Min, Math.Min(1, d.Max))).Select(c => c.Index).ToArray()),
                    "reveal" => new(state.Id, d.Id, ReplyAction.Choose, Choices: []),
                    _ when d.PlayerChoices.Length > 0 => new(state.Id, d.Id, ReplyAction.SelectPlayer, Player: d.PlayerChoices[0]),
                    _ when d.OkEnabled => new(state.Id, d.Id, ReplyAction.Confirm),
                    _ => null
                };
                if (reply != null) try { await seat.ReplyAsync(reply); }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("choice has changed", StringComparison.Ordinal)) { }
            }
            await Task.Delay(30);
        }
        throw new TimeoutException("Three-player opening decisions did not reach the first main phase");
    }
    private void Click(string id) { Step(.1, () => MoveHit(view.Hits.Single(h => h.Id == id))); Step(0, () => mouse = Mouse(ButtonState.Pressed)); Step(0, () => mouse = Mouse()); }
    private void MoveHit(Mana.Table.Interaction.TableHit hit) { var p = SmokePointer.Hit(view, hit); mouse = new(p.X, p.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released); }
    private MouseState Mouse(ButtonState left = ButtonState.Released) => new(mouse.X, mouse.Y, 0, left, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    private void Step(double delay, Action action) => steps.Enqueue((delay, null, action));
    private void Wait(Func<bool> ready, double maximum, Action action) => steps.Enqueue((maximum, ready, action));
    private void Check(bool valid, string text) { if (!valid) throw new InvalidOperationException(text); checks.Add(text); }
    private void Capture(string name) {
        using var file = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(file, view.Surface.Width, view.Surface.Height);
        view.CaptureWindow(Path.Combine(view.Profile, name + "-window.png"));
    }
    public void Frame(Texture2D surface)
    {
        try {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Three-player lobby journey exceeded5minutes at step " + step);
            if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
            if (background != null) {
                if (!background.IsCompleted) return;
                background.GetAwaiter().GetResult(); background = null;
                // Guest startup is separate from the following observation wait.
                // Start its deadline after the asynchronous operation completes.
                lastStep = view.Now;
            }
            if (!view.Loaded || view.Busy || view.ActivityOpen || steps.Count == 0) return;
            var next = steps.Peek();
            if (view.Now - lastStep < next.Delay && (next.Ready == null || !next.Ready())) return;
            steps.Dequeue(); step++; next.Action(); lastStep = view.Now;
        } catch {
            File.WriteAllText(Path.Combine(view.Profile, "lobby-three-player-failure.json"), JsonSerializer.Serialize(new { step, view.Now, checks,
                view.Scope, view.SetupModal, view.Lobby, view.Match, privateViews,
                hits = view.Hits.Select(h => new { h.Id, h.Bounds }) }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            Capture("failure"); throw;
        }
    }
    public void Dispose()
    {
        try { background?.GetAwaiter().GetResult(); } catch { }
        foreach (var guest in guests) guest.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
