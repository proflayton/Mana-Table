# Multiplayer Network Match Blueprint

## Goal

Make hosted Forge multiplayer games playable through the Mana Table table UI.

The lobby checkpoint proves that Mana Table can:

- host a Forge network lobby
- join from a second Mana Table instance
- show connected seats
- assign saved decks to lobby slots
- mark players ready

The remaining blocker is the game handoff. Forge can create a hosted/network match, but Mana Table does not yet provide a network-capable game UI adapter. Pressing **Start game** currently reports:

> Mana Table needs a network match adapter before hosted games can render.

## Current Start Path

The host path starts in `MultiplayerSession.start()`:

1. `MultiplayerSession.start()`
2. `GameLobby.startGame()`
3. `GameLobby` validates:
   - at least two non-open seats
   - every non-open seat is ready
   - every non-open seat has a deck
4. `GameLobby.startGame()` asks each slot for an `IGuiGame`:
   - host local slot: `FServerManager.getGui(index)`
   - remote slot: `RemoteClientGuiGame`
5. Forge creates a `HostedMatch` and opens game views for each player.

The current failure happens because Mana Table's headless platform returns `null` for `GuiBase.getInterface().getNewGuiGame()` unless a local `MatchSession` is active.

Relevant files:

- `forge-api/src/main/java/forge/api/MultiplayerSession.java`
- `forge-gui/src/main/java/forge/gamemodes/match/GameLobby.java`
- `forge-gui/src/main/java/forge/gamemodes/net/server/FServerManager.java`
- `forge-gui/src/main/java/forge/gamemodes/net/server/RemoteClientGuiGame.java`

## Forge Network View Flow

### Host Local Seat

For the host's own seat, Forge needs a local `IGuiGame` from:

- `GuiBase.getInterface().getNewGuiGame()`
- called through `FServerManager.getGui(index)`

Today `HeadlessPlatform` answers that with `active == null ? null : active.gui()`, where `active` is a `MatchSession`.

For multiplayer, the platform needs to return a Mana Table network game adapter when the hosted lobby starts a match.

### Remote Seat On Host

For remote players, the host uses `RemoteClientGuiGame`.

`RemoteClientGuiGame`:

- extends `NetworkGuiGame`
- forwards full game views with `ProtocolMethod.setGameView`
- forwards local-player opening with `ProtocolMethod.openView`
- forwards prompts and blocking choices with protocol methods like:
  - `getAbilityToPlay`
  - `assignCombatDamage`
  - `showOptionDialog`
  - `getChoices`
  - `order`

Relevant file:

- `forge-gui/src/main/java/forge/gamemodes/net/server/RemoteClientGuiGame.java`

### Joined Client

The joined Mana Table instance owns an `FGameClient`.

`FGameClient` installs `GameClientHandler`, which receives network protocol calls and invokes the client's `IGuiGame`.

Important `GameClientHandler` behavior:

- `setGameView` initializes or updates the client's `GameView`
- `openView` calls `gui.setNetGame()`
- `openView` sets the client's local player controllers
- later protocol calls invoke prompt/action methods on the same `IGuiGame`

Relevant files:

- `forge-gui/src/main/java/forge/gamemodes/net/client/FGameClient.java`
- `forge-gui/src/main/java/forge/gamemodes/net/client/GameClientHandler.java`
- `forge-gui/src/main/java/forge/gamemodes/net/NetworkGuiGame.java`

## Mana Table Table Requirements

Mana Table's current table UI is driven by `MatchSession`.

The renderer expects:

- `matchState`
  - board snapshot
  - players
  - zones
  - visible cards
  - stack
  - combat state
  - prompt state
  - action history
- `matchAction`
  - answers the current prompt
  - clicks cards/players
  - attacks/blocks
  - passes priority
- `matchConcede`

`MatchSession` currently owns all of these concerns:

- creates the local `Game`
- creates a Mana Table `IGuiGame` proxy
- subscribes to game events
- snapshots `GameView` into renderer JSON
- translates Forge prompts into Mana Table prompt JSON
- sends renderer actions back to `PlayerControllerHuman`

Relevant file:

- `forge-api/src/main/java/forge/api/MatchSession.java`

