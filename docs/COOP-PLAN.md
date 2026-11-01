# CoopCampaign plan (AdmiralRob fork)

The shared plan for this fork. Rob, Morgan and both AI assistants (Claude, ChatGPT) work from
this file. **Read it before starting work, and update it when a decision changes.** Upstream's
architecture and test history stay in [DESIGN.md](DESIGN.md); this file covers only what this
fork adds.

## Goal

Two players (later 3–4) play one BattleTech Career together.

- The host owns the company and the save, and makes Career decisions: contracts, travel, time.
- Each player commands their own 'Mechs in combat.
- First, the vanilla 4-'Mech lance split 2+2. Later, one full lance per player (8 units) with
  Bigger Drops / Mission Control.

## How we work

- **Fork:** `AdmiralRob/battletech-coop`. `upstream` is `Cryswar/battletech-coop` (MIT). We
  merge upstream releases as they ship.
- **`main` stays playable.** All work happens on feature branches, merged by pull request.
- **Cross-review:** each pair (Rob + Claude, Morgan + ChatGPT) reviews the other pair's PRs.
  The humans make the call.
- **Issues** hold bugs and play-test reports. Attach **both** players' `BattletechCoop.log`
  files (from `BATTLETECH\Mods\BattletechCoop\`) and note who was host.
- **Same build for both players.** The mod refuses mismatched installs, so one person builds
  from `main` and publishes a GitHub Release zip, and both install that zip before a session.
  Builds are local, because they need the game's DLLs.
- **Never commit game files or decompiled game code.** Naming game classes and methods is fine,
  as DESIGN.md does.
- **Upstream PRs:** small, tested fixes (e.g. the guest HUD bug) are offered back to Cryswar.

### Branches and owners

| Branch | Pair | Status |
|---|---|---|
| `fix/guest-hud-orders` | Rob + Claude | Root cause found, see below |
| `feature/ship-navigation` | Morgan + ChatGPT (proposed) | Design in Morgan's proposal |
| `feature/split-lance-2x2` | TBD | After the HUD fix |

### Reserved message channels

Upstream uses 1 and 10–28. Unhandled channels from 10 up fall through to combat handling in
`CoopController.OnGameMessage`, so a collision fails quietly. Each new channel must be
dispatched explicitly.

| Range | Feature |
|---|---|
| 29–31 | Ship navigation |
| 32–34 | Lance ownership |
| 35–39 | Spare; claim here before use |

## Phases

| # | Focus | Done when |
|---|---|---|
| 0 | Baseline: fork, both on the same release, logs collected | Done 2026-11-01 (four missions played on 0.0.13) |
| 1 | **Combat correctness:** guest HUD fix and a host legality guard | Full two-human contract; no illegal repeat moves; zero desyncs; legal move+fire, jump, melee, DFA and free-move all work |
| 2 | Ship navigation: host-led room/tab sync (NAV-A first) | Guest follows the host through the main ship screens without a save reload per click |
| 3 | 2+2 ownership: unit → player table, HUD restriction, host-side enforcement | Each player commands only their two 'Mechs through a full mission |
| 4 | Career soak: chained contracts, refits, salvage, save/reload between missions | Company stays consistent across several sessions |
| 5 | Bigger drops: Bigger Drops + Mission Control, 4+4 lances, enemy scaling | 8-unit mission without desync |
| 6 | 3–4 players, packaging | A friend installs from the release zip alone |

Phases 1 and 2 can run in parallel on separate branches. Combat correctness comes first because
the illegal-move bug is game-breaking.

## Finding: guest can move a 'Mech repeatedly (2026-11-01)

**Observed:** Morgan, as guest, jumped then DFA'd in one activation. In a later activation he
moved three times, then fired. The host accepted all of it (host log: combat `1538836484`,
activation 8 = seq 19–20; activation 19 = seq 47–50; unit `ebc06b6a…`). There were no desyncs:
both games ran the same illegal orders.

**Root cause:** found by static analysis of build 20387569; not yet verified in game.

- The HUD's `SelectionState.PublishInvocation` subscribes to `InvocationStackSequenceCreated`
  (message type 410), publishes the invocation, and stores the resulting sequence as `Orders`.
  This relies on the order running synchronously.
- `SelectionState.IsComplete` is true only when `Orders` is set and complete.
  `CombatSelectionHandler.RemoveCompletedItems` advances the HUD (move step → fire step; fire
  step → done) only when the active step `IsComplete`.
- On the guest, `InvocationLogPatch` intercepts the order and sends it to the host instead of
  running it. `Orders` is never set ("No Orders assigned from invocation!", which DESIGN.md
  calls harmless). So the move step never completes, and the HUD keeps offering movement.
- The game state itself is correct. `OrderSequence.OnAdded` sets `HasMovedThisRound`, and
  `OrderSequence.OnComplete` sets `HasFiredThisRound`, on both machines. But nothing checks
  these flags when an order arrives, and `CombatSync.ValidateRequest` does no legality check.

**Fix plan (`fix/guest-hud-orders`):**

1. **Guest:** remember the `SelectionState` that published the outstanding request. When the
   host streams that order back (`G<player>.<n>`) and it runs on the guest, capture its
   `InvocationStackSequenceCreated` and call `AssignOrders(sequence)` on that state. There is
   only one outstanding guest request at a time, so matching it is simple. Clear it on reject.
2. **Host guard (defence in depth):** in `ValidateRequest`, deserialize the order and find its
   actor. Reject movement-type orders (move, sprint, jump, melee, DFA) when `HasMovedThisRound`
   is set, and attacks when `HasFiredThisRound` is set. Log a clear reason. The 2+2 ownership
   filter goes in the same place later.
3. **Check before merging:** the free-move phase (non-interleaved, before contact) re-allows
   movement. Confirm how the game resets `HasMovedThisRound` there, so the guard doesn't block
   legal re-moves.

## Open questions

- Ship navigation: which game methods cover each room and screen (`RoomManager` is a lead, not
  verified)? Should the guest follow only rooms, or tabs and selections too?
- Guest edits during host-led navigation: keep, defer or block?
- Test install: a disposable career save at minimum. A second game folder is needed for
  upstream's `tools/two-copy-test.ps1`.
- When to tell Cryswar we're working on per-player lances (it's on their roadmap).
