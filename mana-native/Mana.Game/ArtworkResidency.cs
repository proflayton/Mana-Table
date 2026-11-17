using Mana.Renderer;

namespace Mana.Table;

internal sealed record ArtworkResidencyLimits(long ResidentBytes = 224L * 1024 * 1024,
    long LowWaterBytes = 192L * 1024 * 1024, long UploadPeakBytes = 256L * 1024 * 1024,
    int TextureCount = 1024)
{
    internal void Validate()
    {
        if (ResidentBytes <= 0 || LowWaterBytes < 0 || LowWaterBytes > ResidentBytes
            || UploadPeakBytes < ResidentBytes || TextureCount <= 0) throw new ArgumentOutOfRangeException(nameof(ResidentBytes));
    }
}

/// <summary>Device-owner accounting. Does not allocate, dispose or retain concrete graphics resources.</summary>
internal sealed class ArtworkResidency(ArtworkResidencyLimits limits)
{
    private sealed class Entry(long bytes, long used)
    {
        internal long Bytes = bytes, Used = used;
        internal int Pins;
    }
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private long ordinal;
    private Reservation? reserved;
    internal long Bytes { get; private set; }
    internal long PeakBytes { get; private set; }
    internal long CapacityGeneration { get; private set; }
    internal long Evictions { get; private set; }
    private long pinnedBytes;
    private int pinnedCount;
    internal int Count => entries.Count;
    internal bool Contains(string key) => entries.ContainsKey(key);
    internal void Touch(string key) { if (entries.TryGetValue(key, out var item)) item.Used = ++ordinal; }

    internal bool CanAdmit(ArtworkImageInfo image)
    {
        return image.Bytes > 0 && image.SourceBytes > 0
            && pinnedBytes + (reserved?.Image.Bytes ?? 0) + image.Bytes <= limits.ResidentBytes
            && pinnedBytes + (reserved?.Image.Bytes ?? 0) + image.Bytes + image.SourceBytes <= limits.UploadPeakBytes
            && pinnedCount + (reserved == null ? 0 : 1) < limits.TextureCount;
    }
    internal Reservation? TryReserve(string key, ArtworkImageInfo image)
    {
        if (reserved != null) throw new InvalidOperationException("Only one device upload can own an artwork reservation.");
        if (entries.ContainsKey(key)) throw new InvalidOperationException("Artwork already has a resident texture.");
        if (!CanAdmit(image)) return null;
        bool Fits(long bytes, int count) => bytes + image.Bytes <= limits.ResidentBytes
            && bytes + image.Bytes + image.SourceBytes <= limits.UploadPeakBytes && count < limits.TextureCount;
        var evicted = new List<string>();
        if (!Fits(Bytes, Count)) {
            long remaining = Bytes; int count = Count;
            long target = Math.Min(limits.LowWaterBytes, Math.Min(limits.ResidentBytes - image.Bytes,
                limits.UploadPeakBytes - image.Bytes - image.SourceBytes));
            foreach (var candidate in entries.Where(item => item.Value.Pins == 0)
                .OrderBy(item => item.Value.Used).ThenBy(item => item.Key, StringComparer.Ordinal)) {
                evicted.Add(candidate.Key); remaining -= candidate.Value.Bytes; count--;
                if (remaining <= target && Fits(remaining, count)) break;
            }
            if (!Fits(remaining, count)) throw new InvalidOperationException("Artwork feasibility and eviction disagree.");
            foreach (string victim in evicted) { Bytes -= entries[victim].Bytes; entries.Remove(victim); Evictions++; }
            if (evicted.Count > 0) CapacityGeneration++;
        }
        PeakBytes = Math.Max(PeakBytes, checked(Bytes + image.Bytes + image.SourceBytes));
        return reserved = new(this, key, image, evicted.ToArray());
    }
    internal void Pin(IEnumerable<string> keys)
    {
        bool changed = false;
        foreach (string key in keys) {
            var entry = entries[key];
            if (entry.Pins == 0) { changed = true; pinnedCount++; pinnedBytes += entry.Bytes; }
            entry.Pins = checked(entry.Pins + 1);
        }
        if (changed) CapacityGeneration++;
    }
    internal void Unpin(IEnumerable<string> keys)
    {
        bool changed = false;
        foreach (string key in keys) {
            if (!entries.TryGetValue(key, out var entry)) continue;
            if (entry.Pins <= 0) throw new InvalidOperationException("Artwork frame pins cannot become negative.");
            if (--entry.Pins == 0) { changed = true; pinnedCount--; pinnedBytes -= entry.Bytes; }
        }
        if (changed) CapacityGeneration++;
    }
    internal void Clear() { entries.Clear(); Bytes = pinnedBytes = 0; pinnedCount = 0; reserved = null; CapacityGeneration++; }

    internal sealed class Reservation(ArtworkResidency owner, string key, ArtworkImageInfo image, string[] evicted) : IDisposable
    {
        internal ArtworkImageInfo Image { get; } = image;
        internal string[] Evicted { get; } = evicted;
        private bool completed;
        internal void Commit(long actualBytes)
        {
            if (completed || !ReferenceEquals(owner.reserved, this) || actualBytes != Image.Bytes)
                throw new InvalidOperationException("An artwork upload must commit its exact owned footprint once.");
            owner.entries.Add(key, new(actualBytes, ++owner.ordinal));
            owner.Bytes = checked(owner.Bytes + actualBytes); owner.CapacityGeneration++;
            completed = true; owner.reserved = null;
        }
        public void Dispose()
        {
            if (completed) return;
            completed = true;
            if (ReferenceEquals(owner.reserved, this)) { owner.reserved = null; owner.CapacityGeneration++; }
        }
    }
}
