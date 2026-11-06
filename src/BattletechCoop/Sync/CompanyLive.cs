using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BattleTech;
using BattletechCoop.Net;
using Harmony;
using HBS;

namespace BattletechCoop.Sync
{
    // Keeps the guest's copy of the host's company current while the host plays, without
    // reloading the ship. The full ship (ShipView) is the starting point; after it the host sends
    // a CompanyStatus whenever its company changes:
    //
    // - Each day (SimGameState.OnDayPassed): the work-queue steps the game ran that day, in order
    //   (UpdateMechLabWorkQueue(passDay), UpdateInjuries()). The guest runs the same steps on its
    //   copy. They depend only on the queues and the company's MechTech/MedTech skills, so both
    //   copies finish the same refits and heal the same pilots.
    // - Every change: the company's and every pilot's stats that changed since the last status
    //   (C-Bills, morale, inventory counts, XP, injuries...), and the day counters. The guest sets
    //   them with the game's own StatCollection.Set/AddStatistic. Anything stat-level, such as an
    //   event's results, is covered this way; the host notices those by checking once a second.
    // - A fingerprint of the company (days, all company and pilot stats, roster, bays, both
    //   queues). If the guest's copy doesn't match after applying, something structural differs
    //   (a 'Mech or pilot an event added, a new star system...) and the guest catches up with a
    //   fresh ship, once it isn't busy (ShipRequests.WantHeal).
    //
    // Statuses wait on the guest while its ship is loading, and while the MechLab is open or a
    // pilot has unconfirmed training, so nothing the player is editing changes underneath them.
    public static class CompanyLive
    {
        public const ushort Channel = 36; // host -> guests: CompanyStatus

        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

        // Host: what the guests have (the last ship or status sent).
        private static Dictionary<string, StatValue> baseCompany;
        private static Dictionary<string, Dictionary<string, StatValue>> basePilots;
        private static uint baseFingerprint;
        private static DateTime nextCheck = DateTime.MinValue;
        private static bool inDay;
        private static readonly List<byte> dayOps = new List<byte>();
        // A day's status goes out when the day ends; one sent before the host settled is partial
        // and a settled one follows (see Settled, HostStatusGate).
        private static readonly HostStatusGate gate = new HostStatusGate();

        private static uint baseMarket; // host: market hash the guests have

        // Guest: statuses wait in ShipRequests.Inbox, in order with ordinary ship updates.
        private static bool replaying;
        private static MarketData pendingMarket; // waits for this copy's own shop loading

        public static void Reset()
        {
            baseCompany = null;
            basePilots = null;
            inDay = false;
            dayOps.Clear();
            gate.Reset();
            pendingMarket = null;
        }

        // A new full ship supersedes a market still waiting here.
        internal static void ShipSaveReceived() => pendingMarket = null;

        // Guest: while it shows the host's ship, its stores and hiring halls come only from the
        // host. The game makes its own after a jump, late: SetCurrentSystem -> OnSystemChange ->
        // RefreshShops (async item collections), and FindRouteTo(TargetSystem) ->
        // OnTargetSystemFound -> RefreshSystem -> GeneratePilots + RefreshShops, a pathfinding
        // callback that came after the host's market was applied and replaced it (live test
        // 2026-11-05: guest pilots PilotGen_SGRef_1000000861.. against the host's _861..).
        // StarSystemRefreshShopsPatch / StarSystemGeneratePilotsPatch skip those on the guest.
        internal static bool HostMarketOnly
        {
            get
            {
                var session = Session;
                return session != null && session.State == SessionState.Active && session.Role == SessionRole.Guest && ShipView.Ready;
            }
        }

        // Guest: when it last followed a jump. The game's own arrival work (the pathfinding
        // callback above, the new system's data loading) runs for a few seconds after it, and a
        // ship loaded meanwhile never finished loading (live test 2026-11-05), so ShipView waits.
        internal static float LastJumpAt { get; private set; } = -100f;

        private static CoopSession Session => CoopController.Instance?.Session;

        private static SimGameState ShipSim()
        {
            var game = SceneSingletonBehavior<UnityGameInstance>.HasInstance ? UnityGameInstance.BattleTechGame : null;
            return game != null && game.Combat == null ? game.Simulation : null;
        }

        private static bool HostLive(out CoopSession session, out SimGameState sim)
        {
            session = Session;
            sim = ShipSim();
            return session != null && session.State == SessionState.Active && session.Role == SessionRole.Host
                && session.Players.Any(p => p.Index != 0) && sim != null && CombatSync.Current == null && baseCompany != null
                && !ShipView.SnapshotPending;
        }

        // ---- Snapshots ----

