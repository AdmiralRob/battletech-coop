using System;
using System.IO;
using System.Linq;
using BattleTech;
using BattleTech.UI;
using BattletechCoop.Net;
using Harmony;

namespace BattletechCoop.Sync
{
    // The rest of the ship (0.0.12): Hiring Hall, Store, Mech Bay readying/storing/scrapping,
    // dismissing pilots, cancelling work orders, and company customization. Same pattern as the
    // refit and pilot requests: captured where the game's screens call the SimGameState/StarSystem/
    // Shop function, sent with the ID counter, and repeated through that same function elsewhere.
    public static partial class ShipRequests
    {
        private const byte KindHire = 5, KindBuy = 6, KindSell = 7, KindReady = 8, KindStore = 9,
            KindScrapActive = 10, KindScrapStored = 11, KindDismiss = 12, KindCancelOrder = 13, KindHeraldry = 14;

        // ---- ID counter ----
        // SimGameState.GenerateSimGameUID is "SGRef_" + ++UIDCount. A guest counts in its own range
        // (player index x 1e9 above the host's), so IDs made on different copies never collide, and
        // a change applied elsewhere runs from its originator's counter, giving the same new IDs.

        public static uint UidCount(SimGameState sim) => Traverse.Create(sim).Field("UIDCount").GetValue<uint>();

        public static void SetUidCount(SimGameState sim, uint value) => Traverse.Create(sim).Field("UIDCount").SetValue(value);

        public static void UseGuestUidRange(SimGameState sim, byte playerIndex)
        {
            var count = UidCount(sim) % 1000000000u + 1000000000u * playerIndex;
            SetUidCount(sim, count);
            Log.Info($"SHIP guest: new IDs start at SGRef_{count + 1}");
        }

        // Capture prefix state: the counter before the call, or -1 to not send.
        internal static long Start(SimGameState sim) => CaptureSession() == null || sim == null ? -1L : (long)UidCount(sim);

        // Several of these call each other (ReadyMech uses up the stored 'Mech through
        // ScrapInactiveMech, CancelWorkOrder can call UnreadyMech), so only the outermost call is
        // sent; the other side's game makes the inner calls itself. Begin/End bracket each patched
        // call; Update clears the depth each frame in case a call threw before its postfix.
        private static int captureDepth;

        internal static long Begin(SimGameState sim)
        {
            var state = captureDepth > 0 ? -1L : Start(sim);
            captureDepth++;
            return state;
        }

        internal static void End()
        {
            if (captureDepth > 0) captureDepth--;
        }

        internal static void ClearCaptureDepth() => captureDepth = 0;

        private static void SendIf(long start, byte kind, string what, Action<BinaryWriter> body)
        {
            var session = CaptureSession();
            if (start < 0 || session == null) return;
            Send(session, kind, what, body, (uint)start);
        }

        // ---- Capture ----

        internal static void CaptureHire(long start, PilotDef def) =>
            SendIf(start, KindHire, $"hire '{def.Description.Callsign}'", w => w.Write(def.Description.Id));

        internal static void CaptureBuy(long start, Shop shop, string id, Shop.PurchaseType purchaseType, ShopItemType itemType) =>
            SendIf(start, KindBuy, $"buy {id}", w =>
            {
                w.Write((int)shop.ThisShopType); w.Write(id); w.Write((int)purchaseType); w.Write((int)itemType);
            });

        internal static void CaptureSell(long start, Shop shop, ShopDefItem item) =>
            SendIf(start, KindSell, $"sell {item.ID}{(item.IsDamaged ? " (damaged)" : "")} for {item.SellCost} C-Bills", w =>
            {
                w.Write((int)shop.ThisShopType); w.Write(item.ID); w.Write((int)item.Type); w.Write(item.IsDamaged); w.Write(item.SellCost);
            });

        internal static void CaptureReady(long start, int baySlot, string id) =>
            SendIf(start, KindReady, $"ready {id} in bay {baySlot}", w => { w.Write(baySlot); w.Write(id); });

        internal static void CaptureStore(long start, int baySlot, MechDef def) =>
            SendIf(start, KindStore, $"put '{def.Name}' in storage", w => { w.Write(baySlot); w.Write(def.GUID); });

        internal static void CaptureScrapActive(long start, int baySlot, MechDef def) =>
            SendIf(start, KindScrapActive, $"scrap '{def.Name}'", w => { w.Write(baySlot); w.Write(def.GUID); });

        internal static void CaptureScrapStored(long start, string id, bool pay) =>
            SendIf(start, KindScrapStored, $"scrap stored {id}", w => { w.Write(id); w.Write(pay); });

