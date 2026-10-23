namespace Mana.Table;

internal sealed partial class TableApplication
{
    private bool tableGuideOpen;
    private bool ExperienceOpen => tableGuideOpen;
    private string ExperienceScope => $"guide:{tableGuideOpen}";

    private void ClearExperienceInput()
    {
        session.CancelAdvance(); priority.Reset();
        pressed = dragSource = null; dragging = false;
        hovered = null; cardPresentation.Collapse();
    }

    private void ChoosePlayMode(bool solo)
    {
        if (!loaded || busy || Connected || match != null) return;
        ClearExperienceInput(); SetupMode(solo);
        message = solo ? "Choose your deck and play." : "Host a table or join your friends.";
    }
    private void OpenDeckLibrary()
    {
        if (!loaded || busy || match != null) return;
        ClearExperienceInput(); OpenDeckPicker();
    }
    private void OpenPlayHome()
    {
        if (!loaded || busy || match != null) return;
        CloseDeckEditor(); CloseSetupModal(); ClearExperienceInput();
    }

    private void OpenTableGuide()
    {
        tableGuideOpen = true; ClearExperienceInput();
    }

    private void CloseTableGuide()
    {
        tableGuideOpen = false; ClearExperienceInput();
    }
}
