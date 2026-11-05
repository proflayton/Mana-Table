using Mana.Contracts;

namespace Mana.Table;

// Test-driver query retirement only. Callers must observe again before asking
// for another offer; execution/readback are deliberately outside this helper.
internal static class SmokeSourceReads
{
    internal static async Task<SourceActionSet?> TryReadAsync(Func<Task<SourceActionSet>> query,
        params string[] legacyRejections)
    {
        try { return await query(); }
        catch (EngineSourceReadException error) when (error.Reason == EngineSourceReadFailure.Superseded) { return null; }
        catch (EngineCommandRejectedException error) when (legacyRejections.Contains(error.Message, StringComparer.Ordinal)) { return null; }
    }
}