        internal static StatValue? ValueOf(Variant v)
        {
            var t = v?.type;
            if (t == typeof(bool)) return StatValue.Of(v.Value<bool>());
            if (t == typeof(int)) return StatValue.Of(v.Value<int>());
            if (t == typeof(float)) return StatValue.Of(v.Value<float>());
            if (t == typeof(string)) return StatValue.Of(v.Value<string>());
            return null;
        }

        internal static Dictionary<string, StatValue> Snapshot(StatCollection stats)
        {
            var d = new Dictionary<string, StatValue>(StringComparer.Ordinal);
            if (stats == null) return d;
            foreach (var name in stats.Items.ToList())
            {
                var value = ValueOf(stats.GetStatistic(name)?.CurrentValue);
                if (value.HasValue) d[name] = value.Value;
            }
            return d;
        }

        private static IEnumerable<Pilot> Pilots(SimGameState sim)
        {
            if (sim.Commander != null) yield return sim.Commander;
            foreach (var p in sim.PilotRoster) if (p != null && p != sim.Commander) yield return p;
        }

        private static Dictionary<string, Dictionary<string, StatValue>> SnapshotPilots(SimGameState sim) =>
            Pilots(sim).Where(p => p.GUID != null).GroupBy(p => p.GUID)
                .ToDictionary(g => g.Key, g => Snapshot(g.First().StatCollection), StringComparer.Ordinal);

        // Everything the two copies must agree on, in named sections: the same text on every PC.
        internal static List<KeyValuePair<string, string>> FingerprintSections(SimGameState sim) =>
            CompanyFingerprint.Sections(
                sim.DaysPassed, sim.DayRemainingInQuarter, sim.CurSystem?.ID, (int)sim.TravelState, sim.TravelTime,
                Snapshot(sim.CompanyStats),
                Pilots(sim).Select(p => new CompanyFingerprint.PilotEntry { Guid = p.GUID, Callsign = p.Callsign, Stats = Snapshot(p.StatCollection) }),
                sim.ActiveMechs.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value.GUID} '{kv.Value.Name}'"),
                sim.MechLabQueue.Select(e => $"{e.GetType().Name} {(e as WorkOrderEntry_MechLab)?.MechID} {e.GetRemainingCost().ToString(CultureInfo.InvariantCulture)}"),
                // The MedBay queue is one work order whose sub-entries are the pilots healing.
                (sim.MedBayQueue?.SubEntries ?? new List<WorkOrderEntry>())
                    .Select(e => $"{e.GetType().Name} {e.GetRemainingCost().ToString(CultureInfo.InvariantCulture)}"),
                sim.FinancialReportNotification?.GetRemainingCost() ?? -1,
                sim.Starmap?.TravelOrder?.GetRemainingCost() ?? -1,
                RouteIds(sim), TravelIndex(sim), MarketLines(sim),
                ReadTravelOrder(sim.Starmap?.TravelOrder)?.Lines());

        // ---- Travel route and market ----

        // The route the host is on: the systems of Starmap.ActivePath, and how far along.
        internal static List<string> RouteIds(SimGameState sim) =>
            (sim.Starmap?.ActivePath ?? new List<StarSystemNode>()).Select(n => n?.System?.ID ?? "?").ToList();

        internal static int TravelIndex(SimGameState sim) =>
            sim.Starmap == null ? 0 : Traverse.Create(sim.Starmap).Field("<travelIndex>k__BackingField").GetValue<int>();

        private static Shop[] Shops(SimGameState sim)
        {
            var system = sim.CurSystem;
            return new[] { system?.SystemShop, system?.FactionShop, system?.BlackMarketShop };
        }

        // Shop.RefreshShop fills the stock asynchronously (IsPending until it's in).
        private static bool AnyShopPending(SimGameState sim) => Shops(sim).Any(shop => shop != null && shop.IsPending);

        internal static IEnumerable<string> MarketLines(SimGameState sim)
        {
            var shops = Shops(sim);
            for (var i = 0; i < shops.Length; i++)
            {
                var shop = shops[i];
                if (shop == null) { yield return $"shop {i} none"; continue; }
                yield return $"shop {i} refresh {shop.LastRefreshDay}";
                foreach (var item in shop.ActiveInventory ?? new List<ShopDefItem>())
                    yield return $"item {i} {item.ID} {item.Type} {item.Count} {item.IsInfinite} {item.IsDamaged} " +
                                 item.DiscountModifier.ToString("R", CultureInfo.InvariantCulture);
            }
            var system = sim.CurSystem;
            if (system == null) yield break;
            yield return $"forhire refresh {system.LastRefreshDay}";
            foreach (var def in system.AvailablePilots ?? new List<PilotDef>())
                yield return "forhire " + def?.Description?.Id;
        }

