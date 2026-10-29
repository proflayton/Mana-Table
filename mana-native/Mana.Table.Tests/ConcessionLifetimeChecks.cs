using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Exercise the real concession gesture and shipping polled session. The port
// supplies authoritative public turns; actual rules and multiplayer continuation
// are independently covered by the real-engine lifecycle regression.
internal static class ConcessionLifetimeChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (var viewport in new[] { new Point(1000, 740), new Point(1540, 980) })
        foreach (string mode in new[] { "hosting", "joined" }) {
            string path = Path.Combine(profile, "concession-lifetime-" + mode + "-" + viewport.X);
            Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true, TextScale = 1.3f }.Save(path);
            var port = new Port(mode);
            var catalog = new InteractionScenarioEngine();
            using var run = new PresentationRun(new(catalog, port, port, () => Task.CompletedTask), path, port.State, viewport,
                ObservationDeliveryMode.Polled);
            run.View.SetConnectedTable(port.Lobby);
            Point pointer = new(20, 60);
            void Tick(ButtonState left = ButtonState.Released) {
                var physical = RenderViewport.Fit(viewport, run.View.DesignHeight).ToPhysical(pointer.ToVector2());
                run.Tick(TimeSpan.FromSeconds(1d / 60), new((int)MathF.Ceiling(physical.X), (int)MathF.Ceiling(physical.Y),
                    0, left, default, default, default, default));
            }
            void Until(Func<bool> ready, string step) {
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (!ready() && DateTime.UtcNow < deadline) { Tick(); Thread.Sleep(1); }
                if (!ready()) throw new InvalidOperationException("Concession lifetime stopped at " + step + ": " + run.View.Error);
            }
            bool Has(string id) => run.Frame.Hits.Any(hit => hit.Id == id);
            void Click(string id) {
                pointer = run.Frame.Hits.Single(hit => hit.Id == id).Bounds.Center;
                Tick(ButtonState.Pressed); Tick();
                pointer = new(20, 60); Tick();
            }
            string Text() => string.Join(" ", run.Frame.Drawing.Commands.Select(command => command switch {
                DrawCommand.Text text => text.Value, DrawCommand.CenterText text => text.Value, _ => "" }));
            Tick(); Tick();
            Until(() => !run.View.Busy && Has("concede"), "current table");
            Click("concede"); Click("confirm-concede");
            Until(() => !run.View.Busy && run.View.Match?.Status == "resolving", "concession admission");
            check(run.View.Match is { Decision: null, Viewer.Eliminated: false }
                    && run.View.Lobby?.MatchActive == true && run.View.Overlay != "result"
                    && !Has("return") && !Has("confirm") && port.Replies == 0 && run.View.Error.Length == 0,
                mode + " accepts a concession receipt before actual rules loss without inventing a result or another decision at " + viewport);
            Until(() => !run.View.Busy && !run.View.ActivityOpen && run.View.Match?.Viewer?.Eliminated == true,
                "conceded seat publication");
            check(port.Concessions == 1 && port.Leaves == 0 && port.Returns == 0 && port.Closes == 0
                    && run.View.Match is { Status: "playing", Viewer.Eliminated: true }
                    && run.View.Lobby is { MatchActive: true } && run.View.Overlay != "result"
                    && Text().Contains("Watching the table") && !Has("return") && !Has("concede"),
                mode + " concession eliminates one seat and keeps its table, connection and live spectator view at " + viewport);

            int observations = port.Observations;
            foreach (int actor in port.State.Players.Where(player => !player.Eliminated).Select(player => player.Id)) {
                port.AdvancePublicTurn(actor);
                long revision = port.State.Revision;
                Until(() => run.View.Match?.Revision == revision, "remaining seat " + actor + " public turn");
                check(run.View.Match is { Status: "playing", Decision: null, Viewer.Eliminated: true } state
                        && state.ActivePlayerId == actor && state.Players.Single(player => player.Id == actor).Life == 39
                        && port.Replies == 0 && run.View.Error.Length == 0 && !run.View.NeedsRecovery,
                    mode + " spectator continues receiving seat " + actor + " public turns without granting the eliminated viewer decision authority at " + viewport);
            }
            check(port.Observations > observations && port.LobbyReads > 0 && run.View.Match!.Players
                    .Where(player => player.Id != run.View.Match.ViewerId).All(player => player.Zone("Hand").Cards.IsEmpty),
                mode + " keeps ordinary lobby and match polling after concession without disclosing remaining private hands at " + viewport);

            Click("eliminated-leave");
            check(run.View.Overlay == "leave-table" && Has("confirm-leave-table")
                    && (mode != "hosting" || Text().Contains("Leaving closes the table connection for everyone")),
                mode + " keeps explicit table departure separate from the already completed seat concession at " + viewport);
            Click("close-overlay");
            check(port.Concessions == 1 && port.Leaves == 0 && port.Returns == 0 && port.Closes == 0
                    && run.View.Match?.Status == "playing" && run.View.Lobby?.MatchActive == true,
                mode + " choosing Stay preserves the live table after the player has conceded at " + viewport);
            File.WriteAllText(Path.Combine(path, "concession-lifetime.json"), JsonSerializer.Serialize(new {
                frame = run.Frame.Observe(), port.Concessions, port.Leaves, port.Returns, port.Closes,
                port.Observations, port.LobbyReads, port.Replies,
                scope = "Authored seat-filtered public turns through production pointer and polled session paths; actual rules are tested independently"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private sealed class Port : IMatchConnection, ITableSetup, IAsyncDisposable
    {
        private readonly object gate = new();
        private GameSnapshot state;
        private readonly Lobby lobby;
        private bool concessionPending, admissionObserved;
        internal int Concessions, Leaves, Returns, Closes, Observations, LobbyReads, Replies;
        internal Port(string mode) {
            var initial = PresentationSmoke.Example();
            int viewer = mode == "hosting" ? 0 : 1;
            state = initial with { Id = "concession-" + mode, Revision = 1, BoardRevision = 1, ViewerId = viewer,
                ActivePlayerId = 2, Decision = null, DecisionOwnerId = 2, PriorityPlayerId = 2, Stack = [], Combat = null,
                Players = initial.Players.Select(player => player with { Priority = player.Id == 2,
                    Zones = player.Zones.Select(zone => zone.Name == "Hand" && player.Id != viewer
                        ? zone with { Cards = [], TopCard = null } : zone).ToImmutableArray() }).ToImmutableArray() };
            lobby = new(mode, "Playing", null, false, null, true, "", null, [],
                [new(0, "Host", "human", "Host deck", true, viewer == 0), new(1, "Guest", "human", "Guest deck", true, viewer == 1),
                 new(2, "AI 1", "ai", "AI deck 1", true, false), new(3, "AI 2", "ai", "AI deck 2", true, false)], "concession-table");
        }
        internal GameSnapshot State { get { lock (gate) return state; } }
        internal Lobby Lobby => lobby;
        public async Task ConcedeAsync(string gameId) {
            await Task.Delay(15);
            lock (gate) {
                if (gameId != state.Id) throw new InvalidOperationException("Concession must retain the current game scope.");
                Concessions++;
                state = state with { Revision = state.Revision + 1, Status = "resolving", Decision = null };
                concessionPending = true;
            }
        }
        internal void AdvancePublicTurn(int actor) {
            lock (gate) state = state with { Revision = state.Revision + 1, BoardRevision = state.BoardRevision + 1,
                Turn = state.Turn + 1, ActivePlayerId = actor, DecisionOwnerId = actor, PriorityPlayerId = actor,
                Players = state.Players.Select(player => player with { Priority = player.Id == actor,
                    Life = player.Id == actor ? 39 : player.Life }).ToImmutableArray() };
        }
        public Task<GameSnapshot?> ObserveAsync() {
            lock (gate) {
                Observations++;
                if (concessionPending) {
                    if (!admissionObserved) admissionObserved = true;
                    else {
                        concessionPending = false;
                        state = state with { Revision = state.Revision + 1, BoardRevision = state.BoardRevision + 1,
                            Status = "playing", Result = "Defeat", Decision = null,
                            Players = state.Players.Select(player => player.Id != state.ViewerId ? player : player with {
                                Eliminated = true, Priority = false,
                                Zones = player.Zones.Select(zone => zone with { Count = 0, Cards = [], TopCard = null }).ToImmutableArray()
                            }).ToImmutableArray() };
                    }
                }
                return Task.FromResult<GameSnapshot?>(state);
            }
        }
        public Task<Lobby> LobbyAsync() { lock (gate) { LobbyReads++; return Task.FromResult(lobby); } }
        public Task ExecuteAsync(GameCommand command) { Replies++; throw new InvalidOperationException("An eliminated spectator has no current game command."); }
        public Task LeaveAsync() { Leaves++; return Task.CompletedTask; }
        public Task ReturnToLobbyAsync() { Returns++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Closes++; return ValueTask.CompletedTask; }
        public Task<SoloSetup> PrepareSoloAsync(string deckId) => throw new NotSupportedException();
        public Task StartSoloAsync(string deckId, ImmutableArray<string> opponentIds) => throw new NotSupportedException();
        public Task<Lobby> HostAsync(int players, bool automaticPortForwarding) => throw new NotSupportedException();
        public Task<Lobby> JoinAsync(string address) => throw new NotSupportedException();
        public Task<Lobby> SelectDeckAsync(LobbyDeckSelection selection) => throw new NotSupportedException();
        public Task SetReadyAsync(bool ready) => throw new NotSupportedException();
        public Task StartAsync() => throw new NotSupportedException();
    }
}
