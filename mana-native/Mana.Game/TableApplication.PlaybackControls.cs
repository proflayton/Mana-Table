namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void UseAutomaticControl(bool value) { StopQuietPriority(); SetAutomatic(value); }
    private void ToggleHoldControl() { StopQuietPriority(); held = !held; priority.Reset(); }
}
