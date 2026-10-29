namespace Mana.Table;

internal sealed partial class TableApplication
{
    private bool tableGuideOpen;
    private bool ExperienceOpen => tableGuideOpen;
    private string ExperienceScope => $"guide:{tableGuideOpen}";

    private Hit ShellHit(string id, Microsoft.Xna.Framework.Rectangle bounds, UiIntent intent)
        => new(id, ScopeFor(intent), bounds, intent);

    private void HandleShellIntent(ShellIntent intent)
    {
        switch (intent) {
            case ShellIntent.DismissError: error = ""; break;
            case ShellIntent.ReturnToSetup: ReturnFromMatch(); break;
            case ShellIntent.Lobby lobbyIntent: HandleLobbyIntent(lobbyIntent); break;
            case ShellIntent.Update update: HandleUpdateIntent(update); break;
            case ShellIntent.Social social: HandleSocialIntent(social); break;
            case ShellIntent.Activity activity: HandleActivityIntent(activity); break;
            case ShellIntent.Guide guide:
                if (guide is ShellIntent.OpenGuide) OpenTableGuide();
                else if (guide is ShellIntent.CloseGuide) CloseTableGuide();
                break;
            case ShellIntent.Lab lab: HandleLabIntent(lab); break;
            default: throw new NotSupportedException($"Unsupported shell intent: {intent.GetType().Name}");
        }
    }

    // Reconcile navigation once during Update, before the next frame publishes
    // its hit scopes. Reading a frame never changes its selected item or page.
    private void PrepareShellPresentation()
    {
        if (match == null) PrepareLobbyPresentation();
        PrepareSocialPresentation();
        if (ExperienceLabOpen) labGalleryPage = Math.Clamp(labGalleryPage, 0, Math.Max(0, (Mana.Scenarios.ExperienceScenarios.All.Count - 1) / 6));
    }

    private void ClearExperienceInput(bool cancelAdvance = true)
    {
        CancelReleasedPlayIntent();
        if (cancelAdvance) session.CancelAdvance();
        pressed = dragSource = null; dragging = false;
        hovered = null; matchPresentation.Cards.Collapse();
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
