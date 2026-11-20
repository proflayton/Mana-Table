package forge.gamemodes.match.input;

import forge.util.IHasForgeLog;
import forge.gamemodes.match.Selection;
import forge.gui.FThreads;
import forge.gui.error.BugReporter;
import forge.player.PlayerControllerHuman;

import java.util.concurrent.CountDownLatch;

public abstract class InputSyncronizedBase extends InputBase implements InputSynchronized, IHasForgeLog {

    private static final long serialVersionUID = 8756177361251703052L;
    private final CountDownLatch cdlDone;

    public InputSyncronizedBase(final PlayerControllerHuman controller) {
        super(controller);
        cdlDone = new CountDownLatch(1);
    }

    @Override
    public void awaitLatchRelease() {
        FThreads.assertExecutedByEdt(false);
        netLog.trace("awaitLatchRelease() starting on {}, thread = {}", this.getClass().getSimpleName(), Thread.currentThread().getName());
        try (var suspended = getController().getGame().getAction().suspendState()) {
            getController().getGui().awaitInput(cdlDone);
        } catch (final InterruptedException e) {
            BugReporter.reportException(e);
        }
        netLog.trace("awaitLatchRelease() UNBLOCKED on {}, thread = {}", this.getClass().getSimpleName(), Thread.currentThread().getName());
    }

    @Override
    public final void relaseLatchWhenGameIsOver() {
        cdlDone.countDown();
    }

    public void showAndWait() {
        getController().getInputQueue().setInput(this);
        awaitLatchRelease();
    }

    @Override
    public final void stop() {
        onStop();
        // Retire the input before a selection cleanup can publish a new frame.
        // The callback may inspect this input before queue removal completes.
        setFinished();
        getController().getGui().setSelection(Selection.ofChosen(getController().getKeptMarks()));

        // thread irrelevant
        if (getController().getInputQueue().getInput() != null) {
            getController().getInputQueue().removeInput(InputSyncronizedBase.this);
        }
        cdlDone.countDown();
    }

    protected void onStop() { }
}
