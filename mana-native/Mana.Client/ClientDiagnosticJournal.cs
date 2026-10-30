using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Mana.Client;

/// <summary>Bounded, viewer-local diagnostic evidence. Recording never waits for
/// disk and an unavailable journal never changes command admission or execution.</summary>
public sealed class ClientDiagnosticJournal : IDisposable, IAsyncDisposable
{
    public const int MaximumEvents = 64;
    public const int MaximumFileBytes = 256 * 1024;
    public const int MaximumEntryBytes = 8 * 1024;
    private static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly Channel<Pending> pending;
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private int closed, disabled;
    private long dropped;

    public ClientDiagnosticJournal(string profileDirectory, AppBuild build)
        : this(profileDirectory, build, Task.CompletedTask) { }

    // Scheduling only: tests can fill the real channel before the writer starts.
    internal ClientDiagnosticJournal(string profileDirectory, AppBuild build, Task start)
    {
        pending = Channel.CreateBounded<Pending>(new BoundedChannelOptions(MaximumEvents) {
            SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest
        }, item => {
            if (item.Flushed != null) item.Flushed.TrySetResult(false);
            else Interlocked.Increment(ref dropped);
        });
        var header = new Header("header", 1,
            AppBuild.TryVersion(build?.Version, out var version) ? version.ToString(3) : null,
            AppBuild.Hex(build?.SourceRevision, 40) ? build!.SourceRevision.ToLowerInvariant() : null,
            build?.IsRelease == true, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        worker = Task.Run(() => WriteAsync(profileDirectory, header, start));
    }

    /// <summary>Completes after the writer exits, including when storage failed.</summary>
    public Task Completion => worker;

    public void Record(ClientDiagnostic diagnostic)
    {
        try {
            if (diagnostic == null || Volatile.Read(ref closed) != 0 || Volatile.Read(ref disabled) != 0) return;
            var bounded = Bound(diagnostic, out bool truncated);
            if (bounded == null) { Interlocked.Increment(ref dropped); return; }
            pending.Writer.TryWrite(new(bounded, null, truncated));
        } catch { /* Evidence must not become a gameplay failure. */ }
    }

    // A same-queue barrier gives storage tests a deterministic durable boundary.
    // Dropping, closing or failing the writer always completes the waiter.
    internal Task<bool> FlushAsync()
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Volatile.Read(ref closed) != 0 || Volatile.Read(ref disabled) != 0
            || !pending.Writer.TryWrite(new(null, done, false))) done.TrySetResult(false);
        return done.Task;
    }

    public void Dispose()
    {
        Close();
        try { if (!worker.Wait(TimeSpan.FromMilliseconds(100))) stop.Cancel(); }
        catch { /* Exit never waits indefinitely for storage. */ }
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        try { await worker.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch { try { stop.Cancel(); } catch { } }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) == 0) pending.Writer.TryComplete();
    }

    private async Task WriteAsync(string profileDirectory, Header header, Task start)
    {
        FileStream? output = null;
        try {
            await start.WaitAsync(stop.Token);
            string directory = Path.Combine(profileDirectory, "diagnostics");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "client-decisions.jsonl");
            string previous = Path.Combine(directory, "client-decisions.previous.jsonl");
            PruneOversized(previous);
            PruneOversized(path);
            if (File.Exists(path)) File.Move(path, previous, true);
            byte[] heading = Line(header);
            output = await OpenAsync(path, heading);
            int events = 0;
            long sequence = 0;
            async Task WriteEntry(ClientDiagnostic? diagnostic, bool truncated, long lost)
            {
                // Sequence accounts for persisted records and observed losses;
                // loss placement describes writer observations, not engine order.
                long nextSequence = sequence + lost + (diagnostic == null ? 0 : 1);
                byte[] bytes = Line(new Entry(diagnostic == null ? "loss" : "event", nextSequence,
                    DateTimeOffset.UtcNow, lost, truncated, diagnostic));
                if (bytes.Length > MaximumEntryBytes) {
                    Interlocked.Add(ref dropped, lost + 1);
                    return;
                }
                sequence = nextSequence;
                if (events == MaximumEvents || output!.Length + bytes.Length > MaximumFileBytes) {
                    await output.DisposeAsync();
                    output = null;
                    File.Move(path, previous, true);
                    output = await OpenAsync(path, heading);
                    events = 0;
                }
                await output!.WriteAsync(bytes, stop.Token);
                // Make completed choices readable during play without waiting
                // for a full buffer or shutdown. This is not a durable fsync.
                await output.FlushAsync(stop.Token);
                events++;
            }
            await foreach (var item in pending.Reader.ReadAllAsync(stop.Token)) {
                if (item.Flushed != null) {
                    bool flushed = false;
                    try { await output.FlushAsync(stop.Token); flushed = true; }
                    finally { item.Flushed.TrySetResult(flushed); }
                    continue;
                }
                await WriteEntry(item.Diagnostic!, item.Truncated, Interlocked.Exchange(ref dropped, 0));
            }
            long remainingLoss = Interlocked.Exchange(ref dropped, 0);
            if (remainingLoss != 0) await WriteEntry(null, false, remainingLoss);
            await output.FlushAsync(stop.Token);
        } catch {
            // Do not retry a failed path, serialize exceptions, or notify play.
            Volatile.Write(ref disabled, 1);
            pending.Writer.TryComplete();
        } finally {
            if (output != null) { try { await output.DisposeAsync(); } catch { } }
            while (pending.Reader.TryRead(out var abandoned)) abandoned.Flushed?.TrySetResult(false);
            stop.Dispose();
        }
    }

    private async Task<FileStream> OpenAsync(string path, byte[] heading)
    {
        var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous);
        try { await output.WriteAsync(heading, stop.Token); return output; }
        catch { await output.DisposeAsync(); throw; }
    }

    private static void PruneOversized(string path)
    {
        if (!File.Exists(path)) return;
        bool oversized = new FileInfo(path).Length > MaximumFileBytes;
        if (!oversized) {
            // A retained file is bounded in records and individual lines too.
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] buffer = new byte[4096];
            int lines = 0, length = 0, total = 0, count;
            while (!oversized && (count = input.Read(buffer)) != 0) {
                total += count;
                oversized = total > MaximumFileBytes;
                for (int i = 0; !oversized && i < count; i++) {
                    length++;
                    if (length > MaximumEntryBytes) oversized = true;
                    if (buffer[i] == (byte)'\n') { lines++; length = 0; }
                    if (lines > MaximumEvents + 1) oversized = true;
                }
            }
            // A crash may leave an incomplete final JSON line. Keep that bounded
            // evidence; it counts as one physical record, not as an oversized file.
            oversized |= lines + (length == 0 ? 0 : 1) > MaximumEvents + 1;
        }
        if (oversized) File.Delete(path);
    }

    private static ClientDiagnostic? Bound(ClientDiagnostic diagnostic, out bool truncated)
    {
        var facts = diagnostic.Facts;
        truncated = false;
        if (!Enum.IsDefined(diagnostic.Origin) || !Enum.IsDefined(diagnostic.Stage)
            || !Enum.IsDefined(diagnostic.Command) || !Enum.IsDefined(diagnostic.Failure)
            || facts == null || !Enum.IsDefined(facts.Decision)
            || (facts.ExecutionStep is { } step && !Enum.IsDefined(step))) return null;
        string? name = facts.DecisionSourceName;
        truncated = name != null && (name.Length > 160 || name.Any(char.IsControl));
        if (truncated) name = null;
        var mana = facts.RemainingMana;
        var bounded = ImmutableArray.CreateBuilder<ClientDiagnosticManaPart>();
        bool manaTruncated = !mana.IsDefaultOrEmpty && mana.Length > 32;
        if (!mana.IsDefaultOrEmpty) foreach (var part in mana.Take(32)) {
            if (part is { Count: > 0 } && ClientCommandDiagnostics.ManaSymbol(part.Symbol)) bounded.Add(part);
            else manaTruncated = true;
        }
        truncated |= manaTruncated;
        return diagnostic with { Facts = facts with { DecisionSourceName = name, RemainingMana = bounded.ToImmutable(),
            ManaBillUnknownOrTruncated = facts.ManaBillUnknownOrTruncated || manaTruncated } };
    }

    private static byte[] Line<T>(T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        byte[] line = new byte[json.Length + 1];
        json.CopyTo(line, 0); line[^1] = (byte)'\n';
        return line;
    }

    private sealed record Header(string Type, int Schema, string? Version, string? SourceRevision,
        bool Release, string RunId, DateTimeOffset StartedAt);
    private sealed record Entry(string Type, long Sequence, DateTimeOffset RecordedAt,
        long DroppedSincePreviousWrite, bool InputTruncated, ClientDiagnostic? Diagnostic);
    private sealed record Pending(ClientDiagnostic? Diagnostic, TaskCompletionSource<bool>? Flushed, bool Truncated);
}
