using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

// Authored review material rendered by the production window and CardArt path.
// This does not exercise cooperative rules or establish human UX approval.
internal static class LargeTableCapture
{
    internal static void Run(string profile, Point? size, string? artCache, string captureView)
    {
        if (captureView is not ("table" or "lobby" or "access" or "admission" or "admission-failed" or "target-restrictions"))
            throw new ArgumentOutOfRangeException(nameof(captureView));
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(profile);
        bool targeting = captureView == "target-restrictions";
        var state = targeting ? TargetRestrictionWireChecks.Decode(TargetRestrictionWireChecks.Wire())
            : captureView == "access" ? LargeTablePlayAccessChecks.CaptureState() : LargeTableChecks.State(6, 0);
        if (!targeting) state = LargeTableChecks.WithFarMana(LargeTableChecks.WithRemoteHandCount(state, 7));
        string fixture = Path.Combine(profile, targeting ? "target-restriction-authored-fixture.json" : "six-seat-authored-fixture.json");
        File.WriteAllText(fixture, JsonSerializer.Serialize(state));
        var engine = new InteractionScenarioEngine(); engine.Present(state);
        using var source = new MagicArtSource(artCache ?? profile);
        bool admission = captureView is "admission" or "admission-failed";
        bool table = captureView is "table" or "access" or "target-restrictions";
        var services = table ? TestTableServices.For(engine) : CooperativeLobbyInteractionChecks.CreateCaptureServices(
            holdAdmission: admission, failAdmission: captureView == "admission-failed");
        using var window = new TableWindow(services, profile, null, null, size,
            initialState: table ? state : null, artSource: source, enableLiveTableStudy: true);
        // Capture from the real GPU target without presenting an interactive
        // desktop window. Automation supplies both input devices explicitly.
        window.Window.Position = new Point(-32000, -32000);
        if (Control.FromHandle(window.Window.Handle) is Form captureWindow) {
            captureWindow.Enabled = false;
            captureWindow.ShowInTaskbar = false;
            captureWindow.Shown += (_, _) => captureWindow.Hide();
        }
        var cards = state.Players.SelectMany(player => player.Zones).SelectMany(zone => zone.Cards)
            .Where(card => !card.FaceDown).ToArray();
        window.Automation = targeting ? new TargetRestrictionCapture(window.TestPort, engine, cards)
            : table ? new Capture(window.TestPort, cards) : new LobbyCapture(window.TestPort, captureView);
        window.Run();
    }

