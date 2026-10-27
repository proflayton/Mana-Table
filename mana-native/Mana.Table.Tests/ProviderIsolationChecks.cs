using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Mana.Table;

// Inspect the emitted local assembly graph without loading a provider or GPU.
// The Game build target separately rejects resolved unused project references.
internal static class ProviderIsolationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var game = typeof(TableApplication).Assembly;
        check(game.GetName().Name == "Mana.Game" && typeof(TableWindow).Assembly == game,
            "The production application and native window live in Mana.Game, outside the shipping provider/composition executable");
        string directory = Path.GetDirectoryName(game.Location) ?? throw new InvalidOperationException("Missing game assembly directory");
        string[] neutralAssemblies = ["Mana.Game", "Mana.Contracts", "Mana.Client", "Mana.Magic", "Mana.Planning", "Mana.Renderer", "Mana.Scenarios"];
        var pending = new Queue<(string Path, string Chain)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue((game.Location, "Mana.Game"));
        while (pending.TryDequeue(out var entry)) {
            if (!visited.Add(entry.Path)) continue;
            using var file = File.OpenRead(entry.Path);
            using var pe = new PEReader(file);
            if (!pe.HasMetadata) throw new InvalidOperationException("Expected a managed dependency: " + entry.Path);
            var metadata = pe.GetMetadataReader();
            if (entry.Path == game.Location) {
                foreach (var handle in metadata.TypeReferences) {
                    var type = metadata.GetTypeReference(handle);
                    if (metadata.GetString(type.Namespace) == "Mana.Contracts" && metadata.GetString(type.Name) == "ICardEngine")
                        throw new InvalidOperationException("Mana.Game references the aggregate ICardEngine instead of explicit service ports.");
                }
            }
            foreach (var handle in metadata.AssemblyReferences) {
                string name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                if ((name.StartsWith("Mana.", StringComparison.Ordinal) || name == "ManaTable") && !neutralAssemblies.Contains(name))
                    throw new InvalidOperationException("Unreviewed Mana dependency leaked into the game: " + entry.Chain + " -> " + name);
                string dependency = Path.Combine(directory, name + ".dll");
                if (File.Exists(dependency)) pending.Enqueue((dependency, entry.Chain + " -> " + name));
            }
        }
        check(true, "Mana.Game's emitted local dependency closure contains only reviewed neutral Mana assemblies, even when integration tests reference Forge separately");
        check(true, "The emitted game application/API references explicit service ports and never the aggregate ICardEngine");
    }
}
