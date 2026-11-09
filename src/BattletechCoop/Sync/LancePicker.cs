using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BattleTech;
using BattleTech.UI;
using BattletechCoop.Net;
using Harmony;
using UnityEngine;

namespace BattletechCoop.Sync
{
    // The split lance's picking step (COOP-PLAN.md "Feature: split lance", slice 2).
    //
    // Host: the game's own lance configuration screen. Once it has loaded the last lance
    // (LanceConfiguratorPanel.LoadLanceConfiguration, called when the roster is ready), the
    // guest's slots (3-4) are emptied back into the lists and locked, the way contracts lock
    // slots with required units (LanceLoadoutSlot.SetLockState). The host fills slots 1-2 and
    // may keep swapping them. The guest's picks are placed with the game's own sequence for
    // loading a saved lance (mechListWidget.GetMechDefByGUID, pilotListWidget.GetPilot /
    // RemovePilot, LanceLoadoutSlot.SetLockedData), which locks them in. Launch waits until the
    // guest has sent its picks (or "none"); if the guest leaves, its slots unlock for the host.
    //
    // Guest: a picker window listing the host's free 'Mechs and pilots, shown while the host's
    // screen is up and enabled once the host has picked.
    public static class LancePicker
    {
        public const ushort Channel = 33;
        private const byte KindOffer = 1, KindPicks = 2, KindResult = 3;

        // ---- host ----
        private static LanceConfiguratorPanel panel;
        private static bool claimed, released, guestConfirmed;
        private static int[] guestSlots = new int[0];
        private static float openedAt, nextOfferAt;
        private static string lastOfferKey;
        private static byte picker = 1;

        // Host: who picked which pilot for the lance that just launched (pilot ID -> player), for
        // LanceOwners; null when the guest didn't pick (no guest, or the guest left).
        internal static Dictionary<string, byte> LastAssignment { get; private set; }

        // ---- guest ----
        internal static PickOffer GuestOffer { get; private set; }
        private static readonly List<string> chosenMechs = new List<string>();
        private static readonly List<string> chosenPilots = new List<string>();
        private static string resultText = "";
        private static Rect windowRect = new Rect(480, 80, 460, 560);
        private static Vector2 scroll;
        private static bool sending;

        public static void Reset()
        {
            panel = null;
            claimed = released = guestConfirmed = false;
            guestSlots = new int[0];
            lastOfferKey = null;
            LastAssignment = null;
            GuestOffer = null;
            chosenMechs.Clear();
            chosenPilots.Clear();
            resultText = "";
            sending = false;
        }

        private static CoopSession Session => CoopController.Instance?.Session;

        private static bool HostWithGuest(out CoopSession session)
        {
            session = Session;
            return session != null && session.State == SessionState.Active && session.Role == SessionRole.Host
                   && session.Players.Any(p => p.Index != 0);
        }

        private static string PlayerName(CoopSession session, byte index) =>
            session?.Players.FirstOrDefault(p => p.Index == index)?.Name ?? (index == 0 ? "the host" : "your partner");

        private static string GuestName(CoopSession session) =>
            session?.Players.FirstOrDefault(p => p.Index != 0)?.Name ?? "your partner";

        // ---- host: the lance screen ----

        internal static void Opened(LanceConfiguratorPanel opened)
        {
            if (!HostWithGuest(out _) || !Traverse.Create(opened).Property("IsSimGame").GetValue<bool>()) return;
            panel = opened;
            claimed = released = guestConfirmed = false;
            LastAssignment = null;
            guestSlots = new int[0];
            lastOfferKey = null;
            openedAt = Time.realtimeSinceStartup;
            Log.Info("LANCE host: lance screen opened; the guest's slots are claimed once it has loaded");
        }

        internal static void Loaded(LanceConfiguratorPanel loaded)
        {
            if (ReferenceEquals(loaded, panel) && !claimed) Claim();
        }

        internal static void Closed(LanceConfiguratorPanel closed)
        {
            if (!ReferenceEquals(closed, panel)) return;
            Log.Info("LANCE host: lance screen closed");
            if (claimed && !released && guestConfirmed)
            {
                var slots = Slots;
                LastAssignment = guestSlots.Where(i => i < slots.Length && slots[i].SelectedPilot?.Pilot != null)
                    .ToDictionary(i => slots[i].SelectedPilot.Pilot.Description.Id, i => picker);
                Log.Info($"LANCE host: player {picker} picked {LastAssignment.Count} for this mission");
            }
            panel = null;
            SendOffer(new PickOffer { Open = false });
        }

