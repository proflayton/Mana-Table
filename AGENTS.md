# Mana Table contributor instructions

Before changing player-facing behavior, read [CONTRIBUTING.md](CONTRIBUTING.md) and
[Design rules learned from player feedback](docs/Development/Mana-Table-Experience-Review.md#design-rules-learned-from-player-feedback).
Treat that section as the shared design guidance; keep new learnings there.

Before adding controls, define the player intent, source, target, gesture,
engine transition and visible result for the whole journey.
Negative player feedback requires reconsidering the interaction, not merely
moving or restyling the same control.

Keep engine authority, current action scopes and visibility permissions intact;
use the [architecture guide](docs/Development/Mana-Table-Architecture.md) for ownership.
Use focused existing checks from the [testing guide](docs/Development/Mana-Table-Testing.md).
Automated correctness and agent inspection are separate from human UX approval.
Record human review honestly as pending, issues, accepted or not applicable,
with the exact candidate and reviewed journey in the pull request.
