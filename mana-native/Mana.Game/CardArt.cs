using System.Collections.Concurrent;
using Mana.Renderer;
using Microsoft.Xna.Framework.Graphics;
using Mana.Contracts;
using Mana.Table.Presentation;

namespace Mana.Table;

internal sealed class CardArt : IDisposable
{
    private const int VisibleLimit = 512, AnticipatedLimit = 160;
    private const int UploadByteBudget = 8 * 1024 * 1024, MaximumImageBytes = 4 * 1024 * 1024;
    private readonly ICardArtSource source;
    private readonly ConcurrentQueue<(string Name, byte[] Bytes)> loaded = new();
    private readonly Dictionary<string, Texture2D> textures = [];
    private readonly CancellationTokenSource stopped = new();
    private readonly object gate = new();
    private readonly Dictionary<string, ArtRequest> foreground = [];
    private Dictionary<string, ArtRequest> visible = [], anticipated = [];
    private IEnumerable<Card>? previousVisible, previousAnticipated;
    private readonly HashSet<string> warmed = [], uploaded = [];
    private readonly Dictionary<string, DateTime> retryAfter = [];
    private int waitingBytes;
    private bool working, disposed;
    public CardArt(ICardArtSource source) { this.source = source; }

    // Replace speculative work with the current view rather than appending an
    // unbounded queue as players browse. Decks warm disk; visible faces also
    // prepare GPU uploads. A newly read card can always jump ahead of both.
    internal void Preload(IEnumerable<Card> visibleCards, IEnumerable<Card>? anticipatedCards = null)
    {
        if (ReferenceEquals(previousVisible, visibleCards) && ReferenceEquals(previousAnticipated, anticipatedCards)) return;
        previousVisible = visibleCards; previousAnticipated = anticipatedCards;
        var current = Requests(visibleCards, VisibleLimit);
        var upcoming = Requests(anticipatedCards ?? [], AnticipatedLimit);
        lock (gate) {
            if (disposed) return;
            visible = current; anticipated = upcoming;
            StartWorker();
        }
    }

    private static Dictionary<string, ArtRequest> Requests(IEnumerable<Card> cards, int limit)
    {
        var requests = new Dictionary<string, ArtRequest>();
        foreach (var card in cards) {
            if (Mana.Magic.CardPresentation.Artwork(card) is not { } request) continue;
            requests.TryAdd(request.Key, request);
            if (requests.Count == limit) break;
        }
        return requests;
    }

    // Queued bytes are not ready art. Only the window's successful GPU upload
    // counts; a failed face settles to its existing readable fallback.
    internal ArtworkReadiness Readiness(Card[] cards)
    {
        var requests = Requests(cards, VisibleLimit);
        int ready = 0, unavailable = 0;
        lock (gate) {
            foreach (var key in requests.Keys) {
                if (textures.ContainsKey(key)) ready++;
                else if (retryAfter.TryGetValue(key, out var retry) && retry > DateTime.UtcNow) unavailable++;
            }
        }
        return new(requests.Count, ready, unavailable);
    }

    public void Update(GraphicsDevice graphics)
    {
        for (int uploaded = 0; uploaded < 2 && loaded.TryDequeue(out var item); uploaded++) {
            lock (gate) waitingBytes -= item.Bytes.Length;
            try { using var stream = new MemoryStream(item.Bytes); textures[item.Name] = TextureFiltering.Load(graphics, stream); }
            catch {
                // A corrupt image retains the readable fallback and can retry
                // later instead of starting a failing request every frame.
                lock (gate) { this.uploaded.Remove(item.Name); retryAfter[item.Name] = DateTime.UtcNow.AddSeconds(30); }
            }
        }
        lock (gate) StartWorker();
    }
    public Texture2D? Get(Card card)
    {
        if (Mana.Magic.CardPresentation.Artwork(card) is not { } request) return null;
        string cacheName = request.Key;
        if (textures.TryGetValue(cacheName, out var texture)) return texture;
        lock (gate) {
            if (!disposed && Ready(request, true)) {
                // Full-card reading paints after the table. Its latest demand
                // should not wait behind every small face in a crowded scene.
                foreground.Remove(cacheName);
                if (foreground.Count >= VisibleLimit) foreground.Remove(foreground.Keys.First());
                foreground.Add(cacheName, request);
            }
            StartWorker();
        }
        return null;
    }

    // Only this worker enters the art source. Thousands of draw calls cannot
    // occupy the source's network semaphore ahead of the card being inspected.
    private void StartWorker()
    {
        if (working || disposed || loaded.Count >= 16 || waitingBytes >= UploadByteBudget || !HasWork()) return;
        working = true;
        _ = Task.Run(LoadQueued);
    }

    private bool Ready(ArtRequest request, bool upload) => !(upload ? uploaded : warmed).Contains(request.Key)
        && (!retryAfter.TryGetValue(request.Key, out var retry) || retry <= DateTime.UtcNow);

    private bool HasWork() => foreground.Values.Any(request => Ready(request, true))
        || visible.Values.Any(request => Ready(request, true)) || anticipated.Values.Any(request => Ready(request, false));

    private async Task LoadQueued()
    {
        bool normalExit = false;
        try {
            while (true) {
                ArtRequest? request;
                bool upload;
                lock (gate) {
                    // Cached files can finish much faster than GPU uploads. Keep
                    // encoded images bounded even when the game is not drawing.
                    if (loaded.Count >= 16 || waitingBytes >= UploadByteBudget) { normalExit = true; return; }
                    request = foreground.Values.LastOrDefault(candidate => Ready(candidate, true));
                    if (request != null) foreground.Remove(request.Key);
                    request ??= visible.Values.FirstOrDefault(candidate => Ready(candidate, true));
                    upload = request != null;
                    request ??= anticipated.Values.FirstOrDefault(candidate => Ready(candidate, false));
                    if (disposed || request == null) { normalExit = true; return; }
                }
                byte[]? bytes = null;
                try {
                    bytes = await source.LoadAsync(request, stopped.Token);
                } catch (Exception) {
                    // The injected source boundary may fail beyond ordinary
                    // network/cache errors. Back off this face and keep the
                    // remaining cards available instead of stranding ownership.
                }
                lock (gate) {
                    if (disposed) { normalExit = true; return; }
                    if (bytes is { Length: > 0 and <= MaximumImageBytes }) {
                        warmed.Add(request.Key); retryAfter.Remove(request.Key);
                        // A deck prefetch may become a visible card while loading.
                        upload |= foreground.Remove(request.Key) || visible.ContainsKey(request.Key);
                        if (upload && uploaded.Add(request.Key)) {
                            waitingBytes += bytes.Length;
                            loaded.Enqueue((request.Key, bytes));
                        }
                    } else retryAfter[request.Key] = DateTime.UtcNow.AddSeconds(30);
                }
            }
        } finally {
            lock (gate) {
                working = false;
                // Close the wake-up race with a request arriving as a normal
                // drain ends. Exceptional exits never restart themselves.
                if (normalExit) StartWorker();
            }
        }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        stopped.Cancel(); source.Dispose();
        foreach (var texture in textures.Values) texture.Dispose();
        while (loaded.TryDequeue(out _)) { }
    }
}