        internal static void CaptureDismiss(long start, Pilot pilot) =>
            SendIf(start, KindDismiss, $"dismiss '{pilot.Callsign}'", w => w.Write(pilot.GUID));

        internal static void CaptureCancelOrder(long start, SimGameState sim, WorkOrderEntry entry, bool includeRefund)
        {
            // Work orders are found by 'Mech and order kind; their own IDs differ between copies.
            if (!(entry is WorkOrderEntry_MechLab e)) return;
            var name = sim.GetMechByID(e.MechID)?.Name ?? e.MechID;
            SendIf(start, KindCancelOrder, $"cancel the work order for '{name}'", w =>
            {
                w.Write(e.MechID); w.Write(e.GetType().Name); w.Write(includeRefund);
            });
        }

        internal static void CaptureHeraldry(long start, HeraldryDef def) =>
            SendIf(start, KindHeraldry, $"customize the company ('{def.Description.Name}')", w => w.Write(def.ToJSON()));

        // ---- Digest (tests): what both copies must agree on after the same changes ----

        public static string Digest(SimGameState sim, out string text)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("company '").Append(sim.CompanyName).Append("' funds ").Append(sim.Funds).Append('\n');
            foreach (var p in sim.PilotRoster.OrderBy(p => p.GUID, StringComparer.Ordinal))
                sb.Append("pilot ").Append(p.GUID).Append(' ').Append(PilotSummary(p)).Append('\n');
            foreach (var kv in sim.ActiveMechs.OrderBy(kv => kv.Key))
                sb.Append("bay ").Append(kv.Key).Append(' ').Append(kv.Value.GUID).Append(" '").Append(kv.Value.Name).Append("'\n");
            foreach (var e in sim.MechLabQueue.OfType<WorkOrderEntry_MechLab>().OrderBy(e => e.MechID, StringComparer.Ordinal).ThenBy(e => e.GetType().Name))
                sb.Append("order ").Append(e.MechID).Append(' ').Append(e.GetType().Name).Append(' ').Append(CountTasks(e))
                  .Append(" tasks ").Append(e.GetCBillCostForIncompleteTasks()).Append('\n');
            foreach (var name in sim.CompanyStats.Items.Where(n => n.StartsWith("Item.")).OrderBy(n => n, StringComparer.Ordinal))
                sb.Append("stat ").Append(name).Append('=').Append(sim.CompanyStats.GetStatistic(name)?.CurrentValue).Append('\n');
            foreach (var id in (sim.CurSystem?.AvailablePilots ?? new System.Collections.Generic.List<PilotDef>()).Select(d => d.Description.Id).OrderBy(i => i, StringComparer.Ordinal))
                sb.Append("forhire ").Append(id).Append('\n');
            text = sb.ToString();
            var hash = 2166136261u;
            foreach (var ch in text) { hash ^= ch; hash *= 16777619u; }
            return $"{hash:x8} ({sim.PilotRoster.Count} pilots, {sim.ActiveMechs.Count} 'Mechs, {sim.MechLabQueue.Count} orders, funds {sim.Funds})";
        }

        public static void LogDigest(string when)
        {
            var sim = UnityGameInstance.BattleTechGame?.Simulation;
            if (sim == null || !ShipRequestTest.Enabled) return;
            Log.Info($"SHIP digest {Who} {when}: " + Digest(sim, out var text));
            foreach (var line in text.Split('\n').Where(l => l.Length > 0)) Log.Info("SHIP digest line: " + line);
        }

        // ---- Apply ----

