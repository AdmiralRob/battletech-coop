using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BattleTech;
using BattleTech.UI;
using BattletechCoop.Net;
using Harmony;

namespace BattletechCoop.Sync
{
    // Level B ship co-op (DESIGN.md "Campaign layer"): the guest's ship is a throwaway copy of the
    // host's, so its own game applies a refit or pilot change at once (the guest sees it), and the
    // same change goes to the host as a request. The host rebuilds it with its own objects through
    // the game's own functions (work orders, UpgradePilot), so costs, refit time and parts from
    // storage work as if the host had done it, then replies applied/refused.
    //
    // The host's own changes of the same kinds go the other way as updates, which each guest
    // applies to its copy the same way, so small changes need no full ship reload.
    //
    // Refits are sent as their work orders (install/remove, armor, structure, component repair),
    // not as objects: parts the guest took from storage get new IDs on the guest, and the host's
    // game finds those parts in its own storage by part type.
    public static partial class ShipRequests
    {
        public const ushort ChannelRequest = 21;
        public const ushort ChannelReply = 22;
        public const ushort ChannelUpdate = 23;

        private const byte KindRefit = 1, KindRename = 2, KindPilotSkills = 3, KindPilotNames = 4;
        private const byte EntryBase = 0, EntryInstall = 1, EntryArmor = 2, EntryStructure = 3, EntryRepair = 4;

        public static string Notice { get; private set; } = "";
        public static float NoticeUntil { get; private set; }

        // While a change from the other side is being applied, the capture patches stay quiet.
        private static bool applyingRemote;
        // Guest: ship updates and company statuses from the host, in the order it sent them.
        internal static readonly GuestInbox Inbox = new GuestInbox();
        private static bool screensDirty;

        // Guest: a host change didn't apply, so this copy of the ship differs from the host's.
        // Ask for a fresh ship, but only once the player isn't editing (see GuestEditing), and at
        // most once per received ship so a change that can never apply can't loop.
        private static bool healWanted, healAskedForThisShip;
        private static string healReason = "";
        private static float lastHealAt = -1000f;
        private const float MinSecondsBetweenHeals = 300f; // never a reload loop

        // Guest: requests sent to the host that it hasn't answered yet. While any is out, this
        // copy is ahead of the host's, so CompanyLive skips its fingerprint check.
        internal static int PendingRequests { get; private set; }
        internal static bool HasQueuedUpdates => Inbox.Count > 0;
        internal static void MarkScreensDirty() => screensDirty = true;

        // Run a change that came from the host without the capture patches sending it back.
        internal static void RunAsRemote(Action change)
        {
            var was = applyingRemote;
            applyingRemote = true;
            try { change(); }
            finally { applyingRemote = was; }
        }

        private static string Who => CoopController.Instance?.Session?.Role == SessionRole.Guest ? "guest" : "host";

        // A change made here goes out when on the ship in a session: the guest asks the host (only
        // while showing the host's ship), the host tells its guests.
        internal static CoopSession CaptureSession()
        {
            if (applyingRemote) return null;
            var session = CoopController.Instance?.Session;
            if (session == null || session.State != SessionState.Active || CombatSync.Current != null) return null;
            var game = UnityGameInstance.BattleTechGame;
            if (game?.Simulation == null || game.Combat != null) return null;
            if (session.Role == SessionRole.Guest)
            {
                if (!ShipView.GuestViewing) return null;
                // Every capture starts here, in a prefix, before the game makes the change. The
                // host made its earlier changes (and passed its earlier days) before this one, so
                // apply everything still held first (PR #3 review).
                ProcessInbox(true);
                return session;
            }
            return session.Players.Any(p => p.Index != 0) ? session : null;
        }

