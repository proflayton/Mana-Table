using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework.Input;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

// Extends the existing two-size card-reading CI journey. Rendering, pointer
// input, keyboard escape and source inspection are production paths; receipts
// and the read-only archive port are authored presentation fixtures.
internal sealed partial class CardReadingSmoke
{
    private const string HistorySource = "history-spell";
    private const string HistoryElevator = "history-elevator";
    private const string HistoryGame = "history-pointer-scenario";
    private KeyboardState historyKeyboard;
    public KeyboardState? ReadKeyboard() => historyKeyboard;

    private void QueueHistoryChecks()
    {
        int replies = 0;
        string selected = "";
        var elevator = new Card { VisualId = HistoryElevator, CombatId = HistoryElevator,
            Name = "The Eternity Elevator", Type = "Legendary Artifact — Spacecraft", ManaCost = "{5}",
            Text = "{T}: Add {C}{C}{C}.\nStation 20\n{T}: Add X mana of any one color, where X is the number of charge counters on this artifact.",
            Counters = ImmutableDictionary<string, int>.Empty.Add("CHARGE", 32) };
        var spell = new Card { VisualId = HistorySource, CombatId = HistorySource, Name = "Ashnod's Altar",
            Type = "Artifact", ManaCost = "{3}", Text = "Sacrifice a creature: Add {C}{C}." };
        Step(0, () => {
            view.SetAutomatic(false); view.SetTableStudy(true); Move(new(10, 70));
            var seed = PresentationSmoke.Example();
            var own = seed.Viewer!;
            Publish(seed with { Id = HistoryGame, Turn = 57, Phase = "Main phase", PhaseKey = "MAIN1", ActivePlayerId = own.Id,
                Stack = [], Combat = null, Activity = [], Executions = null,
                Players = seed.Players.Select(player => player.Id == own.Id ? player with {
                    Zones = player.Zones.Select(zone => zone.Name == "Battlefield" ? zone with { Count = 2, Cards = [elevator, spell] }
                        : zone.Name == "Hand" ? zone with { Count = 0, Cards = [] } : zone).ToImmutableArray()
                } : player).ToImmutableArray(),
                Decision = new("", new DecisionContext(), new PriorityPayload(new("Pass priority", false), null, false, "", [])),
                DecisionOwnerId = own.Id, PriorityPlayerId = own.Id });
            replies = scenario.Replies.Count;
            Check(view.AcceptHistory(HistoryReceipt(own.Id)), "The history fixture admits typed payment receipts through the audience-bound client session");
        });
        Prepared(() => view.Hits.Any(hit => hit.Id == "settings" && view.CurrentHit(hit)), 3,
            () => MoveHit("settings"), "Settings must remain available before the history text-scale journey"); Click();
        Prepared(() => view.Overlay == "settings" && view.Hits.Any(hit => hit.Id == "text-size"), 2,
            () => MoveHit("text-size"), "The production text-size control must be visible"); Click();
        Step(.05, () => MoveHit("text-size")); Click();
        Step(.05, () => {
            Check(view.TextScale == 1.3f, "Match history is exercised at 130% text in both existing card-reading window sizes");
            MoveHit("close-overlay");
        }); Click();
        Prepared(() => view.Overlay.Length == 0 && view.Hits.Any(hit => hit.Id == "history"), 2,
            () => {
                Check(view.TableStudyEnabled, "History is reviewed on the same shared battlefield layout used by shipping games");
                Capture("history-00-shipping-battlefield"); MoveHit("history");
            }, "The shipping table's actual History control must reopen the recorded journey"); Click();
        Prepared(() => view.Overlay == "history" && view.HistorySelectionId != null && view.Hits.Any(hit => hit.Id == "history-previous"), 2,
            () => MoveHit("history-previous"), "The root spell and its child mana activation must remain individually navigable"); Click();
        Prepared(() => view.HistorySelectionId == "history-payment", 2, () => {
            selected = view.HistorySelectionId!;
            string detail = view.HistoryDetailText;
            Check(detail.Contains("3 generic", StringComparison.Ordinal) && detail.Contains("32 red", StringComparison.Ordinal)
                    && detail.Contains("29 red", StringComparison.Ordinal) && detail.Contains("32 charge", StringComparison.Ordinal),
                "History distinguishes the generic-three bill, actual 32-mana production, charge counters and 29 mana remaining");
            string painted = string.Join("\n", view.Frame!.Drawing.Commands.OfType<Mana.Renderer.DrawCommand.Text>().Select(text => text.Value));
            Check(painted.Contains("produced 32 red", StringComparison.Ordinal) && painted.Contains("Spent: 3 red", StringComparison.Ordinal)
                    && painted.Contains("Mana remaining: 29 red", StringComparison.Ordinal),
                "Produced, spent and remaining mana are painted together on the first detail page at enlarged text");
            Check(!view.HistoryPrivateReport && HitReachable("history-report-scope") && HitReachable("history-read-source"),
                "History defaults to a public report and keeps source reading and the explicit privacy control reachable at enlarged text");
            Check(view.Hits.All(hit => hit.Intent is HistoryIntent or UiIntent.Block),
                "The history surface replaces underlying decision controls instead of leaving invisible battlefield actions active");
            Capture("history-01-payment-130"); MoveHit("history-report-scope");
        }, "The recorded Elevator payment must be readable before any source inspection"); Click();
        Step(.05, () => {
            Check(view.HistoryPrivateReport && scenario.Replies.Count == replies,
                "Including the owner's private choices is an explicit local report option and sends no game command");
            MoveHit("history-report-scope");
        }); Click();
        Step(.05, () => {
            Check(!view.HistoryPrivateReport, "The report scope can return to public facts before export");
            Check(view.AcceptHistory(HistoryArrival(view.Match!.ViewerId)), "A later distinct action enters the same accepted history stream");
        });
        Step(.08, () => {
            Check(view.HistorySelectionId == selected && HitReachable("history-newest") && HitReachable("history-next"),
                "A new action leaves the currently read play selected and offers deliberate navigation to new work");
            MoveHit("history-read-source");
        }); Click();
        Prepared(() => view.Overlay == "inspect" && view.HistoryInspectedCardId == HistorySource, 2, () => {
            Check(scenario.Replies.Count == replies && HitReachable("close-overlay")
                    && !view.Hits.Any(hit => hit.Id is "read-card-action" or "activate-ability" or "pass"),
                "Reading the current source uses the production full-card inspector without offering or submitting a play");
            Capture("history-02-source-reader-130"); MoveHit("close-overlay");
        }, "The historical source must open its currently authorized face"); Click();
        Prepared(() => view.Overlay == "history", 2, () => {
            Check(view.HistorySelectionId == selected && scenario.Replies.Count == replies,
                "Back to history restores the selected payment without answering the current engine decision");
            MoveHit("history-read-source");
        }, "The source reader's Back control must return to the same play"); Click();
        Prepared(() => view.Overlay == "inspect", 2, () => historyKeyboard = new(Keys.Escape),
            "The source reader must open before the actual Escape-key gesture");
        Step(.04, () => historyKeyboard = new());
        Prepared(() => view.Overlay == "history", 2, () => {
            Check(view.HistorySelectionId == selected && scenario.Replies.Count == replies,
                "Escape returns from source inspection to the same historical play without sending a decision");
            MoveHit("history-read-source");
        }, "Escape must restore the history context"); Click();
        Prepared(() => view.Overlay == "inspect", 2, () => Publish(scenario.State with {
            Players = scenario.State.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                Cards = zone.Cards.Select(card => card.VisualId == HistorySource
                    ? card with { FaceDown = true, Name = "Face-down card", ArtName = "", Text = "", ManaCost = "", Type = "" } : card).ToImmutableArray()
            }).ToImmutableArray() }).ToImmutableArray()
        }), "The source inspector must be live before its face becomes concealed");
        Prepared(() => view.Overlay == "history" && view.HistoryInspectedCardId == null, 2, () => {
            Check(view.HistorySelectionId == selected && !view.Hits.Any(hit => hit.Id == "history-read-source")
                    && view.HistoryDetailText.Contains("32 red", StringComparison.Ordinal) && scenario.Replies.Count == replies,
                "Concealing the live face revokes the current reader while preserving the copied public payment facts");
            Capture("history-03-concealed-source"); MoveHit("history-saved");
        }, "A history reader must immediately relinquish a face that the current observation conceals"); Click();
        Prepared(() => view.Hits.Any(hit => hit.Id == "history-match:" + HistorySmokeArchive.Id), 3,
            () => MoveHit("history-match:" + HistorySmokeArchive.Id), "Saved matches must load through the injected archive port"); Click();
        Prepared(() => view.HistoryArchiveId == HistorySmokeArchive.Id && view.Hits.Any(hit => hit.Id == "history-previous"), 3,
            () => MoveHit("history-previous"), "Opening a saved match must rebuild actions from archived typed batches"); Click();
        Prepared(() => view.HistorySelectionId == "history-producer", 2, () => MoveHit("history-previous"),
            "Saved history must preserve the separate mana child before returning to its paid spell"); Click();
        Step(.08, () => {
            Check(view.HistoryArchiveId == HistorySmokeArchive.Id && view.HistoryDetailText.Contains("29 red", StringComparison.Ordinal)
                    && !view.Hits.Any(hit => hit.Id == "history-read-source") && scenario.Replies.Count == replies,
                "A reopened saved payment retains exact production and spending while respecting the currently concealed source");
            Capture("history-04-saved-payment-130"); MoveHit("history-close");
        }); Click();
        Prepared(() => view.Overlay.Length == 0, 2, () => {
            Check(scenario.Replies.Count == replies && view.HistoryInspectedCardId == null,
                "The complete history, report-scope, reader and saved-match journey returns to the table without a game reply");
            observations.Add(new { journey = "history", view.TextScale, selected, replies = scenario.Replies.Count,
                detail = view.HistoryDetailText, archived = HistorySmokeArchive.Id, artwork = "deterministic fixture; human visual review remains separate" });
        }, "Back to table must close the read-only history surface");
    }

    private static HistoryBatch HistoryReceipt(int viewer)
    {
        const string id = "history-payment";
        var occurrence = new ExecutionOccurrence(id, id, ExecutionKind.Spell, ExecutionStep.Preparation) {
            ActorPlayerId = viewer, SourceCardId = HistorySource, SourceName = "Ashnod's Altar" };
        var producer = new ExecutionOccurrence("history-producer", id, ExecutionKind.Mana, ExecutionStep.Preparation) {
            ParentId = id, ActorPlayerId = viewer, SourceCardId = HistoryElevator, SourceName = "The Eternity Elevator" };
        HistoryRecord Execution(long sequence, ExecutionOccurrence value, string exposure) => new(sequence, 57, "MAIN1", exposure) {
            ExecutionId = value.Id, ParentId = value.ParentId, SourceCardId = value.SourceCardId, SourceName = value.SourceName, Execution = new(value) };
        HistoryRecord Payment(long sequence, HistoryPayment payment) => new(sequence, 57, "MAIN1", "seatPrivate") {
            ExecutionId = id, SourceCardId = HistorySource, SourceName = occurrence.SourceName, Payment = payment };
        var bill = ImmutableArray.Create(new ManaBillPart("{3}", 1));
        var mana = ImmutableDictionary<string, int>.Empty;
        return new(1, HistoryGame, viewer, 1, 9, 9, false, [
            Execution(1, occurrence, "seatPrivate"),
            Execution(2, occurrence with { Step = ExecutionStep.Cost }, "seatPrivate"),
            Payment(3, new("opened", viewer, "auto") { BillBefore = bill, BillAfter = bill }),
            Execution(4, producer, "seatPrivate"),
            new(5, 57, "MAIN1", "public") { ExecutionId = producer.Id, SourceCardId = HistoryElevator,
                SourceName = producer.SourceName, Payment = new("produced", viewer, "auto") { ProducerExecutionId = producer.Id, ProducerMode = "Add X mana of any one color.",
                Produced = mana.Add("R", 32), PoolAfter = mana.Add("R", 32), SourceCounters = ImmutableDictionary<string, int>.Empty.Add("CHARGE", 32) } },
            Execution(6, producer with { Step = ExecutionStep.Complete, Outcome = ExecutionOutcome.Resolved }, "seatPrivate"),
            Payment(7, new("spent", viewer, "auto") { BillBefore = bill, BillAfter = [], Spent = mana.Add("R", 3), PoolAfter = mana.Add("R", 29) }),
            Payment(8, new("completed", viewer, "auto") { BillAfter = [], PoolAfter = mana.Add("R", 29) }),
            Execution(9, occurrence with { Step = ExecutionStep.Complete, Outcome = ExecutionOutcome.Resolved }, "public")
        ]);
    }
    private static HistoryBatch HistoryArrival(int viewer)
    {
        var occurrence = new ExecutionOccurrence("history-later", "history-later", ExecutionKind.Trigger, ExecutionStep.Complete) {
            ActorPlayerId = 1, SourceName = "A later trigger", Outcome = ExecutionOutcome.Resolved };
        return new(1, HistoryGame, viewer, 1, 10, 10, false,
            [new(10, 57, "MAIN1", "public") { ExecutionId = occurrence.Id, SourceName = occurrence.SourceName, Execution = new(occurrence) }]);
    }
}

