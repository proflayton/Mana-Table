/*
 * Forge: Play Magic: the Gathering.
 * Copyright (C) 2011  Forge Team
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */
package forge.gamemodes.match.input;

import forge.game.card.Card;
import forge.game.GameEntity;
import forge.game.GameEntityView;
import forge.game.combat.CombatInputState;
import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.player.PlayerView;
import forge.game.spellability.SpellAbility;
import forge.gui.FThreads;
import forge.player.PlayerControllerHuman;
import forge.util.ITriggerEvent;

import java.util.ArrayList;
import java.util.List;
import java.util.Observable;
import java.util.Observer;
import java.util.concurrent.atomic.AtomicReference;

/**
 * <p>
 * GuiInput class.
 * </p>
 * 
 * @author Forge
 * @version $Id: InputProxy.java 24769 2014-02-09 13:56:04Z Hellfish $
 */
public class InputProxy implements Observer {

    /** The input. */
    private AtomicReference<Input> input = new AtomicReference<>();
    private final PlayerControllerHuman controller;
    private long inputSequence;
    private long autoPassedSequence = -1;

//    private static final boolean DEBUG_INPUT = true; // false;

    public InputProxy(final PlayerControllerHuman controller0) {
        controller = controller0;
    }

    public void publishCombatChoices(Input expected, CombatInputState choices) {
        synchronized (input) {
            if (input.get() == expected) controller.getGui().setCombatChoices(expected.getOwner(), inputSequence, choices);
        }
    }

    public boolean assignAttack(long sequence, CardView attackerView, GameEntityView defenderView) {
        synchronized (input) {
            Input current = input.get();
            if (sequence != inputSequence || current != controller.getInputQueue().getInput()
                    || !(current instanceof InputAttack attack) || attack.isFinished()) return false;
            Card attacker = getCard(attackerView);
            GameEntity defender = defenderView instanceof PlayerView player ? controller.getGame().getPlayer(player)
                    : defenderView instanceof CardView card ? getCard(card) : null;
            return attack.assign(attacker, defender);
        }
    }

    public boolean assignBlock(long sequence, CardView attackerView, CardView blockerView) {
        synchronized (input) {
            Input current = input.get();
            if (sequence != inputSequence || current != controller.getInputQueue().getInput()
                    || !(current instanceof InputBlock block) || block.isFinished()) return false;
            return block.assign(getCard(attackerView), getCard(blockerView));
        }
    }

    @Override
    public final void update(final Observable observable, final Object obj) {
        final Input nextInput = controller.getInputQueue().getActualInput(controller);
/*        if(DEBUG_INPUT) 
            System.out.printf("%s ... \t%s on %s, \tstack = %s%n", 
                    FThreads.debugGetStackTraceItem(6, true), nextInput == null ? "null" : nextInput.getClass().getSimpleName(), 
                            game.getPhaseHandler().debugPrintState(), Singletons.getControl().getInputQueue().printInputStack());
*/
        final long sequence;
        synchronized (input) {
            final Input previousInput = input.getAndSet(nextInput);
            if (previousInput != nextInput) {
                inputSequence++;
            }
            sequence = inputSequence;
        }
        Class<?> inputClass = nextInput.getClass();
        while (inputClass.getSimpleName().isEmpty()) {
            inputClass = inputClass.getSuperclass();
        }
        controller.getGui().setInputState(nextInput.getOwner(), inputClass.getSimpleName(), sequence,
                !(nextInput instanceof InputLockUI), nextInput instanceof InputPassPriority priority && priority.canAutoPass());
        if (!(nextInput instanceof InputLockUI)) {
            controller.getGui().setCurrentPlayer(nextInput.getOwner());
        }
        final Runnable showMessage = () -> {
            Input current = getInput();
            controller.getInputQueue().syncPoint();
            //System.out.printf("\t%s > showMessage @ %s/%s during %s%n", FThreads.debugGetCurrThreadId(), nextInput.getClass().getSimpleName(), current.getClass().getSimpleName(), game.getPhaseHandler().debugPrintState());
            current.showMessageInitial();
        };
        FThreads.invokeInEdtLater(showMessage);
    }

    /** The client snapshot is advisory; the host validates and consumes the exact input. */
    public boolean passPriorityIfNoResponse(final long expectedSequence) {
        synchronized (input) {
            final Input current = input.get();
            final Player player = controller.getPlayer();
            if (inputSequence != expectedSequence || autoPassedSequence == expectedSequence
                    || !(current instanceof InputPassPriority priority) || priority.isFinished()
                    || !priority.canAutoPass()
                    || current != controller.getInputQueue().getInput()
                    || player == null || !player.getView().equals(current.getOwner())
                    || player.getView().hasAvailableActions()) {
                return false;
            }
            // Consume before dispatch: mana-loss confirmation can delay completion.
            autoPassedSequence = expectedSequence;
            priority.passPriority();
            return true;
        }
    }
    /**
     * <p>
     * selectButtonOK.
     * </p>
     */
    public final void selectButtonOK() {
        final Input inp = getInput();
        if (inp != null) {
            inp.selectButtonOK();
        }
    }

    /**
     * <p>
     * selectButtonCancel.
     * </p>
     */
    public final void selectButtonCancel() {
        Input inp = getInput();
        if (inp != null) {
            inp.selectButtonCancel();
        }
    }

    public final void selectPlayer(final PlayerView playerView, final ITriggerEvent triggerEvent) {
        final Input inp = getInput();
        if (inp != null) {
            final Player player = controller.getGame().getPlayer(playerView);
            if (player != null) {
                inp.selectPlayer(player, triggerEvent);
            }
        }
    }

    private Card getCard(final CardView cardView) {
        return controller.getCard(cardView);
    }

    public final String getActivateAction(final CardView cardView) {
        final Input inp = getInput();
        if (inp != null) {
            final Card card = getCard(cardView);
            if (card != null) {
                return inp.getActivateAction(card);
            }
        }
        return null;
    }

    public final boolean selectCard(final CardView cardView, final List<CardView> otherCardViewsToSelect, final ITriggerEvent triggerEvent) {
        final Input inp = getInput();
        if (inp != null) {
            final Card card = getCard(cardView);
            if (card != null) {
                List<Card> otherCardsToSelect = null;
                if (otherCardViewsToSelect != null) {
                    for (CardView cv : otherCardViewsToSelect) {
                        final Card c = getCard(cv);
                        if (c != null) {
                            if (otherCardsToSelect == null) {
                                otherCardsToSelect = new ArrayList<>();
                            }
                            otherCardsToSelect.add(c);
                        }
                    }
                }
                return inp.selectCard(card, otherCardsToSelect, triggerEvent);
            }
        }
        return false;
    }

    public final boolean selectAbility(final SpellAbility sa) {
        final Input inp = getInput();
        if (inp != null) {
            if (sa != null) {
                return inp.selectAbility(sa);
            }
        }
        return false;
    }

    public final void alphaStrike() {
        final Input inp = getInput();
        if (inp instanceof InputAttack) {
            ((InputAttack) inp).alphaStrike();
        }
    }

    /** {@inheritDoc} */
    @Override
    public final String toString() {
        Input inp = getInput();
        return null == inp ? "(null)" : inp.toString();
    }

    /** @return {@link forge.gui.InputProxy.InputBase} */
    public Input getInput() {
        return input.get();
    }
}
