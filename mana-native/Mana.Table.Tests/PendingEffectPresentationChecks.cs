using Mana.Contracts;
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
                Type = "Legendary Planeswalker — Chandra", Counters = new() { ["Loyalty"] = 20 } };
            var target = new Card { VisualId = "damage-target", CombatId = "damage-target", Name = "Grizzly Bears",
                Type = "Creature — Bear", Power = 7, Toughness = 8, Selectable = true, Counters = new() { ["+1/+1"] = 5 } };
            var effect = new PendingEffect { Id = "exact-trigger", Kind = "damage", Amount = 3, AmountBasis = "trigger",
                CounterChange = new() { Card = recipient with { Counters = new() { ["Loyalty"] = 999 } }, ActorPlayerId = 0,
                    Counters = [new("LOYALTY", "loyalty", 3)] } };
            var baseline = PresentationSmoke.Example();
            var state = baseline with { Id = "pending-value-journey", ActivePlayerId = 0, PriorityPlayerId = null,
                DecisionOwnerId = 0, ActionClass = "selection", Stack = [], Combat = null,
                Players = baseline.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                    Count = zone.Name == "Battlefield" ? player.Id == 0 ? 2 : player.Id == 1 ? 1 : 0 : zone.Count,
                    Cards = zone.Name == "Battlefield" ? player.Id == 0 ? [source, recipient] : player.Id == 1 ? [target] : [] : []
                }).ToArray() }).ToArray(),
                Decision = new() { Kind = "input", Purpose = DecisionPurpose.TargetSelection, Intent = DecisionIntent.Selection,
                    Message = "GENERIC_RULES_WITH_INTERNAL_ID_4242: deal that much damage. Choose a target.",
                    SourceCard = source, SourceControllerId = 0, Effect = effect, Min = 1, Max = 1,
                    PlayerChoices = [1, 2, 3], CancelEnabled = true, Cancel = "Cancel" } };
            var engine = new InteractionScenarioEngine(); engine.Present(state);
            using var app = new TableApplication(engine, directory, initialState: engine.State) { Automated = true };
            app.Initialize(); app.SetTableStudy(study);
            var view = app.TestPort;
            double seconds = 0;
            Point pointer = new(20, 60);
            PresentationFrame frame = app.ComposePresentation(Time());
            GameTime Time() => new(TimeSpan.FromSeconds(seconds), TimeSpan.FromMilliseconds(10));
            PresentationFrame Tick(Point? next = null, ButtonState left = ButtonState.Released, double advance = .01)
            {
                pointer = next ?? pointer; seconds += advance;
                app.Update(Time(), new(pointer.X, pointer.Y, 0, left, ButtonState.Released, ButtonState.Released,
                    ButtonState.Released, ButtonState.Released), new(), new(1600, 900));
                return frame = app.ComposePresentation(Time());
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
                Cards = zone.Cards.Where(card => card.VisualId != source.VisualId).ToArray()
            }).ToArray() }).ToArray() });
            check(Number("3") && SourceHit().Card?.Name == source.Name && frame.Observe().Text.Any(text => text.Value == recipient.Name),
                "A currently disclosed prompt source retains its effect and exact counter-origin context after leaving the visible battlefield");
            Present(engine.State with { Decision = engine.State.Decision! with { Effect = effect with { Amount = null, AmountBasis = "dynamic" } } });
            check(Number("?") && !Number("0") && !Number("3") && Receipts() >= 2,
                "An unknown pending damage value stays unknown while its independently reported counter increment remains exact");

            Present(engine.State with { Stack = [new(source.Name, "GENERIC_RULES_WITH_INTERNAL_ID_4242", true, source) {
                    Id = "stacked-exact-trigger", ControllerId = 0, Effect = effect,
                    Targets = [new("card", target.VisualId, target.Name, 1)] }],
                PriorityPlayerId = 0, ActionClass = "priority", Decision = new() { Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = true } });
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
            engine.Present(engine.State with { Decision = engine.State.Decision! with { Effect = effect with { Id = "replacement-trigger" } } });
            view.Accept(engine.State);
            Tick(point);
            check(engine.Replies.Count == 0, "A target press from the previous decision cannot submit to a replacement trigger");
            Tick(new(20, 60)); point = TargetPoint();
            string decisionId = view.Match!.Decision!.Id;
            string key = view.Match.Players[1].Zone("Battlefield").Cards.Single().Key;
            Tick(point, ButtonState.Pressed); Tick(point);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (view.Busy && DateTime.UtcNow < deadline) { Tick(); Thread.Yield(); }
            check(!view.Busy && engine.Replies.Count == 1 && engine.Replies[0] is { Action: ReplyAction.SelectCard } reply
                && reply.DecisionId == decisionId && reply.Card == key,
                "Choosing the visible highlighted recipient submits exactly its current engine-issued card key and decision scope");
        }
    }
}
