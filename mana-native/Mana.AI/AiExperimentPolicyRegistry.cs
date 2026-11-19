using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mana.AI;

public sealed record CasualCommanderOptions(bool AttachmentEvaluation = true, bool LibraryAcquisitionEvaluation = false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiExperimentVariant(string FactoryId, bool AttachmentEvaluation = true);

// Deliberately closed: experiment input chooses a supported factory/settings
// pair, never a type name, historical assembly or executable expression.
public static class AiExperimentPolicyRegistry
{
    public const string CasualCommanderFactoryId = "casual-commander.v6";
    public const string LibraryAcquisitionFactoryId = "casual-commander.v7.library-acquisition.v1";

    public static IAiPolicy Create(AiExperimentVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        if (variant.FactoryId != CasualCommanderFactoryId && variant.FactoryId != LibraryAcquisitionFactoryId)
            throw new ArgumentException("Unsupported experiment policy factory.", nameof(variant));
        if (variant.FactoryId == LibraryAcquisitionFactoryId && !variant.AttachmentEvaluation)
            throw new ArgumentException("The acquisition candidate requires full attachment evaluation.", nameof(variant));
        return new CasualCommanderPolicy(new(variant.AttachmentEvaluation, variant.FactoryId == LibraryAcquisitionFactoryId));
    }

    public static AiExperimentVariant Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("An experiment variant must be an object.");
        string? factory = null;
        bool attachment = true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) {
            if (!seen.Add(property.Name)) throw new ArgumentException("Duplicate experiment variant setting.");
            switch (property.Name) {
                case "factoryId" when property.Value.ValueKind == JsonValueKind.String:
                    factory = property.Value.GetString(); break;
                case "attachmentEvaluation" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    attachment = property.Value.GetBoolean(); break;
                default: throw new ArgumentException("Unsupported experiment variant setting.");
            }
        }
        if (factory != CasualCommanderFactoryId && factory != LibraryAcquisitionFactoryId)
            throw new ArgumentException("Unsupported experiment policy factory.");
        if (factory == LibraryAcquisitionFactoryId && !attachment)
            throw new ArgumentException("The acquisition candidate requires full attachment evaluation.");
        return new(factory, attachment);
    }
}