internal sealed class HistorySmokeArchive : IMatchHistoryArchive
{
    internal const string Id = "00112233445566778899aabbccddeeff";
    private readonly object gate = new();
    private ImmutableArray<HistoryBatch> batches = [];
    public HistoryArchiveHealth Health => new(true, 0, null);
    public void Record(HistoryBatch batch) { lock (gate) batches = batches.Add(batch); }
    public void MarkClosed(string gameId, int viewerId, bool finished, bool incomplete = false) { }
    private HistoryArchiveDocument? Snapshot()
    {
        lock (gate) {
            if (batches.IsEmpty) return null;
            var at = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
            var last = batches[^1];
            var summary = new HistoryArchiveSummary(Id, last.GameId, last.ViewerId, "0.2.317", new string('a', 40), at, at,
                HistoryArchiveState.Closed, false, batches.Sum(batch => batch.Records.Length), last.NextSequence,
                last.NextSequence, false, 1000, new string('a', 64));
            return new(summary, batches, []);
        }
    }
    public Task<ImmutableArray<HistoryArchiveSummary>> ListAsync(CancellationToken cancellation = default) =>
        Task.FromResult(Snapshot() is { } document ? ImmutableArray.Create(document.Summary) : []);
    public Task<HistoryArchiveDocument?> ReadAsync(string archiveId, CancellationToken cancellation = default) =>
        Task.FromResult(archiveId == Id ? Snapshot() : null);
    public async Task ExportAsync(string archiveId, Stream destination, HistoryExportScope scope = HistoryExportScope.Public,
        CancellationToken cancellation = default, long? focusSequence = null)
    {
        if (focusSequence is <= 0) throw new ArgumentOutOfRangeException(nameof(focusSequence));
        var document = await ReadAsync(archiveId, cancellation) ?? throw new FileNotFoundException();
        var records = document.Records.Where(record => scope == HistoryExportScope.IncludeOwnPrivate || record.Exposure == "public").ToArray();
        long? selectedRecordSequence = focusSequence is { } selected && records.Any(record => record.Sequence == selected) ? selected : null;
        await JsonSerializer.SerializeAsync(destination, new { selectedRecordSequence,
            focusOmitted = focusSequence.HasValue && selectedRecordSequence == null, records }, cancellationToken: cancellation);
    }
}
