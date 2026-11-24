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

## Live compatibility and source binding

The portable envelope is an offline definition artifact. Live operation graphs
carry only its `attachment` modifier body on an actual Forge `ApiType.Attach`
operation. Java retains the legacy `Unknown` effect/knowledge enum values; new
native clients admit the optional body as internal `Attach`. Older clients keep
the original uncertainty without receiving an unrecognized enum value.

During announcement, the Aura may exist only as the current target prompt's
read-only source portrait. An additive `attachmentSourceCardId` identifies that
portrait without changing the generic zone-entity catalog. Native admission
requires the current active target operation, matching visible nonactionable
portrait and owned actor. Its internal `DecisionSource` binding has no entities
and is valid only in the source role. It cannot identify a recipient, payment
resource, action offer or committed stack source. Ordinary targets retain their
existing current occurrence validation and command permissions.

## Export

After building the engine, run from the repository root:

```text
java -cp forge-api/target/forge-engine.jar forge.api.AttachmentCapabilityExport forge-gui/res Rancor rancor.json
```

An optional final argument selects a zero-based face index. The name selects a
public catalog definition; structural scripts determine every modifier claim.
The command initializes catalog resources without creating a match. The four
fixtures here are actual exports from the versioned extractor, including two
unsupported definitions. Archive validates this same schema and converts it to
potential graph facts with an unbound recipient and explicit live-evaluation gaps.

The schema and authored bridge material in this directory are available under
the accompanying MIT license for use by both projects. Forge implementation
code retains its repository license.
