using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal static class TargetRestrictionPresentationChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) }) Journey(profile, size, check);
    }

    private static void Journey(string profile, Point size, Action<bool, string> check)
    {
        string path = Path.Combine(profile, "target-restriction-" + size.X); Directory.CreateDirectory(path);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(path);
        var engine = new InteractionScenarioEngine(); engine.Present(TargetRestrictionWireChecks.Decode(TargetRestrictionWireChecks.Wire()));
        using var run = new PresentationRun(TestTableServices.For(engine), path, engine.State, size);
        run.EnableLiveTableStudy();
        PresentationFrame frame = null!;
        void Tick(Point? at = null, ButtonState left = ButtonState.Released, Keys? key = null)
        {
            var physical = RenderViewport.Fit(size, run.View.DesignHeight).ToPhysical((at ?? new Point(-100, -100)).ToVector2() + new Vector2(.5f));
            frame = run.Tick(TimeSpan.FromSeconds(.05), new((int)physical.X, (int)physical.Y, 0, left, default, default, default, default),
                key is { } pressed ? new(pressed) : new());
        }
        Point CardPoint(string id)
        {
            var card = frame.Cards.Last(item => item.Card.VisualId == id && item.ChoiceSlot < 0);
            for (int y = card.Hit.Bounds.Top; y < card.Hit.Bounds.Bottom; y++)
            for (int x = card.Hit.Bounds.Left; x < card.Hit.Bounds.Right; x++) {
                // The device accepts physical integer pixels. At a scaled
                // viewport an exposed logical edge may round outside the face.
                var viewport = RenderViewport.Fit(size, run.View.DesignHeight);
                var physical = viewport.ToPhysical(new Vector2(x + .5f, y + .5f));
                var actual = viewport.ToLogical(new((int)physical.X, (int)physical.Y));
                if (run.View.HitAt(actual)?.Card?.VisualId == id) return new(x, y);
            }
            throw new InvalidOperationException("The current target card has no exposed input point: " + id);
        }
        void Click(Point point) { Tick(point, ButtonState.Pressed); Tick(point); Tick(point); }
        void Present(GameSnapshot state) { engine.Present(state); run.QueueObservation(engine.State); Tick(); Tick(); }
        string Text() => string.Join(" ", frame.Observe().Text.Select(item => item.Value));
        bool Popup() => frame.Hits.Any(hit => hit.Id == "target-restriction-help");
        Tick(); Tick();
        var original = engine.State;
        var blockedPoint = CardPoint("omnath"); Tick(blockedPoint); Tick(blockedPoint);
        bool hoverExplained = Popup() && Text().Contains("Can't target: Shroud", StringComparison.Ordinal)
                && Text().Contains("Shroud", StringComparison.Ordinal) && Text().Contains("Lightning Greaves", StringComparison.Ordinal)
                && engine.Replies.Count == 0 && ReferenceEquals(engine.State, original);
        if (!hoverExplained) File.WriteAllText(Path.Combine(path, "hover-first-failure.json"), System.Text.Json.JsonSerializer.Serialize(new {
            popup = Popup(), text = Text(), blockedPoint = new { blockedPoint.X, blockedPoint.Y },
            hit = new { run.View.HitAt(blockedPoint)?.Id, run.View.HitAt(blockedPoint)?.Zone }, currentHit = run.View.CurrentHit(run.View.HitAt(blockedPoint)!),
            replies = engine.Replies.Count, unchanged = ReferenceEquals(engine.State, original),
            run.View.Busy, run.View.NeedsRecovery, run.View.Overlay, run.View.ZoneName,
            run.View.StudyAttentionBounds, run.View.DecisionPanelBounds, run.View.HoverPreview,
            cards = frame.Cards.Select(card => new { card.Card.VisualId, card.Zone, card.ChoiceSlot, card.Focused, card.Display }),
            state = run.View.Match, hits = frame.Hits.Select(hit => new { hit.Id, hit.Bounds }),
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        check(hoverExplained,
            size.X + "px: hovering the actual blocked creature explains the engine's Shroud restriction and public granting Equipment without a command or state change");
        var bubble = frame.Hits.Single(hit => hit.Id == "target-restriction-help");
        Tick(bubble.Bounds.Center); Tick(bubble.Bounds.Center);
        check(Popup() && frame.Hits.Single(hit => hit.Id == "target-restriction-help").Bounds == bubble.Bounds && engine.Replies.Count == 0,
            size.X + "px: moving into the bounded read-only explanation preserves it without flicker or a click-through command");
        Click(CardPoint("omnath"));
        check(run.View.Overlay == "inspect" && engine.Replies.Count == 0
                && Text().Contains("Shroud", StringComparison.Ordinal) && Text().Contains("Lightning Greaves", StringComparison.Ordinal),
            size.X + "px: clicking a blocked card still opens its existing card inspector with the current reason, while never selecting it");
        Tick(key: Keys.Escape); Tick(); Tick();
        Tick(CardPoint("omnath"));
        var retiredBubble = frame.Hits.Single(hit => hit.Id == "target-restriction-help").Bounds.Center;
        var current = run.View.Match!;
        Present(current with { Decision = current.Decision! with { Id = "new-target-input", Payload = ((TargetSelectionPayload)current.Decision.Payload) with { TargetRestrictions = [] } } });
        Tick(retiredBubble);
        check(!Popup() && !Text().Contains("From Lightning Greaves", StringComparison.Ordinal) && engine.Replies.Count == 0,
            size.X + "px: replacing the current target input revokes its old hover explanation even while the pointer remains over its previous bubble");
        current = run.View.Match!;
        var legalKey = current.Players[1].Zone("Battlefield").Cards.Single().Key;
        Click(CardPoint("legal-target"));
        for (int index = 0; index < 20 && engine.Replies.Count == 0; index++) { Tick(); Thread.Yield(); }
        check(engine.Replies is [SelectCard selected] && selected.Card == legalKey
                && selected.GameId == current.Id && selected.DecisionId == current.Decision!.Id,
            size.X + "px: a legal neighbor still dispatches exactly one SelectCard with its actual current decision and action key");
        var annotated = TargetRestrictionWireChecks.Decode(TargetRestrictionWireChecks.Wire());
        foreach (var replacement in new[] {
            annotated with { Decision = annotated.Decision! with { Payload = new EntitySelectionPayload(new("Done", false), new("Cancel", true), []) } },
            annotated with { Decision = annotated.Decision! with { Payload = ((TargetSelectionPayload)annotated.Decision.Payload) with { TargetRestrictions = [] } } },
            annotated with { Players = annotated.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                Cards = zone.Cards.Select(card => card.VisualId == "greaves" ? card with { FaceDown = true, Name = "Face-down card" } : card).ToImmutableArray()
            }).ToImmutableArray() }).ToImmutableArray() }
        }) {
            Present(replacement); Tick(CardPoint("omnath"));
            check(!Popup() && !Text().Contains("From Lightning Greaves", StringComparison.Ordinal),
                size.X + "px: entity/cost inputs, old providers and a no-longer-public granting source cannot retain this target explanation");
        }
    }
}
