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
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal static class AttachmentStackChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        var stages = AttachmentScenario.Create();
        var states = stages.Select(stage => stage.Snapshot).Concat(new[] { 2, 3 }.Select(count =>
            stages[0].Snapshot with { Players = stages[0].Snapshot.Players.Take(count).ToImmutableArray() }));
        foreach (var state in states) foreach (int viewer in state.Players.Select(player => player.Id)) {
            var scene = Build(state with { ViewerId = viewer });
            var nodes = scene.BattlefieldDrawOrder;
            check(nodes.Count == state.Players.Sum(player => player.Zone("Battlefield").Cards.Length)
                && nodes.Select(node => node.Id).Distinct().Count() == nodes.Count,
                "Attachment packing retains every actual occurrence once, including the crowded field");
            foreach (var child in nodes.Where(node => node.Attachment != null)) {
                var host = nodes.Single(node => node.Id == child.Attachment!.RootId);
                var edge = child.Attachment!.Edge(child.Pose);
                var hostSeat = scene.Seat(host.Owner);
                var cloth = hostSeat.Geometry.PlaymatPlacement!.Value; cloth.Inflate(-PlayerPlaymat.HemInset, -PlayerPlaymat.HemInset);
                var lane = PlayerPlaymat.Lane(hostSeat.Index, host.StudyFamily!.Role, state.Players.Length == 2);
                check(host.StudyFamily!.Bounds.Contains(child.Pose.Bounds) && !edge.Intersects(host.Pose.Bounds)
                    && scene.Seat(child.Owner).BattlefieldCard(child.Id) == child
                    && TableSurfaceProjection.Bounds(cloth).Contains(host.StudyFamily.Bounds)
                    && TableSurfaceProjection.Bounds(lane).Contains(host.StudyFamily.Bounds)
                    && TableSurfaceProjectionChecks.InPlacement(cloth, child.Pose)
                    && !TableSurfaceProjectionChecks.Overlap(TableSurfaceProjection.Surface(PlayerPlaymat.Project(hostSeat.Index,
                        PlayerPlaymat.PersonalArea, state.Players.Length == 2)), host.StudyFamily.Surface),
                    $"Each complete attachment assembly fits its host's mat hem and owner-relative row while retaining its actual controller lookup: {child.Id}, viewer {viewer}, edge {edge}, host {host.Pose.Bounds}, assembly {host.StudyFamily.Bounds}");
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
        Settle();
        var phases = frame.Hits.Where(hit => hit.Intent is MatchIntent.TogglePhaseStop).Select(hit => hit.Bounds).ToArray();
        check(phases.Length > 0 && frame.Cards.Where(card => card.Zone == "Battlefield")
                .All(card => phases.All(bounds => !bounds.Intersects(card.Display.Bounds))),
            "The actual prepared phase-control targets stay clear of every displayed attachment and battlefield card before inspection");
        ClickEdge(AttachmentScenario.Boots);
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
                        controls = item.Geometry.TurnControls,
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

        foreach (var focus in new[] {
            (State: stages[4].Snapshot, Owner: 1, Crowded: true, Name: "crowded foreign-host focus"),
            (State: stages[0].Snapshot with { Players = stages[0].Snapshot.Players.Take(3).ToImmutableArray() }, Owner: 2, Crowded: false, Name: "three-player top focus"),
            (State: stages[0].Snapshot, Owner: 0, Crowded: false, Name: "four-player own focus")
        }) {
            var state = focus.State with { Players = focus.State.Players.Select(player => player.Id != focus.State.ViewerId ? player : player with {
                Mana = new[] { "W", "U", "B", "R", "G", "C" }.ToImmutableDictionary(symbol => symbol, _ => 1),
                States = [new("counter:energy", "Energy", "counter", 2, null, "Two public energy counters."),
                    new("counter:poison", "Poison", "counter", 3, null, "Three public poison counters.")]
            }).ToImmutableArray() };
            engine.Present(state); view.Accept(engine.State); Settle();
            var snapshot = view.Match;
            var canonical = frame.Scene!.BattlefieldDrawOrder.ToDictionary(card => card.Id, card => card.Pose);
            int replies = engine.Replies.Count;
            check(view.CounterBadge(state.ViewerId) is { Chips.Length: 2 }
                    && view.ManaPoolBadge(state.ViewerId) is { Parts.Length: 6 }
                    && (!focus.Crowded || canonical.Count == 480),
                $"{size.X}px {focus.Name}: the actual local counter chips and all six mana pips are present before focusing");

            view.OpenBattlefield(focus.Owner); Settle();
            var selected = frame.Scene!.BattlefieldDrawOrder
                .Where(card => (card.Attachment?.RootOwner ?? card.Owner) == focus.Owner).ToArray();
            var shown = frame.Cards.Where(card => card.Zone == "Battlefield").ToDictionary(card => card.Id);
            var phaseControls = frame.Hits.Where(hit => hit.Intent is MatchIntent.TogglePhaseStop).ToArray();
            var portrait = frame.Hits.Single(hit => hit.Id == "player:" + state.ViewerId);
            var badge = view.CounterBadge(state.ViewerId);
            var mana = view.ManaPoolBadge(state.ViewerId);
            check(view.StudyFocusOwner == focus.Owner && !view.BoardView.IsIdentity && selected.Length > 0
                    && phaseControls.Length > 0 && badge is { Chips.Length: 2 } && mana is { Parts.Length: 6 },
                $"{size.X}px {focus.Name}: actual focus retains the visible fixed portrait, phase controls, two counters and six mana pips");
            var protectedAreas = phaseControls.Select(hit => (Id: hit.Id, Bounds: hit.Bounds))
                .Append((Id: portrait.Id, Bounds: portrait.Bounds)).Append((Id: "local-counter-badge", Bounds: badge!.Bounds))
                .Concat(mana!.Parts.Select((part, index) => (Id: "local-mana-" + index, Bounds: part.Bounds))).ToArray();
            var collision = (from node in selected
                let body = new CardPaintGeometry(shown[node.Id].Display)
                from footprint in new[] { body.Backing.Bounds, body.Shadow.Bounds }
                from control in protectedAreas
                where footprint.Intersects(control.Bounds)
                select new { node.Id, Footprint = footprint, Control = control.Id, control.Bounds }).FirstOrDefault();
            check(collision == null,
                $"{size.X}px {focus.Name}: each displayed selected-seat card and attachment backing/shadow clears actual local phase, portrait, counter and mana footprints"
                + (collision == null ? "" : $"; {collision.Id} {collision.Footprint} intersects {collision.Control} {collision.Bounds}"));
            Unchanged("focus");

            if (focus.Crowded) {
                var node = selected.Single(card => card.Id == AttachmentScenario.Foreign);
                var card = shown[node.Id];
                var point = node.Attachment!.Edge(card.Display).Center;
                var hit = view.HitAt(point);
                check(node.Owner == state.ViewerId && node.Attachment.RootOwner == focus.Owner
                        && hit is { Card: { } hitCard } && hitCard.VisualId == node.Id && hitCard.Key == node.Card.Key,
                    $"{size.X}px: the crowded foreign Aura's exposed edge retains its exact current controller, card key and physical hit after focus; "
                        + $"point {point}, edge {node.Attachment.Edge(card.Display)}, pose {card.Display}, camera {view.BoardView}, actual {hit?.Id} {hit?.Card?.VisualId}");
                Tick(point, ButtonState.Pressed); Tick(point); Settle();
                check(frame.Cards.Single(card => card.Focused) is { } reader
                        && reader.Id == node.Id && reader.Request.Owner == node.Owner && reader.Card.Key == node.Card.Key
                        && reader.Display.Size.X >= 300 && MathF.Abs(MathHelper.WrapAngle(reader.Display.Angle)) < .01f,
                    $"{size.X}px: the focused crowded foreign Aura edge opens that same full upright reader");
                Unchanged("foreign attachment reading");
                Tick(point, ButtonState.Pressed); Tick(point); Settle();
                check(frame.Cards.All(card => !card.Focused),
                    $"{size.X}px: the same crowded foreign Aura anchor closes inspection without a game command");
            }

            var back = frame.Hits.Single(hit => hit.Id == "study-focus-return").Bounds.Center;
            Tick(back, ButtonState.Pressed); Tick(back); Settle();
            check(view.StudyFocusOwner == null && view.BoardView.IsIdentity,
                $"{size.X}px {focus.Name}: the actual Whole table control restores the overview");
            Unchanged("return");

            void Unchanged(string step) => check(ReferenceEquals(snapshot, view.Match) && engine.Replies.Count == replies && replies == 0
                    && frame.Scene!.BattlefieldDrawOrder.Count == canonical.Count
                    && frame.Scene.BattlefieldDrawOrder.All(card => canonical.TryGetValue(card.Id, out var pose) && pose == card.Pose),
                $"{size.X}px {focus.Name} {step}: navigation and inspection retain the exact snapshot, every canonical occurrence/pose and zero engine commands");
        }
    }
}