        private static uint MarketHash(SimGameState sim) => Fnv.Hash(string.Join("\n", MarketLines(sim).ToArray()));

        private static MarketData BuildMarket(SimGameState sim)
        {
            var m = new MarketData { SystemId = sim.CurSystem?.ID ?? "" };
            var shops = Shops(sim);
            for (var i = 0; i < shops.Length; i++)
            {
                if (shops[i] == null) continue;
                var data = new MarketData.ShopData { LastRefreshDay = shops[i].LastRefreshDay };
                foreach (var item in shops[i].ActiveInventory ?? new List<ShopDefItem>()) data.Items.Add(item.ToJSON());
                m.Shops[i] = data;
            }
            if (sim.CurSystem != null)
            {
                m.PilotsRefreshDay = sim.CurSystem.LastRefreshDay;
                foreach (var def in sim.CurSystem.AvailablePilots ?? new List<PilotDef>()) m.Pilots.Add(def.ToJSON());
            }
            return m;
        }

        // Guest: the host's stock and pilots for hire, once this copy's shops have finished their
        // own loading (which would otherwise overwrite them). Returns false while it must wait.
        private static bool TryApplyMarket(SimGameState sim, MarketData m)
        {
            if (sim.CurSystem == null || sim.CurSystem.ID != m.SystemId || AnyShopPending(sim)) return false;
            var shops = Shops(sim);
            for (var i = 0; i < shops.Length; i++)
            {
                if (shops[i] == null || m.Shops[i] == null) continue;
                var inventory = shops[i].ActiveInventory;
                inventory.Clear();
                foreach (var json in m.Shops[i].Items) { var item = new ShopDefItem(); item.FromJSON(json); inventory.Add(item); }
                Traverse.Create(shops[i]).Property("LastRefreshDay").SetValue(m.Shops[i].LastRefreshDay);
            }
            // As LanceLoader does for the host's pilots in combat: a PilotDef made from JSON needs
            // this machine's DataManager, its abilities re-linked, and any missing dependencies
            // (portrait parts...) loaded; the Hiring Hall redraws once they're in.
            var pilots = sim.CurSystem.AvailablePilots;
            pilots.Clear();
            var request = new BattleTech.Data.DataManager.InjectedDependencyLoadRequest(sim.DataManager);
            foreach (var json in m.Pilots)
            {
                var def = new PilotDef();
                def.FromJSON(json);
                def.DataManager = sim.DataManager;
                def.ForceRefreshAbilityDefs();
                if (!def.DependenciesLoaded(1000)) def.GatherDependencies(sim.DataManager, request, 1000);
                pilots.Add(def);
            }
            if (request.DependencyCount() > 0)
            {
                var defs = pilots.ToList();
                request.RegisterLoadCompleteCallback(() =>
                {
                    foreach (var def in defs) def.ForceRefreshAbilityDefs();
                    ShipRequests.MarkScreensDirty();
                });
                sim.DataManager.InjectDependencyLoader(request, 1000);
            }
            Traverse.Create(sim.CurSystem).Property("LastRefreshDay").SetValue(m.PilotsRefreshDay);
            ShipRequests.MarkScreensDirty();
            Log.Info($"LIVE guest: store and hiring hall now as the host's in {sim.CurSystem.Name} " +
                     $"({m.Shops.Where(x => x != null).Sum(x => x.Items.Count)} items, {m.Pilots.Count} pilots)");
            return true;
        }

