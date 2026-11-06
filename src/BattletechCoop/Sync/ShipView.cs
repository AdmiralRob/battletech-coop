using System;
using System.IO;
using System.Linq;
using System.Text;
using BattleTech;
using BattleTech.Save;
using BattleTech.Save.SaveGameStructure;
using BattleTech.UI;
using BattletechCoop.Net;
using Harmony;
using HBS;

namespace BattletechCoop.Sync
{
    // Between missions the guest looks at the host's company: the host sends its campaign state as
    // an in-memory save (same bytes as a save file) and the guest loads it, like mission saves
    // (DESIGN.md "Ship view"). The guest's copy is never saved; anything changed there is local and
    // replaced by the next refresh, except requests the host accepts (refits, pilots: next).
    public static class ShipView
    {
        public const ushort ChannelShipSave = 19;
        public const ushort ChannelShipRefresh = 20;

        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);

        // Host
        private static bool dirty;
        private static bool wasInShip;
        private static DateTime lastSent = DateTime.MinValue;
        private static bool urgent; // a guest asked: skip the throttle

        // Guest
        public static bool GuestViewing { get; private set; }
        public static string GuestCompany { get; private set; } = "";
        private static byte[] pendingLoad;
        private static object simBeforeLoad;
        private static bool awaitingLoaded;
        public static float ShowingSince { get; private set; } = -1;
        public static SimGameState UXReady; // set by SimGameAttachUXPatch
        public static bool Refreshing { get; private set; }
        public static float SentAt { get; private set; } = -1; // host: last ship sent
        private static float refreshAskedAt;
        private static bool refreshReceived;
        private static float loadStartedAt = -1, quietWaitSince = -1;
        private static bool stuckNoticeShown;
        private static readonly float JumpSettleSeconds = 8f, MaxQuietWait = 30f, StuckAfter = 90f;

        // The host's ship is showing and no newer one is on its way.
        public static bool Ready => GuestViewing && ShowingSince >= 0 && pendingLoad == null && !awaitingLoaded;
        public static bool Busy => Refreshing || pendingLoad != null || awaitingLoaded;

        public static void Reset()
        {
            dirty = false;
            wasInShip = false;
            pendingLoad = null;
            awaitingLoaded = false;
            GuestViewing = false;
            ShowingSince = -1;
            Refreshing = false;
            urgent = false;
            ShipRequests.Reset();
        }

        // Host: a full ship is waiting to go out (e.g. after a mission, until its rewards are
        // in). Live company updates hold off until it has: it supersedes them.
        internal static bool SnapshotPending => dirty;

        // Something the guest should see changed (or the guest asked).
        public static void MarkDirty(string why)
        {
            if (!dirty) Log.Info($"SHIP host: will send the ship ({why})");
            dirty = true;
        }

        private static bool InShip(GameInstance game) =>
            game != null && game.Simulation != null && game.Combat == null;

        public static void Update(CoopSession session)
        {
            if (session == null || session.State != SessionState.Active) return;
            if (session.Role == SessionRole.Host) UpdateHost(session);
            else UpdateGuest();
        }

