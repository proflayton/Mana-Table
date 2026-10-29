using System.Text.Json;
using Mana.Contracts;
using Mana.Forge;
using Mana.Renderer;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Three independent Forge clients use real lobby commands. The native window
// exercises host controls, then renders an actual guest's read-only projection.
internal sealed class LobbyAiSeatSmoke : ITableAutomation, IDisposable
{
    private readonly TableWindow.Probe view;
    private readonly ForgePaths paths;
    private readonly List<ForgeEngine> guests = [];
    private readonly Queue<(double Delay, Func<bool>? Ready, Action Action)> steps = [];
    private readonly List<string> checks = [];
    private readonly List<int> joinedSeats = [];
    private MouseState mouse;
    private Task? background;
    private IDisposable? observationPause;
    private double lastStep;
    private int step;
    private string firstAi = "", secondAi = "", deckId = "";
    private Lobby? guestView, hostView, hostAfterCapture;
    private bool guestRejected;
    private readonly DateTime deadline = DateTime.UtcNow.AddMinutes(4);
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;

    internal static void Run(ForgePaths paths, Point? size)
    {
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true, TextScale = 1.3f }.Save(paths.Profile);
        var engine = new ForgeEngine(paths);
        try {
            engine.InitializeAsync().GetAwaiter().GetResult();
            engine.ImportAsync("Mixed table host", "Commander\n1 Rograkh, Son of Rohgahh\nDeck\n99 Mountain").GetAwaiter().GetResult();
            using var game = new TableWindow(TestTableServices.For(engine), paths.Profile, null, null, size, artSource: new CardReadingArtFixture());
            using var smoke = new LobbyAiSeatSmoke(game.TestPort, paths);
            game.Automation = smoke;  game.Run();
        } finally { engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    private LobbyAiSeatSmoke(TableWindow.Probe view, ForgePaths paths)
    {
        this.view = view; this.paths = paths;
        Wait(() => view.Loaded && !view.Busy && !view.ActivityOpen && view.DeckDetails != null, 40, () => {
            Check(view.TextScale == 1.3f, "Mixed-table controls use130% text at the requested physical window size");
            deckId = view.DeckDetails!.Id;
        });
        Click("friends-tab"); Click("connection-options"); Click("forward"); Click("setup-close"); Click("host");
        Wait(() => view.Lobby?.Mode == "hosting" && !view.Busy && !view.ActivityOpen, 20, () => {
            Check(view.Lobby is { Seats.Length: 4, PortMapping: "disabled" } && view.Lobby.AiOpponents.Length >= 2,
                "Hosting exposes compatible AI decks without router forwarding or implicit seat changes");
            Check(view.Hits.Any(h => h.Id == "lobby-ai:1") && view.Hits.All(h => h.Id != "lobby-ai:0"),
                "Only the host sees Add AI on open nonhost seats");
            firstAi = view.Lobby!.AiOpponents[0].Id; secondAi = view.Lobby.AiOpponents[1].Id;
            Capture("01-host-open-seats");
        });
        Click("review-deck"); Click("confirm-deck"); Click("ready");
        Step(.1, () => Check(HostReady(), "The host explicitly confirms and readies the saved human deck"));
        Click("lobby-ai:1");
        Step(.1, () => {
            Check(view.SetupModal == "lobby-ai" && view.Hits.Any(h => h.Id == "lobby-ai-deck:" + firstAi),
                "Add AI opens a compatible-deck picker isolated from the table underneath");
            Capture("02-compatible-ai-decks");
        });
        ClickAi(() => firstAi);
        Wait(() => Seat(1)?.Type == "AI" && view.SetupModal.Length == 0, 20, () => {
            Check(Seat(1) is { Ready: true } seat && seat.OpponentId == firstAi && seat.Deck == view.Lobby!.AiOpponents[0].Name,
                "Choosing an AI deck fills the exact seat with its readable deck name and ready state");
            Check(HostReady() && view.DeckConfirmed && view.DeckDetails!.Id == deckId,
                "Adding AI preserves the host's selected deck, confirmation and readiness");
            Capture("03-ai-ready");
        });
        Click("lobby-ai:1"); ClickAi(() => secondAi);
        Wait(() => Seat(1)?.OpponentId == secondAi && view.SetupModal.Length == 0, 20, () => {
            Check(Seat(1) is { Ready: true } && HostReady(), "Replacing the AI deck affects only that AI seat");
        });
        Click("lobby-ai-remove:1");
        Wait(() => Seat(1)?.Type == "OPEN", 20, () => {
            Check(Seat(1) is { Ready: false, Deck: null } && HostReady(), "Removing AI opens its seat while preserving the human ready seat");
            Capture("04-ai-removed");
        });
        Click("lobby-ai:1"); ClickAi(() => firstAi);
        Wait(() => Seat(1)?.Type == "AI" && !view.Busy && !view.ActivityOpen, 20, () => background = JoinGuests());
        Step(.1, () => {
            Check(joinedSeats.SequenceEqual([2, 3]), "Two real friends skip the reserved AI seat and take the remaining open seats");
        });
        Wait(() => view.Lobby?.CanStart == true, 20, () => {
            Check(view.Lobby!.Seats.Count(s => s.Type == "AI") == 1 && view.Lobby.Seats.Count(s => s.Type != "OPEN" && s.Type != "AI") == 3
                && view.Lobby.Seats.All(s => s.Ready), "Three human players and one AI form a complete ready four-player table");
            Check(view.Hits.Any(h => h.Id == "start") && HostReady(), "The host can start once the human and AI seats are ready");
            Check(PairedGuestPreview(view.Lobby) && view.Lobby.Seats.Single(seat => seat.Type == "AI").DeckPreview is { Commanders.Length: > 0 },
                "The host receives both partner commander faces and the AI's public commander preview");
        });
        Step(.25, () => {
            Check(PartnerPortraits(view.Frame!), "The native host frame draws both public partners inside their shared seat medallion");
            Capture("05-three-friends-one-ai");
            observationPause = view.PauseObservationScheduling();
            background = ReadGuestAndCheckAuthority();
        });
        Wait(() => !view.Polling, 10, () => {
            try {
                Check(guestRejected && guestView?.Mode == "joined" && guestView.Seats.Any(s => s.Type == "AI" && s.Ready),
                    "The guest receives the public AI deck/readiness and the engine rejects its attempt to change a seat");
                Check(PairedGuestPreview(guestView!) && guestView!.Seats.Single(seat => seat.Type == "AI").DeckPreview is { Commanders.Length: > 0 },
                    "The guest receives the same public partner faces and AI commander preview");
                Check(!view.Polling, "The host's in-flight observation finishes before presenting the guest projection");
                var frame = view.CaptureLobbySnapshot(guestView!, Path.Combine(view.Profile, "06-guest-read-only.png"));
                Check(frame.Hits.All(hit => !hit.Id.StartsWith("lobby-ai", StringComparison.Ordinal)) && frame.Hits.All(hit => hit.Id != "start"),
                    "Rendering the actual guest snapshot exposes no Add, Change, Remove or Start controls");
                Check(PartnerPortraits(frame), "The native guest frame draws both public partner portraits");
                Check(view.Lobby?.Mode == "hosting" && view.DeckConfirmed && HostReady(),
                    "Finite guest snapshot composition restores the actual host owner and its Ready acknowledgment immediately");
            } finally {
                observationPause?.Dispose(); observationPause = null;
            }
            background = Task.Run(async () => hostAfterCapture = await view.Services.Tables.LobbyAsync());
        });
        Wait(() => view.Lobby?.Mode == "hosting" && view.Hits.Any(h => h.Id == "lobby-ai-remove:1"), 10, () => {
            Check(view.DeckConfirmed && HostReady() && view.Lobby!.CanStart, "Reviewing the guest projection leaves the real host table and readiness intact");
            Check(hostAfterCapture is { CanStart: true } && hostAfterCapture.Seats.Select(seat => (seat.Index, seat.Ready, seat.DeckPreview?.SelectionId))
                    .SequenceEqual(hostView!.Seats.Select(seat => (seat.Index, seat.Ready, seat.DeckPreview?.SelectionId))),
                "The authoritative provider confirms snapshot inspection changed no seat assignment or Ready consent");
            File.WriteAllText(Path.Combine(view.Profile, "lobby-ai-seat-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        });
        Step(.05, () => view.Exit());
    }
    private async Task JoinGuests()
    {
        string address = view.Lobby!.Addresses.First(a => a.Url.StartsWith("127.0.0.1:", StringComparison.Ordinal)).Url;
        for (int i = 0; i < 2; i++) {
            var guest = new ForgeEngine(paths with { Profile = Path.Combine(paths.Profile, "friend-" + i) }); guests.Add(guest);
            await guest.InitializeAsync();
            var deck = await guest.ImportAsync("Friend " + (i + 1), i == 0
                ? "Commander\n1 Pir, Imaginative Rascal\n1 Toothy, Imaginary Friend\nDeck\n49 Forest\n49 Island"
                : "Commander\n1 Rograkh, Son of Rohgahh\nDeck\n99 Mountain");
            var joined = await guest.JoinAsync(address);
            joinedSeats.Add(joined.Seats.Single(s => s.Local).Index);
            await guest.SelectDeckAsync(new(joined.TableId, deck.Id, deck.LibraryToken)); await guest.SetReadyAsync(true);
        }
    }
    private async Task ReadGuestAndCheckAuthority()
    {
        guestView = await guests[0].LobbyAsync();
        try { await ((ITableSeatSetup)guests[0]).SetSeatAsync(guestView.TableId, 1, "OPEN"); }
        catch (Exception ex) when (ex.Message.Contains("Only the host can change this table", StringComparison.Ordinal)) { guestRejected = true; }
        if (!guestRejected) throw new InvalidOperationException("A guest unexpectedly changed the host's AI seat");
        hostView = await view.Services.Tables.LobbyAsync();
    }
    private bool HostReady() => view.Lobby?.Seats.FirstOrDefault(s => s.Index == 0) is { Ready: true, Deck: "Mixed table host" };
    private static bool PairedGuestPreview(Lobby state) => state.Seats.Single(seat => seat.Index == 2).DeckPreview?.Commanders
        .Select(card => card.Name).Order().SequenceEqual(new[] { "Pir, Imaginative Rascal", "Toothy, Imaginary Friend" }.Order()) == true;
    private static bool PartnerPortraits(Mana.Table.Presentation.PresentationFrame frame)
    {
        var center = new Microsoft.Xna.Framework.Vector2(280 + 2 * 347, 390);
        return frame.Drawing.Commands.OfType<DrawCommand.Portrait>().Count(command => command.Center == center && command.Radius == 66) == 2
            && frame.Drawing.Commands.OfType<DrawCommand.BeginClip>().Count(command => command.Bounds.Y == 324 && command.Bounds.Height == 132
                && command.Bounds.Width == 66 && command.Bounds.X is 908 or 974) == 2;
    }
    private Seat? Seat(int index) => view.Lobby?.Seats.FirstOrDefault(s => s.Index == index);
    private void ClickAi(Func<string> id) { Step(.1, () => MoveHit(view.Hits.Single(h => h.Id == "lobby-ai-deck:" + id()))); MouseClick(); }
    private void Click(string id) { Step(.1, () => MoveHit(view.Hits.Single(h => h.Id == id))); MouseClick(); }
    private void MouseClick() { Step(0, () => mouse = Mouse(ButtonState.Pressed)); Step(0, () => mouse = Mouse()); }
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
            if (DateTime.UtcNow > deadline) throw new TimeoutException("AI lobby journey exceeded4minutes at step " + step);
            if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
            if (background != null) {
                if (!background.IsCompleted) return;
                background.GetAwaiter().GetResult(); background = null;
            }
            if (!view.Loaded || view.Busy || view.ActivityOpen || steps.Count == 0) return;
            var next = steps.Peek();
            if (view.Now - lastStep < next.Delay && (next.Ready == null || !next.Ready())) return;
            steps.Dequeue(); step++; next.Action(); lastStep = view.Now;
        } catch {
            File.WriteAllText(Path.Combine(view.Profile, "lobby-ai-seat-failure.json"), JsonSerializer.Serialize(new { step, view.Now, checks,
                view.Scope, view.SetupModal, view.Lobby, joinedSeats, guestRejected, guestView,
                hits = view.Hits.Select(h => new { h.Id, h.Bounds }) }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            Capture("failure"); throw;
        }
    }
    public void Dispose()
    {
        observationPause?.Dispose(); observationPause = null;
        try { background?.GetAwaiter().GetResult(); } catch { }
        foreach (var guest in guests) guest.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