## Proposed Adapter Shape

### 1. Extract A Reusable Table Adapter

Create a reusable adapter from the parts of `MatchSession` that are not inherently AI-only.

Possible name:

- `ManaTableGameAdapter`

Responsibilities:

- own an `IGuiGame` proxy
- track the active viewer/player
- produce the same state shape used by `matchState`
- publish prompts
- accept renderer actions
- handle `IGuiGame` methods used by Forge local and network games

`MatchSession` would keep responsibility for creating local AI games, but delegate rendering/action logic to this adapter.

### 2. Add A Network Match Session

Possible name:

- `NetworkMatchSession`

Responsibilities:

- create a `ManaTableGameAdapter` for the local network seat
- register that adapter with `HeadlessPlatform`
- expose state/action/concede methods to `DesktopEngine`
- know whether it is host-side or client-side
- transition the renderer from lobby to match view when `openView` arrives

This session should not create a `Game` itself. Forge networking creates or receives the game view.

### 3. Update HeadlessPlatform

`HeadlessPlatform` currently stores:

```java
private static volatile MatchSession active;
```

This needs to become a more general active game adapter/session.

Possible direction:

```java
interface ManaTableGameSession {
    IGuiGame gui();
    void publishInput();
    Object platformDialog(String method, Object[] args);
    void fail(Throwable error);
}
```

Then both local `MatchSession` and future `NetworkMatchSession` can be active.

### 4. Wire Multiplayer Start

`MultiplayerSession.start()` should:

1. create/activate the host's `NetworkMatchSession`
2. call `lobby.startGame()`
3. run the returned start runnable
4. return a state that tells the renderer a match is active

The joined client needs a corresponding session before the host starts, because `FGameClient` already has its `IGuiGame` at join time.

That likely means `MultiplayerSession.join()` should create a network adapter-backed `IGuiGame` instead of using `GuiBase.getInterface().getNewGuiGame()` directly through `NetConnectUtil.join(...)`.

## Renderer Shape

Prefer reusing the existing match renderer.

Target behavior:

- multiplayer lobby receives `matchActive: true`, or a separate event/state marker
- renderer calls existing `show()` path for `match-view`
- `matchState` returns network match state if a network match is active
- `matchAction` routes to network match adapter

Avoid creating a second table UI unless the existing match renderer truly cannot support network games.

## Proof Test

Add a small integration smoke test that documents the current boundary and future success condition.

Current setup:

1. start host engine with `forge-desktop/.data`
2. start guest engine with `forge-desktop/.data-second`
3. host calls `multiplayerHost`
4. guest calls `multiplayerJoin` with `127.0.0.1:36743`
5. both call `multiplayerSelectDeck`
6. both call `multiplayerReady`
7. host calls `multiplayerStart`

Current expected result:

- both slots have decks
- both slots are ready
- `multiplayerStart` reports the network match adapter boundary

Future expected result:

- host and guest both report an active match state
- both match states include a `viewerId`
- both match states include players/zones
- at least one window receives a prompt or playable priority state

## Risks And Questions

- Hidden information must be filtered per viewer. The adapter should always render from that instance's local player view.
- Prompt methods may arrive through `IGuiGame` protocol calls rather than `PlayerControllerHuman` input polling.
- Blocking prompt methods like `getChoices` and `order` need the renderer response path to complete the Forge call.
- The host and guest may need different state sources:
  - host local seat can read from host game objects
  - guest seat receives serialized `GameView` and protocol prompts
- Existing `MatchSession` assumes one local human and AI opponents. Extracting carefully is safer than making `MatchSession` handle both modes directly.

## Suggested Implementation Order

1. Introduce a shared session/adapter interface for `HeadlessPlatform`.
2. Extract `MatchSession` rendering/action helpers into a reusable table adapter without changing behavior.
3. Add a no-op/skeleton `NetworkMatchSession` that can return an `IGuiGame` and report state.
4. Change join/host start paths to install the network adapter before Forge asks for a GUI.
5. Make `openView`/`setGameView` publish a Mana Table match state.
6. Route prompt protocol methods into the same prompt response system used by `matchAction`.
7. Replace the current graceful start-boundary error with the real match transition.
