using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal static class AttachmentStackChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        var stages = AttachmentScenario.Create();
        var states = stages.Select(stage => stage.Snapshot).Append(stages[0].Snapshot with { Players = stages[0].Snapshot.Players.Take(2).ToImmutableArray() });
        foreach (var state in states) foreach (int viewer in state.Players.Select(player => player.Id)) {
            var scene = Build(state with { ViewerId = viewer });
            var nodes = scene.BattlefieldDrawOrder;
            check(nodes.Count == state.Players.Sum(player => player.Zone("Battlefield").Cards.Length)
                && nodes.Select(node => node.Id).Distinct().Count() == nodes.Count,
                "Attachment packing retains every actual occurrence once, including the crowded field");
            foreach (var child in nodes.Where(node => node.Attachment != null)) {
                var host = nodes.Single(node => node.Id == child.Attachment!.RootId);
                var edge = child.Attachment!.Edge(child.Pose);
                check(host.StudyFamily!.Bounds.Contains(child.Pose.Bounds) && !edge.Intersects(host.Pose.Bounds)
                    && scene.Seat(child.Owner).BattlefieldCard(child.Id) == child
                    && scene.Seat(host.Owner).Geometry.PlaymatBounds!.Value.Contains(host.StudyFamily.Bounds),
                    $"Each attachment reserves space on its host's physical mat and retains its actual controller lookup: {child.Id}, viewer {viewer}, edge {edge}, host {host.Pose.Bounds}, assembly {host.StudyFamily.Bounds}");
                check(Array.IndexOf(nodes.ToArray(), child) < Array.IndexOf(nodes.ToArray(), host),
                    "Attachments paint beneath the host without obscuring its current stats");
            }
        }
        var ordinary = Build(stages[0].Snapshot);
        var foreign = ordinary.BattlefieldCard(0, AttachmentScenario.Foreign)!;
        var target = ordinary.BattlefieldDrawOrder.Single(node => node.Id == foreign.Attachment!.RootId);
        check(target.Owner == 1 && foreign.Owner == 0 && foreign.Attachment!.HostId == foreign.Card.AttachedTo!.Id,
            "A cross-controller Aura follows its exact host without changing its controller or action identity");
        var moved = Build(stages[1].Snapshot).BattlefieldCard(0, AttachmentScenario.Boots)!;
        var before = ordinary.BattlefieldCard(0, AttachmentScenario.Boots)!;
        check(moved.Id == before.Id && moved.Attachment!.HostId != before.Attachment!.HostId && moved.Pose != before.Pose,
            "Re-equipping moves the same occurrence to the new host");
        check(Build(stages[2].Snapshot).BattlefieldCard(0, AttachmentScenario.Boots)!.Attachment == null,
            "Detached Equipment returns to independent battlefield packing");
        CheckInvalidRelations(stages[0].Snapshot, check);
        CheckNested(stages[0].Snapshot, check);
        var distinctIds = stages[0].Snapshot with { Players = stages[0].Snapshot.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Cards = zone.Cards.Select(card => card with {
                CombatId = "combat:" + card.VisualId, AttachedTo = card.AttachedTo is { } target ? target with { Id = "combat:" + target.Id } : null
            }).ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray() };
        check(Build(distinctIds).BattlefieldCard(0, AttachmentScenario.Boots) is { Attachment.RootId: AttachmentScenario.Host, Card.AttachedTo.Id: "combat:" + AttachmentScenario.Host },
            "Public attachment references resolve to scene occurrences without confusing combat IDs, visual IDs or action keys");
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) }) Present(profile, stages, size, check);
    }

    private static TableScene Build(GameSnapshot state) => TableScene.Build(state, new(-100, -100), null, null, false,
        CardPresentation.IsResource, sharedTableStudy: true);

    private static void CheckInvalidRelations(GameSnapshot baseline, Action<bool, string> check)
    {
        var a = new Card { VisualId = "cycle-a", CombatId = "cycle-a", Name = "Aura A", Type = "Enchantment", AttachedTo = new("cycle-b", "Aura B", "Enchantment") };
        var b = a with { VisualId = "cycle-b", CombatId = "cycle-b", Name = "Aura B", AttachedTo = new("cycle-a", "Aura A", "Enchantment") };
        var absent = a with { VisualId = "missing", CombatId = "missing", AttachedTo = new("not-visible", "Unknown", "Permanent") };
        var state = baseline with { Players = baseline.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Cards = [a, b, absent], Count = 3 }).ToImmutableArray()
        }).ToImmutableArray() };
        check(new[] { a, b, absent }.All(card => Build(state).BattlefieldCard(0, card.VisualId)?.Attachment == null),
            "Cyclic or unavailable relationships leave real cards visible instead of inventing a host or dropping cards");
    }

    private static void CheckNested(GameSnapshot baseline, Action<bool, string> check)
    {
        var nested = new Card { VisualId = "nested-aura", CombatId = "nested-aura", Name = "Aura on Equipment", Type = "Enchantment — Aura",
            AttachedTo = new(AttachmentScenario.Boots, "Swiftfoot Boots", "Artifact — Equipment") };
        var state = baseline with { Players = baseline.Players.Select(player => player.Id != 1 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Cards = [.. zone.Cards, nested], Count = zone.Count + 1 }).ToImmutableArray()
        }).ToImmutableArray() };
        check(Build(state).BattlefieldCard(1, nested.VisualId)?.Attachment is { Depth: 2, HostId: AttachmentScenario.Boots, RootId: AttachmentScenario.Host, RootOwner: 0 },
            "An attachment on attached Equipment keeps its immediate host, root assembly and independent controller");
    }

    private static void Present(string profile, IReadOnlyList<ExperienceStage> stages, Point size, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "attachment-stack-" + size.X); Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(directory);
        var engine = new InteractionScenarioEngine(); engine.Present(stages[0].Snapshot);
        using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State) { Automated = true };
        app.Initialize(); app.SetTableStudy(true);
        var view = app.TestPort; PresentationFrame frame = null!; double time = 0;
        void Tick(Point? position = null, ButtonState left = ButtonState.Released)
        {
            time += .025;
            var physical = RenderViewport.Fit(size, view.DesignHeight).ToPhysical((position ?? new Point(-100, -100)).ToVector2() + new Vector2(.5f));
            app.Update(new(TimeSpan.FromSeconds(time), TimeSpan.FromSeconds(.025)),
                new((int)physical.X, (int)physical.Y, 0, left, default, default, default, default), new(), size);
            frame = app.ReadPresentation();
        }
        void Settle() { for (int n = 0; n < 10; n++) Tick(); }
        void ClickEdge(string id)
        {
            var node = frame.Scene!.BattlefieldCard(0, id)!;
            var pose = frame.Cards.Single(card => card.Id == id).Rest;
            var point = node.Attachment!.Edge(pose).Center;
            check(view.HitAt(point)?.Card?.VisualId == id, "The attachment's exposed edge owns the actual physical pointer target");
            Tick(point, ButtonState.Pressed); Tick(point); Settle();
        }
        Settle(); ClickEdge(AttachmentScenario.Boots);
        check(frame.Cards.Single(card => card.Id == AttachmentScenario.Boots).Focused && engine.Replies.Count == 0,
            "Clicking an attachment edge reads that exact card without activating or choosing it");
        check(view.AttachmentConnections.Count == 0, "A tucked attachment highlights its host without drawing a relationship line");
        check(view.BattlefieldLabels(AttachmentScenario.Jitte).Any(label => label.Kind == "counter" && label.Text == "2"),
            "Counters on an attached permanent remain on its exposed edge, independently of the host's current P/T: "
            + frame.Scene!.BattlefieldCard(0, AttachmentScenario.Jitte)!.Attachment!.Edge(frame.Cards.Single(card => card.Id == AttachmentScenario.Jitte).Rest));
        ClickEdge(AttachmentScenario.Aura);
        check(frame.Cards.Single(card => card.Focused).Id == AttachmentScenario.Aura && engine.Replies.Count == 0,
            "Reading can switch directly between exposed attachment edges without a modal");
        ClickEdge(AttachmentScenario.Aura);
        check(frame.Cards.All(card => !card.Focused), "Clicking the same exposed edge closes its reader");
        ClickEdge(AttachmentScenario.Foreign);
        check(frame.Cards.Single(card => card.Focused).Request.Owner == 0,
            "A foreign host does not transfer the attachment's input controller to that host");
        engine.Present(stages[2].Snapshot); view.Accept(engine.State); Settle();
        check(frame.Scene!.BattlefieldCard(0, AttachmentScenario.Boots)!.Attachment == null && engine.Replies.Count == 0,
            "The current snapshot removes obsolete stacking geometry without submitting a game command");
        foreach (int seat in new[] { 0, 1, 2 }) {
            var node = frame.Scene!.Seats.Single(item => item.Index == seat).Battlefield.SelectMany(cluster => cluster.Cards)
                .First(card => card.Attachment == null && card.GroupFront && card.Card.AttachedTo == null);
            var point = frame.Cards.Single(card => card.Id == node.Id).Rest.Center.ToPoint();
            if (view.HitAt(point)?.Card?.VisualId != node.Id) {
                var actual = view.HitAt(point);
                File.WriteAllText(Path.Combine(directory, "seat-facing-pointer-failure.json"), JsonSerializer.Serialize(new {
                    size, seat, point, state = view.Match, frame = frame.Observe(),
                    expected = new { node.Id, node.Owner, node.Zone, node.Pose, node.GroupFront, node.Attachment },
                    actual = actual == null ? null : new { actual.Id, actual.Scope, actual.Bounds,
                        cardId = actual.Card?.VisualId, actual.Zone, actual.PlayerId, intent = actual.Intent.ToString() },
                    cards = frame.Cards.Select(card => new { card.Id, card.Card.Key, card.Zone, card.Request.Owner,
                        card.Focused, card.Rest, card.Display, card.ReaderTarget, card.Request.ReadingAnchor,
                        hit = new { card.Hit.Id, card.Hit.Bounds, intent = card.Hit.Intent.ToString() } }).ToArray(),
                    seats = frame.Scene.Seats.Select(item => new { item.Index, item.PlayerId, item.Geometry.Hero,
                        controls = TurnControlLayout.For(item.Geometry.Hero),
                        cards = item.Battlefield.SelectMany(cluster => cluster.Cards)
                            .Select(card => new { card.Id, card.Owner, card.Pose, card.GroupFront, card.Attachment }).ToArray() }).ToArray(),
                    phases = frame.Hits.Where(hit => hit.Intent is MatchIntent.TogglePhaseStop)
                        .Select(hit => new { hit.Id, hit.Bounds, intent = hit.Intent.ToString() }).ToArray(),
                    paint = frame.Drawing.Commands.Select((command, index) => command switch {
                        DrawCommand.Surface surface => new { index, kind = surface.Texture == null ? "backing-or-shadow" : "artwork",
                            surface.Pose, bounds = surface.Pose.Bounds, strokeWidth = 0 },
                        DrawCommand.CardFrame border => new { index, kind = "card-frame", border.Pose,
                            bounds = border.Pose.Bounds, strokeWidth = border.Width },
                        _ => null
                    }).OfType<object>().ToArray(),
                    view.PrioritySeats, view.ManaJourney, sources = view.ManaSources.ToArray(), view.Overlay,
                    replies = engine.Replies.Count,
                    scope = "Failure-only copied current composition; no update, pump or gesture before the original assertion."
                }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            }
            check(view.HitAt(point)?.Card?.VisualId == node.Id,
                "A seat-facing battlefield face retains its exact physical pointer target at seat " + seat);
            Tick(point, ButtonState.Pressed); Tick(point); Settle();
            check(frame.Cards.Single(card => card.Focused) is { } reader && reader.Id == node.Id
                && MathF.Abs(MathHelper.WrapAngle(reader.Display.Angle)) < .01f && engine.Replies.Count == 0,
                "Reading a turned opponent card opens that exact occurrence upright without a game action at seat " + seat);
            Tick(point, ButtonState.Pressed); Tick(point); Settle();
            check(frame.Cards.All(card => !card.Focused), "The same battlefield anchor closes its upright reader at seat " + seat);
        }
    }
}
