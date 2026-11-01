using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Mana.Contracts;

namespace Mana.Client;

/// <summary>A bounded local receipt archive, independent of the operational diagnostic ring.
/// Its single worker owns storage; accepted batches contain no snapshots or action capabilities.</summary>
public sealed partial class MatchHistoryArchive : IMatchHistoryArchive, IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // These are standalone UTF-8 JSON files, never an HTML/script fragment.
        // Keep ordinary Unicode readable; non-BMP surrogate escaping still has
        // a separate bounded physical allowance below the actual journal cap.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private const string EmptyHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private readonly string directory, runId = Guid.NewGuid().ToString("N");
    private readonly AppBuild build;
    private readonly HistoryArchiveLimits limits;
    private readonly Channel<Work> pending;
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly Dictionary<Scope, Manifest> open = [];
    private readonly object lossGate = new();
    private readonly Dictionary<Scope, (long Count, string Reason)> losses = [];
    private readonly Dictionary<Scope, (bool Finished, bool Incomplete, long Through)> closures = [];
    private long admitted, processed, captures;
    private long dropped;
    private int closed, unavailable;
    private string? issue;

    public MatchHistoryArchive(string profileDirectory, AppBuild build)
        : this(profileDirectory, build, new(), Task.CompletedTask) { }

    internal MatchHistoryArchive(string profileDirectory, AppBuild build, HistoryArchiveLimits limits, Task start)
    {
        directory = Path.GetFullPath(Path.Combine(profileDirectory, "history"));
        this.build = build; this.limits = limits;
        if (limits.MaximumMatches < 1 || limits.MaximumQueuedBatches < 1 || limits.MaximumBatchBytes < 1024
            || limits.MaximumBatchBytes > HistoryFacts.MaximumBatchBytes
            || limits.MaximumMatchBytes < 4096 || limits.MaximumTotalBytes < limits.MaximumMatchBytes
            || limits.MaximumJournalEntries < 1) throw new ArgumentOutOfRangeException(nameof(limits));
        pending = Channel.CreateBounded<Work>(new BoundedChannelOptions(limits.MaximumQueuedBatches) {
            SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        worker = Task.Run(() => RunAsync(start));
    }

    public HistoryArchiveHealth Health => new(Volatile.Read(ref unavailable) == 0,
        Interlocked.Read(ref dropped), Volatile.Read(ref issue));
    internal Task Completion => worker;

    public void Record(HistoryBatch acceptedBatch)
    {
        try {
            if (acceptedBatch == null || Volatile.Read(ref closed) != 0 || Volatile.Read(ref unavailable) != 0) return;
            var scope = new Scope(acceptedBatch.GameId, acceptedBatch.ViewerId);
            if (!Valid(scope) || acceptedBatch.Records.IsDefault || acceptedBatch.Records.Length > HistoryFacts.MaximumBatchRecords) {
                NoteLoss(scope, "invalid-batch"); return;
            }
            try { HistoryFacts.Validate(acceptedBatch); } catch (ArgumentException) { NoteLoss(scope, "invalid-batch"); return; }
            bool queued;
            lock (lossGate) {
                queued = pending.Writer.TryWrite(new(acceptedBatch, null, admitted + 1));
                if (queued) admitted++;
            }
            if (!queued) NoteLoss(scope, "queue-overflow");
        } catch { Volatile.Write(ref issue, "record-unavailable"); }
    }

    public void MarkClosed(string gameId, int viewerId, bool finished, bool incomplete = false)
    {
        if (Volatile.Read(ref closed) != 0) return;
        var scope = new Scope(gameId, viewerId);
        if (!Valid(scope)) return;
        lock (lossGate) {
            if (closures.Count < 64 || closures.ContainsKey(scope))
                closures[scope] = (finished || closures.GetValueOrDefault(scope).Finished,
                    incomplete || closures.GetValueOrDefault(scope).Incomplete, admitted);
            else { Interlocked.Increment(ref dropped); Volatile.Write(ref issue, "closure-overflow"); }
        }
        pending.Writer.TryWrite(new(null, null));
    }

    public Task<ImmutableArray<HistoryArchiveSummary>> ListAsync(CancellationToken cancellation = default)
        => QueryAsync(() => LoadManifests().Select(Summary).OrderByDescending(item => item.UpdatedAt).ToImmutableArray(), cancellation);
    public Task<HistoryArchiveDocument?> ReadAsync(string archiveId, CancellationToken cancellation = default)
    {
        if (!Guid.TryParseExact(archiveId, "N", out _)) throw new ArgumentException("Unknown history archive.", nameof(archiveId));
        return QueryAsync(() => Read(archiveId), cancellation);
    }

    public async Task ExportAsync(string archiveId, Stream destination, HistoryExportScope scope = HistoryExportScope.Public,
        CancellationToken cancellation = default, long? focusSequence = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The history destination must be writable.", nameof(destination));
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (focusSequence is <= 0) throw new ArgumentOutOfRangeException(nameof(focusSequence));
        var document = await ReadAsync(archiveId, cancellation).ConfigureAwait(false)
            ?? throw new FileNotFoundException("This history archive is no longer retained.");
        var records = scope == HistoryExportScope.IncludeOwnPrivate ? document.Records : PublicRecords(document.Records);
        long? selectedRecordSequence = focusSequence is { } selected && records.Any(record => record.Sequence == selected) ? selected : null;
        // A purpose-built document, never a ZIP of the profile or a serialized snapshot.
        var export = new { schemaVersion = 1, scope = scope == HistoryExportScope.Public ? "public" : "includeOwnPrivate",
            summary = document.Summary, issues = document.Issues, omittedRecords = document.Records.Length - records.Length,
            authority = "none", selectedRecordSequence, focusOmitted = focusSequence.HasValue && selectedRecordSequence == null, records };
        await JsonSerializer.SerializeAsync(destination, export, Json, cancellation).ConfigureAwait(false);
        await destination.FlushAsync(cancellation).ConfigureAwait(false);
    }

    private async Task<T> QueryAsync<T>(Func<T> read, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref closed) != 0, this);
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new Work(null, () => { try { done.TrySetResult(read()); } catch (Exception error) { done.TrySetException(error); } });
        try { await pending.Writer.WriteAsync(work, cancellation).ConfigureAwait(false); }
        catch (ChannelClosedException) { throw new IOException("The history archive has closed."); }
        return await done.Task.WaitAsync(cancellation).ConfigureAwait(false);
    }

    private void NoteLoss(Scope scope, string reason)
    {
        Interlocked.Increment(ref dropped); Volatile.Write(ref issue, reason);
        if (!Valid(scope)) return;
        lock (lossGate) if (losses.Count < 64 || losses.ContainsKey(scope)) {
            var before = losses.GetValueOrDefault(scope);
            string persistedReason = reason == "queue-overflow" ? "queue-loss" : reason;
            losses[scope] = (before.Count + 1, before.Count > 0 && before.Reason != persistedReason ? "record-loss" : persistedReason);
        }
    }

    private async Task RunAsync(Task start)
    {
        try {
            await start.WaitAsync(stop.Token).ConfigureAwait(false);
            try {
                Directory.CreateDirectory(directory);
                if (Reparse(directory)) throw new IOException("History storage is not a local directory.");
                Prune();
            } catch { StorageFailed(); }
            await foreach (var work in pending.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false)) {
                if (work.Read != null) work.Read();
                else if (work.Batch != null && Volatile.Read(ref unavailable) == 0) {
                    try { await AppendAsync(work.Batch).ConfigureAwait(false); }
                    catch { NoteLoss(new(work.Batch.GameId, work.Batch.ViewerId), "storage-failed"); StorageFailed(); }
                }
                processed = Math.Max(processed, work.Ordinal);
                if (Volatile.Read(ref unavailable) == 0) {
                    try { await ClosePendingAsync().ConfigureAwait(false); }
                    catch { StorageFailed(); }
                }
            }
            if (Volatile.Read(ref unavailable) == 0) {
                foreach (var scope in LossScopes()) await ApplyLossAsync(Get(scope)).ConfigureAwait(false);
                await ClosePendingAsync().ConfigureAwait(false);
                foreach (var manifest in open.Values.Where(item => !item.Closed).ToArray()) {
                    manifest.Closed = true; manifest.UpdatedAt = DateTimeOffset.UtcNow; Save(manifest);
                }
                Prune();
            }
        } catch { StorageFailed(); }
        finally {
            while (pending.Reader.TryRead(out var remaining)) remaining.Read?.Invoke();
            stop.Dispose();
        }
    }

    private void StorageFailed() { Volatile.Write(ref unavailable, 1); Volatile.Write(ref issue, "storage-failed"); }

    private Manifest Get(Scope scope)
    {
        if (open.TryGetValue(scope, out var existing)) return existing;
        if (open.Count >= 64) throw new IOException("Too many local history captures.");
        string id = Guid.NewGuid().ToString("N");
        string path = ArchivePath(id); Directory.CreateDirectory(path);
        var now = DateTimeOffset.UtcNow;
        var manifest = new Manifest { ArchiveId = id, GameId = scope.Game, ViewerId = scope.Viewer,
            RunId = runId, CaptureOrdinal = ++captures, StartedAt = now, UpdatedAt = now,
            Version = AppBuild.TryVersion(build.Version, out var version) ? version.ToString(3) : null,
            SourceRevision = AppBuild.Hex(build.SourceRevision, 40) ? build.SourceRevision.ToLowerInvariant() : null };
        open.Add(scope, manifest); Save(manifest); Prune(); return manifest;
    }

    private async Task AppendAsync(HistoryBatch batch)
    {
        var manifest = Get(new(batch.GameId, batch.ViewerId));
        // A terminal board and its final history query are separate deliveries.
        // Accepted late facts can complete a retained capture without reopening
        // game authority or inventing that its earlier closure was fully observed.
        await ApplyLossAsync(manifest).ConfigureAwait(false);
        manifest.ObservedThrough = Math.Max(manifest.ObservedThrough, batch.NextSequence);
        var records = batch.Records.Where(item => item.Sequence > manifest.LastSequence).ToImmutableArray();
        if (records.IsEmpty) { Save(manifest); return; }
        bool gap = records[0].Sequence > manifest.LastSequence + 1
            || records.Zip(records.Skip(1), (before, after) => after.Sequence - before.Sequence > 1).Any(value => value);
        manifest.HasGaps |= gap;
        if (manifest.Capped) { manifest.DroppedBatches++; Save(manifest); return; }
        var admitted = batch with { Records = records };
        var payload = new Payload("batch", DateTimeOffset.UtcNow, admitted, 0, gap ? "provider-gap" : null);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        // Admission uses canonical provider UTF-8. The local JSON encoder can
        // expand a four-byte non-BMP scalar into twelve surrogate-escape bytes.
        // Bound that physical representation independently, including metadata;
        // the match's actual byte cap below continues to count encoded bytes.
        if (bytes.Length > limits.MaximumBatchBytes * 3 + 2048 || manifest.JournalEntries >= limits.MaximumJournalEntries
            || manifest.JournalBytes + bytes.Length + 2048 > limits.MaximumMatchBytes) {
            manifest.Capped = manifest.HasGaps = true; manifest.DroppedBatches++;
            await WriteAsync(manifest, new("loss", DateTimeOffset.UtcNow, null, 1, "retention-limit")).ConfigureAwait(false);
            Save(manifest); return;
        }
        await WriteAsync(manifest, payload).ConfigureAwait(false);
        manifest.RecordCount += records.Length;
        manifest.LastSequence = records[^1].Sequence;
        Save(manifest);
    }

    private Scope[] LossScopes() { lock (lossGate) return losses.Keys.ToArray(); }
    private async Task ApplyLossAsync(Manifest manifest)
    {
        (long Count, string Reason) loss;
        lock (lossGate) { var scope = new Scope(manifest.GameId, manifest.ViewerId); loss = losses.GetValueOrDefault(scope); losses.Remove(scope); }
        if (loss.Count == 0) return;
        manifest.HasGaps = true; manifest.DroppedBatches += loss.Count;
        if (!manifest.Capped) await WriteAsync(manifest, new("loss", DateTimeOffset.UtcNow, null, loss.Count, loss.Reason)).ConfigureAwait(false);
        Save(manifest);
    }

    private async Task ClosePendingAsync()
    {
        KeyValuePair<Scope, (bool Finished, bool Incomplete, long Through)>[] pendingClosures;
        lock (lossGate) {
            pendingClosures = closures.Where(item => item.Value.Through <= processed).ToArray();
            foreach (var item in pendingClosures) closures.Remove(item.Key);
        }
        foreach (var close in pendingClosures) {
            var manifest = Get(close.Key); await ApplyLossAsync(manifest).ConfigureAwait(false);
            manifest.UnobservedTail |= close.Value.Incomplete;
            manifest.HasGaps |= manifest.UnobservedTail;
            manifest.Closed = true; manifest.Finished |= close.Value.Finished; manifest.UpdatedAt = DateTimeOffset.UtcNow; Save(manifest);
        }
    }

    private async Task WriteAsync(Manifest manifest, Payload payload)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        string hash = Hash(manifest.LastHash, body);
        byte[] prefix = Encoding.UTF8.GetBytes("{\"previousHash\":\"" + manifest.LastHash + "\",\"hash\":\"" + hash + "\",\"payload\":");
        int length = prefix.Length + body.Length + 2;
        if (manifest.JournalBytes + length > limits.MaximumMatchBytes) { manifest.HasGaps = manifest.Capped = true; return; }
        await using var output = new FileStream(OwnedFile(manifest.ArchiveId, "receipts.jsonl"),
            FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
        await output.WriteAsync(prefix, stop.Token).ConfigureAwait(false);
        await output.WriteAsync(body, stop.Token).ConfigureAwait(false);
        await output.WriteAsync("}\n"u8.ToArray(), stop.Token).ConfigureAwait(false);
        await output.FlushAsync(stop.Token).ConfigureAwait(false); // Application flush; not a power-loss guarantee.
        manifest.LastHash = hash; manifest.JournalBytes += length; manifest.JournalEntries++;
        manifest.UpdatedAt = payload.RecordedAt;
    }

    private HistoryArchiveDocument? Read(string id)
    {
        var manifest = Load(id); if (manifest == null) return null;
        var batches = ImmutableArray.CreateBuilder<HistoryBatch>();
        var issues = ImmutableArray.CreateBuilder<HistoryArchiveIssue>();
        string path = Path.Combine(ArchivePath(id), "receipts.jsonl"), previous = EmptyHash;
        long last = 0;
        if (File.Exists(path)) {
            if (Reparse(path) || new FileInfo(path).Length > limits.MaximumMatchBytes) return new(Summary(manifest) with { HasGaps = true }, [], [new("file-limit")]);
            using var input = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8);
            int lines = 0;
            while (input.ReadLine() is { } line) {
                try {
                    if (++lines > limits.MaximumJournalEntries + 1 || Encoding.UTF8.GetByteCount(line) > limits.MaximumBatchBytes * 3 + 4096) throw new JsonException();
                    using var parsed = JsonDocument.Parse(line);
                    var entry = parsed.RootElement;
                    var payloadElement = entry.GetProperty("payload");
                    string hash = Hash(previous, Encoding.UTF8.GetBytes(payloadElement.GetRawText()));
                    if (entry.GetProperty("previousHash").GetString() != previous || entry.GetProperty("hash").GetString() != hash) throw new JsonException();
                    var payload = payloadElement.Deserialize<Payload>(Json) ?? throw new JsonException();
                    if (payload.Kind == "batch" && payload.Batch is { } batch
                        && batch.GameId == manifest.GameId && batch.ViewerId == manifest.ViewerId
                        && !batch.Records.IsDefault && batch.Records.Length <= HistoryFacts.MaximumBatchRecords
                        && batch.Records.All(record => record.Sequence > last)) {
                        HistoryFacts.Validate(batch);
                        if (!batch.Records.IsEmpty) last = batch.Records[^1].Sequence;
                        batches.Add(batch);
                        if (payload.Reason != null) issues.Add(new(payload.Reason));
                    } else if (payload.Kind == "loss" && payload.Batch == null && payload.DroppedBatches > 0)
                        issues.Add(new(payload.Reason ?? "record-loss", payload.DroppedBatches));
                    else throw new JsonException();
                    previous = hash;
                } catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) {
                    issues.Add(new("invalid-or-incomplete-tail")); break;
                }
            }
        }
        if (previous != manifest.LastHash) issues.Add(new("manifest-journal-mismatch"));
        if (manifest.DroppedBatches > 0 && !issues.Any(item => item.Count > 0)) issues.Add(new("record-loss", manifest.DroppedBatches));
        if (manifest.UnobservedTail) issues.Add(new("unobserved-tail"));
        var summary = Summary(manifest) with { RecordCount = batches.Sum(batch => (long)batch.Records.Length),
            LastSequence = last, HasGaps = manifest.HasGaps || issues.Count != 0 };
        return new(summary, batches.ToImmutable(), issues.ToImmutable());
    }

    private List<Manifest> LoadManifests()
    {
        if (!Directory.Exists(directory) || Reparse(directory)) return [];
        var found = new List<Manifest>();
        foreach (string path in Directory.EnumerateDirectories(directory).Take(256)) {
            string id = Path.GetFileName(path);
            if (!Guid.TryParseExact(id, "N", out _) || Reparse(path)) continue;
            if (Load(id) is { } manifest) found.Add(manifest);
        }
        return found;
    }

    private Manifest? Load(string id)
    {
        try {
            string path = Path.Combine(ArchivePath(id), "manifest.json");
            if (!File.Exists(path) || Reparse(path) || new FileInfo(path).Length > 16384) return null;
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(path), Json);
            return manifest is { SchemaVersion: 1 } && manifest.ArchiveId == id && Valid(new(manifest.GameId, manifest.ViewerId))
                && manifest.RecordCount >= 0 && manifest.JournalBytes >= 0 && manifest.LastHash is { Length: 64 } ? manifest : null;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    private HistoryArchiveSummary Summary(Manifest manifest) => new(manifest.ArchiveId, manifest.GameId, manifest.ViewerId,
        manifest.Version, manifest.SourceRevision, manifest.StartedAt, manifest.UpdatedAt,
        manifest.Closed ? HistoryArchiveState.Closed : manifest.RunId == runId ? HistoryArchiveState.Recording : HistoryArchiveState.Interrupted,
        manifest.Finished, manifest.RecordCount, manifest.LastSequence, manifest.ObservedThrough, manifest.HasGaps,
        manifest.JournalBytes, manifest.LastHash);

    private void Save(Manifest manifest)
    {
        string temporary = OwnedFile(manifest.ArchiveId, "manifest.tmp");
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
        File.Move(temporary, OwnedFile(manifest.ArchiveId, "manifest.json"), true);
    }

    private void Prune()
    {
        var retained = LoadManifests().OrderByDescending(item => item.UpdatedAt).ToList();
        long bytes = retained.Sum(item => item.JournalBytes);
        int count = retained.Count;
        var protectedIds = open.Values.Where(item => !item.Closed).Select(item => item.ArchiveId).ToHashSet(StringComparer.Ordinal);
        foreach (var manifest in retained.OrderBy(item => item.UpdatedAt).ThenBy(item => item.RunId == runId ? item.CaptureOrdinal : 0)) {
            if (protectedIds.Contains(manifest.ArchiveId) || count <= limits.MaximumMatches && bytes <= limits.MaximumTotalBytes
                && DateTimeOffset.UtcNow - manifest.UpdatedAt <= limits.MaximumAge) continue;
            string path = ArchivePath(manifest.ArchiveId);
            // Delete only owned regular files in a validated GUID child, never recurse through a profile or link.
            foreach (string name in new[] { "manifest.json", "manifest.tmp", "receipts.jsonl" }) {
                string file = Path.Combine(path, name); if (File.Exists(file) && !Reparse(file)) File.Delete(file);
            }
            if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
            foreach (var scope in open.Where(item => item.Value.ArchiveId == manifest.ArchiveId).Select(item => item.Key).ToArray()) open.Remove(scope);
            bytes -= manifest.JournalBytes; count--;
        }
    }

    private string ArchivePath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Unknown history archive.");
        if (Directory.Exists(directory) && Reparse(directory)) throw new IOException("History storage is unavailable.");
        string path = Path.GetFullPath(Path.Combine(directory, id));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(path) && Reparse(path)) throw new IOException("History archive path is unavailable.");
        return path;
    }
    private string OwnedFile(string id, string name)
    {
        string path = Path.Combine(ArchivePath(id), name);
        if (File.Exists(path) && Reparse(path)) throw new IOException("History file is unavailable.");
        return path;
    }
    private static bool Reparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool Valid(Scope scope) => scope.Viewer >= 0 && scope.Game is { Length: > 0 and <= 256 } && !scope.Game.Any(char.IsControl);
    private static string Hash(string previous, byte[] body)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(previous)); hash.AppendData(body);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    public void Dispose()
    {
        Close(); try { if (!worker.Wait(TimeSpan.FromMilliseconds(100))) stop.Cancel(); } catch { }
    }
    public async ValueTask DisposeAsync()
    {
        Close(); try { await worker.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { try { stop.Cancel(); } catch { } }
    }
    private void Close() { if (Interlocked.Exchange(ref closed, 1) == 0) pending.Writer.TryComplete(); }

    private readonly record struct Scope(string Game, int Viewer);
    private sealed record Work(HistoryBatch? Batch, Action? Read, long Ordinal = 0);
    private sealed record Payload(string Kind, DateTimeOffset RecordedAt, HistoryBatch? Batch, long DroppedBatches, string? Reason);
    private sealed class Manifest
    {
        public Manifest() { }
        public int SchemaVersion { get; set; } = 1;
        public string ArchiveId { get; set; } = "";
        public string GameId { get; set; } = "";
        public int ViewerId { get; set; }
        public string RunId { get; set; } = "";
        public long CaptureOrdinal { get; set; }
        public string? Version { get; set; }
        public string? SourceRevision { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public bool Closed { get; set; }
        public bool Finished { get; set; }
        public long RecordCount { get; set; }
        public long LastSequence { get; set; }
        public long ObservedThrough { get; set; }
        public bool HasGaps { get; set; }
        public bool Capped { get; set; }
        public bool UnobservedTail { get; set; }
        public long DroppedBatches { get; set; }
        public long JournalBytes { get; set; }
        public int JournalEntries { get; set; }
        public string LastHash { get; set; } = EmptyHash;
    }
}
