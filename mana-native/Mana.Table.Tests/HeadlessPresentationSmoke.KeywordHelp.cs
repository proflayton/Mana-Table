using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private const string KeywordSourceId = "keyword-study-source";
    private static readonly PublicState KeywordFlying = new("flying-current", "Flying", "ability", 1, null,
        "This creature can be blocked only by creatures with flying or reach.", "flying");
    private static readonly PublicState KeywordWardTwo = new("ward-two-current", "Ward {2}", "ability", 1, null,
        "Whenever this creature becomes the target of a spell or ability an opponent controls, counter it unless that player pays two generic mana.", "ward");
    private static readonly PublicState KeywordWardThree = new("ward-three-current", "Ward {3}", "ability", 1, null,
        "Whenever this creature becomes the target of a spell or ability an opponent controls, counter it unless that player pays three generic mana.", "ward");
    private static readonly ImmutableArray<PublicState> KeywordDescriptors = [KeywordFlying, KeywordWardTwo, KeywordWardThree];
    private static readonly Point KeywordNeutral = new(20, 60);

    // Actual production composition and pointer input over explicitly supplied
    // public descriptors. These checks neither simulate Magic nor approve UX.
    private static void CheckKeywordHelp(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        CheckKeywordDwellAndTiming(profile, check, trace, performance);
        CheckKeywordFirstAutoHover(profile, check, trace, performance);
        CheckKeywordRowsAndRules(profile, check, trace, performance);
        CheckKeywordDisclosure(profile, check, trace, performance);
        CheckKeywordDismissal(profile, check, trace, performance);
        foreach (bool missing in new[] { false, true }) {
            using var run = new Runner(profile, missing ? "keyword-help-missing-states" : "keyword-help-empty-states",
                KeywordFixture(missing ? null : ImmutableArray<PublicState>.Empty), trace, performance);
            OpenKeywordReader(run);
            check(!KeywordTargets(run).Any(),
                "Missing or authoritatively empty states cannot turn legacy Flying hints into a guessed keyword explanation");
            run.ClickHit("read-card-rules"); run.Settle("unexplained-rules-face");
            check(!KeywordTargets(run).Any() && KeywordPopup(run) == null
                    && run.Engine.Replies.Count == 0 && run.View.Selections.Count == 0,
                "Printed keyword prose without a supplied descriptor stays readable without fabricated hover help or a selection");
        }
    }

    private static void CheckKeywordFirstAutoHover(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        using var run = new Runner(profile, "keyword-help-first-auto-hover", KeywordFixture(KeywordDescriptors, autoPass: true),
            trace, performance, reducedMotion: false);
        run.Tick("initial"); run.View.SetTableStudy(true);
        for (int frame = 0; frame < 45; frame++) run.Tick("settle-before-auto-hover");
        var at = run.PointFor(KeywordTarget(run, KeywordFlying));
        run.View.SetAutomatic(true);
        run.Input(at, label: "first-auto-input-enters-keyword");
        check(!run.View.Busy && run.Engine.Replies.Count == 0
                && run.View.DecisionDock?.PauseReason == DecisionDockPauseReason.Inspection && KeywordPopup(run) == null,
            "The first input tick entering a compact keyword stops an otherwise legal Auto pass before rendering or completing its dwell");
        for (int frame = 0; frame < 12; frame++) { run.Input(at, label: "first-hover-remains-armed"); Thread.Yield(); }
        check(!run.View.Busy && run.Engine.Replies.Count == 0 && KeywordPopup(run) == null,
            "Entering keyword help cannot leave a hidden automatic command scheduled behind its deliberate hover delay");
        run.Input(KeywordNeutral, label: "leave-first-auto-hover");
        run.Until(() => run.Engine.Replies.Count == 1, "first-hover-control-still-permits-ordinary-auto");
        check(run.Engine.Replies is [AutoPassPriority],
            "The first-hover negative control uses a genuinely passable decision whose normal Auto resumes after leaving the keyword");
    }

    private static void CheckKeywordDwellAndTiming(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        using var run = new Runner(profile, "keyword-help-dwell-auto-clock", KeywordFixture(KeywordDescriptors, autoPass: true),
            trace, performance, reducedMotion: false);
        run.Tick("initial"); run.View.SetTableStudy(true);
        for (int frame = 0; frame < 45; frame++) run.Tick("settle-normal-motion");
        var receivedClock = new DecisionTempoFrame(new(TempoPreset.Standard, TempoExpiry.Pass, 60, 60, "keyword-help-policy"),
            [new(0, "keyword-help-host-clock", TempoPhase.Thinking, 12_000, 60_000, 0)]);
        run.Present(run.Engine.State with { Tempo = receivedClock }); run.Tick("host-clock-issued");
        var at = run.PointFor(KeywordTarget(run, KeywordFlying));
        int initialSeconds = KeywordClockNumber(run);
        run.Input(at, label: "arm-compact-keyword"); run.Input(at, label: "keep-compact-keyword-armed");
        check(KeywordPopup(run) == null && !run.Frame.Cards.Any(card => card.Focused),
            "A compact battlefield keyword begins its deliberate hover dwell without opening a card or immediately flashing a popup");
        run.View.SetAutomatic(true);
        for (int frame = 0; frame < 15; frame++) run.Input(at, label: "before-keyword-dwell");
        check(KeywordPopup(run) == null && run.View.DecisionDock?.PauseReason == DecisionDockPauseReason.Inspection
                && run.Engine.Replies.Count == 0,
            "An armed keyword pauses an otherwise authorized automatic pass through the existing Inspection gate before its 350ms dwell completes");
        for (int frame = 0; frame < 12; frame++) run.Input(at, label: "complete-keyword-dwell");
        check(KeywordPopup(run) != null && KeywordPopupText(run).Contains(KeywordFlying.Detail, StringComparison.Ordinal)
                && run.Engine.Replies.Count == 0 && run.View.Selections.Count == 0,
            "Holding the actual compact keyword beyond 350ms renders its entire engine reminder without selecting or submitting a command");
        var popup = KeywordPopup(run)!;
        var popupPoint = run.PointFor(popup);
        run.Input(popupPoint, label: "move-into-keyword-explanation");
        check(KeywordPopup(run)?.Bounds == popup.Bounds && KeywordPopupText(run).Contains(KeywordFlying.Detail, StringComparison.Ordinal),
            "Moving from the keyword into its explanation retains the same popup and complete reminder");
        run.ClickHit("keyword-help-popup");
        for (int frame = 0; frame < 120; frame++) run.Input(popupPoint, label: "read-while-host-clock-runs");
        int remainingSeconds = KeywordClockNumber(run);
        check(initialSeconds > remainingSeconds && remainingSeconds > 0
                && ReferenceEquals(run.View.Match!.Tempo, receivedClock)
                && KeywordPopup(run) != null && run.Engine.Replies.Count == 0 && run.View.Selections.Count == 0,
            "Reading and clicking the popup shield preserves the received host clock while its visible countdown continues and Auto stays paused");
        run.Input(KeywordNeutral, label: "leave-keyword-reading-region");
        run.Until(() => run.Engine.Replies.Count == 1, "ordinary-auto-resumes-after-keyword-reading");
        for (int frame = 0; frame < 15; frame++) run.Input(KeywordNeutral, label: "keyword-transit-grace-ended");
        check(KeywordPopup(run) == null && run.Engine.Replies is [AutoPassPriority]
                && !run.Frame.Cards.Any(card => card.Focused) && run.View.Selections.Count == 0,
            "Leaving the keyword and popup resumes the existing authorized Auto pass, proving that reading alone held it back");
    }

    private static void CheckKeywordRowsAndRules(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        using var run = new Runner(profile, "keyword-help-reader-and-rules", KeywordFixture(KeywordDescriptors), trace, performance);
        OpenKeywordReader(run);
        string decision = run.View.Match!.Decision!.Id;
        run.Input(run.PointFor(KeywordTarget(run, KeywordFlying)), label: "reduced-motion-keyword-row");
        check(KeywordPopup(run) != null && KeywordPopupText(run).Contains(KeywordFlying.Detail, StringComparison.Ordinal),
            "Reduced motion immediately exposes a pinned battlefield keyword row's complete reminder without a reveal animation");
        var flyingPoint = run.PointFor(KeywordTarget(run, KeywordFlying));
        run.Input(flyingPoint, keys: new(Keys.Escape), label: "dismiss-keyword-with-escape");
        for (int frame = 0; frame < 24; frame++) run.Input(flyingPoint, label: "stay-on-dismissed-keyword");
        check(KeywordPopup(run) == null && run.Frame.Cards.Single(card => card.Id == KeywordSourceId).Focused
                && run.Engine.Replies.Count == 0,
            "Escape dismisses only keyword help and stationary hover cannot immediately reopen it or close the pinned card");
        HoverKeyword(run, KeywordFlying);
        check(KeywordPopupText(run).Contains(KeywordFlying.Detail, StringComparison.Ordinal),
            "Moving away and back deliberately reopens an Escape-dismissed keyword through its current target");
        foreach (var descriptor in new[] { KeywordWardTwo, KeywordWardThree }) {
            run.Input(KeywordNeutral, label: "leave-previous-ward");
            var hit = KeywordTarget(run, descriptor);
            run.Input(run.PointFor(hit), label: "read-exact-ward-instance");
            var other = descriptor == KeywordWardTwo ? KeywordWardThree : KeywordWardTwo;
            check(KeywordPopupText(run).Contains(descriptor.Detail, StringComparison.Ordinal)
                    && !KeywordPopupText(run).Contains(other.Detail, StringComparison.Ordinal),
                "Separate Ward costs retain their exact engine reminder rather than merging by the shared ward key");
        }
        run.Input(KeywordNeutral, label: "leave-state-column");
        run.ClickHit("read-card-rules"); run.Settle("flipped-keyword-rules");
        var reader = run.Frame.Cards.Single(card => card.Id == KeywordSourceId && card.Focused).Display.Bounds;
        var two = KeywordTarget(run, KeywordWardTwo, insideReader: true);
        var three = KeywordTarget(run, KeywordWardThree, insideReader: true);
        check(two.Id != three.Id && reader.Contains(two.Bounds.Center) && reader.Contains(three.Bounds.Center)
                && !two.Bounds.IsEmpty && !three.Bounds.IsEmpty,
            "The measured Rules face exposes distinct current keyword text spans for its two parameterized Ward instances");
        var readerPermissions = run.Frame.Hits.Where(hit => hit.Id is "read-card-rules" or "read-card-action")
            .ToDictionary(hit => hit.Id, hit => hit.Intent, StringComparer.Ordinal);
        foreach (var descriptor in new[] { KeywordFlying, KeywordWardTwo, KeywordWardThree }) {
            run.Input(KeywordNeutral, label: "leave-previous-rules-keyword");
            run.Input(run.PointFor(KeywordTarget(run, descriptor, insideReader: true)), label: "hover-measured-keyword-span");
            check(KeywordPopupText(run).Contains(descriptor.Detail, StringComparison.Ordinal),
                "Hovering a measured Rules-face keyword reveals its supplied reminder through the same reader");
            var popup = KeywordPopup(run)!;
            var popupPaint = KeywordPanelPaint(run, popup);
            var controls = run.Frame.Hits.Where(hit => hit.Id is "read-card-rules" or "read-card-action").ToArray();
            var paintedControls = controls.Select(hit => new { hit.Id, hit.Bounds, paint = KeywordPanelPaint(run, hit),
                current = run.View.CurrentHit(hit), ownsCenter = run.View.HitAt(hit.Bounds.Center)?.Id == hit.Id }).ToArray();
            trace.Add(new { scenario = "keyword-help-reader-controls", descriptor.Id, popup.Bounds, popupPaint,
                controls = paintedControls, decision = run.View.Match!.Decision!.Id, replies = run.Engine.Replies.Count });
            check(popupPaint is { } paintedPopup && controls.Length == 2 && readerPermissions.Count == 2
                    && paintedControls.All(control => control.paint is { } paint && !paintedPopup.Intersects(paint)
                        && control.current && control.ownsCenter)
                    && controls.All(hit => readerPermissions.TryGetValue(hit.Id, out var intent) && Equals(intent, hit.Intent))
                    && run.View.Match!.Decision!.Id == decision && run.Engine.Replies.Count == 0 && run.View.Selections.Count == 0,
                "Rules keyword help clears the actual Artwork and reader-action panels including both shadows while retaining their current permissions");
        }
        check(run.View.Match.Decision!.Id == decision && run.Engine.Replies.Count == 0 && run.View.Selections.Count == 0
                && run.Frame.Cards.Single(card => card.Id == KeywordSourceId).Focused,
            "Reading compact state, exact Ward rows and Rules text preserves the same decision and pinned card without a game command or selection");
    }

    private static void CheckKeywordDisclosure(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        using var run = new Runner(profile, "keyword-help-current-disclosure", KeywordFixture(KeywordDescriptors), trace, performance);
        OpenKeywordReader(run); HoverKeyword(run, KeywordFlying);
        string beforeDecision = run.View.Match!.Decision!.Id;
        string beforeKey = run.View.Match.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == KeywordSourceId).Key;
        var refreshPoint = run.PointFor(KeywordPopup(run)!);
        run.Present(run.Engine.State);
        run.Input(refreshPoint, label: "same-descriptors-new-decision"); run.Settle("same-descriptors-still-readable");
        check(run.View.Match!.Decision!.Id != beforeDecision
                && run.View.Match.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == KeywordSourceId).Key != beforeKey
                && KeywordPopupText(run).Contains(KeywordFlying.Detail, StringComparison.Ordinal)
                && run.Engine.Replies.Count == 0 && run.View.Selections.Count == 0,
            "An unrelated decision and action-key refresh retains the same public keyword explanation without borrowing stale command authority");
        var oldTarget = KeywordTarget(run, KeywordFlying);
        var oldPopupPoint = run.PointFor(KeywordPopup(run)!);
        var replacement = KeywordFlying with { Id = "flying-replacement", Detail = "This replacement reminder belongs to the newly published public descriptor." };
        run.Present(ChangeKeywordCard(run.Engine.State, card => card with { States = [replacement, KeywordWardTwo, KeywordWardThree] }));
        run.Input(oldPopupPoint, label: "descriptor-replaced-while-reading"); run.Settle("old-descriptor-retired");
        check(KeywordPopup(run) == null && !KeywordTargets(run).Any(hit => hit.Id.Contains(KeywordFlying.Id, StringComparison.Ordinal))
                && !run.View.CurrentHit(oldTarget) && !string.Join(" ", Text(run.Frame)).Contains(KeywordFlying.Detail, StringComparison.Ordinal),
            "Replacing a descriptor retires its old reminder, hover target and retained popup rather than borrowing the replacement's shared keyword key");
        HoverKeyword(run, replacement);
        check(KeywordPopupText(run).Contains(replacement.Detail, StringComparison.Ordinal),
            "The replacement descriptor remains independently readable through its current public identity");
        run.Present(ChangeKeywordCard(run.Engine.State, card => new Card {
            VisualId = card.VisualId, CombatId = card.CombatId, FaceDown = true
        }));
        run.Tick("concealed-keyword-source"); run.Settle("concealed-help-cleared");
        check(KeywordPopup(run) == null && !KeywordTargets(run).Any() && !run.Frame.Cards.Any(card => card.Focused)
                && !string.Join(" ", Text(run.Frame)).Contains(replacement.Detail, StringComparison.Ordinal)
                && run.Engine.Replies.Count == 0 && run.View.Selections.Count == 0,
            "Losing disclosure closes the keyword helper and reader without retaining concealed reminder text or submitting a choice");
    }

    private static void CheckKeywordDismissal(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        foreach (string reason in new[] { "reader-close", "overlay", "source-removed", "foreground-loss", "drag" }) {
            using var run = new Runner(profile, "keyword-help-dismiss-" + reason, KeywordFixture(KeywordDescriptors), trace, performance);
            OpenKeywordReader(run); HoverKeyword(run, KeywordFlying);
            switch (reason) {
                case "reader-close":
                    run.Input(KeywordNeutral); run.ClickCard(KeywordSourceId); run.Settle("reader-closed"); break;
                case "overlay":
                    run.ClickHit("settings"); run.Settle("settings-obscures-keywords");
                    check(run.View.Overlay == "settings", "The keyword dismissal control actually opens the production settings overlay");
                    break;
                case "source-removed":
                    run.Present(run.Engine.State with { Players = run.Engine.State.Players.Select(player => player.Id != 0 ? player : player with {
                        Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Count = 0, Cards = [] }).ToImmutableArray()
                    }).ToImmutableArray() });
                    run.Tick("source-left-public-battlefield"); run.Settle("removed-source-help-cleared"); break;
                case "foreground-loss":
                    run.Deactivate(); run.Input(KeywordNeutral, active: false, label: "inactive-keyword-reader"); break;
                case "drag":
                    var hand = run.Frame.Cards.Single(card => card.Id == "hand-a");
                    var start = run.PointFor(run.Hit(hand.Hit.Id));
                    run.Input(start, left: ButtonState.Pressed, label: "press-hand-before-drag");
                    run.Input(start + new Point(90, -30), left: ButtonState.Pressed, label: "drag-with-keyword-reader-open"); break;
            }
            check(KeywordPopup(run) == null && run.Engine.Replies.Count == 0 && run.View.Selections.Count == 0,
                "Keyword help dismisses on " + reason + " without selecting, casting, passing or answering the current decision");
            if (reason == "overlay") check(!KeywordTargets(run).Any(),
                "An obscuring overlay removes underlying keyword targets as well as their visible popup");
        }
    }

    private static GameSnapshot KeywordFixture(ImmutableArray<PublicState>? states, bool autoPass = false)
    {
        var state = Example();
        return state with { PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority",
            Decision = state.Decision! with { Payload = ((PriorityPayload)state.Decision!.Payload) with { CanAutoPass = autoPass } },
            Players = state.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Cards = zone.Cards.Select(card => card with {
                    VisualId = KeywordSourceId, CombatId = KeywordSourceId, Name = "Wisp", Type = "Creature",
                    Text = "Flying\nWard {2}\nWard {3}", States = states, CombatKeywords = ["flying"]
                }).ToImmutableArray() }).ToImmutableArray()
            }).ToImmutableArray()
        };
    }

    private static GameSnapshot ChangeKeywordCard(GameSnapshot state, Func<Card, Card> change) => state with {
        Players = state.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Cards = zone.Cards.Select(card => card.VisualId == KeywordSourceId ? change(card) : card).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray()
    };

    private static void OpenKeywordReader(Runner run)
    {
        run.Tick("keyword-fixture"); run.View.SetTableStudy(true); run.Settle("keyword-table-study");
        run.ClickCard(KeywordSourceId); run.Settle("keyword-reader-open");
    }

    private static IEnumerable<TableHit> KeywordTargets(Runner run) => run.Frame.Hits.Where(hit => hit.Id.StartsWith("keyword-help:", StringComparison.Ordinal));
    private static TableHit? KeywordPopup(Runner run) => run.Frame.Hits.SingleOrDefault(hit => hit.Id == "keyword-help-popup");
    private static TableHit KeywordTarget(Runner run, PublicState descriptor, bool insideReader = false)
    {
        var entry = run.Frame.Cards.Single(card => card.Id == KeywordSourceId);
        var reader = entry.Display.Bounds;
        var target = KeywordTargets(run).LastOrDefault(hit => hit.Id.Contains(descriptor.Id, StringComparison.Ordinal)
            && (!insideReader || reader.Contains(hit.Bounds.Center)) && run.TryPointFor(hit, out _));
        if (target != null) return target;
        object Bounds(Microsoft.Xna.Framework.Rectangle bounds) => new { bounds.X, bounds.Y, bounds.Width, bounds.Height };
        var diagnostic = new {
            descriptor, insideReader, entry.Id, entry.Focused, entry.Zone, display = Bounds(reader),
            entry.Card.Name, entry.Card.States,
            labels = run.View.BattlefieldLabels(KeywordSourceId).Select(label => new {
                label.Kind, label.Text, bounds = Bounds(label.Bounds), label.MeasuredWidth, label.AvailableWidth
            }),
            targets = KeywordTargets(run).Select(hit => new { hit.Id, hit.Scope, bounds = Bounds(hit.Bounds), exposed = run.TryPointFor(hit, out _) }),
            sourceHits = run.Frame.Hits.Where(hit => hit.Card?.VisualId == KeywordSourceId).Select(hit => new {
                hit.Id, hit.Scope, bounds = Bounds(hit.Bounds), atCenter = run.View.HitAt(hit.Bounds.Center)?.Id
            })
        };
        string path = Path.Combine(run.View.Profile, "keyword-target-missing.json");
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(output, diagnostic, new JsonSerializerOptions { WriteIndented = true });
        throw new InvalidOperationException("Missing exposed current keyword help target: " + descriptor.Id + (insideReader ? " inside Rules face" : ""));
    }

    private static void HoverKeyword(Runner run, PublicState descriptor)
    {
        run.Input(KeywordNeutral, label: "leave-prior-keyword");
        run.Input(run.PointFor(KeywordTarget(run, descriptor)), label: "hover-current-keyword");
    }

    private static string KeywordPopupText(Runner run)
    {
        if (KeywordPopup(run) is not { } popup) return "";
        var parts = run.Frame.Drawing.Commands.Select(command => command switch {
            DrawCommand.Text text when popup.Bounds.Contains(new Point(text.X, text.Y)) => text.Value,
            DrawCommand.CenterText text when popup.Bounds.Intersects(text.Bounds) => text.Value,
            _ => null
        }).Where(value => value != null);
        return Regex.Replace(string.Join(" ", parts), @"\s+", " ").Trim();
    }

    private static int KeywordClockNumber(Runner run) => run.Frame.Drawing.Commands.OfType<DrawCommand.CenterText>()
        .Select(text => int.TryParse(text.Value, out int seconds) && seconds is > 0 and <= 12 ? seconds : -1).DefaultIfEmpty(-1).Max();

    private static Rectangle? KeywordPanelPaint(Runner run, TableHit hit)
    {
        var panel = run.Frame.Drawing.Commands.OfType<DrawCommand.Panel>().FirstOrDefault(command => command.Bounds == hit.Bounds);
        // Observe the actual panel command; Canvas.Panel's existing shadow is
        // two pixels right and five down, as in ReaderControlGeometry checks.
        return panel == null ? null : Rectangle.Union(panel.Bounds,
            new(panel.Bounds.X + 2, panel.Bounds.Y + 5, panel.Bounds.Width, panel.Bounds.Height));
    }
}