        // Guest: follow the host's route and jumps with the game's own travel steps, so the
        // guest travels and arrives without a reload.
        private static void FollowTravel(SimGameState sim, CompanyStatus status)
        {
            var starmap = sim.Starmap;
            if (starmap == null) return;
            if (status.Route.Count > 0 && !RouteIds(sim).SequenceEqual(status.Route))
            {
                // As pressing Go: the route found (PotentialPath) becomes the trip (SetActivePath ->
                // CreateActivePath: travel order, travel state, timeline). The route comes from
                // the host instead of this copy's own pathfinding.
                var nodes = status.Route.Select(id => starmap.GetSystemByID(id)).ToList();
                var order = BuildTravelOrder(status.TravelOrder);
                if (nodes.Any(n => n == null)) Log.Info("LIVE guest: the host's route has a system this copy doesn't know");
                else if (order == null)
                    Traverse.Create(starmap).Property("ActivePath").SetValue(nodes); // a route without a trip: nothing to start
                else
                {
                    // CreateActivePath takes PendingTravelOrder as the trip's timeline entry; the
                    // game makes it in OnPathfindingComplete, which this skips, so it is the
                    // host's order (PR #3 review 3). Any order this copy planned is replaced.
                    Traverse.Create(starmap).Field("PendingTravelOrder").SetValue(order);
                    Traverse.Create(starmap).Property("PotentialPath").SetValue(nodes);
                    try { starmap.SetActivePath(); }
                    // SetActivePath ends with the starmap renderer's UpdateActivePath, after the
                    // trip is made; with the starmap screen not up that throws. SyncTravelOrder
                    // below repairs the timeline entry if anything else went wrong.
                    catch (Exception e) when (starmap.Screen == null) { Log.Info($"LIVE guest: route started without the starmap screen ({e.GetType().Name})"); }
                    Log.Info($"LIVE guest: following the host's route {string.Join(" > ", status.Route.ToArray())}");
                }
            }
            if (!string.IsNullOrEmpty(status.SystemId) && sim.CurSystem?.ID != status.SystemId)
            {
                var index = TravelIndex(sim);
                if (RouteIds(sim).SequenceEqual(status.Route) && index + 1 == status.TravelIndex
                    && status.TravelIndex < status.Route.Count && status.Route[status.TravelIndex] == status.SystemId)
                    starmap.IncrementTravelIndexAndUpdateCurrentSystem(); // the host's own jump step
                else
                {
                    var node = starmap.GetSystemByID(status.SystemId);
                    if (node?.System != null) sim.SetCurrentSystem(node.System, true, false);
                }
                LastJumpAt = UnityEngine.Time.realtimeSinceStartup;
                Log.Info($"LIVE guest: jumped with the host to {sim.CurSystem?.Name}");
            }
            // Arrived: the host's SGTravelManager.HandleNextTravelStep ran
            // Starmap.CompletedTravelClear (TravelItem, TravelOrder and ActivePath to null, the
            // starmap's line cleared); its travel index stays. Do the same here (live test
            // 2026-11-05: the guest kept its route at Illiushin, so 'travel' differed and it
            // reloaded). The travel order's timeline entry goes first, as the game's own does.
            if (status.Route.Count == 0 && RouteIds(sim).Count > 0)
            {
                if (starmap.TravelOrder != null) starmap.RemoveCompletedTravelOrder();
                try { starmap.CompletedTravelClear(); }
                catch (NullReferenceException) when (starmap.Screen == null) { } // no starmap line to clear
                Log.Info($"LIVE guest: arrived with the host in {sim.CurSystem?.Name}");
            }
            if (TravelIndex(sim) != status.TravelIndex)
                Traverse.Create(starmap).Field("<travelIndex>k__BackingField").SetValue(status.TravelIndex);
        }

        // The quarterly finance report and the travel order are timeline entries of their own:
        // OnDayPassed pays them (FinancialReportNotification.PayCost, Starmap.PayTravelOrderCost)
        // and replaces the finance one at quarter rollover. The day counters alone don't move
        // them. Their Cost and PaidCost are private fields (SetCost sets only Cost).
        private static readonly System.Reflection.FieldInfo CostField = AccessTools.Field(typeof(WorkOrderEntry), "Cost");
        private static readonly System.Reflection.FieldInfo PaidField = AccessTools.Field(typeof(WorkOrderEntry), "PaidCost");

        private static void ReadEntry(WorkOrderEntry e, out int cost, out int paid)
        {
            cost = paid = -1;
            if (e == null || CostField == null || PaidField == null) return;
            cost = Convert.ToInt32(CostField.GetValue(e));
            paid = Convert.ToInt32(PaidField.GetValue(e));
        }

        private static void WriteEntry(WorkOrderEntry e, int cost, int paid)
        {
            if (e == null || cost < 0 || CostField == null || PaidField == null) return;
            CostField.SetValue(e, Convert.ChangeType(cost, CostField.FieldType));
            PaidField.SetValue(e, Convert.ChangeType(paid, PaidField.FieldType));
        }

        // The travel order and its legs (TravelOrderData). WorkOrderType: 30 TravelGeneric (the
        // trip), 31 TravelToJumpPoint, 32 TravelJumping, 33 TravelToSystem (checked in build
        // 20387569: their constructors pass these to WorkOrderEntry, and the legs add themselves
        // to the parent's SubEntries).
        internal static TravelOrderData ReadTravelOrder(WorkOrderEntry e)
        {
            if (e == null) return null;
            var t = new TravelOrderData();
            Fill(t.Root, e);
            foreach (var leg in e.SubEntries ?? new List<WorkOrderEntry>()) { var d = new TravelOrderData.Entry(); Fill(d, leg); t.Legs.Add(d); }
            return t;
        }

        private static void Fill(TravelOrderData.Entry d, WorkOrderEntry e)
        {
            d.Type = (int)e.Type;
            d.Id = e.ID ?? "";
            d.Description = e.Description ?? "";
            ReadEntry(e, out d.Cost, out d.Paid);
        }