        // Launch waits for the guest's picks.
        internal static bool AllowConfirm(LanceConfiguratorPanel confirming)
        {
            if (!ReferenceEquals(confirming, panel) || !claimed || released) return true;
            HostWithGuest(out var session);
            var block = PickRules.LaunchBlock(session != null && session.Players.Any(p => p.Index != 0), guestConfirmed, GuestName(session));
            if (block == null) return true;
            ShipRequests.ShowNotice(block, 5f);
            return false;
        }

        public static void Update()
        {
            if (panel == null) { if (!ReferenceEquals(panel, null)) panel = null; return; } // destroyed
            if (!claimed)
            {
                // LoadLanceConfiguration runs only when there is a last lance to load.
                if (Traverse.Create(panel).Property("Initialized").GetValue<bool>() && Time.realtimeSinceStartup > openedAt + 1.5f) Claim();
                return;
            }
            if (!released && !HostWithGuest(out _)) Release();
            if (released || Time.realtimeSinceStartup < nextOfferAt) return;
            nextOfferAt = Time.realtimeSinceStartup + 0.3f;
            var offer = BuildOffer();
            var key = offer.Key();
            if (key == lastOfferKey) return;
            lastOfferKey = key;
            SendOffer(offer);
        }

        private static LanceLoadoutSlot[] Slots =>
            Traverse.Create(panel).Field("loadoutSlots").GetValue<LanceLoadoutSlot[]>() ?? new LanceLoadoutSlot[0];

        private static int LockState(LanceLoadoutSlot slot) => Convert.ToInt32(Traverse.Create(slot).Field("curLockState").GetValue());

        private static void SetLock(LanceLoadoutSlot slot, int state)
        {
            var type = AccessTools.Field(typeof(LanceLoadoutSlot), "curLockState").FieldType;
            Traverse.Create(slot).Method("SetLockState", new object[] { Enum.ToObject(type, state) }).GetValue();
        }

        private static void Claim()
        {
            claimed = true;
            var slots = Slots;
            var maxUnits = Traverse.Create(panel).Field("maxUnits").GetValue<int>();
            var count = Math.Min(slots.Length, maxUnits > 0 ? maxUnits : slots.Length);
            var mine = new List<int>();
            foreach (var i in PickRules.GuestSlotIndices(count))
            {
                if (LockState(slots[i]) != 0) { Log.Info($"LANCE host: slot {i + 1} is locked by the contract; it stays the host's"); continue; }
                ClearSlot(slots[i]);
                SetLock(slots[i], 1);
                mine.Add(i);
            }
            guestSlots = mine.ToArray();
            panel.ValidateLance();
            Log.Info($"LANCE host: slots {string.Join(", ", guestSlots.Select(i => (i + 1).ToString()).ToArray())} are the guest's to pick");
            if (guestSlots.Length == 0) released = true; // nothing to pick: plain lance screen
        }

        // Empties a slot back into the screen's lists (as replacing a slot's 'Mech does: the
        // slot's OnAddItem returns the old one with LanceConfiguratorPanel.ReturnItem).
        private static void ClearSlot(LanceLoadoutSlot slot)
        {
            SetLock(slot, 0);
            var mech = slot.SelectedMech;
            var pilot = slot.SelectedPilot;
            if (mech != null) { slot.OnRemoveItem(mech, false); panel.ReturnItem(mech); }
            if (pilot != null) { slot.OnRemoveItem(pilot, false); panel.ReturnItem(pilot); }
        }

        // The guest left: its slots become the host's to fill (its picks stay, unlocked).
        private static void Release()
        {
            released = true;
            var slots = Slots;
            foreach (var i in guestSlots.Where(i => i < slots.Length)) SetLock(slots[i], 0);
            panel.ValidateLance();
            Log.Info("LANCE host: the guest left; its slots are the host's to fill");
            ShipRequests.ShowNotice("Your co-op partner left: slots 3-4 are yours to fill", 6f);
        }

