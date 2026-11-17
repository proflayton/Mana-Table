using Mana.Renderer;
using Microsoft.Xna.Framework.Graphics;
using Mana.Contracts;
using Mana.Table.Presentation;

namespace Mana.Table;

internal sealed class CardArt : IDisposable
{
    private const int VisibleLimit = 512, AnticipatedLimit = 160, MetadataLimit = 4096, CaptureLimit = 4, MandatoryFrameLimit = 4;
    private const int UploadByteBudget = 8 * 1024 * 1024;
    private sealed class Metadata
    {
        internal bool Warmed, Queued;
        internal long Used, Demanded, DeniedGeneration = -1;
        internal DateTimeOffset RetryAfter;
        internal ArtworkImageInfo? Image;
    }
    private sealed record Upload(string Key, byte[] Bytes, ArtworkImageInfo Image);
    private readonly ICardArtSource source;
    private readonly ArtworkResidency residency;
    private readonly Queue<Upload> loaded = new();
    private readonly Dictionary<string, Texture2D> textures = new(StringComparer.Ordinal);
    private readonly Dictionary<Texture2D, string> ownedTextures = new();
    private readonly Dictionary<string, Metadata> metadata = new(StringComparer.Ordinal);
    private readonly HashSet<FrameLease> leases = [];
    private readonly CancellationTokenSource stopped = new();
    private readonly object gate = new();
    private readonly Dictionary<string, ArtRequest> foreground = new(StringComparer.Ordinal);
    private Dictionary<string, ArtRequest> visible = [], anticipated = [];
    private IEnumerable<Card>? previousVisible, previousAnticipated;
    private TaskCompletionSource queueChanged = Signal();
    private readonly TimeProvider clock;
    private int waitingBytes, heldBytes, uploadingBytes, deviceThread, captures, mandatoryFrames;
    private string? loading, uploading;
    private long ordinal, demandGeneration, pressureDenials;
    private bool working, disposed;
    private Task worker = Task.CompletedTask;
    private long uploadFailures;
    private string lastUploadFailure = "";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CardArt(ICardArtSource source, ArtworkResidencyLimits? limits = null, TimeProvider? clock = null)
    {
        this.source = source; this.clock = clock ?? TimeProvider.System;
        limits ??= new(); limits.Validate(); residency = new(limits);
    }
    internal ArtworkCacheMetrics Metrics {
        get { lock (gate) return new(residency.Count, residency.Bytes, residency.PeakBytes, loaded.Count,
            waitingBytes, heldBytes, uploadingBytes, metadata.Count, residency.CapacityGeneration, residency.Evictions, pressureDenials, captures,
            mandatoryFrames, leases.Count, uploadFailures, lastUploadFailure); }
    }
    internal Task PendingLoad { get { lock (gate) return worker; } }
    // Encoded stage counts exclude the source reader's MemoryStream/copy buffers,
    // decoder buffers and driver storage. They are not a total CPU-memory budget.
    internal sealed record ArtworkCacheMetrics(int Residents, long ResidentBytes, long AccountedPeakBytes,
        int Queued, int QueuedBytes, int HeldBytes, int DeviceUploadBytes, int Metadata, long CapacityGeneration,
        long Evictions, long PressureDenials, int Captures, int MandatoryFrames, int LiveLeases,
        long UploadFailures, string LastUploadFailure);

