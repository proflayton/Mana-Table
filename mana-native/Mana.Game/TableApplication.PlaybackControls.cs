namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void UseAutomaticControl(bool value) {
        StopQuietPriority(); StopStackResolution(); StopGroupBlocks();
        if (value) { held = false; priorityAttention.Resume(); }
        SetAutomatic(value);
    }
    private void ToggleHoldControl() {
        StopQuietPriority(); StopStackResolution(); StopGroupBlocks(); held = !held;
        if (!held) priorityAttention.Resume();
    }
}
