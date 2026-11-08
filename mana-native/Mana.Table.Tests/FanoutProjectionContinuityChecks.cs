using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// These are the real ordinary client's accepted frames and callback cadence,
// reconstructed through production presentation at authored review settings.
// No recorded handle, grant or input acquires transport authority during replay.
internal static class FanoutProjectionContinuityChecks
{
    internal static void Run(string directory, string? sourceRevision, int human,
        FanoutAcceptedProjectionWindow window, GameSnapshot beforeCast, GameSnapshot burst,
        ImmutableHashSet<string> captured, Action<bool, string> check)
    {
        string path = Path.Combine(directory, "projection-human-" + human); Directory.CreateDirectory(path);
        var actualTerminalIds = Terminals(window.Observations, captured);
        check(actualTerminalIds.SetEquals(captured) && window.Observations[0].Executions?.Transitions
                .All(item => !captured.Contains(item.ExecutionId)) != false,
            "The actual human-" + human + " projection window begins before the cast and retains all110 distinct captured terminal outcomes");
        var battlefield = beforeCast.Players.SelectMany(player => player.Zone("Battlefield").Cards
            .Select(card => (Id: CardViews.Identity(card), Owner: player.Id))).ToImmutableHashSet();
        string toothy = CardViews.Identity(beforeCast.Players.SelectMany(player => player.Zone("Battlefield").Cards)
            .Single(card => card.Name == "Toothy, Imaginary Friend"));
        int expectedCounters = window.Observations[^1].Players.SelectMany(player => player.Zone("Battlefield").Cards)
            .Single(card => CardViews.Identity(card) == toothy).Counters.GetValueOrDefault("+1/+1");
        string archive = Path.Combine(path, "accepted-checkpoints");
        var archived = FanoutProjectionArchive.Write(archive, sourceRevision, window);
        var stored = FanoutProjectionArchive.Read(archive, new(1000, 740));
        var reports = new List<object>();
        foreach (var viewport in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            // Final display-only dwell is declared separately. It cannot add a
            // rules outcome or extend the actual fixture's completion budget.
            long drain = FanoutProjectionArchive.DisplayDrainTicks;
            var recording = new ProjectionRecording(1, stored.ProviderId, stored.SourceRevision, stored.Observations,
                new(stored.Timeline!.AcceptedAtTicks, stored.Timeline.EndTicks,
                    new(false, 1, true, viewport.X, viewport.Y)));
            string sizePath = Path.Combine(path, viewport.X.ToString()); Directory.CreateDirectory(sizePath);
            using var run = new ProjectionReplaySession(recording, Path.Combine(sizePath, "playback"), viewport);
            var seenAnnouncements = new Dictionary<string, object>(StringComparer.Ordinal);
            var announcedSources = new HashSet<string>(StringComparer.Ordinal);
            var groupedSources = new HashSet<string>(StringComparer.Ordinal);
            var seenBattlefield = new HashSet<(string Id, int Owner)>();
            int frames = 0, incomplete = 0, maximumAnnouncements = 0;
            void Presented(PresentationFrame frame) {
                frames++;
                if (frame.Stamp.GameId != window.Observations[0].Id)
                    throw new InvalidDataException("Replay replaced its recorded match scope.");
                incomplete = Math.Max(incomplete, run.View.IncompleteAnnouncements);
                maximumAnnouncements = Math.Max(maximumAnnouncements, run.View.AnnouncementCount);
                if (run.View.CurrentAnnouncement is { } announcement) {
                    bool painted = frame.Drawing.Commands.Any(command => command switch {
                        DrawCommand.Text text => text.Value?.Contains(announcement.Name, StringComparison.Ordinal) == true,
                        DrawCommand.CenterText text => text.Value?.Contains(announcement.Name, StringComparison.Ordinal) == true,
                        _ => false
                    });
                    bool capturedHeadingPainted = announcement.CapturedCount == 55 && frame.Drawing.Commands.Any(command => command switch {
                        DrawCommand.Text text => text.Value?.Contains("55 captured", StringComparison.Ordinal) == true
                            && text.Value.Contains(announcement.Name, StringComparison.Ordinal),
                        DrawCommand.CenterText text => text.Value?.Contains("55 captured", StringComparison.Ordinal) == true
                            && text.Value.Contains(announcement.Name, StringComparison.Ordinal),
                        _ => false
                    });
                    if (painted && announcement.Name.Length > 0) {
                        announcedSources.Add(announcement.Name);
                        if (capturedHeadingPainted) groupedSources.Add(announcement.Name);
                        if (announcement.CapturedCount != 55 || capturedHeadingPainted)
                            seenAnnouncements.TryAdd(announcement.Id, new { announcement.Id, announcement.ExecutionId,
                                announcement.Name, announcement.EventKind, announcement.CapturedCount, capturedHeadingPainted, firstRenderedTicks = run.ElapsedTicks });
                    }
                }
                if (frame.Scene is { } scene) {
                    var represented = scene.BattlefieldDrawOrder.Select(card => (card.Id, card.Owner)).ToHashSet();
                    if (scene.BattlefieldDrawOrder.Count != battlefield.Count || !represented.SetEquals(battlefield))
                        throw new InvalidDataException("The production scene lost, duplicated or reassigned a physical battlefield member.");
                    seenBattlefield.UnionWith(represented);
                    if (scene.Seats.Any(seat => seat.Cohorts.Any(cohort => cohort.MemberIds.Any(id =>
                            !battlefield.Contains((id, seat.PlayerId))))))
                        throw new InvalidDataException("A painted cohort mixed physical owners or invented its represented membership.");
                }
                foreach (var card in frame.Cards.Where(card => card.Zone == "Battlefield" && card.Request.Owner.HasValue)) {
                    var key = (card.Id, card.Request.Owner!.Value);
                    if (!battlefield.Contains(key)) throw new InvalidDataException("The fanout replay rebuilt or reassigned a physical battlefield occurrence.");
                }
            }
            run.Presented += Presented; Presented(run.Frame);
            run.TickTo(window.EndTicks);
            int pendingAtRecordedEnd = run.View.AnnouncementCount;
            run.Finish();
            var final = run.View.Match!;
            var acceptedTerminalIds = Terminals(run.Accepted.Select(item => item.State), captured);
            var settledToothy = run.Frame.Cards.Single(card => card.Zone == "Battlefield" && card.Id == toothy);
            check(run.Accepted.Count == window.Observations.Length
                    && run.Accepted.Select(item => item.State.Revision).SequenceEqual(window.Observations.Select(item => item.Revision))
                    && run.Accepted.Select(item => item.At.Ticks).SequenceEqual(window.AcceptedAtTicks)
                    && acceptedTerminalIds.SetEquals(captured) && final.Revision == window.Observations[^1].Revision,
                "Production replay retains every distinct actual human-" + human + " accepted payload/first offset and all110 terminal IDs at " + viewport);
            check(seenBattlefield.SetEquals(battlefield) && final.Stack.IsEmpty
                    && settledToothy.Card.Counters.GetValueOrDefault("+1/+1") == expectedCounters
                    && final.FiniteStackYield is { Status: FiniteStackYieldStatus.Completed, RemainingExecutionIds.IsEmpty: true },
                "Actual fanout replay preserves physical card owners and identities, completed finite state and the settled Toothy counters at " + viewport);
            check(incomplete == 0 && announcedSources.Contains("Shamanic Revelation")
                    && groupedSources.Contains("Toothy, Imaginary Friend") && groupedSources.Contains("Mind's Eye")
                    && run.View.AnnouncementCount == 0 && pendingAtRecordedEnd <= 4,
                "The recorded cast and both55-trigger sources remain readable as finite announcements without a110-entry presentation backlog at " + viewport);
            check(run.Playback.ObservationRequests == 0 && run.Playback.RejectedCommands == 0 && run.View.Error.Length == 0,
                "Grant-bearing replay has no provider reads, attempted commands, consent adoption or presentation error at " + viewport);
            reports.Add(new { viewport = new { width = viewport.X, height = viewport.Y }, frames, accepted = run.Accepted.Count,
                window.AcceptedCallbacks, window.IdenticalCallbacks, terminalIds = acceptedTerminalIds.Count,
                maximumAnnouncements, pendingAtRecordedEnd, incomplete, announcements = seenAnnouncements.Values,
                finalRevision = final.Revision, expectedCounters, displayedCounters = settledToothy.Card.Counters,
                sourceQueueSize = burst.Stack.Length, actualCallbackEndTicks = window.EndTicks, syntheticDisplayDrainTicks = drain });
        }
        File.WriteAllText(Path.Combine(path, "result.json"), JsonSerializer.Serialize(new {
            audience = "viewer-private", human, passed = true,
            archive = new { archived.TotalCheckpoints, archived.AcceptedCallbacks, archived.IdenticalCallbacks,
                parts = archived.Parts.Select(part => new { part.File, part.FirstOrdinal, part.EndOrdinal, part.StartTicks, part.EndTicks, part.Bytes, part.Sha256 }) },
            scope = "Actual accepted ordinary checkpoints at their callback cadence replayed through production observation-only presentation with authored viewports and normal motion.",
            limitations = "No camera/pointer inputs, GPU/artwork timing, original finite gesture, live presentation ACK timing or human UX acceptance were recorded. Separate gesture tests own control/interrupt evidence. The12s final display-only drain is synthetic and does not extend rules qualification.",
            reports
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ImmutableHashSet<string> Terminals(IEnumerable<GameSnapshot> frames, ImmutableHashSet<string> captured)
        => frames.SelectMany(state => state.Executions?.Transitions ?? [])
            .Where(item => item.Step == ExecutionStep.Complete && captured.Contains(item.ExecutionId))
            .Select(item => item.ExecutionId).ToImmutableHashSet(StringComparer.Ordinal);

    private sealed record Retained(GameSnapshot BeforeCast, GameSnapshot Burst, ImmutableHashSet<string> Captured,
        ImmutableArray<FanoutAcceptedProjectionWindow> AcceptedProjections);
    internal static async Task ReplayRetainedAsync(string inputDirectory, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string resultFile = Path.Combine(inputDirectory, "result.json"), evidenceFile = Path.Combine(inputDirectory, "private-final-observations.json");
        await using var resultInput = File.OpenRead(resultFile);
        using var result = await JsonDocument.ParseAsync(resultInput);
        var status = result.RootElement;
        if (status.GetProperty("failurePhase").GetString() != "projection-replay"
            || status.GetProperty("commonTerminalReceipts").GetInt32() != 110 || status.GetProperty("cleanupErrors").GetArrayLength() != 0)
            throw new InvalidDataException("Retained projection replay requires an already completed real-rules workload and clean teardown.");
        await using var input = File.OpenRead(evidenceFile);
        var retained = await JsonSerializer.DeserializeAsync<Retained>(input, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Missing retained actual fanout checkpoints.");
        if (retained.AcceptedProjections.Length != 2 || retained.Captured.Count != 110)
            throw new InvalidDataException("Retained replay lost an ordinary audience or captured trigger.");
        string? source = status.GetProperty("runtime").GetProperty("SourceRevision").GetString();
        for (int human = 0; human < retained.AcceptedProjections.Length; human++)
            Run(outputDirectory, source, human, retained.AcceptedProjections[human], retained.BeforeCast, retained.Burst, retained.Captured,
                (passed, message) => { if (!passed) throw new InvalidDataException(message); });
        using var evidence = File.OpenRead(evidenceFile); using var rules = File.OpenRead(resultFile);
        File.WriteAllText(Path.Combine(outputDirectory, "retained-rules-origin.json"), JsonSerializer.Serialize(new {
            audience = "viewer-private", scope = "Replay of retained accepted checkpoints after completed real rules; no rules rerun and no clean CI/source-release claim.",
            resultFile, rulesSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rules)),
            evidenceFile, evidenceSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(evidence)),
            commonTerminalReceipts = 110, projectionViewportsPerHuman = 2
        }));
    }
}