        private static PickOffer BuildOffer()
        {
            var session = Session;
            var slots = Slots;
            var sim = UnityGameInstance.BattleTechGame?.Simulation;
            var offer = new PickOffer
            {
                Open = true,
                Contract = Traverse.Create(panel).Field("activeContract").GetValue<Contract>()?.Name ?? "",
                HostName = PlayerName(session, 0),
                GuestSlots = guestSlots.Length,
                GuestConfirmed = guestConfirmed,
            };
            // In a slot: not free, except the guest's own picks, which it may pick again.
            var hostSlotSet = Enumerable.Range(0, slots.Length).Where(i => !guestSlots.Contains(i)).ToList();
            var inSlots = new HashSet<string>(hostSlotSet.Where(i => slots[i].SelectedMech?.MechDef != null).Select(i => slots[i].SelectedMech.MechDef.GUID));
            var pilotsInSlots = new HashSet<string>(hostSlotSet.Where(i => slots[i].SelectedPilot?.Pilot != null).Select(i => slots[i].SelectedPilot.Pilot.Description.Id));
            foreach (var i in guestSlots.Where(i => i < slots.Length))
            {
                var gm = slots[i].SelectedMech?.MechDef;
                var gp = slots[i].SelectedPilot?.Pilot;
                if (gm != null) offer.Mechs.Add(new PickChoice { Id = gm.GUID, Name = MechName(gm), Detail = $"{gm.Chassis?.Tonnage ?? 0:0} t, yours now" });
                if (gp != null) offer.Pilots.Add(new PickChoice { Id = gp.Description.Id, Name = gp.Callsign, Detail = $"G{gp.Gunnery} P{gp.Piloting} G{gp.Guts} T{gp.Tactics}, yours now" });
            }
            var hostSlots = Enumerable.Range(0, slots.Length).Where(i => !guestSlots.Contains(i)).Take(2).ToList();
            foreach (var i in hostSlots)
            {
                var m = slots[i].SelectedMech?.MechDef;
                var p = slots[i].SelectedPilot?.Pilot;
                offer.HostPicks.Add(new PickChoice { Id = m?.GUID ?? "", Name = m == null ? "(empty)" : MechName(m), Detail = p?.Callsign ?? "" });
            }
            var guestMechs = new HashSet<string>(offer.Mechs.Select(c => c.Id));
            var guestPilots = new HashSet<string>(offer.Pilots.Select(c => c.Id));
            foreach (var item in Traverse.Create(panel).Field("availableMechs").GetValue<IEnumerable>() ?? new object[0])
            {
                if (!(item is MechDef m) || inSlots.Contains(m.GUID) || guestMechs.Contains(m.GUID)) continue;
                if (sim != null && !MechValidationRules.ValidateMechCanBeFielded(sim, m)) continue;
                offer.Mechs.Add(new PickChoice { Id = m.GUID, Name = MechName(m), Detail = $"{m.Chassis?.Tonnage ?? 0:0} t" });
            }
            foreach (var item in Traverse.Create(panel).Field("availablePilots").GetValue<IEnumerable>() ?? new object[0])
            {
                if (!(item is Pilot p) || !p.CanPilot || pilotsInSlots.Contains(p.Description.Id) || guestPilots.Contains(p.Description.Id)) continue;
                offer.Pilots.Add(new PickChoice { Id = p.Description.Id, Name = p.Callsign, Detail = $"G{p.Gunnery} P{p.Piloting} G{p.Guts} T{p.Tactics}" });
            }
            foreach (var i in guestSlots.Where(i => i < slots.Length))
            {
                var m = slots[i].SelectedMech?.MechDef;
                var p = slots[i].SelectedPilot?.Pilot;
                if (m != null && p != null) offer.GuestPicks.Add(new LancePick { MechId = m.GUID, PilotId = p.Description.Id });
            }
            offer.HostReady = PickRules.HostReady(hostSlots.Count(i => slots[i].SelectedMech != null), hostSlots.Count, offer.Mechs.Count);
            return offer;
        }

        private static string MechName(MechDef m) =>
            $"{m.Description?.UIName ?? m.Description?.Name ?? m.Description?.Id} {m.Chassis?.VariantName}".Trim();

        private static void SendOffer(PickOffer offer)
        {
            var session = Session;
            if (session == null || session.State != SessionState.Active || session.Role != SessionRole.Host) return;
            session.SendGame(Channel, Build(w => { w.Write(KindOffer); offer.Write(w); }));
            if (offer.Open)
                Log.Info($"LANCE host: offer sent (host ready {offer.HostReady}, {offer.Mechs.Count} 'Mechs, {offer.Pilots.Count} pilots, " +
                         $"guest has {offer.GuestPicks.Count}, confirmed {offer.GuestConfirmed})");
        }