        private static WorkOrderEntry_TravelGeneric BuildTravelOrder(TravelOrderData t)
        {
            if (t == null || t.Root.Type != 30) return null;
            var root = new WorkOrderEntry_TravelGeneric(t.Root.Id, t.Root.Description, t.Root.Cost, "");
            foreach (var leg in t.Legs)
            {
                if (leg.Type == 31) new WorkOrderEntry_TravelToJumpPoint(leg.Id, leg.Description, leg.Cost, root, "");
                else if (leg.Type == 32) new WorkOrderEntry_TravelJumping(leg.Id, leg.Description, leg.Cost, root, "");
                else if (leg.Type == 33) new WorkOrderEntry_TravelToSystem(leg.Id, leg.Description, leg.Cost, root, "");
                else { Log.Info($"LIVE guest: the host's travel order has a leg of type {leg.Type}"); return null; }
            }
            WriteTravelOrder(root, t);
            return root;
        }

        private static void WriteTravelOrder(WorkOrderEntry e, TravelOrderData t)
        {
            WriteEntry(e, t.Root.Cost, t.Root.Paid);
            for (var i = 0; i < t.Legs.Count; i++) WriteEntry(e.SubEntries[i], t.Legs[i].Cost, t.Legs[i].Paid);
        }

        // Guest: the travel order as the host's, legs and payments included. The same trip only
        // gets its payments; another (or none) is replaced on the timeline as the game does
        // (RemoveCompletedTravelOrder, then AddWorkQueueEntry as CreateActivePath does).
        private static void SyncTravelOrder(SimGameState sim, TravelOrderData host)
        {
            var starmap = sim.Starmap;
            if (starmap == null) return;
            var mine = starmap.TravelOrder;
            if (host == null)
            {
                if (mine == null) return;
                starmap.RemoveCompletedTravelOrder();
                starmap.TravelOrder = null;
                return;
            }
            if (mine != null && host.SameShape((int)mine.Type, (mine.SubEntries ?? new List<WorkOrderEntry>()).Select(e => (int)e.Type).ToList()))
            {
                WriteTravelOrder(mine, host);
                return;
            }
            var built = BuildTravelOrder(host);
            if (built == null) return; // the fingerprint catches it
            if (mine != null) starmap.RemoveCompletedTravelOrder();
            starmap.TravelOrder = built;
            if (sim.RoomManager != null)
                Traverse.Create(starmap).Property("TravelItem").SetValue(sim.RoomManager.AddWorkQueueEntry(built));
            Log.Info("LIVE guest: travel order replaced with the host's");
        }

        internal static uint Fingerprint(SimGameState sim) => CompanyFingerprint.Hash(FingerprintSections(sim));

        // ---- Host ----

        // ShipView just sent the whole ship: that is what the guests have now.
        public static void ShipSent(SimGameState sim)
        {
            baseCompany = Snapshot(sim.CompanyStats);
            basePilots = SnapshotPilots(sim);
            baseFingerprint = Fingerprint(sim);
            baseMarket = AnyShopPending(sim) ? 0u : MarketHash(sim);
            nextCheck = DateTime.UtcNow + CheckInterval;
            gate.Reset(); // the ship is settled state
        }

        // Mid-animation (a jump playing) the travel state is in between; a status taken then would
        // tell the guest to force that in-between state. Wait for the animation to finish.
        // Likewise, after arriving the new system's shops are still loading (Shop.IsPending).
        internal static bool Settled(SimGameState sim) =>
            sim.TravelState != SimGameTravelStatus.TRANSITION_ANIMATING && sim.TravelState != SimGameTravelStatus.UNKNOWN
            && !AnyShopPending(sim);

        internal static void BeginDay()
        {
            if (!HostLive(out _, out _)) return;
            inDay = true;
            dayOps.Clear();
        }

        internal static void RecordOp(byte op)
        {
            if (inDay) dayOps.Add(op);
        }

        // The day's status goes out now, before anything the host sends after it (a refit it
        // accepts next must not get this day's progress on the guest: PR #3 review 3).
        internal static void EndDay(SimGameState sim)
        {
            if (!inDay) return;
            inDay = false;
            var ops = dayOps.ToList();
            dayOps.Clear();
            var partial = gate.DayEnded(Settled(sim));
            Send(sim, "day " + sim.DaysPassed, ops, partial);
        }

