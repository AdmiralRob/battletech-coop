# BattleTech Co-op

> **Work in progress. Not finished, expect bugs.** This is a pre-alpha mod developed and
> tested by two players. Back up your saves, and expect breaking changes between versions.

A mod that lets 2+ players play the BattleTech (2018) single-player campaign together over
Steam. The host owns the campaign; friends join it.

## Status (0.0.14 test build)

The movement fix adds host-side checks for repeated player movement and reconnects
guest selections to their delayed replay sequences so the game can advance from
movement to firing. Ordinary move/jump-then-fire and permitted Ace Pilot movement
remain supported. Both players must use the same version.

Full compilation succeeded; 44 movement/control tests and 12 network tests passed.
**Live two-player gameplay verification is still pending.** The movement tests use
game/UI data doubles and do not run Unity or install Harmony patches into a game.

See [the test report and gameplay checklist](TestResults/2026-11-01-movement-fix/REPORT.txt)
and [the shared development handoff](docs/COOP-HANDOFF.txt). The inherited 0.0.13
results below describe the original baseline, not new validation of 0.0.14.

Run `dotnet run --project tests/MovementTests` and `dotnet run --project tests/NetTests`.
Without the SDK, PowerShell 7 can run `tools/run-headless-tests.ps1 -Suite MovementTests`
and `tools/run-headless-tests.ps1 -Suite NetTests` in separate fresh processes.

## Inherited baseline (0.0.13)

Works, tested by hand over Steam and by automated two-copy tests:

- **Missions:** co-op skirmish, Campaign and Career missions. Lockstep on the game's own orders
  with desync detection; every player can command every 'Mech of the player lance (shared
  control). New in 0.0.13, so far tested only by the automated test: both players watch the
  intro and the opening conversation, and either player's Continue moves it on for both.
- **Ship between missions:** friends see the host's company and can refit, repair, rename,
  ready, store and scrap 'Mechs, train, rename, hire and dismiss pilots, buy and sell in the
  Store, cancel work orders and customize the company. The host's game applies those changes,
  and the host's own changes reach the friends without a reload.
- Sessions end cleanly; a friend whose host leaves returns to the main menu.

Not done yet:

- Each player having their own lance (ownership), and drops larger than 4.
- Travel, passing time and accepting contracts are the host's; pilot portraits are not synced.
- Rejoining a battle after a disconnect; saving during co-op combat.
- Larger mod packs (BattleTech Advanced 3062) are untested.

See [docs/DESIGN.md](docs/DESIGN.md) for the architecture, every test result, and what is
verified versus assumed.

## Playing

Download the zip from [Releases](../../releases) (marked pre-release). Every player installs
the same zip; it contains ModTek (unmodified, LGPL-2.1) and this mod, and
`BattletechCoop-README.txt` inside has install, play and uninstall steps.

Target game: Steam build 20387569, BattleTech 1.9.1 (686R). All players need to own the
game and run the exact same mod list.

## Layout

| Path | What |
|---|---|
| `src/BattletechCoop/` | The ModTek/HBS mod (C#, .NET 4.7.2, Harmony) |
| `src/BattletechCoop/Net/Core/` | Unity-free network core: transport interface, TCP transport, protocol, session |
| `tests/NetTests/` | Headless tests for the network core (.NET 8 console, no packages) |
| `docs/DESIGN.md` | Architecture, decisions, milestones |
| `tools/` | Two-copy game test, release builder, read-only analysis scripts (metadata dump, IL call graph, IL dump) |
| `ca/` | Local copy of [coop-anything](https://github.com/cranebeaker/coop-anything), ignored here |
| `local/` | Private, ignored: copied game assemblies, dumps, logs |

## Building

1. Copy `GameDir.props.example` to `GameDir.props` and set `BattleTechDir` to your install.
   (The build only reads game DLLs from it; it never writes there.)
2. `dotnet build src/BattletechCoop -c Release`
3. Optional: set `TestInstallDir` in `GameDir.props` to a **copy** of the game to have the
   build deploy `Mods/BattletechCoop/` there.

## Release zip

`powershell -ExecutionPolicy Bypass -File tools\make-release.ps1` builds
`dist\BattletechCoop-<version>-ModTek-<version>.zip`: the official ModTek release (hash-checked,
unmodified, LGPL-2.1 with source link) plus this mod's Release build and
`BattletechCoop-README.txt` with install, test and uninstall steps. Every player installs the same
zip into their game folder, so their mod fingerprints match.

## Testing

- Network core (no game needed): `dotnet run --project tests/NetTests`. It runs host and guests
  over real localhost TCP sockets.
- Keep the screen awake and unlocked while game tests run: with the display off, Unity hangs at
  "Input initialized" before any mod code runs (seen 2026-10-17).
- Two game copies on one PC: `powershell -ExecutionPolicy Bypass -File tools\two-copy-test.ps1`.
  It starts a host in `TestInstall` and a guest in `TestInstall2` over localhost TCP, waits for
  each side to receive the other's test message, prints both logs and closes the games.
  ModTek allows one game process per install folder, so the second folder is required. It can
  junction `BattleTech_Data` to the first copy.
- Combat lockstep: `tools\two-copy-test.ps1 -Combat [-TargetSeq 60] [-TimeScale 3]`. The host
  auto-launches a fixed 4v4 skirmish with the AI playing both sides, and the guest replays the
  host's invocation stream. It passes when the guest has replayed `-TargetSeq` items (or the
  mission ended) with no desync. Use `-TargetSeq 100000` for a whole match.
  `-InjectDesyncAt <seq>` corrupts guest state on purpose; then PASS means the desync detector
  caught it. `-Shared` makes the Player 1 AI alternate between guest and host each activation,
  so half the orders are guest requests (shared control). It also requires accepted requests and
  none rejected.
- Campaign missions by combat save: `-Combat -SaveTransfer` (skirmish through the same save
  path).
- Ship view: `-ShipView`; the host loads its newest campaign save read-only (`-coop-no-saves`
  on both copies) and the guest must show the same company.
- Ship changes: `-ShipRequests`; both copies make refits, pilot changes, hires, purchases,
  sales, storage and readying, cancels, dismissals and a company rename; the final company
  digests must match.
- In game, **Ctrl+Shift+C** opens the co-op window: host or join over TCP, host via Steam,
  invite Steam friends, send test messages (including a 2 MB one), leave.

## Rules this project follows

From coop-anything's `AGENTS.md`: never modify the real install or saves (test on a copy),
no game files or decompiled source in git, test with bots, and record what has actually
been verified.

## License

This mod's code is under the [MIT License](LICENSE). Release zips also contain ModTek, which
is included unmodified under its own license (LGPL-2.1). BattleTech itself and its files are
not part of this repository.