        private static string ApplyMore(SimGameState sim, byte kind, BinaryReader r)
        {
            switch (kind)
            {
                case KindHire:
                {
                    var id = r.ReadString();
                    var def = sim.CurSystem?.AvailablePilots?.FirstOrDefault(p => p.Description.Id == id);
                    if (def == null) return "that pilot is not for hire here (any more)";
                    sim.CurSystem.HirePilot(def);
                    var hired = sim.PilotRoster.FirstOrDefault(p => p.pilotDef.Description.Id == id);
                    Log.Info($"SHIP {Who}: hired '{def.Description.Callsign}' as {hired?.GUID}");
                    return null;
                }
                case KindBuy:
                {
                    var shop = FindShop(sim, r.ReadInt32());
                    var id = r.ReadString();
                    var purchaseType = (Shop.PurchaseType)r.ReadInt32();
                    var itemType = (ShopItemType)r.ReadInt32();
                    if (shop == null) return "that store is not open here";
                    var funds = sim.Funds;
                    if (!shop.Purchase(id, purchaseType, itemType)) return "the store would not sell it (sold out or not enough C-Bills)";
                    Log.Info($"SHIP {Who}: bought {id}, funds {funds} -> {sim.Funds}");
                    return null;
                }
                case KindSell:
                {
                    var shop = FindShop(sim, r.ReadInt32());
                    var id = r.ReadString();
                    var type = (ShopItemType)r.ReadInt32();
                    var damaged = r.ReadBoolean();
                    var cost = r.ReadInt32();
                    if (shop == null) return "that store is not open here";
                    var funds = sim.Funds;
                    if (!shop.SellInventoryItem(id, type, damaged, cost)) return "nothing like that in storage";
                    Log.Info($"SHIP {Who}: sold {id}, funds {funds} -> {sim.Funds}");
                    return null;
                }
                case KindReady:
                {
                    var slot = r.ReadInt32();
                    var id = r.ReadString();
                    if (sim.ActiveMechs.ContainsKey(slot) || sim.ReadyingMechs.ContainsKey(slot)) return $"bay {slot} is already taken";
                    sim.ReadyMech(slot, id);
                    if (!sim.ReadyingMechs.ContainsKey(slot)) return $"no {id} in storage";
                    Log.Info($"SHIP {Who}: readying {id} in bay {slot} as {sim.ReadyingMechs[slot].GUID}");
                    return null;
                }
                case KindStore:
                {
                    var slot = r.ReadInt32();
                    var mech = sim.GetMechByID(r.ReadString());
                    if (mech == null) return "no such 'Mech";
                    sim.UnreadyMech(slot, mech);
                    return null;
                }
                case KindScrapActive:
                {
                    var slot = r.ReadInt32();
                    var mech = sim.GetMechByID(r.ReadString());
                    if (mech == null) return "no such 'Mech";
                    sim.ScrapActiveMech(slot, mech);
                    return null;
                }
                case KindScrapStored:
                {
                    var id = r.ReadString();
                    var pay = r.ReadBoolean();
                    sim.ScrapInactiveMech(id, pay);
                    return null;
                }
                case KindDismiss:
                {
                    var pilot = FindPilot(sim, r.ReadString());
                    if (pilot == null) return "no such pilot";
                    sim.DismissPilot(pilot);
                    return null;
                }
                case KindCancelOrder:
                {
                    var mechId = r.ReadString();
                    var typeName = r.ReadString();
                    var refund = r.ReadBoolean();
                    var entry = sim.MechLabQueue.FirstOrDefault(e => e is WorkOrderEntry_MechLab m && m.MechID == mechId && e.GetType().Name == typeName);
                    if (entry == null) return "no such work order (already finished?)";
                    sim.CancelWorkOrder(entry, refund);
                    // As the Mech Bay's own cancel (MechBayMechInfoWidget.ConfirmCancelWorkOrder):
                    // CancelWorkOrder leaves the schedule entry; UpdateMechLabWorkQueue(false)
                    // removes it (no day passes) and RefreshTimeline redraws the schedule. Without
                    // this the other player's schedule kept the cancelled order (2026-11-01).
                    sim.UpdateMechLabWorkQueue(false);
                    sim.RoomManager?.RefreshTimeline(false);
                    return null;
                }
                case KindHeraldry:
                {
                    var def = new HeraldryDef();
                    def.FromJSON(r.ReadString());
                    // As SGRoomController_CptQuarters.OnHeraldryClosed.
                    sim.Player1sMercUnitHeraldryDef = def;
                    Action loaded = () =>
                    {
                        sim.RoomManager?.SetHeaderCompanyName(def.Description.Name);
                        sim.RoomManager?.SetHeaderCompanyCrest(def.textureLogoID);
                        sim.CameraController?.SetColors(def);
                    };
                    Traverse.Create(def).Method("RequestResources", new object[] { sim.DataManager, loaded }).GetValue();
                    Log.Info($"SHIP {Who}: company is now '{sim.CompanyName}'");
                    return null;
                }
                default:
                    return $"unknown request {kind} (are both players on the same mod version?)";
            }
        }