        private static void Send(SimGameState sim, string why, List<byte> ops, bool partial)
        {
            if (!HostLive(out var session, out var live) || !ReferenceEquals(live, sim)) return;
            var company = Snapshot(sim.CompanyStats);
            var pilots = SnapshotPilots(sim);
            var sections = FingerprintSections(sim);
            var status = new CompanyStatus
            {
                Why = why,
                DaysPassed = sim.DaysPassed,
                DayRemainingInQuarter = sim.DayRemainingInQuarter,
                TravelState = (int)sim.TravelState,
                TravelTime = sim.TravelTime,
                Company = StatDelta.Between(baseCompany, company),
                Fingerprint = CompanyFingerprint.Hash(sections),
                Partial = partial,
            };
            status.SectionHashes.AddRange(CompanyFingerprint.SectionHashes(sections));
            ReadEntry(sim.FinancialReportNotification, out status.FinanceCost, out status.FinancePaid);
            var market = baseMarket;
            if (!partial)
            {
                status.TravelOrder = ReadTravelOrder(sim.Starmap?.TravelOrder);
                status.SystemId = sim.CurSystem?.ID ?? "";
                status.TravelIndex = TravelIndex(sim);
                status.Route.AddRange(RouteIds(sim));
                market = MarketHash(sim);
                if (market != baseMarket) status.Market = BuildMarket(sim);
            }
            status.Ops.AddRange(ops);
            foreach (var kv in pilots)
            {
                basePilots.TryGetValue(kv.Key, out var before);
                status.Pilots[kv.Key] = StatDelta.Between(before, kv.Value);
            }
            if (!status.HasChanges && status.Fingerprint == baseFingerprint && status.Market == null && (partial || !gate.Owed)) return;
            session.SendGame(Channel, status.Encode());
            if (!partial) gate.SentSettled();
            baseMarket = market;
            baseCompany = company;
            basePilots = pilots;
            baseFingerprint = status.Fingerprint;
            Log.Info($"LIVE host: sent {why}{(partial ? " (partial: travel or shops still settling)" : "")}: {status.Ops.Count} queue steps, {status.Company.Set.Count + status.Company.Removed.Count} company stats, " +
                     $"{status.Pilots.Values.Count(p => !p.IsEmpty)} pilots, fingerprint {status.Fingerprint:x8}");
        }

        // ---- Guest ----

        public static void HandleGameMessage(CoopSession session, byte sender, byte[] payload)
        {
            if (session.Role == SessionRole.Host && sender != 0) { LogGuestReport(session, sender, payload); return; }
            if (session.Role != SessionRole.Guest || sender != 0) return;
            ShipRequests.Receive(GuestInbox.Kind.CompanyStatus, payload);
        }

        // Host: a guest's copy didn't match after a status. Log this side of the parts that
        // differ, next to the guest's log of its side, so the cause can be found.
        private static void LogGuestReport(CoopSession session, byte sender, byte[] payload)
        {
            try
            {
                string why;
                var names = new List<string>();
                using (var r = new System.IO.BinaryReader(new System.IO.MemoryStream(payload)))
                {
                    why = r.ReadString();
                    var n = r.ReadByte();
                    for (var i = 0; i < n; i++) names.Add(r.ReadString());
                }
                var sim = ShipSim();
                Log.Info($"LIVE host: player {sender}'s copy differs after {why} in {string.Join(", ", names.ToArray())}; host copy now:");
                if (sim == null) return;
                foreach (var section in FingerprintSections(sim).Where(kv => names.Contains(kv.Key)))
                    foreach (var line in section.Value.Split('\n').Where(l => l.Length > 0)) Log.Info($"LIVE host fp {section.Key}: {line}");
            }
            catch (Exception e) { Log.Error("LIVE host: could not read a guest's mismatch report", e); }
        }

