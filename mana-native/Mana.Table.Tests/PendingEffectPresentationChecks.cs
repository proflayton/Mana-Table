using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;
using Mana.Renderer;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static class PendingEffectPresentationChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (bool study in new[] { false, true }) {
            string directory = Path.Combine(profile, study ? "pending-values-study" : "pending-values-normal");
            Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(directory);
            var source = new Card { VisualId = "pending-source", CombatId = "pending-source", Name = "All Will Be One", Type = "Enchantment",
                Text = "Whenever you put counters on a permanent or player, this enchantment deals that much damage to a target." };
            var recipient = new Card { VisualId = "counter-recipient", CombatId = "counter-recipient", Name = "Chandra, Torch of Defiance",
                Type = "Legendary Planeswalker — Chandra", Counters = ImmutableDictionary<string, int>.Empty.Add("Loyalty", 20) };
            var target = new Card { VisualId = "damage-target", CombatId = "damage-target", Name = "Grizzly Bears",
                Type = "Creature — Bear", Power = 7, Toughness = 8, Selectable = true, Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 5) };
            var effect = new PendingEffect { Id = "exact-trigger", Kind = "damage", Amount = 3, AmountBasis = "trigger",
                CounterChange = new() { Card = recipient with { Counters = ImmutableDictionary<string, int>.Empty.Add("Loyalty", 999) }, ActorPlayerId = 0,
                    Counters = [new("LOYALTY", "loyalty", 3)] } };
            var baseline = PresentationSmoke.Example();
            var state = baseline with { Id = "pending-value-journey", ActivePlayerId = 0, PriorityPlayerId = null,
                DecisionOwnerId = 0, ActionClass = "selection", Stack = [], Combat = null,
                Players = baseline.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                    Count = zone.Name == "Battlefield" ? player.Id == 0 ? 2 : player.Id == 1 ? 1 : 0 : zone.Count,
                    Cards = zone.Name == "Battlefield" ? player.Id == 0 ? [source, recipient] : player.Id == 1 ? [target] : [] : []
                }).ToImmutableArray() }).ToImmutableArray(),
                Decision = new Decision("", new DecisionContext(Message: "GENERIC_RULES_WITH_INTERNAL_ID_4242: deal that much damage. Choose a target.", Source: new DecisionSource(Card: source, ControllerId: 0)), new TargetSelectionPayload(new DecisionReply("Confirm", false), new DecisionReply("Cancel", true), new SelectionRange(1, 1), [1, 2, 3], effect, null)) };
            var engine = new InteractionScenarioEngine(); engine.Present(state);
            using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State) { Automated = true };
            app.Initialize(); app.SetTableStudy(study);
            var view = app.TestPort;
            double seconds = 0;
            Point pointer = new(20, 60);
            PresentationFrame frame = null!;
            Tick();
            GameTime Time() => new(TimeSpan.FromSeconds(seconds), TimeSpan.FromMilliseconds(10));
            PresentationFrame Tick(Point? next = null, ButtonState left = ButtonState.Released, double advance = .01)
            {
                pointer = next ?? pointer; seconds += advance;
                app.Update(Time(), new(pointer.X, pointer.Y, 0, left, ButtonState.Released, ButtonState.Released,
                    ButtonState.Released, ButtonState.Released), new(), new(1600, 900));
                return frame = app.ReadPresentation();
            }
            void Present(GameSnapshot value) { engine.Present(value); view.Accept(engine.State); Tick(new(20, 60)); }
            bool Number(string value) => frame.Drawing.Commands.OfType<DrawCommand.CenterText>().Any(text => text.Value == value && text.Size >= 1.4f);
            int Receipts() => frame.Drawing.Commands.OfType<DrawCommand.Text>().Count(text => text.Value == "+3 loyalty");
            bool RawRules() => frame.Observe().Text.Any(text => text.Value.Contains("GENERIC_RULES_WITH_INTERNAL_ID_4242", StringComparison.Ordinal));
            TableHit SourceHit() => frame.Hits.Single(hit => hit.Id == "targeting-source:" + view.Match!.Decision!.Id);
            Point TargetPoint()
            {
                var hit = frame.Cards.Single(card => card.Id == target.VisualId).Hit;
                for (int y = hit.Bounds.Top; y < hit.Bounds.Bottom; y++) for (int x = hit.Bounds.Left; x < hit.Bounds.Right; x++)
                    if (view.HitAt(new(x, y)) is { } actual && actual.Id == hit.Id && actual.Scope == hit.Scope) return new(x, y);
                throw new InvalidOperationException("The pending effect obscured its actual legal card target");
            }

            Tick();
            check(Number("3") && Receipts() >= 2 && !RawRules()
                && frame.Observe().Text.Any(text => text.Value == recipient.Name)
                && view.Match!.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == recipient.VisualId).Counters["Loyalty"] == 20
                && frame.Cards.Single(card => card.Id == target.VisualId).Card is { Power: 7, Toughness: 8 }
                && !frame.Hits.Any(hit => hit.Id is "instructions" or "decision-read-all") && frame.Hits.Any(hit => hit.Id == "cancel"),
                "The actual targeting screen distinguishes the authoritative three damage and +3 loyalty receipt from twenty settled counters, preserves current P/T and cancel, and omits raw internal prompt text");
            Tick(advance: 20);
            check(Number("3") && Receipts() >= 2, "The exact trigger's attached counter receipt remains available throughout an unanswered targeting choice after transient feedback expires");
            var header = SourceHit();
            Tick(header.Bounds.Center); Tick(header.Bounds.Center, ButtonState.Pressed); Tick(header.Bounds.Center);
            check(engine.Replies.Count == 0 && !view.Busy && frame.Hits.Any(hit => hit.Id == "stack-card-preview")
                && view.Overlay.Length == 0, "Hovering the targeting source uses the existing full-card preview and clicking that reference neither targets nor activates it");

            Present(engine.State with { Players = engine.State.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                Cards = zone.Cards.Where(card => card.VisualId != source.VisualId).ToImmutableArray()
            }).ToImmutableArray() }).ToImmutableArray() });
            check(Number("3") && SourceHit().Card?.Name == source.Name && frame.Observe().Text.Any(text => text.Value == recipient.Name),
                "A currently disclosed prompt source retains its effect and exact counter-origin context after leaving the visible battlefield");
            Present(engine.State with { Decision = engine.State.Decision! with { Payload = ((TargetSelectionPayload)engine.State.Decision.Payload) with { Effect = effect with { Amount = null, AmountBasis = "dynamic" } } } });
            check(Number("?") && !Number("0") && !Number("3") && Receipts() >= 2,
                "An unknown pending damage value stays unknown while its independently reported counter increment remains exact");

            Present(engine.State with { Stack = [new(source.Name, "GENERIC_RULES_WITH_INTERNAL_ID_4242", true, source) {
                    Id = "stacked-exact-trigger", ControllerId = 0, Effect = effect,
                    Targets = [new("card", target.VisualId, target.Name, 1)] }],
                PriorityPlayerId = 0, ActionClass = "priority", Decision = new Decision("", new DecisionContext(), new PriorityPayload(new DecisionReply("Confirm", true), null, CanAutoPass: false, QuietYieldToken: "", Abilities: [])) });
            check(Number("3") && Receipts() >= 2 && !RawRules() && frame.Observe().Text.Any(text => text.Value == recipient.Name),
                "The same exact amount and full counter-recipient context carry into the existing pending stack face without generic effect prose");

            if (study) {
                var focus = frame.Hits.Single(hit => hit.Id == "focus:1");
                Tick(focus.Bounds.Center, ButtonState.Pressed); Tick(focus.Bounds.Center);
                Tick(TargetPoint());
                var amountBounds = frame.Drawing.Commands.OfType<DrawCommand.CenterText>()
                    .Single(text => text.Value == "3" && text.Size >= 1.4f).Bounds;
                var confirm = frame.Hits.Single(hit => hit.Id == "confirm");
                check(view.StudyFocusOwner == 1 && view.ReferencePreviewCardId == target.VisualId
                    && view.ReferencePreviewBounds is { } preview
                    && !preview.Intersects(amountBounds) && view.StackRows.All(row => !preview.Intersects(row.Bounds))
                    && !preview.Intersects(confirm.Bounds) && view.HitAt(confirm.Bounds.Center)?.Id == "confirm"
                    && !preview.Intersects(frame.Cards.Single(card => card.Id == target.VisualId).Display.Bounds)
                    && !preview.Intersects(frame.Cards.Single(card => card.Id == recipient.VisualId).Display.Bounds)
                    && engine.Replies.Count == 0,
                    "Focused target inspection keeps the numeric pending effect, exact target and counter recipient visible and leaves the current priority answer exposed without submitting it");
                Tick(new(20, 60));
                var whole = frame.Hits.Single(hit => hit.Id == "study-focus-return");
                Tick(whole.Bounds.Center, ButtonState.Pressed); Tick(whole.Bounds.Center);
            }

            Present(state);
            var point = TargetPoint();
            Tick(point, ButtonState.Pressed);
            engine.Present(engine.State with { Decision = engine.State.Decision! with { Payload = ((TargetSelectionPayload)engine.State.Decision.Payload) with { Effect = effect with { Id = "replacement-trigger" } } } });
            view.Accept(engine.State);
            Tick(point);
            check(engine.Replies.Count == 0, "A target press from the previous decision cannot submit to a replacement trigger");
            Tick(new(20, 60)); point = TargetPoint();
            string decisionId = view.Match!.Decision!.Id;
            string key = view.Match.Players[1].Zone("Battlefield").Cards.Single().Key;
            Tick(point, ButtonState.Pressed); Tick(point);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (view.Busy && DateTime.UtcNow < deadline) { Tick(); Thread.Yield(); }
            check(!view.Busy && engine.Replies.Count == 1 && engine.Replies[0] is SelectCard reply
                && reply.DecisionId == decisionId && reply.Card == key,
                "Choosing the visible highlighted recipient submits exactly its current engine-issued card key and decision scope");
        }
        CheckOptionalTargetProgress(profile, check);
    }

    private static void CheckOptionalTargetProgress(string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "optional-target-progress"); Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(directory);
        JsonNode Wire(int selected, bool complete = true) {
            var wire = JsonNode.Parse("""
                {"id":"optional-target-progress","status":"playing","viewerId":0,"turn":3,"phaseKey":"MAIN1",
                 "activePlayerId":0,"decisionOwnerId":0,"actionClass":"selection","stack":[],
                 "players":[{"id":0,"name":"Actor","life":40,"mana":{},"zones":[]},
                            {"id":1,"name":"Targeted opponent","life":40,"mana":{},"zones":[]},
                            {"id":2,"name":"Other opponent","life":40,"mana":{},"zones":[]},
                            {"id":3,"name":"Last opponent","life":40,"mana":{},"zones":[]}],
                 "prompt":{"id":"optional-targets","kind":"input","task":"selection","purpose":"targets",
                   "inputType":"InputSelectTargets","message":"Choose up to two targets.","min":0,"max":2,
                   "ok":"Confirm","okEnabled":true,"cancel":"Cancel","cancelEnabled":true,"playerChoices":[0,1,2,3],
                   "sourceCardId":"optional-damage-source","sourceControllerId":0,"sourceZone":"Stack",
                   "sourceCard":{"name":"Crackle with Power","visualId":"optional-damage-source","combatId":"optional-damage-source",
                     "type":"Sorcery","manaCost":"{X}{X}{X}{R}{R}","text":"Deal five times X damage to each of up to X targets."},
                   "effect":{"id":"optional-damage","kind":"damage","amount":10,"amountBasis":"announced"}}}
                """)!;
            wire["prompt"]!["targetSelectionProgress"] = new JsonObject {
                ["selectedCount"] = selected,
                ["selectedPlayers"] = selected == 0 ? new JsonArray() : new JsonArray(1)
            };
            wire["prompt"]!["okEnabled"] = complete;
            return wire;
        }
        GameSnapshot Snapshot(int selected, bool complete = true) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(Wire(selected, complete)))!;
        var engine = new InteractionScenarioEngine(); engine.Present(Snapshot(0));
        var commands = new ConcurrentQueue<GameCommand>();
        int staged = 0;
        engine.ReplyOverride = command => {
            if (command is SelectPlayer { Player: 1 }) { staged = 1 - staged; engine.Present(Snapshot(staged)); }
            else if (command is CompleteSelection)
                engine.Present(engine.State with { Decision = null, DecisionOwnerId = null, ActionClass = "resolving" });
            else throw new InvalidOperationException("The optional-target journey received an unrequested command");
            commands.Enqueue(command); return Task.CompletedTask;
        };
        using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State) { Automated = true };
        app.Initialize(); app.SetTableStudy(true);
        var view = app.TestPort;
        PresentationFrame frame = null!; double seconds = 0;
        void Tick(Point? position = null, ButtonState left = ButtonState.Released) {
            seconds += .025; var pointer = position ?? new Point(20, 60);
            app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.025)),
                new(pointer.X, pointer.Y, 0, left, default, default, default, default), new(), new(1600, 900));
            frame = app.ReadPresentation();
        }
        Point Exposed(TableHit hit) {
            for (int y = hit.Bounds.Top; y < hit.Bounds.Bottom; y++) for (int x = hit.Bounds.Left; x < hit.Bounds.Right; x++)
                if (view.HitAt(new(x, y)) is { } current && current.Id == hit.Id && current.Scope == hit.Scope) return new(x, y);
            throw new InvalidOperationException("The current optional-target control has no exposed pointer target: " + hit.Id);
        }
        void Click(Point point) { Tick(point); Tick(point, ButtonState.Pressed); Tick(point); }
        void Await(Func<bool> ready, string description) {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!ready() && DateTime.UtcNow < deadline) { Tick(); Thread.Yield(); }
            check(ready(), description);
        }
        Tick(); Tick();
        File.WriteAllText(Path.Combine(directory, "initial-before-assert.json"), JsonSerializer.Serialize(new {
            wire = Wire(0), state = view.Match, frame = frame.Observe(),
            hits = frame.Hits.Select(hit => new { hit.Id, hit.Bounds, intent = hit.Intent.ToString(),
                centerHit = view.HitAt(hit.Bounds.Center)?.Id }).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        var source = frame.Hits.Single(hit => hit.Id == "targeting-source:" + view.Match!.Decision!.Id);
        check(source.Card is { VisualId: "optional-damage-source", Name: "Crackle with Power" }
                && frame.Drawing.Commands.OfType<DrawCommand.CenterText>().Any(text => text.Value == "10" && text.Size >= 1.4f),
            "An optional target decision keeps the actual disclosed spell portrait and authoritative ten damage visible");
        Click(Exposed(source));
        for (int repeat = 0; repeat < 8; repeat++) Tick();
        check(commands.IsEmpty && !view.Busy && view.Overlay.Length == 0,
            "Reading and polling the current optional-target source cannot select a recipient or complete the choice");
        check(frame.Observe().Text.Any(text => text.Value == "0 of up to 2 targets")
                && frame.Observe().Text.Any(text => text.Value == "Choose no targets")
                && !frame.Observe().Text.Any(text => text.Value == "Selected: Targeted opponent"),
            "Known zero optional targets is explicit and offers deliberate empty completion without inventing a selected recipient");
        var target = frame.Hits.Single(hit => hit.Id == "player:1");
        check(target.Intent is MatchIntent.UsePlayer { PlayerId: 1 }, "The visible recipient carries its current player-target intent");
        string firstDecision = view.Match!.Decision!.Id;
        Click(Exposed(target));
        Await(() => !view.Busy && commands.Count == 1 && view.Match!.Decision!.Id != firstDecision,
            "The first target click publishes one selected player without completing an optional two-target choice");
        for (int repeat = 0; repeat < 8; repeat++) Tick();
        var confirm = frame.Hits.Single(hit => hit.Id == "confirm");
        File.WriteAllText(Path.Combine(directory, "selected-subset-before-assert.json"), JsonSerializer.Serialize(new {
            wire = Wire(1), state = view.Match, frame = frame.Observe(), selectedCount = 1, selectedPlayers = new[] { 1 },
            source = new { source.Id, source.Card!.VisualId }, target = new { target.Id, intent = target.Intent.ToString() },
            confirm = new { confirm.Id, confirm.Scope, confirm.Bounds, centerHit = view.HitAt(confirm.Bounds.Center)?.Id },
            commands = commands.Select(command => new { kind = command.GetType().Name,
                decision = (command as DecisionCommand)?.DecisionId }).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        check(frame.Observe().Text.Any(text => text.Value == "1 of up to 2 targets")
                && frame.Observe().Text.Any(text => text.Value == "Selected: Targeted opponent")
                && frame.Observe().Text.Any(text => text.Value == "Confirm 1 target"),
            "The current optional-target presentation shows its updated selected count and exact selected player beside explicit completion");
        check(commands.Count == 1 && commands.Single() is SelectPlayer { Player: 1 }
                && confirm.Intent is MatchIntent.ConfirmDecision && view.HitAt(confirm.Bounds.Center)?.Id == confirm.Id,
            "The first target stays staged and the exposed current confirmation remains a deliberate separate gesture");
        string currentDecision = view.Match!.Decision!.Id;
        Click(confirm.Bounds.Center);
        Await(() => !view.Busy && commands.Count == 2, "The actual current confirmation center completes the selected subset once");
        check(commands.OfType<CompleteSelection>().Single().DecisionId == currentDecision,
            "Explicit target confirmation submits exactly one CompleteSelection for the current selected-subset scope");

        // Continue the same real pointer surface with an authored current input;
        // Java separately verifies the controller's actual target toggles.
        staged = 0; engine.Present(Snapshot(0)); view.Accept(engine.State); Tick(); Tick();
        Click(Exposed(frame.Hits.Single(hit => hit.Id == "player:1")));
        Await(() => !view.Busy && commands.Count == 3, "A subsequent current target input stages its own player choice");
        Click(Exposed(frame.Hits.Single(hit => hit.Id == "player:1")));
        Await(() => !view.Busy && commands.Count == 4, "Clicking the currently selected recipient publishes deselection");
        check(staged == 0 && commands.OfType<CompleteSelection>().Count() == 1
                && frame.Observe().Text.Any(text => text.Value == "0 of up to 2 targets")
                && frame.Observe().Text.Any(text => text.Value == "Choose no targets")
                && !frame.Observe().Text.Any(text => text.Value.StartsWith("Selected:", StringComparison.Ordinal)),
            "Deselection refreshes the current count and removes the selected-player cue without automatically completing");
        confirm = frame.Hits.Single(hit => hit.Id == "confirm");
        Tick(confirm.Bounds.Center); Tick(confirm.Bounds.Center, ButtonState.Pressed);
        engine.Present(Snapshot(0)); view.Accept(engine.State);
        Tick(confirm.Bounds.Center);
        check(commands.Count == 4 && !view.Busy,
            "A confirmation press from the previous publication cannot complete the replacement target scope");

        Click(Exposed(frame.Hits.Single(hit => hit.Id == "player:1")));
        Await(() => !view.Busy && commands.Count == 5, "The current recipient remains selectable after a stale confirmation is retired");
        confirm = frame.Hits.Single(hit => hit.Id == "confirm");
        string sameOwner = view.Match!.Decision!.Id;
        var unavailable = Snapshot(1, false);
        unavailable = unavailable with { Revision = view.Match.Revision,
            Decision = unavailable.Decision! with { Id = sameOwner } };
        engine.ObserveOverride = () => Task.FromResult<GameSnapshot?>(unavailable);
        Await(() => !view.Busy && view.Match!.Decision?.Id == sameOwner
                && view.Match.Decision.Payload is TargetSelectionPayload { Complete.Enabled: false },
            "The actual polled provider publishes disabled completion for the same target owner before authority is checked");
        File.WriteAllText(Path.Combine(directory, "same-owner-disabled-before-assert.json"), JsonSerializer.Serialize(new {
            wire = Wire(1, false), state = view.Match, frame = frame.Observe(),
            oldConfirm = new { confirm.Id, confirm.Scope, confirm.Bounds },
            hits = frame.Hits.Select(hit => new { hit.Id, hit.Bounds, intent = hit.Intent.ToString() }).ToArray(),
            commands = commands.Select(command => command.GetType().Name).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        check(view.Match.Decision!.Id == sameOwner && frame.Observe().Text.Any(text => text.Value == "1 of up to 2 targets")
                && frame.Observe().Text.Any(text => text.Value == "Selected: Targeted opponent")
                && !frame.Hits.Any(hit => hit.Id == "confirm") && !view.Activate(confirm) && commands.Count == 5,
            "A same-owner refresh retains descriptive progress while disabled completion revokes the old confirmation authority");
        engine.ObserveOverride = null;
        Await(() => !view.Busy && view.Match!.Decision?.Id == sameOwner
                && view.Match.Decision.Payload is TargetSelectionPayload { Complete.Enabled: true },
            "The actual polled provider restores enabled completion for the same target owner before a fresh gesture");
        confirm = frame.Hits.Single(hit => hit.Id == "confirm");
        Click(confirm.Bounds.Center);
        Await(() => !view.Busy && commands.Count == 6, "Re-enabled current completion accepts one fresh separate confirmation gesture");
        check(commands.OfType<CompleteSelection>().Count() == 2
                && commands.Last() is CompleteSelection final && final.DecisionId == sameOwner,
            "Reading, refreshing and deselecting add no completions; each current explicit confirmation contributes exactly one");
    }
}
