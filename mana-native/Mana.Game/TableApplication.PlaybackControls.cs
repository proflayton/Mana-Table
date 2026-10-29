namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void UseAutomaticControl(bool value) {
        if (!value) session.ClearAllOptionalPaymentPolicies();
        StopQuietPriority(); StopStackResolution(); StopGroupBlocks();
        if (value) { held = false; priorityAttention.Resume(); }
        SetAutomatic(value);
    }
    private void ToggleHoldControl() {
        session.ClearAllOptionalPaymentPolicies();
        StopQuietPriority(); StopStackResolution(); StopGroupBlocks(); held = !held;
        if (!held) priorityAttention.Resume();
    }
}