        // Called by ShipRequests.ProcessInbox, in the order the host sent it.
        internal static void ApplyStatus(SimGameState sim, byte[] payload)
        {
            CompanyStatus status;
            try { status = CompanyStatus.Decode(payload); }
            catch (Exception e) { Log.Error("LIVE guest: could not read a company update", e); return; }

            var missing = new List<string>();
            ShipRequests.RunAsRemote(() =>
            {
                replaying = true;
                try
                {
                    foreach (var op in status.Ops)
                    {
                        if (op == CompanyStatus.OpMechLabDay) sim.UpdateMechLabWorkQueue(true);
                        else if (op == CompanyStatus.OpMechLabNoDay) sim.UpdateMechLabWorkQueue(false);
                        else if (op == CompanyStatus.OpInjuries) sim.UpdateInjuries();
                    }
                }
                finally { replaying = false; }
                if (!status.Partial) ApplyTravel(sim, status);
                WriteEntry(sim.FinancialReportNotification, status.FinanceCost, status.FinancePaid);
                StatApply.Apply(sim.CompanyStats, status.Company);
                foreach (var kv in status.Pilots)
                {
                    var pilot = ShipRequests.FindPilot(sim, kv.Key);
                    if (pilot == null) missing.Add(kv.Key);
                    else StatApply.Apply(pilot.StatCollection, kv.Value);
                }
                var t = Traverse.Create(sim);
                t.Property("DaysPassed").SetValue(status.DaysPassed);
                t.Property("DayRemainingInQuarter").SetValue(status.DayRemainingInQuarter);
            });
            if (status.Market != null)
            {
                pendingMarket = status.Market;
                if (TryApplyMarket(sim, pendingMarket)) pendingMarket = null;
            }
            Redraw(sim);
            Log.Info($"LIVE guest: applied {status.Why}{(status.Partial ? " (partial)" : "")}: {status.Ops.Count} queue steps, " +
                     $"{status.Company.Set.Count + status.Company.Removed.Count} company stats, {status.Pilots.Count} pilots");

            // Check only when nothing of ours is still on its way to the host or waiting here, and
            // the host had settled (a partial status's travel and shops come in the next one).
            if (status.Partial || ShipRequests.PendingRequests > 0 || ShipRequests.HasQueuedUpdates || pendingMarket != null) return;
            CheckFingerprint(sim, status, missing);
        }

        // The host's route, jump, travel state and time, and travel order.
        private static void ApplyTravel(SimGameState sim, CompanyStatus status)
        {
            FollowTravel(sim, status);
            // Travel isn't a company stat: the host's travel state (docked, in transit...) and
            // time left. ForceTravelState is the state machine's own direct setter (it also
            // updates the travel-related company stats); the stats below then match the host.
            var travel = (SimGameTravelStatus)status.TravelState;
            if ((int)sim.TravelState != status.TravelState && sim.TravelManager != null
                && travel != SimGameTravelStatus.TRANSITION_ANIMATING && travel != SimGameTravelStatus.UNKNOWN)
            {
                Log.Info($"LIVE guest: travel {sim.TravelState} -> {(SimGameTravelStatus)status.TravelState}");
                sim.TravelManager.ForceTravelState(travel);
            }
            if (sim.TravelTime != status.TravelTime) sim.SetTravelTime(status.TravelTime, "coop");
            // The travel order with its legs, as the host's (the redraw refreshes the timeline).
            SyncTravelOrder(sim, status.TravelOrder);
        }

        private static void CheckFingerprint(SimGameState sim, CompanyStatus status, List<string> missing)
        {
            var sections = FingerprintSections(sim);
            var mine = CompanyFingerprint.Hash(sections);
            if (mine == status.Fingerprint) return;
            var differing = CompanyFingerprint.Differing(status.SectionHashes, CompanyFingerprint.SectionHashes(sections));
            Log.Info($"LIVE guest: company differs from the host's after {status.Why} in {string.Join(", ", differing.ToArray())} " +
                     $"(host {status.Fingerprint:x8}, here {mine:x8}{(missing.Count > 0 ? ", missing pilots " + string.Join(",", missing.ToArray()) : "")}); this copy:");
            foreach (var section in sections.Where(kv => differing.Contains(kv.Key)))
                foreach (var line in section.Value.Split('\n').Where(l => l.Length > 0)) Log.Info($"LIVE guest fp {section.Key}: {line}");
            ReportMismatch(status.Why, differing);
            ShipRequests.WantHeal("company changed on the host after " + status.Why);
        }

