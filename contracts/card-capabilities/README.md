# Attachment capability 1.0

This portable contract describes only the direct continuous modifiers of a
normal creature Aura. Archive and Mana Table share the schema and conformance
fixtures. It contains no live card occurrences, action permissions, deck-private
state or executable rules. Forge retains legality and resolution authority.

The two declared features are literal signed power/toughness deltas and a closed
list of unconditional granted keywords. Both features must be examined. A Known
modifier has bounded integer deltas and at least one nonzero delta or keyword.
An Unknown/Unsupported modifier has null deltas and no keyword claims. Unknown
coverage is partial with at least one reason; Known coverage is complete for
these declared features with no unsupported reasons. Consumers enforce these
cross-field constraints in addition to JSON Schema shape validation.

The provider fingerprint identifies the structural source inspected by the
versioned extractor. It is a compatibility/provenance key, not authentication.
Live consumers still bind the descriptor to the current entitled source and
current offered target domain. A modified, concealed, stale or unsupported
source cannot inherit a cached Known description.

Future triggers, eventual return-to-hand value, other card abilities, responses,
replacement effects and an entire card's strategic usefulness are outside this
contract. Complete coverage here is not complete understanding of a card or a
guarantee that its effect occurs. Potential graph conversion preserves this scope.

The schema and authored bridge material in this directory are available under
the accompanying MIT license for use by both projects. Forge implementation
code retains its repository license.
