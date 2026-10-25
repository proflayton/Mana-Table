using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal static class CardPlayChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var stages = ExperienceScenarios.Find("card-play.v1").Create();
        var cursor = new PublicActivityCursor();
        var effects = new SceneEffects();
        void Observe(GameSnapshot? before, GameSnapshot current, double time) => effects.Observe(cursor.Observe(before, current), time);
        Observe(null, stages[2].Snapshot, 0);
        check(effects.Cards.Count == 0, "Loading a retained cast as baseline does not replay a table's history");
        effects.Clear(); cursor.Clear();
        Observe(null, stages[0].Snapshot, 0);
        Observe(stages[0].Snapshot, stages[1].Snapshot, 1);
        check(effects.Cards.Single(cue => cue.Kind == CueKind.Play) is { VisualId: "play-forest", PlayerId: 0, Reveal: true },
            "A direct land play has one physical play journey from its reported player");
        Observe(stages[1].Snapshot, stages[2].Snapshot, 2);
        Observe(stages[2].Snapshot, stages[2].Snapshot with { Revision = 30 }, 2.3);
        check(effects.Cards.Single(cue => cue.Kind == CueKind.Play && cue.VisualId == "play-bears").Started == 2,
            "A cast event and its stack occurrence, repeated polls and new revisions do not restart the same play");
        Observe(stages[2].Snapshot, stages[3].Snapshot, 5);
        check(effects.Cards.Single(cue => cue.Kind == CueKind.Play) is { VisualId: "play-bears", Reveal: false },
            "A spell resolving after a long response window flies to the battlefield without replaying its cast reveal");
        Observe(stages[3].Snapshot, stages[4].Snapshot, 6);
        check(effects.Cards.Single(cue => cue.Kind == CueKind.Play && cue.VisualId == "play-ring").PlayerId == 1,
            "An opponent's hidden hand supplies no face until an actual public spell is reported");
        Observe(stages[4].Snapshot, stages[5].Snapshot, 9);
        Observe(stages[5].Snapshot, stages[6].Snapshot, 12);
        check(effects.Cards.All(cue => cue.Kind != CueKind.Play), "An ability keeps its source in place instead of announcing the permanent as another cast");
        Observe(stages[6].Snapshot, stages[4].Snapshot with { ViewerId = 1 }, 13);
        check(effects.Cards.Count == 0, "Switching viewers clears every historical play cue");
    }

    internal static void RunPresentation(string profile, Action<bool, string> check)
    {
        foreach (bool reduced in new[] { false, true }) foreach (bool study in new[] { false, true }) {
            string directory = Path.Combine(profile, $"card-play-{study}-{reduced}");
            Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = reduced, Sounds = false }.Save(directory);
            var stages = ExperienceScenarios.Find("card-play.v1").Create();
            var engine = new InteractionScenarioEngine(); engine.Present(stages[0].Snapshot);
            using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State) { Automated = true };
            app.Initialize(); app.SetTableStudy(study);
            var view = app.TestPort;
            double time = 0;
            PresentationFrame Tick(double elapsed = .016)
            {
                time += elapsed;
                app.Update(new(TimeSpan.FromSeconds(time), TimeSpan.FromSeconds(elapsed)),
                    new(-100, -100, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released,
                        ButtonState.Released, ButtonState.Released), new(), new(1600, 900));
                return app.ReadPresentation();
            }
            PresentationFrame Present(GameSnapshot state)
            { engine.Present(state); view.Accept(engine.State); return Tick(); }
            PresentationFrame Settle() { PresentationFrame result = null!; for (int n = 0; n < 100; n++) result = Tick(); return result; }
            var frame = Settle();
            var hand = frame.Cards.Single(card => card.Id == "play-forest");
            check(view.Activate(hand.Hit), "A land can be inspected in hand before its authorized play");
            frame = Settle();
            check(frame.Cards.Single(card => card.Id == "play-forest").Focused, "The production hand reader is open before the land leaves");
            frame = Present(stages[1].Snapshot);
            var land = frame.Cards.Single(card => card.Id == "play-forest");
            check(!land.Focused && (reduced ? !land.Playing && Near(land.Display, land.Rest) : land.Playing && !Near(land.Display, land.Rest)),
                "Closing a played hand reader revokes its old controls while the same physical land travels instead of snapping; reduced motion settles directly");
            frame = Settle();
            check(Near(frame.Cards.Single(card => card.Id == "play-forest").Display, frame.Cards.Single(card => card.Id == "play-forest").Rest),
                "The land finishes in its actual composed battlefield slot");
            frame = Present(stages[2].Snapshot);
            var spell = frame.Cards.Single(card => card.Id == "play-bears");
            check(spell.Zone == "Stack" && spell.Playing == !reduced && frame.Hits.Count(hit => hit.Id == spell.Hit.Id) == 1,
                "The compact pending panel composes one real moving spell occurrence and one canonical source hit");
            frame = Settle();
            spell = frame.Cards.Single(card => card.Id == "play-bears");
            check(!spell.Playing && Near(spell.Display, spell.Rest), "The physical spell remains on the actual stack after its reveal finishes");
            var pending = engine.State;
            frame = Present(pending with { Decision = new() { Task = DecisionTask.TargetSelection, SourceCard = pending.Stack[0].Card,
                Effect = new() { Kind = "damage", Amount = 1 }, Min = 1, Max = 1 } });
            check(frame.Cards.Any(card => card.Id == "play-bears" && card.Zone == "Stack"),
                "Choosing a response target retains the existing physical stack instead of discarding its motion");
            frame = Present(pending with { Decision = new() { Task = DecisionTask.Payment, SourceCard = new() { VisualId = "response-payment", Name = "Response spell" } } });
            check(frame.Cards.Count(card => card.Id == "play-bears") == 1,
                "A separate payment preview preserves the actual pending spell with its own physical identity");
            Present(pending); frame = Settle(); spell = frame.Cards.Single(card => card.Id == "play-bears");
            check(view.Activate(spell.Hit), "The moving spell's current canonical source remains inspectable");
            frame = Settle();
            check(frame.Cards.Single(card => card.Id == "play-bears").Focused && engine.Replies.Count == 0,
                "Reading the physical pending spell opens one noncommitting reader instead of casting or resolving it");
            view.Activate(frame.Cards.Single(card => card.Id == "play-bears").Hit); frame = Settle();
            spell = frame.Cards.Single(card => card.Id == "play-bears");
            if (study) { view.OpenBattlefield(0); frame = Settle(); spell = frame.Cards.Single(card => card.Id == "play-bears"); }
            // Let the cast cue expire before resolving, as with a real response window.
            Tick(4);
            frame = Present(stages[3].Snapshot);
            var arriving = frame.Cards.Single(card => card.Id == "play-bears");
            check(arriving.Zone == "Battlefield" && arriving.Playing == !reduced
                    && (reduced || Vector2.Distance(arriving.Display.Center, spell.Display.Center) < 200),
                "Resolution keeps the same physical pose and full printed face while moving from the stack to the current battlefield");
            frame = Settle();
            check(Near(frame.Cards.Single(card => card.Id == "play-bears").Display, frame.Cards.Single(card => card.Id == "play-bears").Rest),
                "The resolved spell lands at the current battlefield pose");
            frame = Present(stages[4].Snapshot);
            check(frame.Cards.Single(card => card.Id == "play-ring") is { Zone: "Stack", Card.FaceDown: false },
                "An opponent cast first becomes a visible physical card at its public stack occurrence");
            var hidden = stages[4].Snapshot with { Stack = [], Activity = [], Players = stages[4].Snapshot.Players.Select(player => player.Id != 1 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with { Cards = [], Count = 7 }).ToImmutableArray()
            }).ToImmutableArray() };
            frame = Present(hidden);
            check(frame.Cards.All(card => card.Id != "play-ring") && engine.Replies.Count == 0,
                "Losing public visibility immediately removes the flying face and every animation remains presentation only");
            Present(stages[5].Snapshot); Settle(); Tick(4);
            frame = Present(stages[6].Snapshot);
            check(frame.Cards.Count(card => card.Id == "play-ring") == 1
                    && frame.Cards.Single(card => card.Id == "play-ring") is { Zone: "Battlefield", Playing: false },
                "A pending ability uses its source reference without duplicating or replaying the battlefield permanent");
        }
    }

    private static bool Near(Pose first, Pose second) => Vector2.Distance(first.Center, second.Center) < 2
        && Vector2.Distance(first.Size, second.Size) < 2;
}