        private static void ApplyPicks(CoopSession session, byte sender, List<LancePick> picks)
        {
            string result;
            if (panel == null || !claimed || released) result = "the host's lance screen isn't waiting for picks";
            else result = PickRules.Validate(picks, BuildOffer()) ?? Place(picks);
            var ok = result == null;
            if (ok) { guestConfirmed = true; picker = sender; }
            Log.Info($"LANCE host: player {sender} picked {picks.Count}: {(ok ? "placed" : result)}");
            ShipRequests.ShowNotice(ok
                ? $"{PlayerName(session, sender)} picked {(picks.Count == 0 ? "no 'Mechs" : picks.Count == 1 ? "a 'Mech" : picks.Count + " 'Mechs")}"
                : $"{PlayerName(session, sender)}'s pick didn't fit: {result}", 5f);
            session.SendGame(Channel, Build(w => { w.Write(KindResult); w.Write(ok); w.Write(result ?? ""); }));
            lastOfferKey = null; // send the new state
        }

        private static string Place(List<LancePick> picks)
        {
            var slots = Slots;
            var targets = guestSlots.Where(i => i < slots.Length).ToList();
            foreach (var i in targets) ClearSlot(slots[i]);
            string failure = null;
            var mechList = Traverse.Create(panel).Field("mechListWidget").GetValue<MechBayMechStorageWidget>();
            var pilotList = Traverse.Create(panel).Field("pilotListWidget").GetValue<SGBarracksRosterList>();
            var available = Traverse.Create(panel).Field("availablePilots").GetValue<List<Pilot>>();
            var sim = UnityGameInstance.BattleTechGame?.Simulation;
            for (var k = 0; k < picks.Count && failure == null; k++)
            {
                var pick = picks[k];
                var mechItem = mechList?.GetMechDefByGUID(pick.MechId);
                if (mechItem == null) { failure = "that 'Mech isn't in the host's list"; break; }
                if (sim != null && !MechValidationRules.ValidateMechCanBeFielded(sim, mechItem.MechDef)) { failure = "that 'Mech can't be fielded"; break; }
                var pilotItem = pilotList?.GetPilot(pick.PilotId);
                var pilot = (pilotItem as SGBarracksRosterSlot)?.Pilot;
                if (pilot == null || !pilot.CanPilot) { failure = "that pilot can't drop"; break; }
                pilotList.RemovePilot(available?.Find(p => p.Description.Id == pick.PilotId));
                slots[targets[k]].SetLockedData(mechItem, pilotItem, false);
            }
            foreach (var i in targets.Where(i => slots[i].SelectedMech == null && slots[i].SelectedPilot == null)) SetLock(slots[i], 1);
            panel.ValidateLance();
            return failure;
        }

        // ---- guest: the picker ----

        private static void HandleOfferAsGuest(PickOffer offer)
        {
            var wasOpen = GuestOffer?.Open == true;
            GuestOffer = offer;
            if (!offer.Open)
            {
                if (wasOpen) Log.Info("LANCE guest: the host's lance screen closed");
                chosenMechs.Clear(); chosenPilots.Clear(); resultText = ""; sending = false;
                return;
            }
            if (!wasOpen)
            {
                Log.Info($"LANCE guest: the host's lance screen is up for {offer.Contract}");
                ShipRequests.ShowNotice($"{offer.HostName} is picking the lance; you pick yours next", 6f);
            }
            if (chosenMechs.Count == 0 && chosenPilots.Count == 0)
            {
                chosenMechs.AddRange(offer.GuestPicks.Select(p => p.MechId));
                chosenPilots.AddRange(offer.GuestPicks.Select(p => p.PilotId));
            }
            chosenMechs.RemoveAll(id => !offer.MechAvailable(id));
            chosenPilots.RemoveAll(id => !offer.PilotAvailable(id));
        }

        public static void OnGUI()
        {
            var offer = GuestOffer;
            var session = Session;
            if (offer == null || !offer.Open || session == null || session.Role != SessionRole.Guest) return;
            windowRect = GUILayout.Window(0x0B7C33, windowRect, DrawPicker, $"Pick your 'Mechs: {offer.Contract}");
        }

        private static void DrawPicker(int id)
        {
            var offer = GuestOffer;
            GUILayout.Label($"{offer.HostName}'s 'Mechs:");
            foreach (var h in offer.HostPicks) GUILayout.Label($"   {h.Name}{(h.Detail.Length > 0 ? " - " + h.Detail : "")}");
            if (!offer.HostReady)
            {
                GUILayout.Label($"Waiting for {offer.HostName} to pick their 'Mechs...");
                GUI.DragWindow();
                return;
            }
            GUILayout.Label($"Pick up to {offer.GuestSlots} 'Mechs and a pilot for each (in the same order):");
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(330));
            GUILayout.Label("'Mechs");
            foreach (var m in offer.Mechs) Toggle(chosenMechs, m, offer.GuestSlots);
            GUILayout.Label("Pilots");
            foreach (var p in offer.Pilots) Toggle(chosenPilots, p, offer.GuestSlots);
            GUILayout.EndScrollView();

