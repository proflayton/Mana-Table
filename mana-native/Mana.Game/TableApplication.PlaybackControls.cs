namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void UseAutomaticControl(bool value) { StopQuietPriority(); StopStackResolution(); SetAutomatic(value); }
    private void ToggleHoldControl() { StopQuietPriority(); StopStackResolution(); held = !held; }
}
