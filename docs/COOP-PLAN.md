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
- **Building:** on Windows, run `dotnet build src/BattletechCoop -c Release` with
  `GameDir.props` set (see the README). On Linux without NuGet access (Claude's workspace),
  copy the game DLLs into a folder outside the repo, point `GameDir.props` at it, and run
  `tools/build-linux.sh --install --tests`.
- **Never commit game files or decompiled game code.** Naming game classes and methods is fine,
  as DESIGN.md does.
- **Upstream PRs:** small, tested fixes (e.g. the guest HUD bug) are offered back to Cryswar.

### Branches and owners

| Branch | Pair | Status |
|---|---|---|
| `codex/0.0.14-movement-fix` | Morgan + ChatGPT | Merged (PR #1); in live testing |
| `feature/settings-sync` | Rob + Claude | PR #2: host's combat pacing settings apply on the guest |
| `feature/live-company` | Rob + Claude | Guest's copy of the company keeps up live (builds on PR #2) |
| `feature/ship-navigation` | Morgan + ChatGPT (proposed) | Design in Morgan's proposal |
| `feature/split-lance-2x2` | Rob + Claude | Started 2026-11-06 (builds on `feature/live-company`); test builds are `0.1.0-lances.N` |

### Reserved message channels

Upstream uses 1 and 10–28. Unhandled channels from 10 up fall through to combat handling in
`CoopController.OnGameMessage`, so a collision fails quietly. Each new channel must be
dispatched explicitly.

| Range | Feature |
|---|---|
| 29–31 | Ship navigation |
| 32 | Lance ownership: unit → player table (`LanceOwners.Channel`) |
| 33 | Lance picking: offer / picks / result (`LancePicker.Channel`) |
| 34 | Spare for lance work |
| 35 | Settings sync (`SettingsSync.Channel`) |
| 36 | Live company updates (`CompanyLive.Channel`) |
| 37–39 | Spare; claim here before use |

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

**Fix:** implemented by Morgan + ChatGPT in PR #1 (`codex/0.0.14-movement-fix`), with the DFA case
added after review. Live testing on 2026-11-01 looked good so far. The original plan was:

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

## Feature: split lance (2+2), started 2026-11-06

Rob's decisions:
- **Split:** the vanilla 4-'Mech lance, 2 + 2. Bigger drops come later (phase 5).
- **Picking order:** the host picks their own 'Mechs first, then the guest picks theirs.
- **Control:** each player commands only their own units. If a player disconnects, the host
  takes over their units.

**How we work on it:** `feature/split-lance-2x2` branches from `feature/live-company`. Bug fixes
land on `feature/live-company` first and are merged into the lance branch, never the other way.
Play sessions test one build at a time: bug-fix builds are `0.0.N`, lance builds
`0.1.0-lances.N`. Merge order: PR #2 → PR #3 → the lance PR (rebased onto `main`).

### Slice 1: ownership in battle (done, untested in game)

- **Who owns what** (`Sync/LanceOwnership.cs`, Unity-free): the lance configuration's slots split
  evenly in slot order, so slots 1–2 are the host's and 3–4 the guest's. A unit's slot comes from
  its pilot (`SpawnableUnit.PilotId` in `Contract.Lances.GetLanceUnitsIncludeEmptySlots(player
  team)` vs. the actor's `pilotDef.Description.Id`), so an empty host slot doesn't shift the
  guest's 'Mechs. A configuration listing fewer than four slots is padded to four.
- **Table** (`Sync/LanceOwners.cs`): the host builds it once the player lance has spawned (by the
  briefing) and sends it on channel 32. The guest keeps it for the battle with that combat ID.
  Autoplay test modes keep shared control (the AI drives the player team).
- **Which unit an order is for:** the first string property among `ActorGUID`, `MechGUID`,
  `SourceGUID`, `targetGUID`. Checked against every `InvocationMessage` type in build
  20387569 (move/sprint, jump, melee, stand, startup, attack, DFA, sensor lock, active probe,
  eject, mortar, vigilance, Done/reserve, vent, inspire). `SubAttackInvocation` and team-level
  orders name no unit and aren't restricted.
- **Where it's enforced:**
  - Guest HUD orders (`RequestOrder` with a `SelectionState`): refused locally with a notice.
  - Every guest request on the host (`RunGuestOrder`): refused with the reason sent back.
  - Host HUD orders (`SelectionState.PublishInvocation` → `AllowHostOrder`): refused with a
    notice. Not in `GenericInvoke`: that also runs orders the game makes itself and accepted
    guest orders.
- **Disconnect:** a unit whose owner has left is commanded by the host (the host is told once).
  A guest never takes over the host's units; if the host leaves, the session ends anyway.
- **Known gap:** host HUD actions the game publishes straight to the `MessageCenter` (not through
  `SelectionState`) aren't ownership-checked on the host. Guest ones are, by `RunGuestOrder`.
- **Until slice 2:** the host fills all four slots, putting the guest's two 'Mechs in slots 3–4.

### Slice 2: picking in order (done, untested in game)

Rob, 2026-11-08: the host may keep swapping their own 'Mechs after the guest has picked.

**Host** (`Sync/LancePicker.cs`, the game's own lance screen):
- **Claiming slots 3–4.** When the screen opens in a campaign with a guest connected
  (`LanceConfiguratorPanel.SetData`), it is noted. Once the last lance has loaded
  (`LoadLanceConfiguration`, run when the roster is ready, or 1.5 s after `Initialized` if
  there's none), slots 3–4 are emptied back into the lists (`slot.OnRemoveItem` +
  `panel.ReturnItem`, as replacing a slot's 'Mech does) and locked (`SetLockState`, as
  contracts lock slots).
  - A slot the contract already locked stays the host's.
  - With a smaller drop, the guest gets fewer slots (`maxUnits`).
- **Placing the guest's picks.** The game's sequence for loading a saved lance:
  `mechListWidget.GetMechDefByGUID`, `MechValidationRules.ValidateMechCanBeFielded`,
  `pilotListWidget.GetPilot` (`CanPilot`), `RemovePilot`, then
  `LanceLoadoutSlot.SetLockedData(mech, pilot, false)`, which also locks them in. The guest's
  previous picks go back to the lists first.
- **Launch.** `OnConfirmClicked` is refused with "Waiting for HCASE to pick their 'Mechs" until
  the guest has sent its picks or "no 'Mechs for me". The host can still swap slots 1–2 at
  any time.
- **Guest leaves.** Its slots unlock, keeping its picks, and the host is told.
- **On launch** (`OnLanceConfiguratorClosed` after confirming), the guest's pilot IDs are kept
  as `LastAssignment`. `LanceOwners` uses them for battle ownership instead of the slot rule,
  so a contract-locked slot can't skew it.

**Guest:** a picker window, shown while the host's screen is up.
- **Before the host has picked:** it shows "Waiting for Nines…" and the host's picks so far.
- **After:**
  - It lists the host's free 'Mechs (fieldable, not in the host's slots) and pilots
    (`CanPilot`), including the guest's own current picks.
  - The guest clicks up to two 'Mechs and as many pilots; they pair up in click order.
  - "Send" or "No 'Mechs for me" sends the picks. The host replies placed or not, and why.
- The guest can change its picks until the host launches.

**Protocol (channel 33):**
- `Offer` (host → guest): screen open, contract, host ready, the guest's slot count, the
  host's picks, free 'Mechs and pilots, the guest's current picks, confirmed. Re-sent when it
  changes, at most every 0.3 s.
- `Picks` (guest → host).
- `Result` (host → guest).
- The rules (`PickRules`: host first, slot count, available, no duplicates, pilots for every
  'Mech, launch block) and the wire format are tested in `tests/LanceTests`.

**Not covered yet:**
- The game's own "clear lance" would also empty the guest's slots.
- The guest's picker is a plain co-op window, not the game's lance screen.
- Skirmish keeps the slot rule with the host filling all four.

### Slice 3: HUD polish (later)

- Mark the partner's units on the portraits and the initiative track.
- Selecting a partner's unit shows it, but its order buttons are greyed out (now the order is
  refused with a notice).
- Optionally, the camera follows the partner's unit while it acts.

## Feature: settings sync (2026-11-01)

Rob's decisions:
- **What syncs:** the host's combat pacing and cinematic settings from Options > Game: speed up
  combat, attack cam, target cam, move/sprint follow, auto/cinematic zoom chance, and
  enemy/melee/jump cinematics.
- **What doesn't:** camera speeds, invert, cursor lock, volume and keybinds stay personal.
- **How it applies:** the host's values hold on the guest for the session only. Each player
  still steers their own camera.

Design (`Sync/SettingsSync.cs`):

- **Why it's safe:** none of these settings touch the lockstep state. The cinematic chances use
  `UnityEngine.Random` (`CameraControl.ShowAttackCam`), and `speedUpCombat` only changes timing
  (`StackManager.Update`).
- **When the host sends:**
  - on guest join;
  - after any `UserSettings.Save` that changed one of these values.
- **Guest settings are protected.** `CloudUserSettings` is stored by exactly two synchronous
  writers:
  - `UserSettings.Save` (Steam Cloud);
  - `CachedSettings.SaveSettingsToPlayerPrefs` (the PlayerPrefs cache; also the only write when
    there's no SaveManager, and called during settings load).

  On the guest, both are patched to write the guest's own values and put the host's back after.
  The patches act only on the settings object holding the host's values, never call back into
  the game, and count nested writes once. A write that throws is repaired on the next frame
  (`SettingsSync.Update`).
- **Guest edits** are read from the Options screen itself. `GameSettingsModule` keeps
  `original<X>` and `current<X>` from when the screen opened, so `current != original` is
  exactly what the guest changed. A host update while the screen is open isn't mistaken for an
  edit. This came from the PR #2 review by Morgan + ChatGPT.
- **The guest's own values return** when the session closes.
- **Regression tests:** `tests/SettingsTests` compiles the real `SettingsSync.cs` against
  stand-ins that follow the game's save order.

## Feature: live company updates (2026-11-01)

**Problem:** the guest saw the host's company as of the last full ship. Day, C-Bills, XP and
finished refits only showed up after a mission or a manual refresh. Logs from 2026-11-01
showed two causes:

1. **The post-mission ship was sent too early.** It went out when combat ended, but
   `SimGameState.ResolveCompleteContract` (C-Bills, pilot XP, reputation, morale, salvage) runs
   later from `SimGameState.Update`. `OnCompleteContract` sets `CompletedContract`, and
   `ResolveCompleteContract` clears it. Every host pilot-training update then failed on the
   guest ("needs 1300 XP, the pilot has 500").
2. **Nothing sent time passing, events or travel.**

Rob's decisions:
- Live updates, with no routine reloads.
- A reload that is needed waits until the guest isn't editing.

Design (`Sync/CompanyLive.cs`, `Sync/StatDelta.cs`, channel 36):

- **Snapshot timing:** `ShipView` holds the ship while `CompletedContract != null`.
- **Each day:** the host records the work-queue steps `OnDayPassed` ran, in order
  (`UpdateMechLabWorkQueue(passDay)`, `UpdateInjuries()`). The guest replays them with
  `ShipRequests.RunAsRemote`, so nothing is sent back. They depend only on the queues and the
  MechTech/MedTech skills, so refits finish and pilots heal on the same day on both copies.
- **Every change** (each day, plus a once-a-second fingerprint check on the host): the company's
  and each pilot's stats that changed since the last ship or status, set on the guest with
  `StatCollection.Set` / `AddStatistic` / `RemoveStatistic`, plus `DaysPassed` and
  `DayRemainingInQuarter`. The guest redraws with `SGRoomManager.RefreshDay` /
  `RefreshTimeline` / `RefreshDisplay` and the existing screen redraw.
- **Fingerprint:** days, all company and pilot stats, roster, bays, and both queues (no
  work-order IDs, which differ between copies).
  - On a mismatch, the guest logs its copy and catches up with a fresh ship
    (`ShipRequests.WantHeal`).
  - The catch-up waits for `GuestEditing()` (MechLab open, or unconfirmed training).
  - At most one automatic catch-up per received ship and per 5 minutes.
  - The check is skipped while a guest request is unanswered (`PendingRequests`).
- **Also:**
  - When an ordinary host update can't apply, the guest now catches up the same way instead
    of only showing a notice.
  - Statuses wait on the guest while its ship loads or the player is editing, and a new ship
    clears them.
  - The host doesn't send statuses while a ship is pending (`ShipView.SnapshotPending`).
- **Order on the guest** (PR #3 review): ship updates (channel 23) and statuses (channel 36) share
  one queue (`GuestInbox`, owned by `ShipRequests`), applied strictly in the order the host sent
  them. A held status holds everything behind it, so a day replays against the queue as it was
  on the host that day.
  - Before any local guest change, `ShipRequests.CaptureSession` (called first, in a prefix, by
    every capture) applies everything held. The change then lands after the host's earlier days.
- **Travel** (PR #3 review): the current system is part of the fingerprint. The host sends a fresh
  ship on `SimGameState.SetCurrentSystem`, since the new system's store, hiring hall and
  contracts come from the host. On the guest, a ship it didn't ask for waits while it's editing.
- **Live test 2026-11-01 (0.0.17), fixed in 0.0.18:**
  - *Guest stuck "in space" after the host docked.* `SetCurrentSystem` fires at the jump point;
    docking at the planet days later is only a travel-state change (`SGTravelManager`:
    `IN_SYSTEM`, `TRANSIT_FROM_JUMP`...), which wasn't synced or fingerprinted.
    - Statuses now carry `TravelState` and `TravelTime`. The guest applies them with the state
      machine's own `ForceTravelState` and `SetTravelTime`, then `RefreshLeftNavFromShipScreen`
      (Store access), with no reload.
    - The fingerprint has a `travel` section.
    - The host holds a status while a jump animation plays (`TRANSITION_ANIMATING`), keeping
      its days.
  - *A cancelled order stayed on the other player's schedule.* The Mech Bay's own cancel
    (`MechBayMechInfoWidget.ConfirmCancelWorkOrder`) follows `CancelWorkOrder` with
    `UpdateMechLabWorkQueue(false)` and `RefreshTimeline`. The remote cancel now does the same.
  - *An unexplained catch-up after storing a 'Mech.* The fingerprint is now in named sections
    (day, system, travel, company, pilots, bays, mechlab, medbay) with a hash each.
    - On a mismatch, the guest logs the differing sections in full and reports their names to
      the host (channel 36, guest → host).
    - The host logs its own copy of those sections.
- **PR #3 review 2, fixed in 0.0.19:**
  - *Timeline countdowns.* The quarterly finance report and the travel order are timeline
    entries (`WorkOrderEntry` with private `Cost` / `PaidCost`). `OnDayPassed` pays them
    (`FinancialReportNotification.PayCost`, `Starmap.PayTravelOrderCost`) and replaces the
    finance one at quarter rollover; the day counters don't move them.
    - Statuses now carry both entries' cost and paid cost, and the guest sets them exactly. A
      finished travel order is removed as the game does (`Starmap.RemoveCompletedTravelOrder`).
    - Both remaining costs are in the fingerprint (`day` and `travel` sections).
  - *Room-visit flags.* `COMPANY_HasVisitedRoom_*` (set only by each room's `EnterRoom`) and
    `COMPANY_HasSeenTutorial_MechLab` / `COMPANY_HasSeenECMReview_MechLab` (set only by
    `MechLabPanel`) are local to each player. They're left out of the fingerprint
    (`CompanyFingerprint.IsLocalPresentation`) but still sync from the host. Other
    "viewed"/"seen" stats come from the day simulation and stay checked.
  - *Diagnostics.* The guest logs once per hold why host changes are waiting, and how many.
- **Live test 2026-11-01 (0.0.19), fixed in 0.0.20:** the guest went to a black screen and
  reloaded the ship twice per trip, and again after travel events.
  - *At Go:* the host's `SetActivePath` route and travel order weren't in statuses, so the first
    day of travel failed the `travel` section and the guest asked for a catch-up.
  - *At the jump:* the host sent a full ship on `SetCurrentSystem` (a new system's store and
    hiring hall are generated on the host only).
  - Now statuses carry the system id, the route (system ids) and the travel index. The guest
    follows them with the game's own calls: `PotentialPath` + `SetActivePath()` for a new route,
    `IncrementTravelIndexAndUpdateCurrentSystem()` for the next jump on that route, or
    `SetCurrentSystem(system, true, false)` otherwise. The arrival full ship is gone.
  - The market (each shop's `ActiveInventory` and `LastRefreshDay`, the system's
    `AvailablePilots`) is sent as JSON whenever its hash changes. The guest applies it after its
    own `RefreshShops` finishes (`Shop.IsPending`) and the system matches; new pilots are linked
    to the `DataManager` and their dependencies loaded as `LanceLoader` does. The host holds a
    status while its own shops are pending.
  - The fingerprint now includes the route and index (`travel`) and a `market` section.
  - Known gap: after a jump the guest's contract list is empty (`OnSystemExit` →
    `ResetContracts`; contracts are host-only).
- **PR #3 review 3, fixed in 0.0.21:**
  - *Route without its travel order.* `SetActivePath` → `CreateActivePath` takes
    `PendingTravelOrder` (made by `OnPathfindingComplete`, which following a route skips) as the
    trip's timeline entry; with none, `TaskTimelineWidget.AddEntry` threw. Statuses now carry the
    travel order with its legs (`TravelOrderData`), and the guest builds it with the game's own
    constructors (`WorkOrderEntry_TravelGeneric` and its `TravelToJumpPoint` / `TravelJumping` /
    `TravelToSystem` legs) and sets it as `PendingTravelOrder` before activating, replacing any
    order it planned itself.
  - *Leg payments.* `PayCost` pays the legs first and `GetRemainingCost` sums them, so a day's
    travel moves a leg's `PaidCost` while the parent stays 0/0. The guest now sets every leg's
    `Cost` / `PaidCost`; a trip of another shape is replaced on the timeline
    (`RemoveCompletedTravelOrder`, then `AddWorkQueueEntry`). The fingerprint lists each entry's
    type and payments (not the localized descriptions).
  - *Held days overtaken by later actions.* The host no longer holds a day's status: it goes out
    when the day ends, in order with everything sent after it. If the host hasn't settled (jump
    animation, shops loading), it is marked partial: the guest applies its queue steps and stats
    but not travel or the market, and skips the fingerprint check. A settled status follows as
    soon as the host settles (`HostStatusGate`).
  - Checked against the real DLL: the built trip's types, parents and remaining cost; a day's
    `PayCost` paying the first leg with the parent at 0/0; the guest's legs taking the host's
    payments through two stages; the `Starmap` members set.
- **Live test 2026-11-05 (0.0.21), fixed in 0.0.22 and 0.0.23:** travel worked (route, partial/settled
  days, jump, market applied), but right after the jump to Renfield the guest's store and hiring
  hall differed and the catch-up reload never finished.
  - *Market overwritten.* After a jump the game makes its own market late:
    `SetCurrentSystem` → `FindRouteTo(TargetSystem, OnTargetSystemFound)` → `RefreshSystem` →
    `GeneratePilots` + `RefreshShops`, a pathfinding callback that ran after the host's market
    was applied (guest pilots `PilotGen_SGRef_1000000861..` vs the host's `_861..`). While the
    guest shows the host's ship (`ShipView.Ready`) it now skips `StarSystem.RefreshShops` and
    `StarSystem.GeneratePilots`; the market comes only from the host.
  - *Reload stuck.* The catch-up ship arrived ~3 s after the jump and its load never reached
    `_OnAttachUXComplete`. A ship now waits 8 s after a followed jump and while the
    `DataManager` is loading (at most 30 s); a load still unfinished after 90 s is logged and
    tells the player which logs to send.
  - *Root cause of the stuck reload* (guest `battletech_log.txt`): a `NullReferenceException` in
    `SGNavigationButton.ManageHiringHallFlyout`, called from `_OnAttachUXComplete` →
    `SGRoomManager.OnSimGameReady` → `RefreshDisplay`. It aborted `_OnAttachUXComplete`, so the
    loaded ship never finished attaching. A prefix now skips that method (it only adds the
    Barracks button's Hiring Hall flyout) when its sim, current system, def, pilots or owner
    is missing, and logs which. The host also no longer sends a full ship until it has settled
    (`CompanyLive.Settled`: no jump animation, shops not pending).
- **Second test 2026-11-05 (0.0.23), fixed in 0.0.24:**
  - *Arrival not followed.* The host's `SGTravelManager.HandleNextTravelStep` runs
    `Starmap.CompletedTravelClear` on arrival (`ActivePath`, `TravelOrder`, `TravelItem` to null);
    the guest kept its route, so `travel` differed and it reloaded. The guest now does the same
    when the host's route is empty, and always takes the host's travel index.
  - *Reload aborted again,* one line later: `ManageShopFlyout`, with the button's `simState`
    null. Only `SGNavigationButton.SetupElement` sets it, from `SGNavigationList.Start` with
    `UnityGameInstance.BattleTechGame.Simulation` at the time Unity runs `Start`; after a reload
    that can be none or the replaced sim. A prefix on `SGNavigationList.RefreshButtonStates`
    (the only caller of `SetStateAccordingToSimDropship`, which runs both flyouts and reads
    `simState`) points all seven buttons at the sim it is handed first.
- **Not live yet** (caught by the fingerprint, then a catch-up):
  - things events add or remove (pilots, 'Mechs, items as objects rather than counts);
  - the contract list of a new system;
  - Argo upgrade construction.
- **Tests:** `tests/CompanyLiveTests` covers the deltas, the wire format, the guest's application
  order (both review scenarios, against a model of the Mech Bay queue) and the fingerprint
  (including the system). The `StatCollection`
  apply path and the `OnDayPassed` patch target were checked against the real game DLL.

## Open questions

- Ship navigation: which game methods cover each room and screen (`RoomManager` is a lead, not
  verified)? Should the guest follow only rooms, or tabs and selections too?
- Guest edits during host-led navigation: keep, defer or block?
- Test install: a disposable career save at minimum. A second game folder is needed for
  upstream's `tools/two-copy-test.ps1`.
- When to tell Cryswar we're working on per-player lances (it's on their roadmap).
