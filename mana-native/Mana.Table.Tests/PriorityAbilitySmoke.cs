using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed class PriorityAbilitySmoke(TableWindow.Probe view, InteractionScenarioEngine scenario) : ITableAutomation
{
    private int stage;
    private double changed;
    private readonly List<string> checks = [];
    private MouseState mouse = new(0, 0, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    private string? click;
    private Mana.Table.Interaction.TableHit? staleUndo;
    private Mana.Table.Interaction.TableHit? staleEntry;
    private string manualGame = "", manualDecision = "", manualCard = "";
    public string? ExpectedClick => click;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;
    private void Check(bool value, string message)
    {
        if (!value) {
            File.WriteAllText(Path.Combine(view.Profile, "priority-ability-failure.json"), JsonSerializer.Serialize(new {
                stage, view.Now, view.Busy, view.Polling, view.ActivityOpen, view.PreparingTableArtwork,
                view.TableArtworkProgress, view.Overlay, Match = view.Match, Frame = view.Frame?.Observe()
            }, new JsonSerializerOptions { WriteIndented = true }));
            throw new InvalidOperationException(message);
        }
        checks.Add(message);
    }
    private void Publish(GameSnapshot state) { scenario.Present(state); view.Accept(scenario.State); }
    private void Next() { stage++; changed = view.Now; }
    private void Capture(Texture2D surface, string name) { using var file = File.Create(Path.Combine(view.Profile, name + ".png")); surface.SaveAsPng(file, surface.Width, surface.Height); }
    private void MouseAt(Point point, ButtonState button = ButtonState.Released)
    {
        var viewport = view.GraphicsDevice.Viewport; float scale = Math.Min(viewport.Width / 1600f, viewport.Height / 900f);
        mouse = new((int)(point.X * scale + (viewport.Width - 1600 * scale) / 2), (int)(point.Y * scale + (viewport.Height - 900 * scale) / 2), 0,
            button, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    private void Press(string id) { click = id; ClickAccepted = false; MouseAt(view.Hits.Single(h => h.Id == id).Bounds.Center, ButtonState.Pressed); }
    private void Release() { var hit = view.Pressed ?? throw new InvalidOperationException("Expected a pressed control"); MouseAt(hit.Bounds.Center); }

    internal static GameSnapshot Example()
    {
        var state = PresentationSmoke.Example();
        var mesa = new Card { Key = "mesa", VisualId = "mesa", CombatId = "mesa", Name = "Arid Mesa", ArtName = "Arid Mesa", Type = "Land", Selectable = true,
            Text = "{T}, Pay 1 life, Sacrifice Arid Mesa: Search your library for a Mountain or Plains card, put it onto the battlefield, then shuffle." };
        return state with { ActivePlayerId = 1, Phase = "Main phase", PhaseKey = "MAIN1", Combat = null, Stack = [],
            Players = state.Players.Select(p => p.Id != state.ViewerId ? p : p with { Zones = p.Zones.Select(z => z.Name == "Battlefield" ? z with { Count = 1, Cards = [mesa] }
                : z.Name is "Hand" or "Command" ? z with { Count = 0, Cards = [] } : z).ToImmutableArray() }).ToImmutableArray(),
            Decision = new Decision("", new DecisionContext(), new PriorityPayload(new DecisionReply("Confirm", true), null, CanAutoPass: false, QuietYieldToken: "", Abilities: [new("mesa-fetch", "mesa", "Arid Mesa", mesa.Text, false)])) };
    }

    internal static void CheckArtworkHandoff(string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "priority-artwork-handoff");
        Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = false, Sounds = false }.Save(directory);
        var scenario = new InteractionScenarioEngine(); scenario.Present(Example());
        bool artworkReady = false;
        var resources = new Mana.Table.Presentation.PresentationResources {
            Artwork = faces => new(faces.Length, artworkReady ? faces.Length : 0, 0)
        };
        // This authored presentation fixture must never advance its engine.
        using var app = new TableApplication(TestTableServices.For(scenario), directory, fixture: "priority-preference-frame",
            initialState: scenario.State, resources: resources) { Automated = true };
        app.Initialize();
        var probe = app.TestPort;
        probe.PresentLobby(new("local", "Priority artwork handoff", null, false, null, true, "", null, [], []), pausePolling: true);
        void Tick(double seconds) => app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.016)),
            new MouseState(20, 60, 0, default, default, default, default, default), new(), new(1280, 720));
        Tick(.1); Tick(1.5);
        var loading = app.ReadPresentation().Observe();
        check(probe.ActivityOpen && probe.PreparingTableArtwork && !probe.Busy
            && probe.Match?.Decision?.Payload is PriorityPayload { Abilities.Length: 1 }
            && !loading.Controls.Any(hit => hit.Id == "ability-auto-pass-open"),
            "Pending initial artwork owns the priority frame even after the command connection becomes idle");
        artworkReady = true; Tick(1.6);
        var ready = app.ReadPresentation().Observe();
        check(!probe.ActivityOpen && !probe.PreparingTableArtwork
            && ready.Controls.Any(hit => hit.Id == "ability-auto-pass-open") && scenario.Replies.Count == 0,
            "Settling initial artwork reveals the existing single-ability preference without advancing gameplay");
        var ignored = ((PriorityPayload)scenario.State.Decision!.Payload).Abilities.Single() with { IgnoredUntilNextTurn = true };
        scenario.Present(scenario.State with { IgnoredPriorityAbilities = [ignored],
            Decision = scenario.State.Decision! with { Payload = ((PriorityPayload)(scenario.State.Decision!).Payload) with { CanAutoPass = true, Abilities = [] } } });
        probe.Accept(scenario.State); probe.SetAutomatic(true); Tick(1.7);
        var ignoredFrame = app.ReadPresentation();
        check(probe.DecisionDock?.Surface == DecisionDockSurface.None
                && ignoredFrame.Hits.Any(hit => hit.Id == "ability-auto-pass-open" && hit.Bounds.Height >= 60
                    && hit.Intent is Mana.Table.Interaction.MatchIntent.OpenPriorityPassing { AbilityId: "mesa-fetch" })
                && !ignoredFrame.Hits.Any(hit => hit.Id is "confirm" or "quiet-priority-stop") && scenario.Replies.Count == 0,
            "An ignored ability retains its named read entry during a quiet automatic window without a direct preference toggle or gameplay command");
        scenario.Present(scenario.State with { IgnoredPriorityAbilities = [ignored, ignored with { Id = "second-ignored-ability" }] });
        probe.Accept(scenario.State); Tick(1.8);
        var multipleIgnoredFrame = app.ReadPresentation();
        check(probe.DecisionDock?.Surface == DecisionDockSurface.None
                && multipleIgnoredFrame.Hits.Any(hit => hit.Id == "ability-auto-pass-open"
                    && hit.Intent is Mana.Table.Interaction.MatchIntent.OpenPriorityPassing)
                && scenario.Replies.Count == 0,
            "Several ignored abilities retain their existing scoped Edit route while ordinary Auto keeps the decision dock quiet");
        File.WriteAllText(Path.Combine(directory, "frames.json"), JsonSerializer.Serialize(new { Loading = loading, Ready = ready,
            Ignored = ignoredFrame.Observe(), MultipleIgnored = multipleIgnoredFrame.Observe() },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Frame(Texture2D surface)
    {
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (view.Now > 35) throw new TimeoutException("Ability auto-pass stage " + stage);
        if (view.Busy || view.ActivityOpen || view.Now - changed < (stage == 0 ? 1.5 : .17)) return;
        var state = scenario.State;
        switch (stage) {
            case 0:
                view.SetAutomatic(true); view.SetHeld(true);
                Check(view.Hits.Any(h => h.Id == "ability-auto-pass-open" && h.Bounds.Height >= 60
                    && h.Intent is Mana.Table.Interaction.MatchIntent.OpenPriorityPassing { AbilityId: "mesa-fetch" }),
                    "A single activation has a named readable entry with an exact ability intent and a substantial hit area");
                Check(state.Decision?.Payload is PriorityPayload { CanAutoPass: false }, "Arid Mesa initially prevents automatic passing");
                Press("ability-auto-pass-open"); Next(); break;
            case 1: Capture(surface, "01-ability-available"); Release(); Next(); break;
            case 2:
                Check(ClickAccepted && view.Overlay == "ability-auto-pass" && scenario.Replies.Count == 0
                    && view.Hits.Any(h => h.Id == "ability-pass:mesa-fetch")
                    && state.Decision is { Payload: PriorityPayload { Abilities: [{ SourceCardKey: var source }] } }
                    && source == state.Viewer!.Zone("Battlefield").Cards.Single().Key,
                    "Opening the single activation reads its current authorized source and rules without toggling a preference or passing");
                Capture(surface, "02-ability-source-and-rules"); Press("ability-pass:mesa-fetch"); Next(); break;
            case 3: Release(); Next(); break;
            case 4:
                Check(ClickAccepted && scenario.Replies is [IgnorePriorityAbility { AbilityId: "mesa-fetch" }], "The contextual control submits only the selected ability preference");
                Check(state.Decision?.Payload is PriorityPayload { CanAutoPass: true } && state.IgnoredPriorityAbilities is [{ Id: "mesa-fetch", IgnoredUntilNextTurn: true }], "Engine-authorized Auto becomes eligible after ignoring the sole response");
                Check(view.Automatic && view.Held && view.Hits.Any(h => h.Id == "confirm"), "Hold continues to expose manual priority after an ability is ignored");
                Check(view.Hits.Any(h => h.Id == "ability-auto-pass-open") && !view.Hits.Any(h => h.Id == "ability-undo:mesa-fetch"),
                    "The armed preference keeps its named source entry rather than an unexplained direct Restore switch");
                Capture(surface, "03-ability-ignored-held");
                Publish(state with { Decision = null, Status = "resolving" }); view.SetHeld(false); Next(); break;
            case 5:
                Check(view.Hits.Any(h => h.Id == "ability-auto-pass-open") && state.IgnoredPriorityAbilities[0].SourceCardKey.Length == 0,
                    "The retained policy stays readable between prompts without retaining a source action handle");
                Check(view.Hits.Any(h => h.Id == "priority-take-control"),
                    "Take control remains visible while automatic play follows resolution between decisions");
                var followingFrame = view.Frame ?? throw new InvalidOperationException("Expected the ignored-ability presentation frame");
                var activationBounds = followingFrame.Hits.Single(hit => hit.Id == "ability-auto-pass-open").Bounds;
                var commandCards = followingFrame.Cards.Where(card => card.Zone == "Command").ToArray();
                Check(commandCards.Length > 0
                    && commandCards.All(card => !activationBounds.Intersects(card.Display.Bounds) && !activationBounds.Intersects(card.Hit.Bounds))
                    && followingFrame.Hits.Where(hit => hit.Zone == "Command").All(hit => !activationBounds.Intersects(hit.Bounds)),
                    "The measured activation entry leaves every rendered commander face and command-zone card or pile hit unobstructed");
                Capture(surface, "04-ability-ignored-following"); Press("ability-auto-pass-open"); Next(); break;
            case 6: Release(); Next(); break;
            case 7:
                Check(ClickAccepted && view.Overlay == "ability-auto-pass" && state.Decision == null && scenario.Replies.Count == 1,
                    "Reading a retained policy without an authorized source face uses its disclosed rules and sends no command");
                staleUndo = view.Hits.Single(h => h.Id == "ability-undo:mesa-fetch");
                Capture(surface, "05-ability-rules-only"); Press("ability-undo:mesa-fetch"); Next(); break;
            case 8: Release(); Next(); break;
            case 9:
                Check(ClickAccepted && state.IgnoredPriorityAbilities.Length == 0, "Undo clears the preference without waiting for local priority");
                Check(scenario.Replies[^1] is RestorePriorityAbility, "Undo uses the match preference command rather than a gameplay decision");
                Check(scenario.Replies.Count == 2 && state.Decision == null, "Changing the preference never activates the card or advances a decision");
                var example = Example();
                Publish(example with { Decision = example.Decision! with { Payload = ((PriorityPayload)(example.Decision!).Payload) with { Abilities = [((PriorityPayload)example.Decision.Payload).Abilities[0], new("mesa-second", "mesa", "Arid Mesa", "A second independent ability on the same card.", false)] } } });
                view.SetHeld(true); Next(); break;
            case 10:
                Check(view.Hits.Any(h => h.Id == "ability-auto-pass-open"), "Multiple abilities open individual choices instead of a card-wide ignore");
                Press("ability-auto-pass-open"); Next(); break;
            case 11: Release(); Next(); break;
            case 12:
                Check(view.Overlay == "ability-auto-pass" && view.Hits.Any(h => h.Id == "ability-pass:mesa-fetch"), "Ability details show the exact first ability and its preference");
                Check(scenario.Replies.Count == 2, "Opening several activation preferences still submits no gameplay or setting command");
                Capture(surface, "06-ability-details"); Press("ability-next"); Next(); break;
            case 13: Release(); Next(); break;
            case 14:
                Check(view.Hits.Any(h => h.Id == "ability-pass:mesa-second"), "The next ability on the same source has an independent scoped control");
                Press("ability-pass:mesa-second"); Next(); break;
            case 15: Release(); Next(); break;
            case 16:
                Check(state.IgnoredPriorityAbilities is [{ Id: "mesa-second" }] && state.Decision?.Payload is PriorityPayload { CanAutoPass: false }, "Ignoring one ability does not suppress another ability on the same card");
                Check(view.Hits.Any(h => h.Id == "confirm"), "Another available response still stops Auto");
                var mesaHit = view.Hits.First(h => h.Card?.VisualId == "mesa" && h.Zone == "Battlefield");
                Check(mesaHit.Card!.Selectable && CardActions.CanActivate(state, mesaHit.Card), "An ignored source remains available for manual activation");
                manualGame = state.Id; manualDecision = state.Decision!.Id; manualCard = mesaHit.Card.Key;
                Press(mesaHit.Id); Next(); break;
            case 17: Release(); Next(); break;
            case 18:
                if (view.DisplayedPose("mesa") is not { Size.X: >= 400 } || !view.Hits.Any(hit => hit.Id == "read-card-action")) return;
                Check(scenario.Replies.Count == 3 && state.Decision is { Payload: PriorityPayload }
                    && state.IgnoredPriorityAbilities is [{ Id: "mesa-second" }],
                    "Reading the ignored source sends no gameplay reply and preserves its independent ability preference");
                Press("read-card-action"); Next(); break;
            case 19: Release(); Next(); break;
            case 20:
                Check(scenario.Replies[^1] is ActivateCard reply && reply.GameId == manualGame
                    && reply.DecisionId == manualDecision && reply.Card == manualCard && state.Decision is { Payload: AbilityChoicePayload },
                    "Explicit manual activation opens the normal ability chooser with the exact current game, decision and card scope");
                var nextTurn = Example();
                Publish(nextTurn with { Turn = nextTurn.Turn + 1, ActivePlayerId = nextTurn.ViewerId, IgnoredPriorityAbilities = [] }); Next(); break;
            case 21:
                view.Activate(staleUndo!);
                Check(!view.Busy && scenario.Replies.Count == 4, "An Undo captured before expiry quietly does nothing after that preference has cleared");
                Check(view.Hits.Any(h => h.Id == "ability-auto-pass-open") && !view.Hits.Any(h => h.Id.StartsWith("ability-undo:")), "The viewer's next turn shows the named ability entry again after engine expiry");
                Check(!TurnGuide.CanAutoPass(state, true, false, new HashSet<string>(), false), "After expiry the available ability once again stops Auto");
                Check(scenario.Replies.Count == 4 && scenario.Replies.All(r => r is IgnorePriorityAbility or RestorePriorityAbility or ActivateCard), "Preference and inspection flows never submit an automatic activation or extra pass");
                Capture(surface, "07-ability-next-turn");
                staleEntry = view.Hits.Single(hit => hit.Id == "ability-auto-pass-open");
                var replacement = Example() with { Turn = state.Turn, ActivePlayerId = state.ActivePlayerId };
                Publish(replacement with { Decision = replacement.Decision! with { Payload = ((PriorityPayload)replacement.Decision.Payload) with {
                    Abilities = [((PriorityPayload)replacement.Decision.Payload).Abilities[0] with { Id = "replacement-fetch" }]
                } } }); Next(); break;
            case 22:
                view.Activate(staleEntry!);
                Check(view.Overlay.Length == 0 && scenario.Replies.Count == 4,
                    "A source entry captured for an expired activation cannot silently open its replacement");
                Press("ability-auto-pass-open"); Next(); break;
            case 23: Release(); Next(); break;
            case 24:
                Check(view.Overlay == "ability-auto-pass" && view.Hits.Any(hit => hit.Id == "ability-pass:replacement-fetch"),
                    "A fresh source entry opens the exact replacement activation");
                Publish(Example() with { Turn = state.Turn, ActivePlayerId = state.ActivePlayerId }); Next(); break;
            case 25:
                Check(view.Overlay.Length == 0 && scenario.Replies.Count == 4,
                    "A focused activation which disappears closes instead of rebinding its Skip button to a surviving ability");
                File.WriteAllText(Path.Combine(view.Profile, "priority-ability-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                view.Exit(); break;
        }
    }
}
