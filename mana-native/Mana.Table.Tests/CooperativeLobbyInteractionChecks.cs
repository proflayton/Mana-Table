using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

// Production composition and physical pointer input over authored provider
// publications. This port models setup/receipt ordering, not Magic rules.
internal static partial class CooperativeLobbyInteractionChecks
{
    internal static TableServices CreateCaptureServices()
    {
        var catalog = PregameFlowChecks.CooperativeCatalog();
        var port = new Port { Catalog = catalog };
        return new(catalog, port, port, () => Task.CompletedTask) { Seats = port, MatchSetup = port, Tempo = port };
    }

    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (var viewport in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            CheckHomeAndSixSeats(profile, viewport, check);
            CheckPreservedOpposition(profile, viewport, check);
            CheckLargeFreeForAll(profile, viewport, 5, check);
            CheckLargeFreeForAll(profile, viewport, 6, check);
            CheckStaleConfiguration(profile, viewport, check);
            CheckSetupProblems(profile, viewport, check);
            CheckStaleReady(profile, viewport, check);
            CheckGuestLobby(profile, viewport, check);
            CheckCompletion(profile, viewport, "hosting", check);
            CheckCompletion(profile, viewport, "joined", check);
            CheckUnknownCompletion(profile, viewport, "hosting", check);
            CheckUnknownCompletion(profile, viewport, "joined", check);
            CheckEarlyFailurePresentation(profile, viewport, check);
            using var unsupported = new Journey(profile, "unsupported", viewport, new Port { SupportsMatchSetup = false }, check);
            check(!unsupported.Has("cooperative-tab") && unsupported.Has("solo-tab") && unsupported.Has("friends-tab"),
                "An absent provider capability hides co-op without removing existing play modes at " + viewport);
        }
    }

    private static void CheckHomeAndSixSeats(string profile, Point viewport, Action<bool, string> check)
    {
        var port = new Port();
        using var run = new Journey(profile, "six-seat", viewport, port, check);
        run.Click("cooperative-tab");
        check(run.Has("host-cooperative") && run.Text.Contains("Choose 1 for solo")
                && Enumerable.Range(1, 4).All(count => run.Has("cooperative-party-size:" + count)) && !run.Has("start-solo"),
            "Co-op Home explicitly offers solo or one-to-four party places before hosting at " + viewport);
        run.Click("cooperative-party-size:4");
        run.Click("host-cooperative");
        run.Until(() => run.View.Lobby?.MatchSetup?.RulesetId == MatchRulesets.CooperativeCommander && !run.View.Busy,
            "Home hosting establishes the provider's explicit cooperative specification");
        check(port.ConfigureCalls == 1 && port.LastSpecification?.Seats.Length == 5 && port.ReadyCalls == 0
                && run.View.Lobby!.Seats.Count(seat => seat.Role == MatchSeatRole.Party && seat.Type == "OPEN") == 3
                && run.Shows("Invite friends"),
            "Hosting the chosen four-person party immediately leaves three real friend places beside its AI opposition, without implicit Ready consent");
        run.Click("match-setup"); run.Click("match-party-size:4"); run.Click("match-opposition-size:2");
        check(run.Text.Contains("Separate turns, life totals, cards and mana") && run.Has("match-setup-apply"),
            "The existing rules modal exposes the four-person party/two-enemy setup and its actual rules at " + viewport);
        run.Click("match-setup-apply");
        run.Until(() => run.View.Lobby?.Seats.Length == 6 && !run.View.Busy && run.View.SetupModal.Length == 0,
            "Applying rules presents the six-seat roster acknowledged by the provider");
        port.FriendJoins(1);
        run.Until(() => run.View.Lobby?.Seats[1].Type == "REMOTE", "A friend's ordinary lobby publication fills an allied seat");
        foreach (int seat in new[] { 2, 3 }) {
            run.Click("lobby-ai:" + seat); run.Click("lobby-ai-deck:ai-a");
            run.Until(() => run.View.Lobby?.Seats[seat].Type == "AI" && run.View.SetupModal.Length == 0 && !run.View.Busy,
                "An open allied seat accepts its selected saved AI deck");
        }
        run.Tick();
        var roster = run.View.Lobby!.Seats;
        check(roster.Length == 6 && roster.Take(4).All(seat => seat.Role == MatchSeatRole.Party)
                && roster.Skip(4).All(seat => seat.Role == MatchSeatRole.Opposition)
                && roster.All(seat => seat.DeckPreview != null && run.Text.Contains(seat.DeckPreview.Name))
                && roster.SelectMany(seat => seat.DeckPreview!.Commanders).All(card => run.ArtRequests.Contains(card.Name)),
            "All six seats retain public deck identity and request their commander portraits through the shipping artwork port at " + viewport);
        check(run.Shows("Connection") && !run.Shows("Invite friends"),
            "A fully enrolled party exposes connection details without promising an unavailable friend place at " + viewport);
        var management = new[] { "lobby-ai:2", "lobby-ai:3", "lobby-ai:4", "lobby-ai:5", "edit-deck", "review-deck", "ready" };
        run.Save("six-seat-controls-before-settle", run.ControlEvidence(management));
        run.Until(() => !run.View.Busy && !run.View.ActivityOpen && run.View.SetupModal.Length == 0,
            "The acknowledged AI assignment settles back to the interactive party");
        run.Save("six-seat-controls-settled", run.ControlEvidence(management));
        check(management.All(run.Has) && management.All(id => new Rectangle(0, 0, 1600, run.View.DesignHeight).Contains(run.Hit(id).Bounds))
                && !run.Hit("lobby-ai:2").Bounds.Intersects(run.Hit("lobby-ai:3").Bounds)
                && !run.Hit("lobby-ai:4").Bounds.Intersects(run.Hit("lobby-ai:5").Bounds),
            "The six-seat layout preserves reachable local deck/Ready and individual AI-management gestures at " + viewport);
        run.Click("lobby-ai:5"); run.Click("lobby-ai-deck:ai-b");
        run.Until(() => run.View.Lobby?.Seats[5].OpponentId == "ai-b" && !run.View.Busy,
            "The far opposition seat's own deck control changes only its assigned deck");
        int configured = port.ConfigureCalls;
        run.Click("match-setup"); run.Click("match-party-size:1");
        check(!run.Has("match-setup-apply") && port.ConfigureCalls == configured && run.View.Lobby!.Seats.Length == 6,
            "A size choice that cannot retain the enrolled allies disables Apply without silently moving or replacing them");
        run.Click("setup-close");
        port.FriendReadies(1);
        run.Until(() => run.View.Lobby?.Seats[1].Ready == true, "The friend explicitly acknowledges the final party enrollment");
        run.Click("ready");
        run.Until(() => run.View.Lobby?.Seats.Single(seat => seat.Local).Ready == true && !run.View.Busy,
            "The human explicitly readies its synchronized saved deck");
        run.Save("six-seat-ready-before-settle", new { controls = run.ControlEvidence(["start", "unready"]),
            port.ReadyCalls, port.StartCalls, port.LastReadySetup, port.LastReadyTempo, run.View.DeckConfirmed,
            expectedSetup = run.View.Lobby!.MatchSetupConsent, expectedTempo = run.View.Lobby.Tempo?.Revision,
            run.View.Lobby.CanStart, seats = run.View.Lobby.Seats.Select(seat => new { seat.Index, seat.Type, seat.Ready, hasDeck = seat.DeckPreview != null }) });
        run.Until(() => !run.View.Busy && !run.View.ActivityOpen, "The acknowledged Ready settles back to the host's start controls");
        run.Save("six-seat-ready-settled", new { controls = run.ControlEvidence(["start", "unready"]),
            port.ReadyCalls, port.StartCalls, port.LastReadySetup, port.LastReadyTempo, run.View.DeckConfirmed,
            expectedSetup = run.View.Lobby!.MatchSetupConsent, expectedTempo = run.View.Lobby.Tempo?.Revision, run.View.Lobby.CanStart });
        check(port.ReadyCalls == 1 && run.Has("start") && port.StartCalls == 0
                && port.LastReadySetup == run.View.Lobby!.MatchSetupConsent && port.LastReadyTempo == run.View.Lobby.Tempo?.Revision,
            "A complete ready party can start but does not enter a match until the host commits that intent");
        run.Click("start");
        run.Until(() => run.View.Match?.Players.Length == 6 && !run.View.Busy,
            "Start encounter enters the provider's six-player public game");
        check(port.StartCalls == 1 && run.View.Match!.Encounter != null
                && run.View.Match.Players.Count(player => player.TeamId == 0) == 4,
            "The authored started publication preserves explicit team/participant identity without inferring it from seat artwork");
        run.Save("six-seat-journey", new { port.ConfigureCalls, port.ReadyCalls, port.StartCalls });
    }

    private static void CheckStaleConfiguration(string profile, Point viewport, Action<bool, string> check)
    {
        var port = new Port();
        using var run = new Journey(profile, "stale-setup", viewport, port, check);
        run.Click("cooperative-tab"); run.Click("host-cooperative");
        run.Until(() => run.View.Lobby != null && !run.View.Busy, "The revision-race journey opens its current table");
        run.Click("match-setup"); run.Click("match-party-size:4");
        port.HoldConfiguration(); int before = port.ConfigureCalls;
        run.Click("match-setup-apply");
        run.Until(() => port.ConfigureCalls == before + 1, "One physical Apply captures one current revision");
        long requested = port.LastExpectedRevision;
        port.AdvanceRevision(); port.ReleaseConfiguration();
        run.Until(() => !run.View.Busy && run.View.Error.Length > 0, "A stale setup rejection becomes an explicit recoverable error", allowError: true);
        check(port.Lobby.Revision > requested && port.Lobby.Seats.Length == 2 && run.View.SetupModal == "match-setup"
                && port.AppliedConfigurations == 1,
            "A late stale setup cannot silently replace the table or dismiss its rules choice at " + viewport);
        run.Save("stale-setup", new { requested, current = port.Lobby.Revision, port.AppliedConfigurations, error = run.View.Error });
    }

    private static void CheckPreservedOpposition(string profile, Point viewport, Action<bool, string> check)
    {
        var port = new Port();
        using var run = new Journey(profile, "preserve-opposition", viewport, port, check);
        run.Click("cooperative-tab"); run.Click("host-cooperative");
        run.Until(() => run.View.Lobby?.Seats.Length == 2 && !run.View.Busy,
            "The default solo party has one explicitly enrolled enemy");
        var enemy = run.View.Lobby!.Seats[1];
        run.Click("match-setup"); run.Click("match-party-size:4"); run.Click("match-opposition-size:2"); run.Click("match-setup-apply");
        run.Until(() => run.View.Lobby?.Seats.Length == 6 && !run.View.Busy && run.View.SetupModal.Length == 0,
            "Growing the party acknowledges an explicit six-seat specification");
        var grown = run.View.Lobby!;
        check(port.LastSpecification!.Seats[1] == new MatchSeatAssignment(1, enemy.TeamId!.Value, MatchSeatRole.Opposition)
                && grown.Seats[1] is { Type: "AI", Role: MatchSeatRole.Opposition }
                && grown.Seats[1].ParticipantId == enemy.ParticipantId && grown.Seats[1].DeckPreview?.SelectionId == enemy.DeckPreview?.SelectionId
                && grown.Seats.Where(seat => seat.Role == MatchSeatRole.Party).Select(seat => seat.Index).SequenceEqual([0, 2, 3, 4])
                && grown.Seats.Count(seat => seat.Role == MatchSeatRole.Party && seat.Type == "OPEN") == 3,
            "The actual resize gesture adds three open allied places without recruiting, replacing or relabeling the enrolled enemy at " + viewport);
        run.Save("preserve-opposition", new { enemy.ParticipantId, enemy.OpponentId, specification = port.LastSpecification });
    }

    private static void CheckLargeFreeForAll(string profile, Point viewport, int seats, Action<bool, string> check)
    {
        var port = new Port(); port.HostAsync(seats, false).GetAwaiter().GetResult();
        using var run = new Journey(profile, "ffa-" + seats, viewport, port, check);
        run.View.SetConnectedTable(port.Lobby);
        run.Until(() => run.Has("lobby-ai:" + (seats - 1)) && !run.View.Busy,
            "A large free-for-all exposes its actual last seat's management control");
        check(run.Shows("Open seat") && run.Shows("Add AI") && !run.Shows("Open ally seat")
                && !run.Shows("Add AI ally") && !run.Text.Contains("add an AI ally") && !run.Shows("YOUR SIDE"),
            "A " + seats + "-player free-for-all keeps opponent/empty-seat language instead of calling its players allies at " + viewport);
        run.Click("lobby-ai:" + (seats - 1)); run.Click("lobby-ai-deck:ai-a");
        run.Until(() => run.View.Lobby?.Seats[seats - 1].Type == "AI" && !run.View.Busy && run.View.SetupModal.Length == 0,
            "The far free-for-all seat accepts its own AI opponent assignment");
        check(run.View.Lobby!.Seats.All(seat => seat.Role == MatchSeatRole.Participant)
                && run.View.Lobby.Seats.Select(seat => seat.TeamId).Distinct().Count() == seats,
            "Managing a large free-for-all AI does not turn its distinct individual teams into a cooperative side");
        run.Save("ffa-" + seats, new { seats });
    }

    private static void CheckCompletion(string profile, Point viewport, string mode, Action<bool, string> check)
    {
        var port = new Port(); port.AuthorTerminal(mode);
        var preceding = port.State! with { Status = "playing", Outcome = null, Result = null, Revision = 19, BoardRevision = 19,
            Players = port.State!.Players.Select(player => player with { Eliminated = false }).ToImmutableArray() };
        using var run = new Journey(profile, "checkpoint-" + mode, viewport, port, check, preceding);
        run.View.SetConnectedTable(port.Lobby); run.Tick();
        run.Until(() => run.View.Overlay == "result", "The finished encounter opens its normal result overlay");
        check(!run.Has("return") && !run.Has("encounter-save-retry") && !run.Has("encounter-recovery-leave") && run.Text.Contains("Saving this encounter"),
            "Pending checkpoint writes keep the terminal match readable without exposing Return or retry at " + mode + " " + viewport);
        port.FailSave();
        run.Until(() => run.View.Lobby?.PartyProgress?.SaveState == EncounterSaveState.Failed,
            "A failed checkpoint arrives through ordinary lobby polling while the result overlay stays open");
        check(!run.Has("return") && !run.Has("encounter-recovery-leave")
                && run.Has("encounter-save-retry") == (mode == "hosting") && port.ReturnCalls == 0,
            "Only the host's explicit retry permission exposes the save gesture and no unsaved result advances the party at " + mode + " " + viewport);
        if (mode == "hosting") {
            run.Click("encounter-save-retry");
            run.Until(() => port.RetryCalls == 1 && run.View.Lobby?.PartyProgress?.SaveState == EncounterSaveState.Pending && !run.View.Busy,
                "Retry dispatches one scoped save request and displays its acknowledged pending state");
            check(!run.Has("return") && !run.Has("encounter-save-retry"), "An admitted retry is not a durable checkpoint or a repeatable per-frame action");
        }
        port.CommitSave();
        run.Until(() => run.Has("return") && run.Text.Contains("Encounter saved"),
            "Only the matching committed receipt enables returning from the finished encounter");
        check(run.View.Match?.Status == "finished" && port.ReturnCalls == 0,
            "Saving progress alone preserves the result for inspection until the player chooses to return");
        if (mode == "hosting") {
            run.Click("return");
            run.Until(() => port.ReturnCalls == 1 && run.View.Match == null && !run.View.Busy,
                "Return keeps the connected party and its durable progress for the next encounter");
            check(run.View.Lobby?.PartyProgress is { CompletedEncounters: 1, Victories: 1 }, "The next lobby retains the provider's committed public counters");
            string capturedSelection = run.View.Lobby!.PartyProgress!.LastReceipt!.Participants.Single(participant => participant.Index == 5).DeckSelectionId;
            run.Click("lobby-ai:5"); run.Click("lobby-ai-deck:ai-b");
            run.Until(() => run.View.Lobby?.Seats[5].OpponentId == "ai-b" && !run.View.Busy,
                "After returning, the party can choose a new opposition deck for its next encounter");
            check(run.View.Lobby!.Seats[5].DeckPreview!.SelectionId != capturedSelection
                    && run.View.Lobby.PartyProgress!.LastReceipt!.Participants.Single(participant => participant.Index == 5).DeckSelectionId == capturedSelection,
                "A later deck change leaves the completed receipt pinned to its original public deck-selection identity");
        } else check(!run.Has("match-setup") && !run.Has("lobby-ai:4"), "A joined guest never inherits host setup or AI-management authority");
        run.Save("checkpoint-" + mode, new { port.RetryCalls, port.ReturnCalls });
    }

    private static void CheckStaleReady(string profile, Point viewport, Action<bool, string> check)
    {
        var port = new Port();
        using var run = new Journey(profile, "stale-ready", viewport, port, check);
        run.Click("cooperative-tab"); run.Click("host-cooperative");
        run.Until(() => run.View.Lobby != null && !run.View.Busy, "The Ready-race journey opens its party");
        run.Click("match-setup"); run.Click("match-party-size:2"); run.Click("match-setup-apply");
        run.Until(() => run.View.Lobby?.Seats.Length == 3 && !run.View.Busy && run.View.SetupModal.Length == 0,
            "The party opens one additional allied enrollment");
        int openAlly = run.View.Lobby!.Seats.Single(seat => seat.Role == MatchSeatRole.Party && seat.Type == "OPEN").Index;
        port.HoldReady(); run.Click("ready");
        run.Until(() => port.ReadyAttempts == 1, "Ready captures both policies displayed before dispatch");
        string? reviewed = port.LastReadySetup;
        port.FriendJoins(openAlly); port.ReleaseReady();
        run.Until(() => !run.View.Busy && run.View.Error.Length > 0, "A changed enrollment rejects the stale Ready explicitly", allowError: true);
        check(reviewed != port.Lobby.MatchSetupConsent && port.LastReadyTempo == port.Lobby.Tempo?.Revision
                && port.ReadyCalls == 0 && !port.Lobby.Seats.Single(seat => seat.Local).Ready && port.StartCalls == 0,
            "A queued Ready cannot acknowledge an unseen participant replacement even when its tempo token is still current at " + viewport);
        run.Save("stale-ready", new { reviewed, actual = port.Lobby.MatchSetupConsent, port.ReadyAttempts, port.ReadyCalls });
    }

    private static void CheckUnknownCompletion(string profile, Point viewport, string mode, Action<bool, string> check)
    {
        var port = new Port { FailLeave = true }; port.AuthorTerminal(mode); port.MakeCheckpointUnknown();
        var preceding = port.State! with { Status = "playing", Outcome = null, Result = null, Revision = 19, BoardRevision = 19,
            Players = port.State!.Players.Select(player => player with { Eliminated = false }).ToImmutableArray() };
        using var run = new Journey(profile, "unknown-checkpoint-" + mode, viewport, port, check, preceding);
        run.View.SetConnectedTable(port.Lobby);
        run.Until(() => run.View.Overlay == "result" && run.Has("encounter-recovery-leave"),
            "An unknown completed checkpoint exposes its explicit table-departure recovery");
        check(!run.Has("return") && !run.Has("encounter-save-retry"),
            "Unknown checkpoint facts grant neither advancement nor retry authority at " + mode + " " + viewport);
        run.Click("encounter-recovery-leave");
        check(run.View.Overlay == "leave-table" && run.Has("confirm-leave-table"),
            "Unknown recovery reuses the existing visible Leave table confirmation");
        string tableId = run.View.Lobby!.TableId, gameId = run.View.Match!.Id;
        run.Click("confirm-leave-table");
        run.Until(() => port.LeaveCalls == 1 && !run.View.Busy && run.View.Error.Length > 0,
            "A refused departure remains an explicit failure", allowError: true);
        run.Save("unknown-checkpoint-after-rejected-leave", new { expectedTable = tableId, actualTable = run.View.Lobby?.TableId,
            expectedGame = gameId, actualGame = run.View.Match?.Id, run.View.Overlay, run.View.ActivityOpen,
            port.ReturnCalls, port.RetryCalls, port.LeaveCalls, error = run.View.Error });
        check(run.View.Lobby?.TableId == tableId && run.View.Match?.Id == gameId
                && run.View.Lobby.MatchActive && run.View.Match.Status == "finished" && !run.View.LocalMatch
                && port.ReturnCalls == 0 && port.RetryCalls == 0,
            "A backend-rejected recovery leave preserves the connected match and cannot silently reset or advance the party at " + mode + " " + viewport);
        run.Save("unknown-checkpoint-" + mode, new { port.LeaveCalls, tableId, gameId, error = run.View.Error });
    }

    private static void CheckEarlyFailurePresentation(string profile, Point viewport, Action<bool, string> check)
    {
        const string diagnostic = "Could not prepare the opening roster.";
        var failed = ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(new {
            id = "cooperative-game", status = "error", error = diagnostic, viewerId = 7, players = Array.Empty<object>(), stack = Array.Empty<object>(),
            partyId = "party", encounterId = "encounter", outcome = (object?)null, prompt = (object?)null
        }))!;
        var port = new Port(); port.AuthorTerminal("hosting"); port.PublishEarlyFailure(failed);
        using var run = new Journey(profile, "early-startup-error", viewport, port, check, failed);
        run.View.SetConnectedTable(port.Lobby);
        run.Until(() => run.Has("error-leave") && run.Text.Contains("Game interrupted"),
            "The real application renders a decoded early error without an opening roster");
        check(run.View.Match is { Players.IsEmpty: true, Outcome: null, Decision: null }
                && run.View.Match.Error == diagnostic && run.View.Match.Encounter == failed.Encounter
                && run.Frame.Cards.Count == 0 && !run.Text.Contains("Victory") && !run.Text.Contains("Defeat")
                && port.ReadyCalls == 0 && port.StartCalls == 0 && port.ReturnCalls == 0,
            "Early failure preserves diagnostic/linkage while inventing no participants, card faces, winner or checkpoint authority at " + viewport);
        run.Click("error-leave");
        check(run.View.Overlay == "leave-table" && run.Has("confirm-leave-table") && port.LeaveCalls == 0,
            "The roster-free interruption still offers the existing explicit departure confirmation");
        run.Save("early-startup-error", new { failed.Id, failed.Encounter, failed.Error, players = failed.Players.Length });
    }

    private static void CheckGuestLobby(string profile, Point viewport, Action<bool, string> check)
    {
        var port = new Port(); port.AuthorGuestLobby();
        using var run = new Journey(profile, "guest-lobby", viewport, port, check);
        run.View.SetConnectedTable(port.Lobby);
        run.Until(() => run.Has("ready") && !run.View.Busy && !run.View.ActivityOpen,
            "The joined guest synchronizes its own deck in the six-seat party");
        check(!run.Has("match-setup") && !run.Has("start")
                && Enumerable.Range(0, 6).All(index => !run.Has("lobby-ai:" + index) && !run.Has("lobby-ai-remove:" + index)),
            "A guest's actual lobby has Ready but no host rules, start or AI-management gestures at " + viewport);
        run.Click("ready");
        run.Until(() => run.View.Lobby?.Seats.Single(seat => seat.Local).Ready == true && !run.View.Busy,
            "The guest can explicitly Ready its own acknowledged deck");
        check(port.ReadyCalls == 1 && port.ConfigureCalls == 0 && port.StartCalls == 0,
            "A guest Ready does not configure teams, start a match or mutate another seat's authority");
        run.Save("guest-lobby", new { port.ReadyCalls, port.ConfigureCalls, port.StartCalls });
    }

    private sealed class Journey : IDisposable
    {
        private readonly TableApplication app;
        private readonly Action<bool, string> check;
        private readonly Point viewport;
        private readonly string directory;
        private double now;
        internal readonly HashSet<string> ArtRequests = [];
        internal TableApplication.Probe View => app.TestPort;
        internal PresentationFrame Frame { get; private set; } = null!;
        internal string Text => string.Join('\n', Frame.Drawing.Commands.Select(command => command switch {
            DrawCommand.Text text => text.Value, DrawCommand.CenterText text => text.Value, _ => ""
        }));
        internal Journey(string profile, string name, Point viewport, Port port, Action<bool, string> check, GameSnapshot? state = null)
        {
            directory = Path.Combine(profile, "cooperative-" + name + "-" + viewport.X); Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(directory);
            this.viewport = viewport; this.check = check;
            var catalog = PregameFlowChecks.CooperativeCatalog(); port.Catalog = catalog;
            var resources = new PresentationResources { Card = card => { ArtRequests.Add(card.Name); return null; } };
            var services = new TableServices(catalog, port, port, () => Task.CompletedTask) { Seats = port, MatchSetup = port, Tempo = port };
            app = new(services, directory, initialState: state, resources: resources) { Automated = true };
            app.Initialize();
            Until(() => View.Loaded && !View.Busy && !View.ActivityOpen, "The co-op production interaction composition is ready");
        }
        internal void Tick(Point? pointer = null, ButtonState button = ButtonState.Released)
        {
            now += .025;
            var physical = RenderViewport.Fit(viewport, View.DesignHeight).ToPhysical((pointer ?? new Point(20, 70)).ToVector2() + new Vector2(.5f));
            app.Update(new(TimeSpan.FromSeconds(now), TimeSpan.FromSeconds(.025)),
                new((int)physical.X, (int)physical.Y, 0, button, default, default, default, default), new(), viewport);
            Frame = app.ReadPresentation();
        }
        internal bool Has(string id) => Frame.Hits.Any(hit => hit.Id == id);
        internal bool Shows(string text) => Frame.Drawing.Commands.Any(command => command switch {
            DrawCommand.Text value => value.Value == text, DrawCommand.CenterText value => value.Value == text, _ => false
        });
        internal Mana.Table.Interaction.TableHit Hit(string id) => Frame.Hits.LastOrDefault(hit => hit.Id == id)
            ?? throw new InvalidOperationException("Missing production co-op control: " + id);
        internal void Click(string id)
        {
            Until(() => !View.Busy && !View.ActivityOpen && Has(id), "The production intent is reachable: " + id);
            var point = Hit(id).Bounds.Center; Tick(point, ButtonState.Pressed); Tick(point);
        }
        internal void Until(Func<bool> condition, string description, bool allowError = false)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while ((!condition() || Frame == null) && (allowError || View.Error.Length == 0) && DateTime.UtcNow < deadline) { Tick(); Thread.Sleep(1); }
            check(condition() && (allowError || View.Error.Length == 0), description + (View.Error.Length > 0 ? ": " + View.Error : "")); Tick();
        }
        internal void Save(string name, object evidence) => File.WriteAllText(Path.Combine(directory, name + ".json"),
            JsonSerializer.Serialize(new { evidence, frame = Frame.Observe(), scope = "Production pointer/setup lifecycle over authored public provider facts; not rules simulation or human visual approval" },
                new JsonSerializerOptions { WriteIndented = true }));
        internal object ControlEvidence(string[] ids) => new {
            designHeight = View.DesignHeight, busy = View.Busy, activityOpen = View.ActivityOpen, modal = View.SetupModal,
            missing = ids.Where(id => !Has(id)).ToArray(),
            hits = Frame.Hits.Where(hit => ids.Contains(hit.Id)).Select(hit => new { hit.Id,
                x = hit.Bounds.X, y = hit.Bounds.Y, width = hit.Bounds.Width, height = hit.Bounds.Height,
                contained = new Rectangle(0, 0, 1600, View.DesignHeight).Contains(hit.Bounds) }).ToArray(),
            partyIntersect = Has("lobby-ai:2") && Has("lobby-ai:3") ? Hit("lobby-ai:2").Bounds.Intersects(Hit("lobby-ai:3").Bounds) : (bool?)null,
            oppositionIntersect = Has("lobby-ai:4") && Has("lobby-ai:5") ? Hit("lobby-ai:4").Bounds.Intersects(Hit("lobby-ai:5").Bounds) : (bool?)null
        };
        public void Dispose() => app.Dispose();
    }

    private sealed class Port : ITableSetup, ITableSeatSetup, ITableMatchSetup, ITableTempo, IMatchConnection
    {
        private readonly object gate = new();
        private Lobby? lobby;
        private GameSnapshot? state;
        private TaskCompletionSource? configurationHold;
        private TaskCompletionSource? readyHold;
        internal IEngineCatalog Catalog = null!;
        public bool SupportsMatchSetup { get; init; } = true;
        internal int ConfigureCalls, AppliedConfigurations, ReadyCalls, ReadyAttempts, StartCalls, ReturnCalls, RetryCalls;
        internal int LeaveCalls;
        internal bool FailLeave;
        internal long LastExpectedRevision;
        internal MatchSetupSpecification? LastSpecification;
        internal string? LastReadySetup, LastReadyTempo;
        public bool SupportsTempo => true;
        internal Lobby Lobby { get { lock (gate) return lobby!; } }
        internal GameSnapshot? State { get { lock (gate) return state; } }
        private static MatchSetupCapabilities Capabilities => new([MatchRulesets.FreeForAllCommander, MatchRulesets.CooperativeCommander], 6, 4, 2);
        private static Card Leader(string name) => new() { Name = name, ArtName = name, ArtSet = "m19", Type = "Legendary Creature" };
        private static LobbyDeckPreview Preview(int index, string name, string commander) => new("selection-" + index + "-" + Guid.NewGuid(), name, [Leader(commander)], null);
        private static Seat NewSeat(int index, MatchSeatAssignment assignment, bool guest = false) => new(index,
            index == 0 ? "Host" : "Seat " + (index + 1), index == (guest ? 1 : 0) ? "LOCAL" : index == 0 ? "REMOTE"
                : assignment.Role == MatchSeatRole.Opposition ? "AI" : "OPEN", null, false, index == (guest ? 1 : 0)) {
            ParticipantId = Guid.NewGuid().ToString(), TeamId = assignment.TeamId, Role = assignment.Role
        };
        private static Seat Ai(Seat seat, string choice) => seat with { Type = "AI", Name = "AI " + (seat.Index + 1), Ready = true,
            OpponentId = choice, Deck = (choice == "ai-a" ? "Ramp allies " : "Cinder foes ") + seat.Index,
            DeckPreview = Preview(seat.Index, (choice == "ai-a" ? "Ramp allies " : "Cinder foes ") + seat.Index,
                choice == "ai-a" ? "Goreclaw, Terror of Qal Sisma" : "Torbran, Thane of Red Fell") };
        private void Refresh() => lobby = lobby! with { Revision = lobby.Revision + 1,
            CanStart = lobby.Seats.All(seat => seat.Type != "OPEN" && seat.Ready && seat.DeckPreview != null) };
        public Task<Lobby> HostAsync(int players, bool forwarding)
        {
            lock (gate) {
                var specification = new MatchSetupSpecification(MatchRulesets.FreeForAllCommander,
                    Enumerable.Range(0, players).Select(index => new MatchSeatAssignment(index, index, MatchSeatRole.Participant)).ToImmutableArray());
                lobby = new("hosting", "Open", null, false, "Choose decks and Ready", false, "", null, [],
                    specification.Seats.Select(seat => NewSeat(seat.Index, seat)).ToImmutableArray(), "cooperative-table") {
                    Revision = 1, MatchSetup = specification, MatchSetupCapabilities = Capabilities,
                    MatchSetupConsent = Guid.NewGuid().ToString(), Tempo = new(TempoPreset.Standard, TempoExpiry.Pass, 60, 60, "reviewed-tempo"),
                    AiOpponents = [new("ai-a", "Ramp allies", "Saved Commander list"), new("ai-b", "Cinder foes", "Saved Commander list")]
                };
                return Task.FromResult(lobby);
            }
        }
        public async Task<Lobby> ConfigureMatchAsync(string tableId, long expectedLobbyRevision, MatchSetupSpecification specification)
        {
            Task? hold;
            lock (gate) { ConfigureCalls++; LastExpectedRevision = expectedLobbyRevision; LastSpecification = specification; hold = configurationHold?.Task; }
            if (hold != null) await hold;
            lock (gate) {
                if (lobby?.TableId != tableId || lobby.Revision != expectedLobbyRevision || lobby.Mode != "hosting" || lobby.MatchActive)
                    throw new EngineCommandRejectedException("The table changed before the setup was accepted.");
                MatchSetupFacts.Validate(specification, Capabilities);
                var seats = specification.Seats.Select(assignment => {
                    var old = lobby.Seats.FirstOrDefault(seat => seat.Index == assignment.Index) ?? NewSeat(assignment.Index, assignment);
                    var next = old with { TeamId = assignment.TeamId, Role = assignment.Role, Ready = old.Type == "AI" };
                    return assignment.Role == MatchSeatRole.Opposition && (next.Type != "AI" || next.DeckPreview == null)
                        ? Ai(next, next.OpponentId ?? "ai-a") : next;
                }).ToImmutableArray();
                lobby = lobby with { MatchSetup = specification, Seats = seats,
                    PartyProgress = new("party", null, 0, 0, EncounterSaveState.Ready, null, false, null) };
                RotateConsent(); AppliedConfigurations++; Refresh(); return lobby;
            }
        }
        internal void HoldConfiguration() { lock (gate) configurationHold = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        internal void ReleaseConfiguration() { lock (gate) { configurationHold!.TrySetResult(); configurationHold = null; } }
        internal void HoldReady() { lock (gate) readyHold = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        internal void ReleaseReady() { lock (gate) { readyHold!.TrySetResult(); readyHold = null; } }
        internal void AdvanceRevision() { lock (gate) Refresh(); }
        internal void FriendJoins(int index) { lock (gate) {
            var peer = lobby!.Seats[index] with { Type = "REMOTE", Name = "Friend", Ready = true, ParticipantId = Guid.NewGuid().ToString(),
                Deck = "Friend's partner deck", DeckPreview = Preview(index, "Friend's partner deck", "Toothy, Imaginary Friend") };
            lobby = lobby with { Seats = lobby.Seats.SetItem(index, peer) }; RotateConsent(); Refresh();
        } }
        internal void FriendReadies(int index) { lock (gate) { lobby = lobby! with { Seats = lobby.Seats.SetItem(index, lobby.Seats[index] with { Ready = true }) }; Refresh(); } }
        private void RotateConsent() => lobby = lobby! with { MatchSetupConsent = Guid.NewGuid().ToString(),
            Seats = lobby.Seats.Select(seat => seat with { Ready = seat.Type == "AI" }).ToImmutableArray() };
        public async Task<Lobby> SelectDeckAsync(LobbyDeckSelection selection)
        {
            var deck = selection.DeckId.Length == 0 ? null : await Catalog.OpenDeckAsync(selection.DeckId);
            lock (gate) {
                if (lobby?.TableId != selection.TableId || lobby.MatchActive) throw new InvalidOperationException("Deck selection needs this current pregame table.");
                var local = lobby!.Seats.Single(seat => seat.Local);
                var preview = deck == null ? null : new LobbyDeckPreview("local-" + deck.LibraryToken, deck.Name,
                    deck.Entries.Where(entry => entry.Section == "Commander").Select(entry => entry.Card).ToImmutableArray(), null);
                lobby = lobby with { Seats = lobby.Seats.SetItem(local.Index, local with { Deck = deck?.Name, DeckPreview = preview, Ready = false }) };
                Refresh(); return lobby;
            }
        }
        public Task<Lobby> SetSeatAsync(string tableId, int index, string type, string? opponentId = null) { lock (gate) {
            if (lobby?.TableId != tableId || lobby.Mode != "hosting" || lobby.MatchActive) throw new InvalidOperationException("Only the current pregame host can manage AI seats.");
            var old = lobby!.Seats[index];
            bool enrollmentChanged = old.Type != type;
            var seat = type == "AI" ? Ai(old with { ParticipantId = enrollmentChanged ? Guid.NewGuid().ToString() : old.ParticipantId }, opponentId ?? "ai-a")
                : old with { Type = "OPEN", Deck = null, DeckPreview = null, Ready = false, OpponentId = null, ParticipantId = Guid.NewGuid().ToString() };
            lobby = lobby with { Seats = lobby.Seats.SetItem(index, seat) }; if (enrollmentChanged) RotateConsent(); Refresh(); return Task.FromResult(lobby);
        } }
        private void SetReady(bool ready) { ReadyCalls++; var local = lobby!.Seats.Single(seat => seat.Local);
            lobby = lobby with { Seats = lobby.Seats.SetItem(local.Index, local with { Ready = ready }) }; Refresh();
        }
        public Task SetReadyAsync(bool ready) { lock (gate) {
            if (ready && lobby?.MatchSetup?.RulesetId == MatchRulesets.CooperativeCommander) throw new InvalidOperationException("Co-op Ready requires explicit setup and tempo consent.");
            SetReady(ready); return Task.CompletedTask;
        } }
        public Task SetReadyAsync(string tableId, bool ready, string tempoRevision) { lock (gate) {
            if (ready && (lobby?.MatchSetup?.RulesetId == MatchRulesets.CooperativeCommander || lobby?.Tempo?.Revision != tempoRevision))
                throw new InvalidOperationException("Ready requires both reviewed policies.");
            SetReady(ready); return Task.CompletedTask;
        } }
        public async Task SetMatchReadyAsync(string tableId, string matchSetupConsent, bool ready, string? tempoRevision = null) {
            Task? hold;
            lock (gate) { ReadyAttempts++; LastReadySetup = matchSetupConsent; LastReadyTempo = tempoRevision; hold = readyHold?.Task; }
            if (hold != null) await hold;
            lock (gate) {
            if (lobby?.TableId != tableId || ready && (lobby.MatchSetupConsent != matchSetupConsent || lobby.Tempo is { Enabled: true } && lobby.Tempo.Revision != tempoRevision))
                throw new EngineCommandRejectedException("Review the current setup and tempo before Ready.");
            SetReady(ready);
        } }
        public Task<Lobby> ConfigureTempoAsync(string tableId, TempoPreset preset, TempoExpiry expiry) { lock (gate) {
            lobby = lobby! with { Tempo = new(preset, expiry, preset == TempoPreset.Off ? 0 : 60, 60, Guid.NewGuid().ToString()),
                Seats = lobby.Seats.Select(seat => seat with { Ready = seat.Type == "AI" }).ToImmutableArray() }; Refresh(); return Task.FromResult(lobby);
        } }
        public Task PresentedAsync(string gameId, string clockId) => Task.CompletedTask;
        public Task StartAsync() { lock (gate) {
            if (lobby is not { Mode: "hosting", MatchActive: false, CanStart: true }) throw new InvalidOperationException("The host must observe every seat ready before starting.");
            StartCalls++; state = Game(false); lobby = lobby with { MatchActive = true }; Refresh(); return Task.CompletedTask;
        } }
        public Task<Lobby> LobbyAsync() { lock (gate) return Task.FromResult(lobby!); }
        public Task<GameSnapshot?> ObserveAsync() { lock (gate) return Task.FromResult(state); }
        internal void AuthorTerminal(string mode) { lock (gate) {
            var specification = new MatchSetupSpecification(MatchRulesets.CooperativeCommander,
                Enumerable.Range(0, 6).Select(index => new MatchSeatAssignment(index, index < 4 ? 0 : 1,
                    index < 4 ? MatchSeatRole.Party : MatchSeatRole.Opposition)).ToImmutableArray());
            var seats = specification.Seats.Select(assignment => {
                var seat = NewSeat(assignment.Index, assignment, mode == "joined");
                return seat.Local || seat.Index == 0 ? seat with { Deck = "Party deck " + seat.Index, Ready = true,
                    DeckPreview = Preview(seat.Index, "Party deck " + seat.Index, "Rhys the Redeemed") } : Ai(seat, "ai-a");
            }).ToImmutableArray();
            lobby = new(mode, "Finished", null, false, null, true, "", null, [], seats, "cooperative-table") {
                Revision = 10, MatchSetup = specification, MatchSetupCapabilities = Capabilities,
                MatchSetupConsent = Guid.NewGuid().ToString(), Tempo = new(TempoPreset.Standard, TempoExpiry.Pass, 60, 60, "reviewed-tempo"),
                AiOpponents = [new("ai-a", "Ramp allies", "Saved Commander list"), new("ai-b", "Cinder foes", "Saved Commander list")],
                PartyProgress = new("party", "encounter", 0, 0, EncounterSaveState.Pending, null, false, null)
            };
            state = Game(true);
        } }
        internal void AuthorGuestLobby() {
            AuthorTerminal("joined");
            lock (gate) {
                state = null;
                lobby = lobby! with { MatchActive = false,
                    Seats = lobby.Seats.Select(seat => seat with { Ready = seat.Type == "AI" }).ToImmutableArray(),
                    PartyProgress = new("party", null, 0, 0, EncounterSaveState.Ready, null, false, null) };
                Refresh();
            }
        }
        private GameSnapshot Game(bool terminal) => new("cooperative-game", terminal ? "finished" : "playing", null, terminal ? "Victory" : null,
            lobby!.Seats.Single(seat => seat.Local).Index + 7, 8, "Main", "MAIN1", 7,
            lobby.Seats.Select(seat => new Player(seat.Index + 7, seat.Name, 40, false, terminal && seat.Role == MatchSeatRole.Opposition,
                ImmutableDictionary<string, int>.Empty, []) { ParticipantId = seat.ParticipantId, TeamId = seat.TeamId }).ToImmutableArray(), [], null, null) {
                Revision = 20, BoardRevision = 20, Encounter = new("party", "encounter"),
                Outcome = terminal ? new("cooperative-game", MatchOutcomeKind.Win, [7], [0], "AllOpposingTeamsLost") : null
            };
        internal void FailSave() { lock (gate) { lobby = lobby! with { PartyProgress = lobby.PartyProgress! with {
            SaveState = EncounterSaveState.Failed, SaveError = "Disk unavailable", RetryAllowed = lobby.Mode == "hosting" } }; Refresh(); } }
        internal void MakeCheckpointUnknown() { lock (gate) { lobby = lobby! with { PartyProgress = null }; Refresh(); } }
        internal void PublishEarlyFailure(GameSnapshot failure) { lock (gate) {
            state = failure;
            lobby = lobby! with { PartyProgress = new("party", "encounter", 0, 0, EncounterSaveState.Ready, null, false, null) };
            Refresh();
        } }
        public Task<Lobby> RetryEncounterSaveAsync(string tableId, string encounterId) { lock (gate) {
            if (lobby?.TableId != tableId || lobby.PartyProgress?.EncounterId != encounterId || !lobby.PartyProgress.RetryAllowed) throw new InvalidOperationException("Invalid checkpoint retry scope.");
            RetryCalls++; lobby = lobby with { PartyProgress = lobby.PartyProgress with { SaveState = EncounterSaveState.Pending, SaveError = null, RetryAllowed = false } };
            Refresh(); return Task.FromResult(lobby);
        } }
        internal void CommitSave() { lock (gate) {
            var receipt = new EncounterReceipt("receipt", "party", "encounter", state!.Id, MatchRulesets.CooperativeCommander, 0, state.Outcome!,
                lobby!.Seats.Select(seat => new EncounterParticipant(seat.ParticipantId, seat.Index, seat.Index + 7, seat.TeamId!.Value, seat.Role!.Value,
                    seat.DeckPreview!.SelectionId)).ToImmutableArray(),
                new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
            lobby = lobby with { PartyProgress = new("party", "encounter", 1, 1, EncounterSaveState.Ready, null, false, receipt) }; Refresh();
        } }
        public Task ReturnToLobbyAsync() { lock (gate) { ReturnCalls++; state = null; lobby = lobby! with { MatchActive = false }; Refresh(); return Task.CompletedTask; } }
        public Task<Lobby> ConfigureSeatsAsync(string tableId, int players) => throw new NotSupportedException();
        public Task<SoloSetup> PrepareSoloAsync(string deckId) => throw new NotSupportedException();
        public Task StartSoloAsync(string deckId, ImmutableArray<string> opponents) => throw new NotSupportedException();
        public Task<Lobby> JoinAsync(string address) => throw new NotSupportedException();
        public Task LeaveAsync() { lock (gate) {
            LeaveCalls++;
            if (FailLeave) throw new EngineCommandRejectedException("The current table cannot be retired yet.");
            state = null; lobby = null; return Task.CompletedTask;
        } }
        public Task ExecuteAsync(GameCommand command) => throw new InvalidOperationException("Setup/checkpoint presentation cannot answer a gameplay decision.");
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
    }
}

internal static partial class PregameFlowChecks
{
    // Reuse the existing saved-deck catalog fixture without expanding the
    // cooperative provider into another deck editor or card-rules simulator.
    internal static ICardEngine CooperativeCatalog() => new PregameEngine();
}
