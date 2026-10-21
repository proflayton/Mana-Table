using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Velopack;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Mana.Client.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Mana.Update.Lifecycle.Tests")]

namespace Mana.Client;

// The package's file contents are the contract. Native patching can produce a
// different ZIP layout while reconstructing the exact same application files.
internal sealed class UpdatePackageContract
{
    internal sealed record Entry(string Path, long Bytes, string Sha256);
    internal const int MaximumMetadataBytes = 4 * 1024 * 1024;
    internal const long MaximumPackageBytes = 750L * 1024 * 1024;
    private const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;
    internal string Version { get; }
    internal string SourceRevision { get; }
    internal string PackageId { get; }
    internal IReadOnlyDictionary<string, Entry> Entries { get; }

    private UpdatePackageContract(string version, string revision, string packageId, Dictionary<string, Entry> entries)
    {
        Version = version; SourceRevision = revision; PackageId = packageId;
        Entries = new System.Collections.ObjectModel.ReadOnlyDictionary<string, Entry>(entries);
    }

    internal static UpdatePackageContract Parse(byte[] bytes, VelopackAsset target, string channel)
    {
        if (bytes.Length > MaximumMetadataBytes) throw Invalid();
        using var document = AppBuild.Parse(bytes);
        var root = document.RootElement;
        if (root.GetProperty("updateProtocol").GetInt32() != 2 || AppBuild.String(root, "framework") != "velopack"
            || AppBuild.String(root, "packageId") != target.PackageId || AppBuild.String(root, "channel") != channel
            || AppBuild.String(root, "version") != target.Version.ToString() || !AppBuild.Hex(AppBuild.String(root, "sourceRevision"), 40)) throw Invalid();
        var full = root.GetProperty("fullPackage");
        if (AppBuild.String(full, "fileName") != target.FileName || full.GetProperty("sizeBytes").GetInt64() != target.Size
            || !AppBuild.String(full, "sha256").Equals(target.SHA256, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in root.GetProperty("entries").EnumerateArray()) {
            string path = AppBuild.String(item, "path"), hash = AppBuild.String(item, "sha256");
            long length = item.GetProperty("sizeBytes").GetInt64();
            if (!SafePath(path) || path.EndsWith('/') || !AppBuild.Hex(hash, 64) || length < 0 || length > int.MaxValue
                || entries.Count >= 20_000 || !entries.TryAdd(path, new(path, length, hash))) throw Invalid();
            total = checked(total + length);
            if (total > MaximumExpandedBytes) throw Invalid();
        }
        if (entries.Count == 0 || !entries.ContainsKey("lib/app/build.json") || !entries.Keys.Any(path => path.EndsWith(".nuspec", StringComparison.Ordinal))) throw Invalid();
        return new(AppBuild.String(root, "version"), AppBuild.String(root, "sourceRevision"), target.PackageId, entries);
    }

    internal async Task ValidateAsync(string packagePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(packagePath) || (File.GetAttributes(packagePath) & FileAttributes.ReparsePoint) != 0) throw Invalid();
        await using var file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, true);
        if (file.Length <= 0 || file.Length > MaximumPackageBytes) throw Invalid();
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SafePath(entry.FullName)) throw Invalid();
            // ZIP writers may retain empty old directories; they are not files.
            if (entry.FullName.EndsWith('/')) {
                if (entry.Length != 0) throw Invalid();
                continue;
            }
            if (!seen.Add(entry.FullName) || !Entries.TryGetValue(entry.FullName, out var expected)
                || entry.Length != expected.Bytes) throw Invalid();
            await using var input = entry.Open();
            var hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
            if (!Convert.ToHexString(hash).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        }
        if (seen.Count != Entries.Count) throw Invalid();
        var build = archive.GetEntry("lib/app/build.json") ?? throw Invalid();
        if (build.Length > 65_536) throw Invalid();
        await using var buildInput = build.Open();
        using var metadata = await JsonDocument.ParseAsync(buildInput, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (AppBuild.String(metadata.RootElement, "version") != Version || AppBuild.String(metadata.RootElement, "sourceRevision") != SourceRevision
            || metadata.RootElement.GetProperty("release").ValueKind != JsonValueKind.True) throw Invalid();
    }

    internal static bool SafePath(string path) => path is { Length: > 0 and < 1024 }
        && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl)
        && path.TrimEnd('/').Split('/').All(part => part.Length > 0 && part is not "." and not ".." && !part.EndsWith(' ') && !part.EndsWith('.'));
    private static InvalidDataException Invalid() => new("The prepared update does not match its published application contents. Download it again.");
}
