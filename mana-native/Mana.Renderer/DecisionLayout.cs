using Mana.Contracts;

namespace Mana.Renderer;

// Shared by table reservations and the game painter. Source context uses the
// existing occurrence; only real stack items retain their stack-area footprint.
public static class DecisionLayout
{
    public static bool UsesSourceWorkspace(Decision? decision) => decision is {
        Payload: OpeningHandPayload or ConfirmationPayload
    } or { Payload: TargetSelectionPayload { Search: not null } }
        or { Payload: EntitySelectionPayload { CounterPayment: not null } }
        or { Payload: OptionsPayload { CounterPayment: not null } }
        or { Payload: OptionsPayload { ManaPaymentContext: not null } };
}