        // Every change carries the ID counter its side started from (see UidStart), so a change
        // that creates a pilot or 'Mech gives it the same ID on every copy.
        internal static void Send(CoopSession session, byte kind, string what, Action<BinaryWriter> body, uint? uidStart = null)
        {
            var uid = uidStart ?? UidCount(UnityGameInstance.BattleTechGame.Simulation);
            var payload = Build(w => { w.Write(kind); w.Write(what); w.Write(uid); body(w); });
            if (session.Role == SessionRole.Guest)
            {
                session.SendGame(ChannelRequest, payload);
                PendingRequests++;
                Log.Info("SHIP guest: asked the host to " + what);
            }
            else
            {
                session.SendGame(ChannelUpdate, payload);
                Log.Info("SHIP host: told the guests: " + what);
            }
        }

        // ---- Capture (both sides) ----

        public static void CaptureRename(MechDef mech, string newName)
        {
            var session = CaptureSession();
            if (session == null || mech == null || newName == null || mech.Name == newName) return;
            Send(session, KindRename, $"rename '{mech.Name}' to '{newName}'", w => { w.Write(mech.GUID); w.Write(newName); });
        }

        public static void CaptureRefit(SimGameState sim, WorkOrderEntry_MechLab entry)
        {
            var session = CaptureSession();
            if (session == null || entry == null) return;
            var mech = sim.GetMechByID(entry.MechID);
            var name = mech?.Name ?? entry.MechID;
            try
            {
                var body = Build(w => { w.Write(entry.MechID); WriteEntry(w, entry, mech); });
                Send(session, KindRefit, $"refit '{name}' ({CountTasks(entry)} tasks, {entry.GetCBillCostForIncompleteTasks()} C-Bills)",
                    w => { w.Write(body.Length); w.Write(body); });
            }
            catch (NotSupportedException e)
            {
                ShowNotice($"Refit not shared with the other players: {e.Message}");
                Log.Info($"SHIP {Who}: refit not sent: " + e.Message);
            }
        }

        public static void CapturePilotSkills(Pilot before, Pilot after)
        {
            var session = CaptureSession();
            if (session == null || before == null || after == null) return;
            var xp = after.SpentXP - before.SpentXP;
            Send(session, KindPilotSkills,
                $"train '{after.Callsign}' to G{after.Gunnery} P{after.Piloting} U{after.Guts} T{after.Tactics} ({xp} XP)",
                w =>
                {
                    w.Write(after.GUID);
                    w.Write(after.Gunnery); w.Write(after.Piloting); w.Write(after.Guts); w.Write(after.Tactics);
                    w.Write(xp);
                });
        }

        public static void CapturePilotNames(Pilot pilot)
        {
            var session = CaptureSession();
            if (session == null || pilot == null) return;
            var d = pilot.pilotDef.Description;
            Send(session, KindPilotNames, $"rename pilot to '{d.Callsign}' ({d.FirstName} {d.LastName})", w =>
            {
                w.Write(pilot.GUID);
                w.Write(d.FirstName ?? ""); w.Write(d.LastName ?? ""); w.Write(d.Callsign ?? "");
                w.Write((int)d.Gender);
                w.Write(pilot.pilotDef.Voice ?? "");
            });
        }

        private static void WriteEntry(BinaryWriter w, WorkOrderEntry entry, MechDef mech)
        {
            switch (entry)
            {
                case WorkOrderEntry_InstallComponent e:
                    w.Write(EntryInstall);
                    w.Write(e.MechComponentID); w.Write((int)e.ComponentType); w.Write(e.ComponentSimGameUID ?? "");
                    w.Write((int)e.DamageLevel); w.Write((int)e.DesiredLocation); w.Write((int)e.PreviousLocation);
                    w.Write(e.HardpointSlot);
                    break;
                case WorkOrderEntry_ModifyMechArmor e:
                    w.Write(EntryArmor);
                    w.Write((int)e.Location); w.Write(e.DesiredFrontArmor); w.Write(e.DesiredRearArmor);
                    break;
                case WorkOrderEntry_RepairMechStructure e:
                    w.Write(EntryStructure);
                    w.Write((int)e.Location); w.Write(e.StructureAmount);
                    break;
                case WorkOrderEntry_RepairComponent e:
                    w.Write(EntryRepair);
                    w.Write(e.MechComponentID); w.Write((int)e.ComponentType); w.Write(e.ComponentSimGameUID ?? "");
                    w.Write((int)e.DamageLevel);
                    w.Write(mech?.Inventory?.Any(c => c.SimGameUID == e.ComponentSimGameUID) == true);
                    break;
                case WorkOrderEntry_MechLab e when e.GetType() == typeof(WorkOrderEntry_MechLab):
                    w.Write(EntryBase);
                    w.Write((int)e.Type); w.Write(e.ID ?? ""); w.Write(e.Description ?? ""); w.Write(e.ToastDescription ?? "");
                    break;
                default:
                    throw new NotSupportedException($"{entry.GetType().Name} work orders are not supported in co-op yet");
            }
            var subs = entry.SubEntries ?? new List<WorkOrderEntry>();
            w.Write(subs.Count);
            foreach (var sub in subs) WriteEntry(w, sub, mech);
        }