    internal void Preload(IEnumerable<Card> visibleCards, IEnumerable<Card>? anticipatedCards = null)
    {
        if (ReferenceEquals(previousVisible, visibleCards) && ReferenceEquals(previousAnticipated, anticipatedCards)) return;
        previousVisible = visibleCards; previousAnticipated = anticipatedCards;
        var current = Requests(visibleCards, VisibleLimit);
        var upcoming = Requests(anticipatedCards ?? [], AnticipatedLimit);
        lock (gate) {
            if (disposed) return;
            visible = current; anticipated = upcoming;
            foreach (string key in foreground.Keys.Where(key => metadata[key].Demanded < demandGeneration
                && !visible.ContainsKey(key)).ToArray()) foreground.Remove(key);
            PruneMetadata(); WakeQueue(); StartWorker();
        }
    }
    private static Dictionary<string, ArtRequest> Requests(IEnumerable<Card> cards, int limit)
    {
        var requests = new Dictionary<string, ArtRequest>(StringComparer.Ordinal);
        foreach (var card in cards) {
            if (Mana.Magic.CardPresentation.Artwork(card) is not { } request) continue;
            requests.TryAdd(request.Key, request);
            if (requests.Count == limit) break;
        }
        return requests;
    }
    internal ArtworkReadiness Readiness(Card[] cards)
    {
        var requests = Requests(cards, VisibleLimit);
        int ready = 0, unavailable = 0;
        lock (gate) {
            foreach (var key in requests.Keys) {
                if (textures.ContainsKey(key)) ready++;
                else if (metadata.TryGetValue(key, out var entry)
                    && (entry.RetryAfter > clock.GetUtcNow() || entry.Image is { } image
                        && entry.DeniedGeneration >= 0 && !residency.CanAdmit(image))) unavailable++;
            }
        }
        return new(requests.Count, ready, unavailable);
    }
    public void Update(GraphicsDevice graphics)
    {
        AssertDevice(bind: true);
        lock (gate) { if (disposed) return; demandGeneration++; }
        for (int uploaded = 0; uploaded < 2; uploaded++) {
            Upload item; ArtworkResidency.Reservation? admission;
            lock (gate) {
                if (!loaded.TryDequeue(out item!)) break;
                waitingBytes -= item.Bytes.Length; var entry = metadata[item.Key]; entry.Queued = false;
                WakeQueue();
                if (!Wanted(item.Key) || residency.Contains(item.Key)) continue;
                admission = residency.TryReserve(item.Key, item.Image);
                if (admission == null) { entry.DeniedGeneration = residency.CapacityGeneration; pressureDenials++; continue; }
                uploading = item.Key; uploadingBytes = item.Bytes.Length;
                foreach (string key in admission.Evicted) {
                    var old = textures[key]; textures.Remove(key); ownedTextures.Remove(old); old.Dispose();
                }
            }
            Texture2D? texture = null;
            using (admission) {
                try {
                    texture = TextureFiltering.Load(graphics, item.Bytes, item.Image);
                    long bytes = EncodedArtwork.ColorBytes(texture.Width, texture.Height, texture.LevelCount);
                    if (texture.Format != SurfaceFormat.Color || bytes != item.Image.Bytes)
                        throw new InvalidDataException("The uploaded artwork contradicted its reserved footprint.");
                    lock (gate) {
                        textures.Add(item.Key, texture);
                        try { ownedTextures.Add(texture, item.Key); admission.Commit(bytes); }
                        catch { textures.Remove(item.Key); ownedTextures.Remove(texture); throw; }
                        metadata[item.Key].DeniedGeneration = -1;
                    }
                    texture = null;
                } catch (Exception error) {
                    texture?.Dispose();
                    lock (gate) {
                        metadata[item.Key].RetryAfter = clock.GetUtcNow().AddSeconds(30); uploadFailures++;
                        string detail = error.GetType().Name + ": " + error.Message;
                        lastUploadFailure = detail[..Math.Min(detail.Length, 1024)];
                    }
                } finally { lock (gate) { uploading = null; uploadingBytes = 0; } }
            }
        }
        lock (gate) { PruneMetadata(); StartWorker(); }
    }
    public Texture2D? Get(Card card)
    {
        AssertDevice();
        if (Mana.Magic.CardPresentation.Artwork(card) is not { } request) return null;
        lock (gate) {
            if (disposed) return null;
            var entry = Entry(request.Key); entry.Demanded = demandGeneration;
            if (textures.TryGetValue(request.Key, out var texture)) { residency.Touch(request.Key); return texture; }
            foreground.Remove(request.Key);
            if (foreground.Count >= VisibleLimit) foreground.Remove(foreground.Keys.MinBy(key => metadata[key].Used)!);
            foreground.Add(request.Key, request);
            StartWorker(); return null;
        }
    }
    internal FrameLease PinFrame(DrawingFrame frame)
    {
        AssertDevice();
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (mandatoryFrames >= MandatoryFrameLimit) throw new InvalidOperationException("Required artwork frame ownership exceeded its fixed roles.");
            return Lease(frame, capture: false);
        }
    }
    internal bool TryPinCapture(DrawingFrame frame, out FrameLease? lease)
    {
        AssertDevice();
        lock (gate) {
            lease = null;
            if (disposed || captures >= CaptureLimit) return false;
            lease = Lease(frame, capture: true); return true;
        }
    }
    private FrameLease Lease(DrawingFrame frame, bool capture)
    {
        string[] keys = frame.TextureResources.Where(ownedTextures.ContainsKey).Select(texture => ownedTextures[texture]).Distinct().ToArray();
        if (frame.TextureResources.Any(texture => texture.IsDisposed)) throw new InvalidOperationException("A retained artwork frame references a disposed texture.");
        residency.Pin(keys);
        if (capture) captures++; else mandatoryFrames++;
        var lease = new FrameLease(this, frame, keys, capture); leases.Add(lease); return lease;
    }
    internal sealed class FrameLease : IDisposable
    {
        private CardArt? current;
        private DrawingFrame? drawing;
        private string[]? keys;
        private readonly bool capture;
        internal bool Active => Volatile.Read(ref current) != null;
        internal DrawingFrame? Drawing => Active ? drawing : null;
        internal int RetainedKeys => Active ? keys?.Length ?? 0 : 0;
        internal FrameLease(CardArt owner, DrawingFrame frame, string[] keys, bool capture)
            => (current, drawing, this.keys, this.capture) = (owner, frame, keys, capture);
        // Called only while the owner gate is held, including before GPU shutdown.
        internal void Revoke()
        {
            if (Volatile.Read(ref current) is not { } owner) return;
            var previousKeys = keys!;
            drawing = null; keys = null; Volatile.Write(ref current, null);
            owner.residency.Unpin(previousKeys); owner.leases.Remove(this);
            if (capture) owner.captures--; else owner.mandatoryFrames--;
        }
        public void Dispose()
        {
            var owner = Volatile.Read(ref current);
            if (owner == null) return;
            owner.AssertDevice();
            lock (owner.gate) {
                Revoke();
                if (!owner.disposed) owner.StartWorker();
            }
        }
    }
    private Metadata Entry(string key)
    {
        if (!metadata.TryGetValue(key, out var entry)) { PruneMetadata(reserve: 1); metadata.Add(key, entry = new()); }
        entry.Used = ++ordinal; return entry;
    }
    private void PruneMetadata(int reserve = 0)
    {
        if (metadata.Count + reserve <= MetadataLimit) return;
        foreach (string key in metadata.Where(pair => !pair.Value.Queued && loading != pair.Key && uploading != pair.Key && !residency.Contains(pair.Key)
            && !visible.ContainsKey(pair.Key) && !anticipated.ContainsKey(pair.Key) && !foreground.ContainsKey(pair.Key))
            .OrderBy(pair => pair.Value.Used).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(metadata.Count + reserve - MetadataLimit).Select(pair => pair.Key).ToArray()) metadata.Remove(key);
        if (metadata.Count + reserve > MetadataLimit) throw new InvalidOperationException("Active artwork metadata exceeded its bounded demand envelope.");
    }
    private bool Ready(ArtRequest request, bool upload)
    {
        if (residency.Contains(request.Key) || loading == request.Key || uploading == request.Key) return false;
        if (!metadata.TryGetValue(request.Key, out var entry)) return true;
        if (entry.Queued || entry.RetryAfter > clock.GetUtcNow() || !upload && entry.Warmed) return false;
        if (upload && entry.DeniedGeneration >= 0 && entry.Image is { } image) {
            if (entry.DeniedGeneration == residency.CapacityGeneration) return false;
            if (!residency.CanAdmit(image)) { entry.DeniedGeneration = residency.CapacityGeneration; return false; }
        }
        return true;
    }
    private bool Wanted(string key) => foreground.ContainsKey(key) || visible.ContainsKey(key);
    private bool HasWork() => foreground.Values.Any(request => Ready(request, true))
        || visible.Values.Any(request => Ready(request, true)) || anticipated.Values.Any(request => Ready(request, false));
    private void StartWorker()
    {
        if (working || disposed || loaded.Count >= 16 || waitingBytes >= UploadByteBudget || !HasWork()) return;
        working = true; worker = Task.Run(LoadQueued);
    }
    private void WakeQueue() { var signal = queueChanged; queueChanged = Signal(); signal.TrySetResult(); }
    private async Task LoadQueued()
    {
        bool normalExit = false;
        try {
            while (true) {
                ArtRequest? request;
                lock (gate) {
                    if (disposed || loaded.Count >= 16 || waitingBytes >= UploadByteBudget) { normalExit = true; return; }
                    request = foreground.Values.OrderByDescending(candidate => metadata[candidate.Key].Used).FirstOrDefault(candidate => Ready(candidate, true))
                        ?? visible.Values.FirstOrDefault(candidate => Ready(candidate, true))
                        ?? anticipated.Values.FirstOrDefault(candidate => Ready(candidate, false));
                    if (request == null) { normalExit = true; return; }
                    loading = request.Key; Entry(request.Key);
                }
                byte[]? bytes = null;
                try { bytes = await source.LoadAsync(request, stopped.Token).ConfigureAwait(false); }
                catch (Exception) { /* Keep the current readable fallback; a source failure cannot strand the worker. */ }
                ArtworkImageInfo image = default;
                bool valid = bytes != null && EncodedArtwork.TryDescribe(bytes, out image);
                while (true) {
                    Task? wait = null;
                    lock (gate) {
                        if (disposed) { normalExit = true; return; }
                        var entry = Entry(request.Key);
                        if (!valid) { entry.RetryAfter = clock.GetUtcNow().AddSeconds(30); loading = null; break; }
                        entry.Warmed = true; entry.Image = image; entry.RetryAfter = default;
                        if (!Wanted(request.Key)) { loading = null; break; }
                        if (waitingBytes + bytes!.Length <= UploadByteBudget && loaded.Count < 16) {
                            entry.Queued = true; waitingBytes += bytes.Length;
                            loaded.Enqueue(new(request.Key, bytes, image)); loading = null; heldBytes = 0; break;
                        }
                        heldBytes = bytes!.Length; wait = queueChanged.Task;
                    }
                    await wait!.WaitAsync(stopped.Token).ConfigureAwait(false);
                }
            }
        } catch (OperationCanceledException) when (stopped.IsCancellationRequested) { normalExit = true; }
        finally {
            lock (gate) { loading = null; heldBytes = 0; working = false; if (normalExit) StartWorker(); }
        }
    }
    private void AssertDevice(bool bind = false)
    {
        int thread = Environment.CurrentManagedThreadId;
        if (bind && deviceThread == 0) deviceThread = thread;
        if (deviceThread != 0 && deviceThread != thread) throw new InvalidOperationException("Artwork GPU resources belong to the window thread.");
    }
    public void Dispose()
    {
        AssertDevice();
        Task pending;
        lock (gate) {
            if (disposed) return; disposed = true;
            pending = worker;
            // Retained drawing references lose paint authority before their GPU
            // resources retire. External delayed lease.Dispose calls are inert.
            foreach (var lease in leases.ToArray()) lease.Revoke();
            foreach (var texture in textures.Values) texture.Dispose();
            textures.Clear(); ownedTextures.Clear(); residency.Clear(); loaded.Clear(); metadata.Clear();
            visible.Clear(); anticipated.Clear(); foreground.Clear(); waitingBytes = heldBytes = uploadingBytes = 0; WakeQueue();
        }
        stopped.Cancel();
        try { source.Dispose(); }
        finally {
            _ = pending.ContinueWith(task => { _ = task.Exception; stopped.Dispose(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
