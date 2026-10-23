using Mana.Table.Presentation;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly TableArtworkPreparation tableArtwork = new();
    private void HandleActivityIntent(ShellIntent.Activity intent)
    {
        switch (intent) {
            case ShellIntent.RetryStartup: RetryStartup(); break;
            case ShellIntent.ExitStartup: RequestExit(); break;
            case ShellIntent.DismissActivity: DismissActivity(); break;
        }
    }
    private bool startupActivity, startupFailed, activityTracked, activityForeground, activityFailed, activityWasVisible, activityScene;
    private string activityTitle = "Opening Mana Table", activityDetail = "Preparing your next Commander game.", activityStage = "Card library";
    private double activityStarted, activityCompleted = -1, sceneRevealAt = -1;
    private long activityGeneration;
    private bool ActivityPending => activityTracked && activityCompleted < 0;
    private bool ActivityFailed => activityFailed;
    private bool StartupVisible => initialState == null && fixture == null
        && (!loaded || startupActivity && (ActivityPending || now < activityStarted + .45));
    private bool ActivityOpen => StartupVisible || tableArtwork.Waiting || activityTracked && activityForeground
        && (ActivityPending || activityFailed || now < activityStarted + .22);
    private bool InlineActivityVisible => activityTracked && !activityForeground && match == null && deckEditor
        && (ActivityPending || now < activityCompleted + 1.6);
    private string ActivityScope => $"activity:{activityGeneration}:{startupFailed}:{activityFailed}";

    private void Work(Func<Task> operation, string title = "Updating your table", string detail = "Applying your changes.", bool? foreground = null, bool scene = false)
    {
        if (busy || closing || loaded && ActivityOpen) return;
        error = ""; activityFailed = false; activityTracked = true;
        activityForeground = foreground ?? match == null;
        activityScene = scene;
        activityTitle = title; activityDetail = detail;
        activityStarted = now; activityCompleted = -1; activityGeneration++;
        ClearExperienceInput();
        session.Run(operation);
    }

    private void FinishActivity()
    {
        if (ActivityPending) activityCompleted = now;
    }

    private void FailActivity()
    {
        if (!ActivityPending) return;
        activityFailed = true;
        if (!loaded) startupFailed = true;
    }

    private void DismissActivity()
    {
        if (ActivityPending) return;
        activityTracked = activityForeground = activityFailed = false;
        error = "";
        ClearExperienceInput();
    }

    private void StartupStage(string stage, string title, string detail) => updates.Enqueue(() => {
        activityStage = stage; activityTitle = title; activityDetail = detail;
    });

    private void RetryStartup()
    {
        if (busy || closing || loaded) return;
        startupFailed = false; startupActivity = true; activityStage = "Card library";
        Work(async () => {
            await services.Initialize();
            StartupStage("Your decks", "Finding your decks", "Opening the decks saved for this player.");
            var saved = await catalog.DecksAsync(); updates.Enqueue(() => decks = saved);
            await ReadDeletedDecks();
            StartupStage("Commander presets", "Preparing Commander", "Gathering the precons available at your table.");
            var available = await catalog.PresetsAsync(); updates.Enqueue(() => presets = available);
            StartupStage("Your table", "Setting your place", "Getting your deck and table ready.");
            if (saved.FirstOrDefault() is { } deck) await ReadSelectedDeck(deck);
            updates.Enqueue(() => { loaded = true; message = "Choose your next Commander game."; });
        }, "Opening Mana Table", "Loading your card library. This can take a moment on the first launch.", true);
    }

    private void UpdateActivity()
    {
        tableArtwork.Update(match, match == null ? [] : ArtPreloadCards().Visible, resources.Artwork, now);
        if (startupActivity && loaded && !ActivityPending && now >= activityStarted + .45) startupActivity = false;
        bool visible = ActivityOpen;
        if (activityWasVisible && !visible) { sceneRevealAt = now; ClearExperienceInput(); }
        activityWasVisible = visible;
    }
}