        public static int CountTasks(WorkOrderEntry e) => 1 + (e.SubEntries?.Sum(CountTasks) ?? 0) - (e.GetType() == typeof(WorkOrderEntry_MechLab) ? 1 : 0);

        // ---- Messages ----

        public static void HandleGameMessage(CoopSession session, byte sender, ushort channel, byte[] payload)
        {
            if (channel == ChannelUpdate && session.Role == SessionRole.Guest)
            {
                // In order with company statuses; applied now unless something before it waits
                // (a ship still loading, or a held status).
                Receive(GuestInbox.Kind.ShipUpdate, payload);
                return;
            }
            using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                if (channel == ChannelReply && session.Role == SessionRole.Guest)
                {
                    var ok = r.ReadBoolean();
                    var text = r.ReadString();
                    if (PendingRequests > 0) PendingRequests--;
                    Log.Info($"SHIP guest: host {(ok ? "applied" : "refused")}: {text}");
                    ShowNotice(ok ? "Host applied: " + text : "Host refused: " + text);
                }
                else if (channel == ChannelRequest && session.Role == SessionRole.Host)
                {
                    var kind = r.ReadByte();
                    var what = r.ReadString();
                    var player = session.Players.FirstOrDefault(p => p.Index == sender)?.Name ?? $"player {sender}";
                    var result = ApplyRemote(kind, r, what);
                    var ok = result == null;
                    Log.Info($"SHIP host: {(ok ? "applied" : "refused")} {player}'s request to {what}{(ok ? "" : ": " + result)}");
                    ShowNotice(ok ? $"{player}: {what}" : $"Refused {player}'s request to {what}: {result}");
                    session.SendGame(ChannelReply, Build(w => { w.Write(ok); w.Write(ok ? what : $"{what}: {result}"); }), sender);
                    // Other guests get it as an update; the sender already has it.
                    if (ok)
                        foreach (var p in session.Players.Where(p => p.Index != 0 && p.Index != sender))
                            session.SendGame(ChannelUpdate, payload, p.Index);
                }
            }
        }

        // A full ship save supersedes the updates sent before it.
        public static void ShipSaveReceived()
        {
            Inbox.Clear();
            healWanted = false;
            healAskedForThisShip = false;
        }

        // Guest: something about the host's ship arrived; apply what may be applied now.
        internal static void Receive(GuestInbox.Kind kind, byte[] payload)
        {
            Inbox.Add(kind, payload);
            ProcessInbox(false);
        }

        // Applies queued host changes in order. Statuses wait while the player is editing, unless
        // force: the player is about to make a change of their own, which must land after them.
        private static string holdReason;