    private sealed class TargetRestrictionCapture(TableWindow.Probe view, InteractionScenarioEngine engine, Card[] required) : ITableAutomation
    {
        private readonly DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        private Point pointer = new(-100, -100);
        private int stage, mousePhase;
        public string? ExpectedClick { get; private set; }
        public bool ClickAccepted { get; set; }
        public KeyboardState? ReadKeyboard() => new KeyboardState();
        public MouseState ReadMouse()
        {
            bool down = mousePhase == 1;
            if (mousePhase is 1 or 2) mousePhase++;
            return new(pointer.X, pointer.Y, 0, down ? ButtonState.Pressed : ButtonState.Released,
                ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
        }
        public void Frame(Texture2D surface)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Target explanation GPU capture timed out at stage " + stage);
            if (engine.Replies.Count != 0) throw new InvalidOperationException("Reading a blocked target submitted a command.");
            if (mousePhase is 1 or 2) return;
            if (mousePhase == 3) {
                if (!ClickAccepted) throw new InvalidOperationException("The blocked card lost its ordinary inspection hit.");
                mousePhase = 0; ExpectedClick = null; stage = 3; return;
            }
            if (!view.Loaded || view.Frame?.Scene?.Seats.Count != 4) return;
            var missing = required.Where(card => view.Art(card) == null).Select(card => card.Name).Distinct().ToArray();
            if (missing.Length > 0 && DateTime.UtcNow < deadline.AddSeconds(-5)) return;
            if (stage == 0) {
                var hit = view.Hits.Last(hit => hit.Card?.VisualId == "omnath");
                pointer = SmokePointer.Hit(view, hit); stage = 1; return;
            }
            if (stage == 1) {
                if (!view.Hits.Any(hit => hit.Id == "target-restriction-help")) return;
                Capture("target-restriction-hover");
                var hit = view.Hits.Last(hit => hit.Card?.VisualId == "omnath");
                pointer = SmokePointer.Hit(view, hit); ExpectedClick = hit.Id; ClickAccepted = false;
                mousePhase = 1; stage = 2; return;
            }
            if (stage == 3 && view.Overlay == "inspect") {
                Capture("target-restriction-reader"); stage = 4; return;
            }
            if (stage == 4) view.Exit();

            void Capture(string name)
            {
                string prefix = Path.Combine(view.Profile, name);
                using (var output = File.Create(prefix + ".png")) surface.SaveAsPng(output, surface.Width, surface.Height);
                File.WriteAllText(prefix + ".json", JsonSerializer.Serialize(new {
                    completeArtwork = missing.Length == 0, missingArtwork = missing, replies = engine.Replies.Count,
                    text = view.Frame.Observe().Text, humanReview = "pending", backgroundWindow = true, syntheticInputOnly = true,
                    source = "authored current engine explanation; production decoder, renderer and pointer reading; not a Magic rules simulation"
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }

    private sealed class Capture(TableWindow.Probe view, Card[] required) : ITableAutomation
    {
        private readonly DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        private bool captured;
        public string? ExpectedClick => null;
        public bool ClickAccepted { get; set; }
        public MouseState ReadMouse() => new(-100, -100, 0, ButtonState.Released, ButtonState.Released,
            ButtonState.Released, ButtonState.Released, ButtonState.Released);
        public KeyboardState? ReadKeyboard() => new KeyboardState();
        public void Frame(Texture2D surface)
        {
            if (captured) { view.Exit(); return; }
            if (!view.Loaded || view.Frame?.Scene?.Seats.Count != 6) return;
            var missing = required.Where(card => view.Art(card) == null).Select(card => card.Name).Distinct().ToArray();
            if (missing.Length > 0 && DateTime.UtcNow < deadline) return;
            string prefix = Path.Combine(view.Profile, "six-seat-overview");
            using (var output = File.Create(prefix + ".png")) surface.SaveAsPng(output, surface.Width, surface.Height);
            view.CaptureWindow(prefix + "-window.png");
            File.WriteAllText(prefix + ".json", JsonSerializer.Serialize(new { completeArtwork = missing.Length == 0,
                missingArtwork = missing, view.Frame!.BoardView, seats = view.Frame.Scene.Seats.Select(seat => new {
                    seat.PlayerId, seat.Geometry.Frame, seat.Geometry.PlaymatPlacement }), readouts = view.ReadoutPlans,
                controls = view.Hits.Where(hit => hit.Id is "confirm" or "quiet-priority-arm" or "play-access-unfold"
                    || hit.Id.StartsWith("play-access-source:", StringComparison.Ordinal)).Select(hit => new { hit.Id, hit.Bounds }),
                externalAccess = view.Frame.Scene.PlayAccess.Groups.Select(group => new { group.Id, group.Bounds,
                    cards = group.Cards.Select(card => new { card.Scene.Id, card.Scene.Pose }) }),
                humanReview = "pending", backgroundWindow = true, syntheticInputOnly = true,
                source = "authored public projection; production renderer; no rules simulation" },
                new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            captured = true;
        }
    }

    private sealed class LobbyCapture(TableWindow.Probe view, string captureView) : ITableAutomation
    {
        private readonly DateTime deadline = DateTime.UtcNow.AddSeconds(180);
        private int stage, next, mousePhase, alliedSeat = 1;
        private Point pointer = new(-100, -100);
        public string? ExpectedClick { get; private set; }
        public bool ClickAccepted { get; set; }
        public KeyboardState? ReadKeyboard() => new KeyboardState();
        public MouseState ReadMouse()
        {
            bool down = mousePhase == 1;
            if (mousePhase is 1 or 2) mousePhase++;
            return new(pointer.X, pointer.Y, 0, down ? ButtonState.Pressed : ButtonState.Released,
                ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
        }
        private void Click(string id, int target)
        {
            var hit = view.Hits.LastOrDefault(hit => hit.Id == id)
                ?? throw new InvalidOperationException("Capture control is absent: " + id + " at " + stage);
            pointer = SmokePointer.Hit(view, hit); ExpectedClick = id; ClickAccepted = false;
            mousePhase = 1; next = target;
        }
        private bool CaptureView(Texture2D surface, string name)
        {
            var commanders = view.Lobby?.Seats.SelectMany(seat => seat.DeckPreview?.Commanders ?? []).ToArray()
                ?? view.DeckDetails?.Entries.Where(entry => entry.Section == "Commander").Select(entry => entry.Card).ToArray() ?? [];
            var missing = commanders.Where(card => view.Art(card) == null).Select(card => card.Name).Distinct().ToArray();
            if (missing.Length > 0 && DateTime.UtcNow < deadline.AddSeconds(-15)) return false;
            string prefix = Path.Combine(view.Profile, name);
            using (var output = File.Create(prefix + ".png")) surface.SaveAsPng(output, surface.Width, surface.Height);
            view.CaptureWindow(prefix + "-window.png");
            File.WriteAllText(prefix + ".json", JsonSerializer.Serialize(new {
                completeArtwork = missing.Length == 0, missingArtwork = missing, view.Lobby, view.SetupModal,
                controls = view.Hits.Select(hit => new { hit.Id, hit.Bounds }), humanReview = "pending",
                backgroundWindow = true, syntheticInputOnly = true,
                source = "production Home/setup/lobby composition and pointer gestures over the shared authored provider"
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            return true;
        }
        public void Frame(Texture2D surface)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Background lobby capture timed out at " + stage);
            if (mousePhase is 1 or 2) return;
            if (mousePhase == 3) {
                if (!ClickAccepted) throw new InvalidOperationException("Capture rejected pointer intent " + ExpectedClick);
                mousePhase = 0; ExpectedClick = null; stage = next;
                return;
            }
            if (!view.Loaded || view.Busy || view.ActivityOpen) return;
            switch (stage) {
                case 0: Click("cooperative-tab", 1); break;
                case 1: if (CaptureView(surface, "cooperative-home")) stage = 2; break;
                case 2: Click("cooperative-party-size:4", 3); break;
                case 3: Click("host-cooperative", 4); break;
                case 4: if (view.Lobby != null) Click("match-setup", 5); break;
                case 5: Click("match-party-size:4", 6); break;
                case 6: Click("match-opposition-size:2", 7); break;
                case 7: if (CaptureView(surface, "cooperative-rules")) stage = 8; break;
                case 8: Click("match-setup-apply", 9); break;
                case 9:
                    if (view.Lobby?.Seats.Length != 6 || view.SetupModal.Length > 0) return;
                    if (alliedSeat <= 3) Click("lobby-ai:" + alliedSeat, 10);
                    else stage = 11;
                    break;
                case 10: Click("lobby-ai-deck:ai-a", 12); break;
                case 12: if (view.SetupModal.Length == 0) { alliedSeat++; stage = 9; } break;
                case 11:
                    if (captureView is "admission" or "admission-failed") Click("ready", 14);
                    else if (CaptureView(surface, "cooperative-lobby")) stage = 13;
                    break;
                case 14: if (view.Lobby?.CanStart == true) Click("start", 15); break;
                case 15:
                    var expected = captureView == "admission-failed" ? EncounterStartPhase.Failed : EncounterStartPhase.Recording;
                    if (view.Lobby?.StartState?.Phase == expected && CaptureView(surface, "cooperative-" + captureView)) stage = 13;
                    break;
                case 13: view.Exit(); break;
            }
        }
    }
}