        private static Shop FindShop(SimGameState sim, int shopType)
        {
            var system = sim.CurSystem;
            switch ((Shop.ShopType)shopType)
            {
                case Shop.ShopType.System: return system?.SystemShop;
                case Shop.ShopType.Faction: return system?.FactionShop;
                case Shop.ShopType.BlackMarket: return system?.BlackMarketShop;
                default: return null;
            }
        }
    }

    // ---- Capture patches (both sides; quiet while a remote change is applied) ----

    [HarmonyPatch(typeof(StarSystem), "HirePilot")]
    public static class HirePilotPatch
    {
        static void Prefix(StarSystem __instance, PilotDef def, ref long __state)
        {
            __state = ShipRequests.Begin(__instance.Sim);
            if (!__instance.AvailablePilots.Contains(def)) __state = -1; // HirePilot does nothing then
        }

        static void Postfix(PilotDef def, long __state) { ShipRequests.End(); ShipRequests.CaptureHire(__state, def); }
    }

    [HarmonyPatch(typeof(Shop), "Purchase")]
    public static class ShopPurchasePatch
    {
        static void Prefix(ref long __state) => __state = ShipRequests.Begin(UnityGameInstance.BattleTechGame?.Simulation);

        static void Postfix(Shop __instance, string id, Shop.PurchaseType purchaseType, ShopItemType itemType, bool __result, long __state)
        {
            ShipRequests.End();
            if (__result) ShipRequests.CaptureBuy(__state, __instance, id, purchaseType, itemType);
        }
    }

    [HarmonyPatch(typeof(Shop), "SellInventoryItem", typeof(ShopDefItem))]
    public static class ShopSellPatch
    {
        static void Prefix(ref long __state) => __state = ShipRequests.Begin(UnityGameInstance.BattleTechGame?.Simulation);

        static void Postfix(Shop __instance, ShopDefItem item, bool __result, long __state)
        {
            ShipRequests.End();
            if (__result) ShipRequests.CaptureSell(__state, __instance, item);
        }
    }

    // ReadyMech and ScrapInactiveMech rewrite their id argument (chassis ID -> storage stat ID)
    // before returning, so the arguments are kept from the prefix.
    public class CallState
    {
        public long Start;
        public string Id;
    }

    [HarmonyPatch(typeof(SimGameState), "ReadyMech")]
    public static class ReadyMechPatch
    {
        static void Prefix(SimGameState __instance, string id, ref CallState __state) =>
            __state = new CallState { Start = ShipRequests.Begin(__instance), Id = id };

        static void Postfix(int baySlot, CallState __state) { ShipRequests.End(); ShipRequests.CaptureReady(__state.Start, baySlot, __state.Id); }
    }

    [HarmonyPatch(typeof(SimGameState), "UnreadyMech")]
    public static class UnreadyMechPatch
    {
        static void Prefix(SimGameState __instance, ref long __state) => __state = ShipRequests.Begin(__instance);
        static void Postfix(int baySlot, MechDef def, long __state) { ShipRequests.End(); ShipRequests.CaptureStore(__state, baySlot, def); }
    }

    [HarmonyPatch(typeof(SimGameState), "ScrapActiveMech")]
    public static class ScrapActiveMechPatch
    {
        static void Prefix(SimGameState __instance, ref long __state) => __state = ShipRequests.Begin(__instance);
        static void Postfix(int baySlot, MechDef def, long __state) { ShipRequests.End(); ShipRequests.CaptureScrapActive(__state, baySlot, def); }
    }

    [HarmonyPatch(typeof(SimGameState), "ScrapInactiveMech")]
    public static class ScrapInactiveMechPatch
    {
        static void Prefix(SimGameState __instance, string id, ref CallState __state) =>
            __state = new CallState { Start = ShipRequests.Begin(__instance), Id = id };

        static void Postfix(bool pay, CallState __state) { ShipRequests.End(); ShipRequests.CaptureScrapStored(__state.Start, __state.Id, pay); }
    }

    // DismissPilot(string) goes through DismissPilot(Pilot).
    [HarmonyPatch(typeof(SimGameState), "DismissPilot", typeof(Pilot))]
    public static class DismissPilotPatch
    {
        static void Prefix(SimGameState __instance, Pilot p, ref long __state) => __state = ShipRequests.Begin(__instance);
        static void Postfix(Pilot p, long __state) { ShipRequests.End(); if (p != null) ShipRequests.CaptureDismiss(__state, p); }
    }

    [HarmonyPatch(typeof(SimGameState), "CancelWorkOrder")]
    public static class CancelWorkOrderPatch
    {
        static void Prefix(SimGameState __instance, WorkOrderEntry entry, bool includeRefund, ref long __state)
        {
            __state = ShipRequests.Begin(__instance);
            // Captured before the cancel, while the order can still be described.
            ShipRequests.CaptureCancelOrder(__state, __instance, entry, includeRefund);
        }

        static void Postfix() => ShipRequests.End();
    }

    [HarmonyPatch(typeof(SGRoomController_CptQuarters), "OnHeraldryClosed")]
    public static class HeraldryClosedPatch
    {
        static void Prefix(SGRoomController_CptQuarters __instance, HeraldryDef newHeraldryDef)
        {
            var sim = UnityGameInstance.BattleTechGame?.Simulation;
            if (newHeraldryDef == null || sim == null || ReferenceEquals(newHeraldryDef, sim.Player1sMercUnitHeraldryDef)) return;
            ShipRequests.CaptureHeraldry(ShipRequests.Start(sim), newHeraldryDef);
        }
    }
}