            var pairs = chosenMechs.Zip(chosenPilots, (m, p) => new LancePick { MechId = m, PilotId = p }).ToList();
            GUILayout.Label(chosenMechs.Count == chosenPilots.Count ? $"{pairs.Count} picked" : "Pick as many pilots as 'Mechs");
            GUILayout.BeginHorizontal();
            GUI.enabled = !sending && chosenMechs.Count == chosenPilots.Count && chosenMechs.Count > 0;
            if (GUILayout.Button($"Send to {offer.HostName}")) SendPicks(pairs);
            GUI.enabled = !sending;
            if (GUILayout.Button("No 'Mechs for me")) { chosenMechs.Clear(); chosenPilots.Clear(); SendPicks(new List<LancePick>()); }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (offer.GuestConfirmed) GUILayout.Label($"{offer.HostName} has your picks and can launch. You can still change them.");
            if (resultText.Length > 0) GUILayout.Label(resultText);
            GUI.DragWindow();
        }

        private static void Toggle(List<string> chosen, PickChoice c, int max)
        {
            var at = chosen.IndexOf(c.Id);
            var label = (at >= 0 ? $"[{at + 1}] " : "      ") + c.Name + (c.Detail.Length > 0 ? "  (" + c.Detail + ")" : "");
            if (!GUILayout.Button(label)) return;
            if (at >= 0) chosen.RemoveAt(at);
            else if (chosen.Count < max) chosen.Add(c.Id);
        }

        private static void SendPicks(List<LancePick> picks)
        {
            var reason = PickRules.Validate(picks, GuestOffer);
            if (reason != null) { resultText = "Can't send: " + reason; return; }
            sending = true;
            resultText = "Sent; waiting for the host...";
            Session?.SendGame(Channel, Build(w =>
            {
                w.Write(KindPicks);
                w.Write(picks.Count);
                foreach (var p in picks) p.Write(w);
            }));
            Log.Info($"LANCE guest: sent {picks.Count} picks");
        }

        // ---- messages ----

        public static void HandleGameMessage(CoopSession session, byte sender, byte[] payload)
        {
            try
            {
                using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
                {
                    var kind = r.ReadByte();
                    if (kind == KindOffer && session.Role == SessionRole.Guest && sender == 0)
                        HandleOfferAsGuest(PickOffer.Read(r));
                    else if (kind == KindPicks && session.Role == SessionRole.Host && sender != 0)
                    {
                        var n = r.ReadInt32();
                        var picks = new List<LancePick>();
                        for (var i = 0; i < n; i++) picks.Add(LancePick.Read(r));
                        ApplyPicks(session, sender, picks);
                    }
                    else if (kind == KindResult && session.Role == SessionRole.Guest && sender == 0)
                    {
                        var ok = r.ReadBoolean();
                        var reason = r.ReadString();
                        sending = false;
                        resultText = ok ? "The host has your picks." : "Not placed: " + reason;
                        ShipRequests.ShowNotice(ok ? "Your 'Mechs are in the lance" : "Your pick didn't fit: " + reason, 5f);
                        Log.Info("LANCE guest: " + resultText);
                    }
                }
            }
            catch (Exception e) { Log.Error("LANCE: could not handle a picking message", e); }
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

    [HarmonyPatch(typeof(LanceConfiguratorPanel), "SetData")]
    public static class LanceScreenOpenedPatch
    {
        static void Postfix(LanceConfiguratorPanel __instance) => LancePicker.Opened(__instance);
    }

    [HarmonyPatch(typeof(LanceConfiguratorPanel), "LoadLanceConfiguration")]
    public static class LanceScreenLoadedPatch
    {
        static void Postfix(LanceConfiguratorPanel __instance) => LancePicker.Loaded(__instance);
    }

    [HarmonyPatch(typeof(LanceConfiguratorPanel), "OnConfirmClicked")]
    public static class LanceScreenConfirmPatch
    {
        static bool Prefix(LanceConfiguratorPanel __instance) => LancePicker.AllowConfirm(__instance);
    }

    [HarmonyPatch(typeof(LanceConfiguratorPanel), "OnLanceConfiguratorClosed")]
    public static class LanceScreenClosedPatch
    {
        static void Postfix(LanceConfiguratorPanel __instance) => LancePicker.Closed(__instance);
    }
}
