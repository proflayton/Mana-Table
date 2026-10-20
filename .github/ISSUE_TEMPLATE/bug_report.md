---
name: Bug report
about: Report a Mana Table desktop, deck-building, or match problem
title: ''
labels: ''
assignees: ''
type: 'Bug'

---

**What happened, and what did you expect?**

**Steps to reproduce**

1.
2.
3.

**Match details (if applicable)**

- Format and number of players:
- Local AI, network host, or guest:
- Relevant card names and deck list:
- Turn/phase and exact prompt text:
- Action taken (click, drag, keyboard):
- State before and after the action (including any waiting or error message):

**Environment**

- Mana Table version or source commit:
- Native installer, native portable/source run, or Electron:
- OS, window size, and display scaling:

**Screenshots or logs**

Attach a screenshot/recording and the relevant log excerpt if available.
Native profiles default to `%LOCALAPPDATA%\ManaTableNative`; a source/test run's
`--profile` overrides that location. Include `engine.log`, `client.log` or
`native-error.log` when present. A package's `SOURCE.txt` and `build.json` identify
its source revision.

Electron source runs use `forge-desktop/.data`. Clean Electron packages use
`%APPDATA%\Mana Table`, unless an existing adjacent `UserData` directory is used.
`MANA_USER_DATA_DIR` or `FORGE_USER_DATA` can override Electron's location;
neither changes the native profile. Remove personal paths or private deck
information before sharing.