        private static void ReportMismatch(string why, List<string> sections)
        {
            var session = Session;
            if (session == null || session.Role != SessionRole.Guest || !session.IsOpen) return;
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(why ?? "");
                w.Write((byte)sections.Count);
                foreach (var name in sections) w.Write(name);
                w.Flush();
                session.SendGame(Channel, ms.ToArray());
            }
        }

        private static void Redraw(SimGameState sim)
        {
            try
            {
                var rooms = sim.RoomManager;
                if (rooms != null)
                {
                    rooms.RefreshDay();
                    rooms.RefreshTimeline(true);
                    rooms.RefreshDisplay();
                    rooms.RefreshLeftNavFromShipScreen(); // the Store etc. depend on the travel state
                }
            }
            catch (Exception e) { Log.Error("LIVE guest: could not redraw the ship screen", e); }
            ShipRequests.MarkScreensDirty();
        }

        // ---- Every frame in a session ----

        public static void Update(CoopSession session)
        {
            if (session == null || session.State != SessionState.Active) return;
            if (session.Role == SessionRole.Host)
            {
                inDay = false; // OnDayPassed threw before its postfix: never record across frames
                if (!HostLive(out _, out var sim)) return;
                var settled = Settled(sim);
                // A settled status owed after a partial one goes out as soon as the host settles.
                if (DateTime.UtcNow < nextCheck && !(gate.Owed && settled)) return;
                nextCheck = DateTime.UtcNow + CheckInterval;
                if (gate.CheckDue(settled, settled && Fingerprint(sim) != baseFingerprint))
                    Send(sim, gate.Owed ? "settled" : "company change", new List<byte>(), false);
            }
            // Guest: statuses are applied by ShipRequests.ProcessInbox (every frame); a market
            // waits here for this copy's shops to finish loading.
            else if (pendingMarket != null)
            {
                var guestSim = ShipSim();
                try { if (guestSim != null && TryApplyMarket(guestSim, pendingMarket)) pendingMarket = null; }
                catch (Exception e)
                {
                    pendingMarket = null;
                    Log.Error("LIVE guest: could not apply the host's store and hiring hall", e);
                    ShipRequests.WantHeal("the host's store could not be shown here");
                }
            }
        }

        internal static bool Replaying => replaying;
    }

    // Sets stats with the game's own StatCollection API, so listeners and validators run.
    internal static class StatApply
    {
        internal static Type ClrType(byte type) =>
            type == StatValue.Bool ? typeof(bool) : type == StatValue.Int ? typeof(int) : type == StatValue.Float ? typeof(float) : typeof(string);

        internal static Variant ToVariant(StatValue v)
        {
            var variant = new Variant(ClrType(v.Type));
            if (v.Type == StatValue.Bool) variant.SetValue(v.B);
            else if (v.Type == StatValue.Int) variant.SetValue(v.I);
            else if (v.Type == StatValue.Float) variant.SetValue(v.F);
            else variant.SetValue<string>(v.S);
            return variant;
        }

        internal static void Apply(StatCollection stats, StatDelta delta)
        {
            if (stats == null) return;
            foreach (var name in delta.Removed)
                if (stats.ContainsStatistic(name)) stats.RemoveStatistic(name);
            foreach (var kv in delta.Set)
            {
                var v = kv.Value;
                if (stats.ContainsStatistic(kv.Key))
                {
                    var current = stats.GetStatistic(kv.Key)?.CurrentValue;
                    if (current != null && current.type == ClrType(v.Type))
                    {
                        stats.Set(kv.Key, ToVariant(v));
                        continue;
                    }
                    stats.RemoveStatistic(kv.Key);
                }
                if (v.Type == StatValue.Bool) stats.AddStatistic(kv.Key, v.B);
                else if (v.Type == StatValue.Int) stats.AddStatistic(kv.Key, v.I);
                else if (v.Type == StatValue.Float) stats.AddStatistic(kv.Key, v.F);
                else stats.AddStatistic(kv.Key, v.S ?? "");
            }
        }
    }

    // Host: record the day's work-queue steps, in the order the game runs them.
    [HarmonyPatch(typeof(SimGameState), "OnDayPassed")]
    public static class DayPassedPatch
    {
        static void Prefix() { try { CompanyLive.BeginDay(); } catch (Exception e) { Log.Error("LIVE: day start", e); } }
        static void Postfix(SimGameState __instance) { try { CompanyLive.EndDay(__instance); } catch (Exception e) { Log.Error("LIVE: day end", e); } }
    }

    [HarmonyPatch(typeof(SimGameState), "UpdateMechLabWorkQueue")]
    public static class MechLabQueuePatch
    {
        static void Prefix(bool passDay) =>
            CompanyLive.RecordOp(passDay ? CompanyStatus.OpMechLabDay : CompanyStatus.OpMechLabNoDay);
    }

    // Guest: no store or hiring hall of its own while it shows the host's (CompanyLive.HostMarketOnly).
    [HarmonyPatch(typeof(StarSystem), "RefreshShops")]
    public static class StarSystemRefreshShopsPatch
    {
        static bool Prefix(StarSystem __instance)
        {
            if (!CompanyLive.HostMarketOnly) return true;
            Log.Info($"LIVE guest: skipped this copy's own store refresh in {__instance?.Name} (the host's comes over)");
            return false;
        }
    }

    [HarmonyPatch(typeof(StarSystem), "GeneratePilots")]
    public static class StarSystemGeneratePilotsPatch
    {
        static bool Prefix(StarSystem __instance)
        {
            if (!CompanyLive.HostMarketOnly) return true;
            Log.Info($"LIVE guest: skipped this copy's own hiring hall in {__instance?.Name} (the host's comes over)");
            return false;
        }
    }

    [HarmonyPatch(typeof(SimGameState), "UpdateInjuries")]
    public static class InjuriesPatch
    {
        static void Prefix() => CompanyLive.RecordOp(CompanyStatus.OpInjuries);
    }
}
