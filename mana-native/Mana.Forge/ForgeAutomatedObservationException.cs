using System.Text.Json;

namespace Mana.Forge;

// The adapter's friend-test diagnostics can retain the rejected wire before
// decoding succeeds. This never becomes a policy frame or command authority,
// and the private contents are excluded from public exception text/properties.
internal sealed class ForgeAutomatedObservationException(JsonElement rejectedWire, Exception inner)
    : IOException("The rules provider published an invalid automated observation.", inner)
{
    internal JsonElement RejectedWire { get; } = rejectedWire.Clone();
}
