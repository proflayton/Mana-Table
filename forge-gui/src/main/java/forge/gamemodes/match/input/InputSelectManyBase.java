package forge.gamemodes.match.input;

import com.google.common.collect.Iterables;
import forge.game.GameEntity;
import forge.game.GameEntityView;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.spellability.SpellAbility;
import forge.localinstance.properties.ForgePreferences;
import forge.model.FModel;
import forge.player.PlayerControllerHuman;

import java.util.ArrayList;
import java.util.Collection;
import java.util.List;

public abstract class InputSelectManyBase<T extends GameEntity> extends InputSyncronizedBase {
    private static final long serialVersionUID = -2305549394512889450L;

    protected boolean bCancelled = false;
    protected final int min;
    protected final int max;
    protected boolean allowCancel = false;
    protected SpellAbility sa = null;
    protected CardView card;
    protected String tallyType;
    protected int tally;

    protected String message = "Source-Card-Name - Select %d more card(s)";

    /** The engine operation owning this selection, independent of its display message. */
    public SpellAbility getSpellAbility() { return sa; }

    public final boolean isSelectionActive() { return !isFinished(); }

    /** A publication may copy the concrete input's current candidates once.
     * The returned objects remain rules-owned and must not enter a wire value. */
    public final List<T> getSelectableEntitiesSnapshot() {
        if (!isSelectionActive()) { return List.of(); }
        final List<T> candidates = new ArrayList<>();
        for (T candidate : getSelectableEntities()) { candidates.add(candidate); }
        return List.copyOf(candidates);
    }

    /** Concrete inputs own click membership: selected list/convoke entities can
     * toggle, while a counter source is clickable only while it can allocate. */
    public final boolean canSelectEntity(GameEntity entity) {
        if (!isSelectionActive() || entity == null) { return false; }
        for (T candidate : getSelectableEntities()) if (candidate == entity) { return true; }
        return false;
    }

    protected InputSelectManyBase(final PlayerControllerHuman controller, final int min, final int max) {
        super(controller);
        if (min > max) {
            throw new IllegalArgumentException("Min must not be greater than Max");
        }
        this.min = min;
        this.max = max;
    }
    
    protected InputSelectManyBase(final PlayerControllerHuman controller, final int min, final int max, final SpellAbility sa0) {
    	this(controller,min,max);
    	this.sa = sa0;
        if (sa0 != null) {
            this.card = sa0.getView().getHostCard();
        }
    }

    protected InputSelectManyBase(final PlayerControllerHuman controller, final int min, final int max, final SpellAbility sa0, final String tallyType0, final int tally0) {
        this(controller,min,max);
        this.sa = sa0;
        if (sa0 != null) {
            this.card = sa0.getView().getHostCard();
        }
        this.tallyType = tallyType0;
        this.tally = tally0;
    }

    protected InputSelectManyBase(final PlayerControllerHuman controller, final int min, final int max, final CardView card0) {
    	this(controller,min,max);
    	this.sa = null;
    	this.card = card0;
    }

    protected void refresh() {
        if (hasAllTargets()) {
            selectButtonOK();
        } else {
            this.showMessage();
        }
    }

    protected abstract boolean hasEnoughTargets();
    protected abstract boolean hasAllTargets();

    protected abstract String getMessage();

    /** Current input authority, including selected entities that can be toggled off. */
    protected abstract Iterable<? extends T> getSelectableEntities();

    @Override
    public final void showMessage() {
        // Publish after this input owns the UI, never while constructing it.
        // Every concrete selection input must provide its engine candidates.
        final List<CardView> cards = new ArrayList<>();
        for (T entity : getSelectableEntities()) {
            if (entity instanceof Card selectableCard) {
                cards.add(selectableCard.getView());
            }
        }
        getController().getGui().setSelectables(cards, min, max);
        if (FModel.getPreferences().getPrefBoolean(ForgePreferences.FPref.UI_DETAILED_SPELLDESC_IN_PROMPT) &&
                card != null) {
            final StringBuilder sb = new StringBuilder();
            sb.append(card.toString());
            if (sa != null) {
                if (sa.isSpell() && sa.getHostCard().isPermanent() && !sa.hasParam("SpellDescription") && !sa.getPayCosts().isOnlyManaCost()) {
                    sb.append(" - ").append(sa.getPayCosts().toString());
                } else if (!sa.toString().isEmpty()) { // some spell abilities have no useful string value
                    sb.append("\n - ").append(sa.toString());
                } else {
                    sb.append("\n");
                }
            }
            sb.append("\n").append(getMessage());
            showMessage(sb.toString(), card);
        } else if (card != null) {
            showMessage(getMessage(), card);
        } else {
            showMessage(getMessage());
        }
        updateSelectionButtons();
    }

    protected void updateSelectionButtons() {
        getController().getGui().updateButtons(getOwner(), hasEnoughTargets(), allowCancel, true);
    }

    @Override
    protected final void onStop() {
        setFinished();
        getController().getGui().clearSelectables();
        super.onStop();
    }

    @Override
    protected void onCancel() {
        setFinished();
        bCancelled = true;
        resetUsedToPay();
        this.getSelected().clear();
        this.stop();
    }

    public final boolean hasCancelled() {
        return bCancelled;
    }

    public abstract Collection<T> getSelected();
    public T getFirstSelected() { return Iterables.getFirst(getSelected(), null); }

    @Override
    protected final void onOk() {
        setFinished();
        resetUsedToPay();
        this.stop();
    }

    public void setMessage(String message0) {
        this.message = message0;
    }

    protected void onSelectStateChanged(final GameEntity ge, final boolean newState) {
        getController().getGui().setHighlighted(List.of(GameEntityView.get(ge)), newState);
    }

    private void resetUsedToPay() {
        final List<GameEntityView> views = new ArrayList<>();
        for (final GameEntity ge : getSelected()) views.add(GameEntityView.get(ge));
        getController().getGui().setHighlighted(views, false);
    }

    public final void setCancelAllowed(boolean allow) {
        this.allowCancel = allow;
    }
}
