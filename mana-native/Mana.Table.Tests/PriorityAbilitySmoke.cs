using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Mana.Table.Interaction;
using Mana.Renderer;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal sealed class PriorityAbilitySmoke(TableWindow.Probe view, InteractionScenarioEngine scenario) : ITableAutomation
{
    private int stage;
    private double changed;
    private readonly List<string> checks = [];
    private MouseState mouse = new(0, 0, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    private string? click;
    private Mana.Table.Interaction.TableHit? staleUndo;
    private Mana.Table.Interaction.TableHit? staleRow;
    private Mana.Table.Interaction.TableHit? staleSkip;
    private string manualGame = "", manualDecision = "", manualCard = "";
    public string? ExpectedClick => click;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;
    public KeyboardState? ReadKeyboard() => new KeyboardState();
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
        new PlayPreferences { Automatic = false, Sounds = false, TextScale = 1.3f }.Save(directory);
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
        probe.SetTableStudy(true);
        probe.PresentLobby(new("local", "Priority artwork handoff", null, false, null, true, "", null, [], []), pausePolling: true);
        void Tick(double seconds, Point? viewport = null, KeyboardState keys = default) => app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.016)),
            new MouseState(20, 60, 0, default, default, default, default, default), keys, viewport ?? new(1280, 720));
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
        scenario.Present(scenario.State with {
            Decision = scenario.State.Decision! with { Payload = ((PriorityPayload)scenario.State.Decision.Payload) with { CanAutoPass = true } }
        });
        probe.Accept(scenario.State); probe.SetAutomatic(true); Tick(1.65);
        var availableAutoFrame = app.ReadPresentation();
        var availableEntry = availableAutoFrame.Hits.Single(hit => hit.Id == "ability-auto-pass-open");
        check(probe.DecisionDock?.Surface == DecisionDockSurface.None && scenario.State.IgnoredPriorityAbilities.IsEmpty
                && availableAutoFrame.Drawing.Commands.OfType<DrawCommand.Panel>().Any(panel => panel.Bounds == availableEntry.Bounds)
                && availableAutoFrame.Drawing.Commands.OfType<DrawCommand.CenterText>().Any(text => text.Value == "Auto · Abilities · 1 ›"
                    && text.Size == .76f && availableEntry.Bounds.Contains(text.Bounds))
                && probe.HitAt(availableEntry.Bounds.Center)?.Id == availableEntry.Id && scenario.Replies.Count == 0,
            "A visible Auto ability button remains reachable for current unskipped options even when ordinary Auto has no decision dock");
        var ignored = ((PriorityPayload)scenario.State.Decision!.Payload).Abilities.Single() with { IgnoredUntilNextTurn = true };
        scenario.Present(scenario.State with { IgnoredPriorityAbilities = [ignored],
            Decision = scenario.State.Decision! with { Payload = ((PriorityPayload)(scenario.State.Decision!).Payload) with { CanAutoPass = true, Abilities = [] } } });
        probe.Accept(scenario.State); probe.SetAutomatic(true); Tick(1.7);
        var ignoredFrame = app.ReadPresentation();
        check(probe.DecisionDock?.Surface == DecisionDockSurface.None
                && ignoredFrame.Hits.Any(hit => hit.Id == "ability-auto-pass-open" && hit.Bounds.Height <= 48
                    && hit.Intent is Mana.Table.Interaction.MatchIntent.OpenPriorityPassing { AbilityId: "" })
                && !ignoredFrame.Hits.Any(hit => hit.Id is "confirm" or "quiet-priority-stop") && scenario.Replies.Count == 0,
            "An ignored ability retains a compact list entry during a quiet automatic window without a persistent source panel or gameplay command");
        scenario.Present(scenario.State with { IgnoredPriorityAbilities = [ignored, ignored with { Id = "second-ignored-ability" }] });
        probe.Accept(scenario.State); Tick(1.8);
        var multipleIgnoredFrame = app.ReadPresentation();
        check(probe.DecisionDock?.Surface == DecisionDockSurface.None
                && multipleIgnoredFrame.Hits.Any(hit => hit.Id == "ability-auto-pass-open"
                    && hit.Intent is Mana.Table.Interaction.MatchIntent.OpenPriorityPassing)
                && scenario.Replies.Count == 0,
            "Several ignored abilities share one list entry while ordinary Auto keeps the decision dock quiet");
        double clock = 1.8;
        bool automaticBefore = probe.Automatic, heldBefore = probe.Held;
        var stateBeforeReading = scenario.State;
        void OpenControl(string id) {
            check(probe.Activate(app.ReadPresentation().Hits.Single(hit => hit.Id == id)),
                "The current activation navigation target accepts its exact presentation intent: " + id);
            Tick(clock += .1);
        }
        void CheckReadingDidNotChangePlay(string label) => check(probe.Automatic == automaticBefore && probe.Held == heldBefore
                && scenario.State.Decision?.Id == stateBeforeReading.Decision?.Id
                && scenario.State.IgnoredPriorityAbilities.SequenceEqual(stateBeforeReading.IgnoredPriorityAbilities)
                && scenario.Replies.Count == 0,
            label + " preserves Auto, Hold, every retained preference and the unanswered decision");
        OpenControl("ability-auto-pass-open"); OpenControl("ability-read:mesa-fetch");
        var reader = app.ReadPresentation();
        var readerBody = reader.Hits.Single(hit => hit.Id == "ability-reader-body");
        var readerBackdrop = reader.Hits.Single(hit => hit.Id == "ability-reader-backdrop");
        check(probe.TableStudyEnabled && readerBody.Intent is UiIntent.Block
                && readerBackdrop.Intent is Mana.Table.Interaction.MatchIntent.CloseView
                && probe.HitAt(new(readerBody.Bounds.X + 2, readerBody.Bounds.Y + 2))?.Id == readerBody.Id
                && probe.HitAt(new(200, 100))?.Id == readerBackdrop.Id,
            "The shipping-table reader blocks empty interior space and offers a separate dismissible outside backdrop");
        check(probe.Activate(readerBody), "The reader's empty body consumes an interior click");
        Tick(clock += .1);
        check(probe.Overlay == "ability-auto-pass", "Clicking the reader body keeps the exact ability open");
        CheckReadingDidNotChangePlay("Reading and clicking inside the ability");
        OpenControl("ability-reader-backdrop");
        check(probe.Overlay.Length == 0 && app.ReadPresentation().Hits.Any(hit => hit.Id == "ability-auto-pass-open"),
            "Clicking outside the ability reader collapses it back to the compact activation entry");
        CheckReadingDidNotChangePlay("Outside dismissal of the reader");
        OpenControl("ability-auto-pass-open");
        Tick(clock += .1, keys: new(Keys.Escape)); Tick(clock += .1);
        check(probe.Overlay.Length == 0, "Escape collapses the explicit activation list");
        CheckReadingDidNotChangePlay("Escape from the list");
        OpenControl("ability-auto-pass-open"); OpenControl("ability-read:mesa-fetch");
        Tick(clock += .1, keys: new(Keys.Escape)); Tick(clock += .1);
        check(probe.Overlay.Length == 0, "Escape collapses the exact activation reader");
        CheckReadingDidNotChangePlay("Escape from the reader");
        var listFrames = new List<object>();
        var abilities = Enumerable.Range(0, 20).Select(index => new PriorityAbility("many-ability-" + index, "",
            index < 2 ? "A Very Long Named Commander and Their Equally Long Named Partner"
                : $"A Long Named Source With a Distinct Complete Ending Number {index}",
            $"Ability {index}: Pay one life and remove a counter from this permanent: Draw a card, then choose a card from your hand and put it on the bottom of your library.",
            index % 3 == 0)).ToImmutableArray();
        var baseState = scenario.State;
        foreach (var viewport in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            scenario.Present(baseState with {
                IgnoredPriorityAbilities = abilities.Where(ability => ability.IgnoredUntilNextTurn).ToImmutableArray(),
                Decision = baseState.Decision! with { Payload = ((PriorityPayload)baseState.Decision.Payload) with {
                    CanAutoPass = false, Abilities = abilities
                } }
            });
            probe.Accept(scenario.State); Tick(clock += .1, viewport);
            var collapsed = app.ReadPresentation();
            var entry = collapsed.Hits.Single(hit => hit.Id == "ability-auto-pass-open");
            check(entry.Bounds.Height is >= 40 and <= 48
                && collapsed.Drawing.Commands.OfType<DrawCommand.Panel>().Any(panel => panel.Bounds == entry.Bounds)
                && collapsed.Drawing.Commands.OfType<DrawCommand.CenterText>().Any(text => text.Value == "Auto · Abilities · 20 ›"
                    && text.Size == .76f && entry.Bounds.Contains(text.Bounds))
                && probe.HitAt(entry.Bounds.Center)?.Id == entry.Id
                && !collapsed.Hits.Any(hit => hit.Id.StartsWith("ability-read:", StringComparison.Ordinal))
                && !collapsed.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => abilities.Any(ability => text.Value == ability.SourceName)),
                "Many long-named activations preserve one compact entry and reserve no persistent source rows at " + viewport);
            check(probe.Activate(entry), "The collapsed activation entry opens through its current presentation intent at " + viewport);
            Tick(clock += .1, viewport);
            int previousPages = 0;
            while (app.ReadPresentation().Hits.SingleOrDefault(hit => hit.Id == "ability-list-prev") is { } previous) {
                check(++previousPages < abilities.Length && probe.Activate(previous),
                    "A reopened list can return from its retained page through the existing Previous control at " + viewport);
                Tick(clock += .1, viewport);
            }
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pages = new List<object>();
            int page = 0;
            while (true) {
                var frame = app.ReadPresentation();
                var rows = frame.Hits.Where(hit => hit.Id.StartsWith("ability-read:", StringComparison.Ordinal)).ToArray();
                var text = frame.Drawing.Commands.Select(command => command switch {
                    DrawCommand.Text value => value.Value, DrawCommand.CenterText value => value.Value, _ => null
                }).Where(value => value != null).Cast<string>().ToArray();
                check(probe.Overlay == "priority-stops" && rows.Length > 0
                    && text.Any(value => value.EndsWith("7 of 20 skipping until your next turn", StringComparison.Ordinal))
                    && scenario.Replies.Count == 0,
                    "The expanded list distinguishes seven skipped abilities from twenty exact options without advancing gameplay at " + viewport);
                foreach (var row in rows) {
                    var read = row.Intent as Mana.Table.Interaction.MatchIntent.ReadPriorityAbility;
                    var ability = abilities.SingleOrDefault(ability => ability.Id == read?.Id);
                    check(ability != null && row.Id == "ability-read:" + ability.Id && visited.Add(ability.Id)
                        && text.Contains(ability.SourceName) && text.Contains(ability.Text),
                        "Every full-size named list entry preserves its complete source, rules and independent exact ability identity at " + viewport);
                    check(row.Bounds.Left >= 0 && row.Bounds.Top >= 60 && row.Bounds.Right <= 1600 && row.Bounds.Bottom <= 900
                        && rows.All(other => other.Id == row.Id || !row.Bounds.Intersects(other.Bounds))
                        && frame.Hits.Where(hit => hit.Id is "close-overlay" or "ability-list-prev" or "ability-list-next")
                            .All(control => !row.Bounds.Intersects(control.Bounds))
                        && probe.HitAt(row.Bounds.Center)?.Id == row.Id,
                        "Each activation row stays within the table workspace, clear of other rows and navigation, with an uncovered target at " + viewport);
                    if (ability == null) throw new InvalidOperationException("A reader must retain its exact ability.");
                    var setting = frame.Hits.Single(hit => hit.Id == (ability.IgnoredUntilNextTurn ? "ability-undo:" : "ability-pass:") + ability.Id);
                    check(setting.Intent is MatchIntent.SetPriorityAbility change && change.Id == ability.Id
                            && change.Ignored == !ability.IgnoredUntilNextTurn
                            && setting.Bounds.Height >= 40 && setting.Bounds.Left >= 0 && setting.Bounds.Right <= 1600
                            && setting.Bounds.Top >= 60 && setting.Bounds.Bottom <= 900
                            && rows.All(other => !setting.Bounds.Intersects(other.Bounds))
                            && frame.Hits.Where(hit => hit.Intent is MatchIntent.SetPriorityAbility && hit.Id != setting.Id)
                                .All(other => !setting.Bounds.Intersects(other.Bounds))
                            && probe.HitAt(setting.Bounds.Center)?.Id == setting.Id,
                        "Each exact ability has a separate reachable Skip or Stop button clear of every noncommitting reader and other preference at " + viewport);
                }
                pages.Add(frame.Observe());
                var next = frame.Hits.SingleOrDefault(hit => hit.Id == "ability-list-next");
                if (next == null) break;
                check(++page < abilities.Length && probe.Activate(next), "The expanded full-size list advances only through its explicit remaining-activations control at " + viewport);
                Tick(clock += .1, viewport);
            }
            check(visited.SetEquals(abilities.Select(ability => ability.Id)) && page > 0,
                "Every activation, including two independent abilities on one long-named source, is reachable after expanding the workspace at " + viewport);
            var close = app.ReadPresentation().Hits.Single(hit => hit.Id == "close-overlay");
            check(probe.Activate(close), "Back to table closes the explicit activation list at " + viewport);
            Tick(clock += .1, viewport);
            check(probe.Overlay.Length == 0 && scenario.Replies.Count == 0
                && scenario.State.IgnoredPriorityAbilities.Length == 7
                && scenario.State.Decision?.Payload is PriorityPayload { Abilities.Length: 20 },
                "Collapsing the large list leaves every current and skipped ability unchanged at " + viewport);
            listFrames.Add(new { Viewport = viewport, Pages = pages, Collapsed = app.ReadPresentation().Observe() });
        }
        File.WriteAllText(Path.Combine(directory, "frames.json"), JsonSerializer.Serialize(new { Loading = loading, Ready = ready,
            AvailableAuto = availableAutoFrame.Observe(), Ignored = ignoredFrame.Observe(), MultipleIgnored = multipleIgnoredFrame.Observe(), Lists = listFrames },
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
                if (!view.TableStudyEnabled) { view.SetTableStudy(true); changed = view.Now; return; }
                view.SetAutomatic(true); view.SetHeld(true);
                Check(view.Hits.Any(h => h.Id == "ability-auto-pass-open" && h.Bounds.Height is >= 40 and <= 48
                    && h.Intent is Mana.Table.Interaction.MatchIntent.OpenPriorityPassing { AbilityId: "" }),
                    "A single activation has a compact readable list entry without preselecting or changing an ability");
                Check(state.Decision?.Payload is PriorityPayload { CanAutoPass: false }, "Arid Mesa initially prevents automatic passing");
                Press("ability-auto-pass-open"); Next(); break;
            case 1: Capture(surface, "01-ability-available"); Release(); Next(); break;
            case 2:
                Check(ClickAccepted && view.Overlay == "priority-stops" && scenario.Replies.Count == 0
                    && view.Hits.Any(h => h.Id == "ability-read:mesa-fetch"
                        && h.Intent is Mana.Table.Interaction.MatchIntent.ReadPriorityAbility { Id: "mesa-fetch" })
                    && view.Hits.Any(h => h.Id == "ability-pass:mesa-fetch"
                        && h.Intent is MatchIntent.SetPriorityAbility { Id: "mesa-fetch", Ignored: true }),
                    "Opening activation stops lists an exact named ability without toggling its preference or answering priority");
                Check(view.Frame!.Drawing.Commands.OfType<DrawCommand.Text>()
                        .Any(text => text.Value?.StartsWith("Hold on; skips paused ·", StringComparison.Ordinal) == true),
                    "The direct preference list explains that Hold pauses configured skips without silently resuming Auto");
                Capture(surface, "02-ability-list"); Press("ability-read:mesa-fetch"); Next(); break;
            case 3: Release(); Next(); break;
            case 4:
                Check(ClickAccepted && view.Overlay == "ability-auto-pass" && scenario.Replies.Count == 0
                    && view.Hits.Any(h => h.Id == "ability-pass:mesa-fetch")
                    && state.Decision is { Payload: PriorityPayload { Abilities: [{ SourceCardKey: var source }] } }
                    && source == state.Viewer!.Zone("Battlefield").Cards.Single().Key,
                    "Choosing the named activation reads its authorized source and exact rules without changing a preference or passing");
                Capture(surface, "03-ability-source-and-rules"); Press("ability-list-back"); Next(); break;
            case 5: Release(); Next(); break;
            case 6:
                Check(ClickAccepted && view.Overlay == "priority-stops" && scenario.Replies.Count == 0
                    && view.Hits.Any(h => h.Id == "ability-read:mesa-fetch"),
                    "Back to list preserves the exact unanswered activation choices without a command");
                Press("ability-list-backdrop"); Next(); break;
            case 7: Release(); Next(); break;
            case 8:
                Check(ClickAccepted && view.Overlay.Length == 0 && scenario.Replies.Count == 0
                    && state.Decision?.Payload is PriorityPayload { CanAutoPass: false }
                    && view.Hits.Any(h => h.Id == "ability-auto-pass-open")
                    && !view.Hits.Any(h => h.Id.StartsWith("ability-read:", StringComparison.Ordinal)),
                    "Clicking outside the list collapses every source row while retaining the same response and stop preference");
                Press("ability-auto-pass-open"); Next(); break;
            case 9: Release(); Next(); break;
            case 10:
                Check(view.Overlay == "priority-stops" && view.Hits.Any(hit => hit.Id == "ability-pass:mesa-fetch"
                        && hit.Intent is MatchIntent.SetPriorityAbility { Id: "mesa-fetch", Ignored: true }),
                    "The list offers Skip directly beside the exact activation without opening its reader");
                Press("ability-pass:mesa-fetch"); Next(); break;
            case 11: Release(); Next(); break;
            case 12:
                Check(ClickAccepted && scenario.Replies is [IgnorePriorityAbility { AbilityId: "mesa-fetch" }], "The contextual control submits only the selected ability preference");
                Check(state.Decision?.Payload is PriorityPayload { CanAutoPass: true } && state.IgnoredPriorityAbilities is [{ Id: "mesa-fetch", IgnoredUntilNextTurn: true }], "Engine-authorized Auto becomes eligible after ignoring the sole response");
                Check(view.Automatic && view.Held && view.Overlay == "priority-stops"
                    && view.Hits.Any(h => h.Id == "ability-undo:mesa-fetch")
                    && !view.Hits.Any(h => h.Id is "confirm" or "ability-auto-pass-open"),
                    "Acknowledging Skip retains the activation list and its current Stop control without exposing the held priority behind it");
                Capture(surface, "04-ability-ignored-held");
                Press("ability-list-backdrop"); stage = 120; changed = view.Now; break;
            case 120: Release(); Next(); break;
            case 121:
                Check(ClickAccepted && view.Overlay.Length == 0 && view.Automatic && view.Held
                    && view.Hits.Any(h => h.Id == "confirm") && view.Hits.Any(h => h.Id == "ability-auto-pass-open"),
                    "Explicit dismissal returns the compact activation entry and held manual priority without changing its preference");
                Publish(state with { Decision = null, Status = "resolving" }); view.SetHeld(false);
                stage = 13; changed = view.Now; break;
            case 13:
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
                    "The compact activation entry leaves every rendered commander face and command-zone card or pile hit unobstructed");
                Capture(surface, "05-ability-ignored-following"); Press("ability-auto-pass-open"); Next(); break;
            case 14: Release(); Next(); break;
            case 15:
                Check(ClickAccepted && view.Overlay == "priority-stops" && state.Decision == null && scenario.Replies.Count == 1
                    && view.Hits.Any(h => h.Id == "ability-read:mesa-fetch")
                    && view.Hits.Any(h => h.Id == "ability-undo:mesa-fetch"
                        && h.Intent is MatchIntent.SetPriorityAbility { Id: "mesa-fetch", Ignored: false }),
                    "A retained exact activation offers reading and direct Stop between prompts without a current source action handle");
                staleUndo = view.Hits.Single(h => h.Id == "ability-undo:mesa-fetch");
                Capture(surface, "06-retained-ability-list"); Press("ability-undo:mesa-fetch"); Next(); break;
            case 16: Release(); Next(); break;
            case 17:
                Check(ClickAccepted && state.IgnoredPriorityAbilities.Length == 0, "Undo clears the preference without waiting for local priority");
                Check(scenario.Replies[^1] is RestorePriorityAbility, "Undo uses the match preference command rather than a gameplay decision");
                Check(scenario.Replies.Count == 2 && state.Decision == null, "Changing the preference never activates the card or advances a decision");
                var example = Example();
                Publish(example with { Decision = example.Decision! with { Payload = ((PriorityPayload)(example.Decision!).Payload) with { Abilities = [((PriorityPayload)example.Decision.Payload).Abilities[0], new("mesa-second", "mesa", "Arid Mesa", "A second independent ability on the same card.", false)] } } });
                view.SetHeld(true); Next(); break;
            case 18:
                Check(view.Hits.Any(h => h.Id == "ability-auto-pass-open"), "Multiple abilities open individual choices instead of a card-wide ignore");
                Press("ability-auto-pass-open"); Next(); break;
            case 19: Release(); Next(); break;
            case 20:
                Check(view.Overlay == "priority-stops"
                    && view.Hits.Any(h => h.Id == "ability-read:mesa-fetch")
                    && view.Hits.Any(h => h.Id == "ability-read:mesa-second")
                    && view.Hits.Any(h => h.Id == "ability-pass:mesa-fetch"
                        && h.Intent is MatchIntent.SetPriorityAbility { Id: "mesa-fetch", Ignored: true })
                    && view.Hits.Any(h => h.Id == "ability-pass:mesa-second"
                        && h.Intent is MatchIntent.SetPriorityAbility { Id: "mesa-second", Ignored: true }),
                    "Two abilities on the same source have independent named readers and direct exact-ID Skip controls");
                Check(scenario.Replies.Count == 2, "Opening several activation preferences still submits no gameplay or setting command");
                Capture(surface, "07-same-source-ability-list"); Press("ability-pass:mesa-second"); Next(); break;
            case 21: Release(); Next(); break;
            case 22:
                Check(state.IgnoredPriorityAbilities is [{ Id: "mesa-second" }] && state.Decision?.Payload is PriorityPayload { CanAutoPass: false }, "Ignoring one ability does not suppress another ability on the same card");
                Check(view.Overlay == "priority-stops" && view.Automatic && view.Held
                    && view.Hits.Any(h => h.Id == "ability-pass:mesa-fetch")
                    && view.Hits.Any(h => h.Id == "ability-undo:mesa-second")
                    && !view.Hits.Any(h => h.Id == "confirm" || h.Zone == "Battlefield"),
                    "Skipping one of two same-source activations retains both independent current list controls and blocks the underlying table");
                Capture(surface, "07-same-source-ability-skipped-list");
                Press("ability-list-backdrop"); stage = 220; changed = view.Now; break;
            case 220: Release(); Next(); break;
            case 221:
                Check(ClickAccepted && view.Overlay.Length == 0 && view.Hits.Any(h => h.Id == "confirm"),
                    "Explicitly dismissing the retained list reveals manual priority while the other available response still stops Auto");
                var mesaHit = view.Hits.First(h => h.Card?.VisualId == "mesa" && h.Zone == "Battlefield");
                Check(mesaHit.Card!.Selectable && CardActions.CanActivate(state, mesaHit.Card), "An ignored source remains available for manual activation");
                manualGame = state.Id; manualDecision = state.Decision!.Id; manualCard = mesaHit.Card.Key;
                Press(mesaHit.Id); stage = 23; changed = view.Now; break;
            case 23: Release(); Next(); break;
            case 24:
                if (view.DisplayedPose("mesa") is not { Size.X: >= 400 } || !view.Hits.Any(hit => hit.Id == "read-card-action")) return;
                Check(scenario.Replies.Count == 3 && state.Decision is { Payload: PriorityPayload }
                    && state.IgnoredPriorityAbilities is [{ Id: "mesa-second" }],
                    "Reading the ignored source sends no gameplay reply and preserves its independent ability preference");
                Press("read-card-action"); Next(); break;
            case 25: Release(); Next(); break;
            case 26:
                Check(scenario.Replies[^1] is ActivateCard reply && reply.GameId == manualGame
                    && reply.DecisionId == manualDecision && reply.Card == manualCard && state.Decision is { Payload: AbilityChoicePayload },
                    "Explicit manual activation opens the normal ability chooser with the exact current game, decision and card scope");
                Check(!TurnGuide.CanAutoPass(state, true, false, new HashSet<string>(), false),
                    "Ignoring an activation cannot authorize Auto to answer its later required ability choice");
                var nextTurn = Example();
                Publish(nextTurn with { Turn = nextTurn.Turn + 1, ActivePlayerId = nextTurn.ViewerId, IgnoredPriorityAbilities = [] }); Next(); break;
            case 27:
                view.Activate(staleUndo!);
                Check(!view.Busy && scenario.Replies.Count == 4, "An Undo captured before expiry quietly does nothing after that preference has cleared");
                Check(view.Hits.Any(h => h.Id == "ability-auto-pass-open") && !view.Hits.Any(h => h.Id.StartsWith("ability-undo:")), "The viewer's next turn retains the collapsed activation-list entry after engine expiry");
                Check(!TurnGuide.CanAutoPass(state, true, false, new HashSet<string>(), false), "After expiry the available ability once again stops Auto");
                Check(scenario.Replies.Count == 4 && scenario.Replies.All(r => r is IgnorePriorityAbility or RestorePriorityAbility or ActivateCard), "Preference and inspection flows never submit an automatic activation or extra pass");
                Capture(surface, "08-ability-next-turn"); Press("ability-auto-pass-open"); Next(); break;
            case 28: Release(); Next(); break;
            case 29:
                Check(view.Overlay == "priority-stops", "Opening the list after expiry reads the current available activations");
                staleRow = view.Hits.Single(hit => hit.Id == "ability-read:mesa-fetch");
                staleSkip = view.Hits.Single(hit => hit.Id == "ability-pass:mesa-fetch");
                var replacement = Example() with { Turn = state.Turn, ActivePlayerId = state.ActivePlayerId };
                Publish(replacement with { Decision = replacement.Decision! with { Payload = ((PriorityPayload)replacement.Decision.Payload) with {
                    Abilities = [((PriorityPayload)replacement.Decision.Payload).Abilities[0] with { Id = "replacement-fetch" }]
                } } }); Next(); break;
            case 30:
                view.Activate(staleRow!); view.Activate(staleSkip!);
                Check(view.Overlay == "priority-stops" && scenario.Replies.Count == 4 && !view.Busy
                    && view.Hits.Any(hit => hit.Id == "ability-pass:replacement-fetch"
                        && hit.Intent is MatchIntent.SetPriorityAbility { Id: "replacement-fetch", Ignored: true }),
                    "Expired reader and direct Skip targets cannot open or change the fresh replacement activation");
                Press("ability-read:replacement-fetch"); Next(); break;
            case 31: Release(); Next(); break;
            case 32:
                Check(view.Overlay == "ability-auto-pass" && view.Hits.Any(hit => hit.Id == "ability-pass:replacement-fetch"),
                    "A fresh named list row opens the exact replacement activation");
                Publish(Example() with { Turn = state.Turn, ActivePlayerId = state.ActivePlayerId }); Next(); break;
            case 33:
                Check(view.Overlay.Length == 0 && scenario.Replies.Count == 4,
                    "A focused activation which disappears closes instead of rebinding its Skip button to a surviving ability");
                File.WriteAllText(Path.Combine(view.Profile, "priority-ability-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                view.Exit(); break;
        }
    }
}