        internal static void ProcessInbox(bool force)
        {
            if (Inbox.Count == 0) { holdReason = null; return; }
            var game = UnityGameInstance.BattleTechGame;
            var sim = game?.Combat == null ? game?.Simulation : null;
            var ready = ShipView.Ready && sim != null;
            var editing = GuestEditing();
            var statusesMayApply = !ShipView.Busy && (force || !editing);
            Inbox.Process(ready, statusesMayApply, force, ApplyUpdate, payload =>
            {
                try { CompanyLive.ApplyStatus(sim, payload); }
                catch (Exception e)
                {
                    Log.Error("LIVE guest: could not apply a company update", e);
                    WantHeal("a company update failed here");
                }
            });
            // Say once per hold why host changes are waiting, so a stalled day can be explained.
            var reason = Inbox.Count == 0 ? null
                : !ready ? (sim == null ? "not on the ship" : "the host's ship is loading")
                : ShipView.Busy ? "a fresh ship was asked for"
                : editing ? "the MechLab is open or pilot training is unconfirmed" : "waiting";
            if (reason != null && reason != holdReason)
                Log.Info($"LIVE guest: holding {Inbox.Count} host change(s): {reason}");
            else if (reason == null && holdReason != null)
                Log.Info("LIVE guest: caught up with the host");
            holdReason = reason;
        }

        // Guest: this copy of the host's ship is out of date (a host change didn't apply here).
        internal static void WantHeal(string reason)
        {
            if (healAskedForThisShip || UnityEngine.Time.realtimeSinceStartup - lastHealAt < MinSecondsBetweenHeals)
            {
                ShowNotice($"Could not show the host's change ({reason}). Use \"Refresh the host's ship\".");
                return;
            }
            healWanted = true;
            healReason = reason;
            Log.Info("SHIP guest: out of date with the host (" + reason + "); will ask for a fresh ship");
        }

        // The player is in the middle of something a ship reload would throw away: the MechLab is
        // open, or the shown pilot has unconfirmed training (as RedrawScreens waits for).
        internal static bool GuestEditing()
        {
            bool IsOpen(UIModule module) => module != null && module.gameObject.activeInHierarchy && module.Visible;
            if (IsOpen(UnityEngine.Object.FindObjectOfType<MechLabPanel>())) return true;
            var barracks = UnityEngine.Object.FindObjectOfType<SGBarracksWidget>();
            return IsOpen(barracks) && barracks.IsCurPilotModified();
        }

        private static void UpdateHeal()
        {
            if (!healWanted || !ShipView.Ready || ShipView.Busy) return;
            var session = CoopController.Instance?.Session;
            if (session == null || session.Role != SessionRole.Guest || !session.IsOpen) { healWanted = false; return; }
            if (GuestEditing())
            {
                if (UnityEngine.Time.realtimeSinceStartup >= NoticeUntil)
                    ShowNotice("The host's ship changed; updating once you finish here", 4f);
                return;
            }
            healWanted = false;
            healAskedForThisShip = true;
            lastHealAt = UnityEngine.Time.realtimeSinceStartup;
            Log.Info("SHIP guest: asking for a fresh ship to catch up (" + healReason + ")");
            ShipView.RequestRefresh(session);
            ShowNotice("Catching up with the host's ship...", 60f);
        }

