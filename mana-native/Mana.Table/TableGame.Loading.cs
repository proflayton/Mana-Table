namespace Mana.Table;

internal sealed partial class TableGame
{
    private bool startupActivity, startupFailed, activityTracked, activityForeground, activityFailed, activityWasVisible, activityScene;
    private string activityTitle = "Opening Mana Table", activityDetail = "Preparing your next Commander game.", activityStage = "Card library";
    private double activityStarted, activityCompleted = -1, sceneRevealAt = -1;
    private long activityGeneration;
    private bool ActivityPending => activityTracked && activityCompleted < 0;
    private bool ActivityFailed => activityFailed;
    private bool StartupVisible => initialState == null && fixture == null
        && (!loaded || startupActivity && (ActivityPending || now < activityStarted + .45));
    private bool ActivityOpen => StartupVisible || activityTracked && activityForeground
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
            await engine.InitializeAsync();
            StartupStage("Your decks", "Finding your decks", "Opening the decks saved for this player.");
            var saved = await engine.DecksAsync(); updates.Enqueue(() => decks = saved);
            StartupStage("Commander presets", "Preparing Commander", "Gathering the precons available at your table.");
            var available = await engine.PresetsAsync(); updates.Enqueue(() => presets = available);
            StartupStage("Your table", "Setting your place", "Getting your deck and table ready.");
            if (saved.FirstOrDefault() is { } deck) await ReadSelectedDeck(deck);
            updates.Enqueue(() => { loaded = true; message = "Choose your next Commander game."; });
        }, "Opening Mana Table", "Loading your card library. This can take a moment on the first launch.", true);
    }

    private void UpdateActivity()
    {
        if (startupActivity && loaded && !ActivityPending && now >= activityStarted + .45) startupActivity = false;
        bool visible = ActivityOpen;
        if (activityWasVisible && !visible) { sceneRevealAt = now; ClearExperienceInput(); }
        activityWasVisible = visible;
    }
}
