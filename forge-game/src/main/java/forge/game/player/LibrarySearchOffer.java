package forge.game.player;

import forge.game.ability.ApiType;
import forge.game.spellability.SpellAbility;
import forge.game.trigger.WrappedAbility;
import forge.game.zone.ZoneType;

/** Public search intent, without examining the contents of any hidden zone. */
public record LibrarySearchOffer(Integer libraryOwnerId, ZoneType destination, String cardName) implements java.io.Serializable {
    public static final String CONTEXT_KEY = "LibrarySearchOffer";

    /** An unresolved player target is deliberately not an implied self-target. */
    public static LibrarySearchOffer targeting(SpellAbility ability) {
        while (ability instanceof WrappedAbility wrapped) ability = wrapped.getWrappedAbility();
        if (ability == null || ability.getApi() != ApiType.ChangeZone
                || ability.hasParam("Defined") || ability.hasParam("ChooseFromDefined") || ability.hasParam("NoLooking")
                || ability.hasParam("OriginAlternative") || !ability.hasParam("Origin")
                || !ZoneType.listValueOf(ability.getParam("Origin")).equals(java.util.List.of(ZoneType.Library))) return null;
        ZoneType destination = ZoneType.smartValueOf(ability.getParam("Destination"));
        if (destination == null) return null;
        return new LibrarySearchOffer(null, destination, exactCardName(ability));
    }

    public LibrarySearchOffer forLibrary(Player owner) {
        return new LibrarySearchOffer(owner.getId(), destination, cardName);
    }

    /** A whole optional trigger can be described as a search only when its
     * sole effect already identifies the default library unambiguously. */
    public static LibrarySearchOffer optionalTrigger(SpellAbility ability) {
        LibrarySearchOffer search = targeting(ability);
        if (search == null || ability.usesTargeting() || ability.hasParam("DefinedPlayer") || ability.hasParam("Chooser")
                || ability.getSubAbility() != null || !ability.getAdditionalAbilities().isEmpty()
                || !ability.getAdditionalAbilityLists().isEmpty()) return null;
        Player owner = ability.getActivatingPlayer();
        return owner == null ? null : search.forLibrary(owner);
    }

    private static String exactCardName(SpellAbility ability) {
        // Match the rules' exact-name property, including its escaped commas.
        // Unions, chosen-name variables and compound restrictions stay opaque.
        String restriction = ability.getParamOrDefault("ChangeType", "");
        if (!restriction.startsWith("Card.named")) return "";
        String name = restriction.substring("Card.named".length());
        if (name.isBlank() || name.contains(",") || name.contains("+")) return "";
        return name.replace(';', ',').replace('_', ' ');
    }
}