        private static void ApplyUpdate(byte[] payload)
        {
            using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                var kind = r.ReadByte();
                var what = r.ReadString();
                var result = ApplyRemote(kind, r, what);
                if (result == null)
                {
                    Log.Info("SHIP guest: applied the host's change: " + what);
                    ShowNotice("Host: " + what);
                }
                else
                {
                    Log.Info($"SHIP guest: could not apply the host's change ({what}): {result}");
                    WantHeal($"{what}: {result}");
                }
            }
        }

        private static string ApplyRemote(byte kind, BinaryReader r, string what)
        {
            var uidStart = r.ReadUInt32();
            var sim = UnityGameInstance.BattleTechGame?.Simulation;
            var ownUid = sim == null ? 0u : UidCount(sim);
            applyingRemote = true;
            try
            {
                // IDs made while applying come from the originator's counter; ours is restored after.
                if (sim != null) SetUidCount(sim, uidStart);
                var result = Apply(kind, r);
                if (result == null)
                {
                    screensDirty = true;
                    LogDigest("after " + what);
                }
                return result;
            }
            catch (Exception e)
            {
                Log.Error($"SHIP {Who}: could not apply '{what}'", e);
                return "the game could not apply it (see BattletechCoop.log)";
            }
            finally
            {
                applyingRemote = false;
                if (sim != null) SetUidCount(sim, ownUid);
            }
        }

        public static void ShowNotice(string text, float seconds = 8f)
        {
            Notice = text;
            NoticeUntil = UnityEngine.Time.realtimeSinceStartup + seconds;
        }

        public static void Reset()
        {
            Inbox.Clear();
            screensDirty = false;
            healWanted = false;
            healAskedForThisShip = false;
            PendingRequests = 0;
        }

        public static void Update()
        {
            ClearCaptureDepth();
            ProcessInbox(false);
            if (screensDirty) RedrawScreens();
            UpdateHeal();
        }

        // After a change from the other side, redraw the Mech Bay or Barracks if open, as the game
        // does after its own changes. Waits while the MechLab is open or the shown pilot has
        // unconfirmed training (SGBarracksWidget.IsCurPilotModified), so nothing the player is
        // editing is thrown away.
        private static void RedrawScreens()
        {
            // The screens stay in the scene after first use; Visible says whether one is up.
            bool IsOpen(UIModule module) => module != null && module.gameObject.activeInHierarchy && module.Visible;

            try
            {
                var sim = UnityGameInstance.BattleTechGame?.Simulation;
                if (sim == null) { screensDirty = false; return; }
                if (IsOpen(UnityEngine.Object.FindObjectOfType<MechLabPanel>())) return;
                var barracks = UnityEngine.Object.FindObjectOfType<SGBarracksWidget>();
                var barracksOpen = IsOpen(barracks);
                if (barracksOpen && barracks.IsCurPilotModified()) return;
                screensDirty = false;

                var bay = UnityEngine.Object.FindObjectOfType<MechBayPanel>();
                if (IsOpen(bay) && bay.Initialized)
                {
                    bay.RefreshData(false);
                    // As the end of MechBayPanel.OnMechLabComplete: select the same bay slot again.
                    var t = Traverse.Create(bay);
                    var group = t.Field("bayGroupWidget").GetValue();
                    var slot = t.Field("selectedBaySlot").GetValue();
                    var unit = group == null ? null : Traverse.Create(group).Method("GetMechUnitForSlot", slot).GetValue();
                    if (unit != null) t.Method("SelectMech", unit, true).GetValue();
                    Log.Info("SHIP: redrew the Mech Bay");
                }
                if (barracksOpen)
                {
                    // UpgradePilot replaces the pilot object, so look the shown pilot up again.
                    var shown = Traverse.Create(barracks).Field("selectedPilot").GetValue<Pilot>();
                    var pilot = (shown == null ? null : FindPilot(sim, shown.GUID)) ?? sim.Commander;
                    barracks.Reset(pilot);
                    Log.Info($"SHIP: redrew the Barracks (showing '{pilot?.Callsign}')");
                }
                var hiring = UnityEngine.Object.FindObjectOfType<SG_HiringHall_Screen>();
                if (IsOpen(hiring))
                {
                    // AddPeople clears and refills the list from the system's pilots for hire.
                    var t = Traverse.Create(hiring);
                    t.Method("AddPeople").GetValue();
                    t.Method("UpdateMoneySpot").GetValue();
                    Log.Info("SHIP: redrew the Hiring Hall");
                }
                var shop = UnityEngine.Object.FindObjectOfType<SG_Shop_Screen>();
                if (IsOpen(shop))
                {
                    // As switching tabs: refill the buy list of the current store, or the sell list.
                    var t = Traverse.Create(shop);
                    if (t.Field("isInBuyingState").GetValue<bool>())
                    {
                        var storeType = Convert.ToInt32(t.Field("currentStoreType").GetValue());
                        var system = sim.CurSystem;
                        var current = storeType == 0 ? system?.SystemShop : storeType == 1 ? system?.FactionShop : system?.BlackMarketShop;
                        if (current != null) shop.ChangeToBuy(current, true);
                    }
                    else shop.ChangeToSell();
                    t.Method("UpdateMoneySpot").GetValue();
                    Log.Info("SHIP: redrew the Store");
                }
            }
            catch (Exception e)
            {
                screensDirty = false;
                Log.Error("SHIP: could not redraw the open screen", e);
            }
        }

        // ---- Apply a change from the other side. Returns null when applied, else why not. ----

        private static string Apply(byte kind, BinaryReader r)
        {
            var game = UnityGameInstance.BattleTechGame;
            var sim = game?.Simulation;
            if (sim == null || game.Combat != null || CombatSync.Current != null) return "not on the ship";
            switch (kind)
            {
                case KindRename: return ApplyRename(sim, r.ReadString(), r.ReadString());
                case KindRefit: return ApplyRefit(sim, r.ReadBytes(r.ReadInt32()));
                case KindPilotSkills: return ApplyPilotSkills(sim, r);
                case KindPilotNames: return ApplyPilotNames(sim, r);
                default: return ApplyMore(sim, kind, r);
            }
        }

        private static string ApplyRename(SimGameState sim, string mechId, string newName)
        {
            var mech = sim.GetMechByID(mechId);
            if (mech == null) return "no such 'Mech";
            mech.RenameMech(newName, null, null);
            sim.RoomManager?.RefreshTimeline(false);
            Log.Info($"SHIP {Who}: 'Mech {mech.GUID} is now '{mech.Name}'");
            return null;
        }

        private static string ApplyRefit(SimGameState sim, byte[] body)
        {
            using (var r = new BinaryReader(new MemoryStream(body), Encoding.UTF8))
            {
                var mechId = r.ReadString();
                var mech = sim.GetMechByID(mechId);
                if (mech == null) return "no such 'Mech";
                if (sim.GetWorkOrderEntryForMech(mech) != null) return $"'{mech.Name}' already has a refit in progress";
                var entry = ReadEntry(r, sim, mech) as WorkOrderEntry_MechLab;
                if (entry == null) return "unexpected work order";
                var cost = entry.GetCBillCostForIncompleteTasks();
                if (cost > sim.Funds) return $"needs {cost} C-Bills, the company has {sim.Funds}";

                // As MechBayPanel.OnMechLabComplete, without the UI.
                if (!sim.MechLabQueue.Contains(entry)) sim.MechLabQueue.Add(entry);
                var funds = sim.Funds;
                sim.InitializeMechLabEntry(entry, 0);
                sim.UpdateMechLabWorkQueue(false);
                sim.RoomManager?.RefreshTimeline(false);
                Log.Info($"SHIP {Who}: queued refit '{mech.Name}' ({CountTasks(entry)} tasks, {cost} C-Bills), funds {funds} -> {sim.Funds}");
                return null;
            }
        }

        private static WorkOrderEntry ReadEntry(BinaryReader r, SimGameState sim, MechDef mech)
        {
            WorkOrderEntry entry;
            var kind = r.ReadByte();
            switch (kind)
            {
                case EntryBase:
                {
                    var type = (WorkOrderType)r.ReadInt32();
                    var id = r.ReadString();
                    var description = r.ReadString();
                    var toast = r.ReadString();
                    var e = new WorkOrderEntry_MechLab(type, id, description, mech.GUID, 0, toast);
                    e.SetMechDef(mech);
                    entry = e;
                    break;
                }
                case EntryInstall:
                {
                    var defId = r.ReadString();
                    var type = (ComponentType)r.ReadInt32();
                    var uid = r.ReadString();
                    var damage = (ComponentDamageLevel)r.ReadInt32();
                    var desired = (ChassisLocations)r.ReadInt32();
                    var previous = (ChassisLocations)r.ReadInt32();
                    var hardpoint = r.ReadInt32();
                    var component = FindOrMakeComponent(sim, mech, defId, type, uid, damage, previous, hardpoint);
                    entry = sim.CreateComponentInstallWorkOrder(mech.GUID, component, desired, previous);
                    break;
                }
                case EntryArmor:
                {
                    var location = (ChassisLocations)r.ReadInt32();
                    var front = r.ReadInt32();
                    var rear = r.ReadInt32();
                    // As MechLabLocationWidget.SetArmor: the change from the 'Mech's current armor.
                    var loadout = mech.GetLocationLoadoutDef(location);
                    var diff = (int)Math.Abs(front - loadout.AssignedArmor) + (int)Math.Abs(rear - loadout.AssignedRearArmor);
                    entry = sim.CreateMechArmorModifyWorkOrder(mech.GUID, location, diff, front, rear);
                    break;
                }
                case EntryStructure:
                {
                    var location = (ChassisLocations)r.ReadInt32();
                    var amount = r.ReadInt32();
                    entry = sim.CreateMechRepairWorkOrder(mech.GUID, location, amount);
                    break;
                }
                case EntryRepair:
                {
                    var defId = r.ReadString();
                    var type = (ComponentType)r.ReadInt32();
                    var uid = r.ReadString();
                    var damage = (ComponentDamageLevel)r.ReadInt32();
                    var onMech = r.ReadBoolean();
                    var component = FindOrMakeComponent(sim, mech, defId, type, uid, damage, ChassisLocations.None, -1);
                    entry = sim.CreateComponentRepairWorkOrder(component, onMech);
                    break;
                }
                default:
                    throw new InvalidDataException($"unknown work order kind {kind}");
            }
            var count = r.ReadInt32();
            for (var i = 0; i < count; i++) entry.AddSubEntry(ReadEntry(r, sim, mech));
            return entry;
        }

        // A part on the host's 'Mech by its ID, else the same part type at that location (the guest
        // may have installed it in its preview, giving it a new ID), else a new reference that the
        // host's game then takes from storage by type.
        private static MechComponentRef FindOrMakeComponent(SimGameState sim, MechDef mech, string defId, ComponentType type,
            string uid, ComponentDamageLevel damage, ChassisLocations location, int hardpoint)
        {
            var inventory = mech.Inventory ?? new MechComponentRef[0];
            var found = inventory.FirstOrDefault(c => !string.IsNullOrEmpty(uid) && c.SimGameUID == uid)
                        ?? (location == ChassisLocations.None ? null
                            : inventory.FirstOrDefault(c => c.ComponentDefID == defId && c.MountedLocation == location && c.DamageLevel == damage));
            if (found != null) return found;
            var component = new MechComponentRef(defId, sim.GenerateSimGameUID(), type, location, hardpoint, damage, false);
            component.DataManager = sim.DataManager;
            component.RefreshComponentDef();
            if (component.Def == null) throw new InvalidDataException($"this game has no part '{defId}' (different mods?)");
            return component;
        }

        internal static Pilot FindPilot(SimGameState sim, string guid) =>
            sim.PilotRoster.FirstOrDefault(p => p.GUID == guid) ?? (sim.Commander?.GUID == guid ? sim.Commander : null);

        private static string ApplyPilotSkills(SimGameState sim, BinaryReader r)
        {
            var pilot = FindPilot(sim, r.ReadString());
            var target = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
            var xp = r.ReadInt32();
            if (pilot == null) return "no such pilot";
            if (target[0] == pilot.Gunnery && target[1] == pilot.Piloting && target[2] == pilot.Guts && target[3] == pilot.Tactics)
                return null; // already applied
            if (xp > pilot.UnspentXP) return $"needs {xp} XP, the pilot has {pilot.UnspentXP}";

            var p = Train(sim, pilot, target, out var error);
            if (p == null) return error;
            if (xp > 0) p.SpendExperience(0, "Advancement", (uint)xp);
            sim.UpgradePilot(p);
            sim.RoomManager?.RefreshTimeline(false);
            Log.Info($"SHIP {Who}: pilot now " + PilotSummary(FindPilot(sim, pilot.GUID)));
            return null;
        }

        public static string PilotSummary(Pilot p) => p == null ? "missing" :
            $"'{p.Callsign}' ({p.pilotDef.Description.FirstName} {p.pilotDef.Description.LastName}) G{p.Gunnery} P{p.Piloting} U{p.Guts} T{p.Tactics} XP {p.UnspentXP}/{p.SpentXP} abilities {string.Join(",", p.pilotDef.abilityDefNames.OrderBy(a => a))}";

        // As SGBarracksAdvancementPanel.SetTempPilotSkill, one level at a time (the confirm then
        // spends the XP and calls UpgradePilot). Gunnery, Piloting, Guts, Tactics.
        public static Pilot Train(SimGameState sim, Pilot pilot, int[] target, out string error)
        {
            error = null;
            var skills = new[] { "Gunnery", "Piloting", "Guts", "Tactics" };
            var current = new[] { pilot.Gunnery, pilot.Piloting, pilot.Guts, pilot.Tactics };
            var p = pilot;
            for (var s = 0; s < skills.Length; s++)
            {
                if (target[s] < current[s]) { error = $"{skills[s]} is already {current[s]}"; return null; }
                for (var level = current[s]; level < target[s]; level++)
                {
                    var def = p.ToPilotDef(true);
                    def.DataManager = sim.DataManager;
                    def.ForceRefreshAbilityDefs();
                    foreach (var ability in sim.AbilityTree[skills[s]][level])
                        if (sim.CanPilotTakeAbility(def, ability, false)) def.abilityDefNames.Add(ability.Description.Id);
                    def.ForceRefreshAbilityDefs();
                    var next = new Pilot(def, p.GUID, true);
                    next.pilotDef.DataManager = p.pilotDef.DataManager;
                    next.ModifyPilotStat_Barracks(0, "Advancement", skills[s], (uint)(level + 1));
                    p = next;
                }
            }
            return p;
        }

        private static string ApplyPilotNames(SimGameState sim, BinaryReader r)
        {
            var pilot = FindPilot(sim, r.ReadString());
            var first = r.ReadString();
            var last = r.ReadString();
            var callsign = r.ReadString();
            var gender = r.ReadInt32();
            var voice = r.ReadString();
            if (pilot == null) return "no such pilot";
            // As SGBarracksMWCustomizationPopup.Save (portraits are not sent yet).
            var d = pilot.pilotDef.Description;
            d.SetFirstName(first);
            d.SetLastName(last);
            d.SetCallsign(callsign);
            d.SetName(callsign);
            d.SetGender((Gender)gender);
            if (voice.Length > 0) pilot.pilotDef.SetVoice(voice);
            sim.RoomManager?.RefreshTimeline(false);
            Log.Info($"SHIP {Who}: pilot now " + PilotSummary(pilot));
            return null;
        }

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

    // ---- Guest capture patches ----

    // MechLab confirm: the rename happens here (also without work orders).
    [HarmonyPatch(typeof(MechBayPanel), "OnMechLabComplete")]
    public static class MechLabCompletePatch
    {
        static void Prefix(MechBayPanel __instance, string newMechNickname)
        {
            var mech = Traverse.Create(__instance).Field("selectedMech").GetValue<MechBayMechUnitElement>()?.MechDef;
            ShipRequests.CaptureRename(mech, newMechNickname);
        }
    }

    // Every refit or repair the player orders (MechLab, repair, repair all) starts here.
    [HarmonyPatch(typeof(SimGameState), "InitializeMechLabEntry")]
    public static class InitializeMechLabEntryPatch
    {
        static void Prefix(SimGameState __instance, WorkOrderEntry_MechLab entry) => ShipRequests.CaptureRefit(__instance, entry);
    }

    [HarmonyPatch(typeof(SGBarracksMWDetailPanel), "OnPilotConfirmAccepted")]
    public static class PilotConfirmPatch
    {
        static void Prefix(SGBarracksMWDetailPanel __instance)
        {
            var t = Traverse.Create(__instance);
            ShipRequests.CapturePilotSkills(t.Field("curPilot").GetValue<Pilot>(), t.Field("tempPilot").GetValue<Pilot>());
        }
    }

    // The customization popup is in the global namespace.
    [HarmonyPatch]
    public static class PilotCustomizationPatch
    {
        static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("SGBarracksMWCustomizationPopup"), "Save");

        static void Postfix(object __instance) =>
            ShipRequests.CapturePilotNames(Traverse.Create(__instance).Property("pilot").GetValue<Pilot>());
    }
}
