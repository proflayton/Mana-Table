namespace Mana.Forge;

// The backend emits this code only before a metadata admission mutates consent.
// It is distinct from rejected gameplay, retired reads and uncertain receipts.
internal sealed class ForgeAutomatedPriorityRetiredException : Exception
{
    internal ForgeAutomatedPriorityRetiredException() : base("The automated priority admission has retired.") { }
}