        private static void UpdateHost(CoopSession session)
        {
            var game = SceneSingletonBehavior<UnityGameInstance>.HasInstance ? UnityGameInstance.BattleTechGame : null;
            var inShip = InShip(game);
            if (inShip && !wasInShip) MarkDirty("host arrived on the ship");
            wasInShip = inShip;
            if (!dirty || !inShip || CombatSync.Current != null) return;
            if (session.Players.Count(p => p.Index != 0) == 0) return;
            if (!urgent && DateTime.UtcNow - lastSent < MinInterval) return;
            if (!game.Simulation.CanSave(SaveReason.MANUAL, false)) return;
            // After a mission the ship is up before the rewards are in: OnCompleteContract sets
            // CompletedContract, and SimGameState.Update calls ResolveCompleteContract (C-Bills,
            // pilot XP, reputation, morale, salvage; it clears CompletedContract) only once no
            // popup is open. A ship sent before that is missing them, and later pilot training
            // updates fail on the guest for lack of XP (seen 2026-11-01).
            if (game.Simulation.CompletedContract != null) return;
            // Not mid-jump or while the new system's shops are still loading: the ship would carry
            // a half-made system (live statuses wait for the same thing).
            if (!CompanyLive.Settled(game.Simulation)) return;

            dirty = false;
            urgent = false;
            lastSent = DateTime.UtcNow;
            try
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                var bytes = SaveTransfer.Serialize(new GameInstanceSave(game, SaveReason.MANUAL));
                var company = game.Simulation.CompanyName ?? "";
                session.SendGame(ChannelShipSave, Build(w => { w.Write(company); w.Write(bytes.Length); w.Write(bytes); }));
                SentAt = UnityEngine.Time.realtimeSinceStartup;
                CompanyLive.ShipSent(game.Simulation);
                Log.Info($"SHIP host: sent {Summary(game.Simulation)} ({bytes.Length} bytes, {started.ElapsedMilliseconds} ms)");
            }
            catch (Exception e)
            {
                Log.Error("SHIP host: could not send the ship", e);
            }
        }

        private static void UpdateGuest()
        {
            // The host sends only while it is on the ship and able to save.
            if (Refreshing && !refreshReceived && UnityEngine.Time.realtimeSinceStartup > refreshAskedAt + 30f)
            {
                Refreshing = false;
                ShipRequests.ShowNotice("No ship from the host yet: it is sent once the host is on the ship screen.", 8f);
            }
            if (awaitingLoaded && !stuckNoticeShown && loadStartedAt >= 0
                && UnityEngine.Time.realtimeSinceStartup > loadStartedAt + StuckAfter)
            {
                stuckNoticeShown = true;
                Log.Info($"SHIP guest: the host's ship has been loading for {StuckAfter:0} s without finishing");
                ShipRequests.ShowNotice("Loading the host's ship seems stuck. Restart the game and send both " +
                                        "BattletechCoop.log files and Mods\\.modtek\\battletech_log.txt.", 20f);
            }
            if (awaitingLoaded)
            {
                var current = SceneSingletonBehavior<UnityGameInstance>.HasInstance ? UnityGameInstance.BattleTechGame : null;
                // The new SimGameState exists before the save is applied to it (default company,
                // funds 0) and before its screens are attached, so wait for the game's own
                // "UX attached" point on the loaded company.
                var sim = current?.Simulation;
                if (sim != null && !ReferenceEquals(sim, simBeforeLoad) && current.Combat == null
                    && ReferenceEquals(UXReady, sim) && sim.CompanyName == GuestCompany)
                {
                    awaitingLoaded = false;
                    ShowingSince = UnityEngine.Time.realtimeSinceStartup;
                    ShipRequests.UseGuestUidRange(sim, CoopController.Instance?.Session?.LocalIndex ?? 1);
                    Log.Info("SHIP guest: now showing " + Summary(current.Simulation));
                    if (Refreshing) ShipRequests.ShowNotice("Ship updated from the host", 5f);
                    Refreshing = false;
                }
            }
            if (pendingLoad == null) return;
            if (CombatSync.Current != null) { pendingLoad = null; return; } // a mission is on its way
            var game = SceneSingletonBehavior<UnityGameInstance>.HasInstance ? UnityGameInstance.BattleTechGame : null;
            if (game == null || game.Combat != null) return; // finish the battle / results first
            if (LazySingletonBehavior<LoadTransitioning>.Instance == null) return;
            // A ship the player didn't ask for (the host arrived in another system, came back from
            // a mission...) waits while they are editing, so nothing they're doing is thrown away.
            if (!Refreshing && ShipRequests.GuestEditing())
            {
                if (UnityEngine.Time.realtimeSinceStartup >= ShipRequests.NoticeUntil)
                    ShipRequests.ShowNotice("The host's ship changed; updating once you finish here", 4f);
                return;
            }

            // Not in the middle of the game's own work: right after following a jump (its
            // pathfinding callback and the new system's data are still coming in) or while data
            // is loading. A ship loaded right after a jump never finished (live test 2026-11-05).
            var now = UnityEngine.Time.realtimeSinceStartup;
            var loading = game.DataManager != null && game.DataManager.IsLoading;
            if (now < CompanyLive.LastJumpAt + JumpSettleSeconds || loading)
            {
                if (quietWaitSince < 0)
                {
                    quietWaitSince = now;
                    Log.Info($"SHIP guest: the host's ship waits for this game to settle ({(loading ? "data loading" : "just jumped")})");
                }
                if (now < CompanyLive.LastJumpAt + JumpSettleSeconds || now < quietWaitSince + MaxQuietWait) return;
                Log.Info("SHIP guest: still loading data after 30 s; loading the host's ship anyway");
            }
            quietWaitSince = -1;

            var bytes = pendingLoad;
            pendingLoad = null;
            try
            {
                var save = SaveTransfer.Deserialize(bytes);
                CombatSync.GuestInHostGame = true;
                GuestViewing = true;
                ShowingSince = -1;
                simBeforeLoad = game.Simulation;
                awaitingLoaded = true;
                loadStartedAt = UnityEngine.Time.realtimeSinceStartup;
                stuckNoticeShown = false;
                Log.Info($"SHIP guest: loading the host's ship '{GuestCompany}'");
                Traverse.Create(LazySingletonBehavior<LoadTransitioning>.Instance)
                    .Method("FileReadComplete", new object[] { true, save }).GetValue();
            }
            catch (Exception e)
            {
                Log.Error("SHIP guest: could not load the host's ship", e);
            }
        }

        public static void HandleGameMessage(CoopSession session, ushort channel, byte[] payload)
        {
            using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                if (channel == ChannelShipSave && session.Role == SessionRole.Guest)
                {
                    GuestCompany = r.ReadString();
                    pendingLoad = r.ReadBytes(r.ReadInt32());
                    Log.Info($"SHIP guest: received the host's ship '{GuestCompany}' ({pendingLoad.Length} bytes)");
                    ShipRequests.ShipSaveReceived();
                    refreshReceived = true;
                    ShipRequests.ShowNotice("Loading the host's ship...", 60f);
                }
                else if (channel == ChannelShipRefresh && session.Role == SessionRole.Host)
                {
                    MarkDirty("guest asked for a refresh");
                    urgent = true;
                }
            }
        }

        public static void RequestRefresh(CoopSession session)
        {
            if (Busy) { ShipRequests.ShowNotice("Already loading the host's ship...", 3f); return; }
            session.SendGame(ChannelShipRefresh, new byte[0]);
            Refreshing = true;
            refreshReceived = false;
            refreshAskedAt = UnityEngine.Time.realtimeSinceStartup;
            ShipRequests.ShowNotice("Asking the host for its ship...", 60f);
            Log.Info("SHIP guest: asked the host for a refresh");
        }

        // Logged on both sides after a ship load, for comparing in tests.
        public static string Summary(SimGameState sim) =>
            sim == null ? "no campaign" : $"'{sim.CompanyName}' funds {sim.Funds} day {sim.DaysPassed} system {sim.CurSystem?.Name}";

        private static byte[] Build(Action<BinaryWriter> body)
        {
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream, Encoding.UTF8))
            {
                body(w);
                w.Flush();
                return stream.ToArray();
            }
        }
    }

    // Live test 2026-11-05: the guest's catch-up ship, loaded right after a jump, never finished.
    // Its battletech_log.txt: a NullReferenceException in SGNavigationButton.ManageHiringHallFlyout
    // (SimGameState._OnAttachUXComplete -> SGRoomManager.OnSimGameReady -> RefreshDisplay ->
    // SGNavigationList.RefreshButtonStates), which aborted _OnAttachUXComplete, so the load
    // never completed. The method only decides whether the Barracks button gets a Hiring Hall
    // flyout, from simState.CurSystem's owner reputation and AvailablePilots.Count; skip it while
    // any of those isn't there yet (the next redraw adds the flyout) and log which one it was.
    //
    // Second test, same day (0.0.23): the same abort one line later, in ManageShopFlyout, and our
    // guard above logged "company not there yet": the button's simState itself was null. It is
    // set only by SGNavigationButton.SetupElement, which SGNavigationList.Start calls with
    // UnityGameInstance.BattleTechGame.Simulation, i.e. whichever sim exists when Unity first
    // runs Start. After a reload that can be none (or the sim the load replaced), while
    // OnSimGameReady already redraws the buttons. SGNavigationList.RefreshButtonStates (the only
    // caller of SetStateAccordingToSimDropship, which runs both flyouts and itself reads
    // simState) is handed the sim being shown, so the buttons are pointed at it first, as
    // SetupElement would.
    [HarmonyPatch(typeof(SGNavigationList), "RefreshButtonStates")]
    public static class NavigationButtonsSimPatch
    {
        private static readonly string[] Buttons =
            { "argoButton", "navigationButton", "cmdCenterButton", "mechBayButton", "barracksButton", "captainQuartersButton", "engineeringButton" };

        static void Prefix(SGNavigationList __instance, SimGameState simState)
        {
            if (simState == null) return;
            try
            {
                var fixedCount = 0;
                foreach (var name in Buttons)
                {
                    var button = Traverse.Create(__instance).Field(name).GetValue<SGNavigationButton>();
                    if (button == null) continue;
                    var field = Traverse.Create(button).Field("simState");
                    if (ReferenceEquals(field.GetValue<SimGameState>(), simState)) continue;
                    field.SetValue(simState);
                    fixedCount++;
                }
                if (fixedCount > 0) Log.Info($"UI: {fixedCount} navigation buttons pointed at the company being shown");
            }
            catch (Exception e) { Log.Error("UI: navigation buttons check", e); }
        }
    }

    [HarmonyPatch(typeof(SGNavigationButton), "ManageHiringHallFlyout")]
    public static class HiringHallFlyoutGuardPatch
    {
        static bool Prefix(SGNavigationButton __instance)
        {
            string missing;
            try
            {
                var sim = Traverse.Create(__instance).Field("simState").GetValue<SimGameState>();
                var system = sim?.CurSystem;
                missing = sim == null ? "company" : system == null ? "current system"
                    : system.Def == null ? "system def" : system.AvailablePilots == null ? "pilots for hire"
                    : system.OwnerValue == null ? "system owner" : null;
            }
            catch (Exception e) { missing = e.GetType().Name; }
            if (missing == null) return true;
            Log.Info($"UI: Hiring Hall flyout skipped ({missing} not there yet)");
            return false;
        }
    }

    [HarmonyPatch(typeof(SimGameState), "_OnAttachUXComplete")]
    public static class SimGameAttachUXPatch
    {
        static void Postfix(SimGameState __instance) => ShipView.UXReady = __instance;
    }
}
