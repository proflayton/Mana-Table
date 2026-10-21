namespace Mana.Table;

internal sealed partial class TableGame
{
    private ExperiencePreferences experience;
    private bool welcomeOpen, tableGuideOpen;
    private bool ExperienceOpen => welcomeOpen || tableGuideOpen;
    private string ExperienceScope => $"experience:{welcomeOpen}:{tableGuideOpen}";

    private void SaveExperience()
    {
        try { experience.Save(profile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = "Could not save your guide preferences: " + ex.Message; }
    }

    private void ClearExperienceInput()
    {
        session.CancelAdvance(); priority.Reset();
        pressed = dragSource = null; dragging = false;
        hovered = null; readingId = readingZone = null;
    }

    private void DismissWelcome()
    {
        welcomeOpen = false;
        experience = experience with { WelcomeSeen = true };
        SaveExperience(); ClearExperienceInput();
    }

    private void ChooseWelcome(string intent)
    {
        if (!loaded || busy || Connected || match != null) return;
        DismissWelcome();
        if (intent == "friends") { SetupMode(false); message = "Host a table or join your friends. Confirm your deck before readying up."; }
        else if (intent == "decks") { OpenDeckPicker(); message = "Choose a saved deck, try a precon, or build your own."; }
        else { SetupMode(true); if (selectedDeck == null) OpenDeckPicker(); message = "Choose and confirm your deck, then pick three AI opponents."; }
    }

    private void OpenWelcome()
    {
        if (match != null || Connected || busy || deckEditor || setupModal.Length > 0) return;
        welcomeOpen = true; ClearExperienceInput();
    }

    private void OpenTableGuide()
    {
        tableGuideOpen = true; ClearExperienceInput();
    }

    private void CloseTableGuide()
    {
        tableGuideOpen = false;
        experience = experience with { TableGuideSeen = true };
        SaveExperience(); ClearExperienceInput();
    }
}
