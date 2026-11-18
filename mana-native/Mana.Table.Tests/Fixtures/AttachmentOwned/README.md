# Actual owned attachment envelopes

These five gzip files contain unmodified `02-owned-observation.json` bytes from
the final passing `forge.api.AuraAttachmentJourneyTest` journeys. They are
synthetic test-game private-audience captures, with no actual user data. Compression
used .NET `GZipStream` directly over the original bytes, without parsing or
reserializing JSON. Decompression reproduces the raw SHA256 values below.

Provider production source: `b2ebacdff27a81af77723b6ecbab694ec599b857`.
Baseline card definitions: `0fd8548f464109264681b9be963764ab47a3ff77`.
The renamed positive Aura is generated structurally by the test at the provider
revision; its name and misleading description/AI hint do not supply semantics.

Capture source: the provider worktree's
`.tools/attachment-execution/journeys-final/<fixture>/02-owned-observation.json`.
The fixture labels correspond to these real journeys:

| Fixture | Java test method |
| --- | --- |
| attachment-rancor | rancorCastsOntoFriendlyCreatureWithBothSidesLegallyOffered |
| attachment-renamed-positive | renamedStructuralAuraCastsAndResolvesWithoutNameOrAiHintAuthority |
| attachment-dead-weight | deadWeightPreservesNegativeModifiersAndOpponentOwnershipThroughResolution |
| attachment-mixed | dynamicMixedModifierRemainsUnknownWhileTheActualRulesDetermineItsStats |
| attachment-conditional | conditionalModifierRemainsUnknownEvenWhileActualBaseEffectResolves |

| Fixture | Raw JSON SHA256 | Compressed file SHA256 |
| --- | --- | --- |
| attachment-rancor | 3828DA0F03C1EE255BAA48C048837FAC5BD347F03E3650C639B44C1394D16DCD | 435F50CAB9ABF4A932BD34CB2D63FD1B43C6787A4004A82893F33D0C452F50DB |
| attachment-renamed-positive | 0661F01F7C0336BFD3C2A254106D2A5E7B94D181CB49D2C4F358B0713B680A55 | 67D407D23C44321F6FDC4C6FBCE0A2247D2090A776706F849407CB821752D151 |
| attachment-dead-weight | 925E01E4978BB50CA9D7F8ACDF103B9F5B04E69F359E40C114958A661ACED6A4 | 10A185E408075BA8678E6E00BA892BD6D4DA84ABBA3A5C248E6BB54A03A9EBBE |
| attachment-mixed | F2497E01B2FBB3AE21561E8EEA0A9EBDFEA05E29FA8017A490A50F605BCC4552 | 897740EA3C6E6DCB6E963B6A8678C588FCAE52007FB76204BC766FDDA346D03E |
| attachment-conditional | 66684FDBA87896EA3669BDF02CCEFB541CEB35F413539D99F01F9F3DA307C84F | DB2A3A50BF5F9F2FBB9690C068DA18FC272652AFA04F1763515795194F3CA355 |

The Java journeys call the provider in-process. They do not capture a transport
handshake. The same provider's `DesktopEngine.java:96` ready declaration includes
`certified-one-step-undo` and `owned-decision-observation`. Native acceptance uses
that explicit diagnostic capability context and verifies that decoding the same
Undo-bearing bytes without the capability is rejected. It never strips Undo.

The `attachment-owned` headless presentation group and the full headless inventory
consume these fixtures through production native admission and bounded policy
replay. Each run writes raw hashes, admitted replays and results beneath its output
profile's `attachment-owned` directory. The CLI also accepts an external directory
with plain `.json` files; when a plain file is absent it reads `.json.gz` instead.
The recorded `sha256` always covers decompressed original envelope bytes, while
`storedSha256` covers the actual input file.

Native replay uses explicitly empty authored diagnostic history and executes no
engine commands. These captures are taken at the target prompt. Their passing
native checks establish admission and scoped policy choices, separately from the
Java journeys' later payment, stack and resolution assertions.
