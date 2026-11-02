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
        // Days whose status waits for the travel animation to settle (see Settled).
        private static readonly List<byte> heldOps = new List<byte>();
        private static string heldWhy;

        // Guest: statuses wait in ShipRequests.Inbox, in order with ordinary ship updates.
        private static bool replaying;

        public static void Reset()
        {
            baseCompany = null;
            basePilots = null;
            inDay = false;
            dayOps.Clear();
            heldOps.Clear();
            heldWhy = null;
        }

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
                sim.Starmap?.TravelOrder?.GetRemainingCost() ?? -1);

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

        internal static uint Fingerprint(SimGameState sim) => CompanyFingerprint.Hash(FingerprintSections(sim));

        // ---- Host ----

        // ShipView just sent the whole ship: that is what the guests have now.
        public static void ShipSent(SimGameState sim)
        {
            baseCompany = Snapshot(sim.CompanyStats);
            basePilots = SnapshotPilots(sim);
            baseFingerprint = Fingerprint(sim);
            nextCheck = DateTime.UtcNow + CheckInterval;
            heldOps.Clear(); // the ship already has those days
            heldWhy = null;
        }

        // Mid-animation (a jump playing) the travel state is in between; a status taken then would
        // tell the guest to force that in-between state. Wait for the animation to finish.
        private static bool Settled(SimGameState sim) =>
            sim.TravelState != SimGameTravelStatus.TRANSITION_ANIMATING && sim.TravelState != SimGameTravelStatus.UNKNOWN;

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

        internal static void EndDay(SimGameState sim)
        {
            if (!inDay) return;
            inDay = false;
            heldOps.AddRange(dayOps);
            heldWhy = "day " + sim.DaysPassed;
            dayOps.Clear();
            SendHeld(sim);
        }

        private static void SendHeld(SimGameState sim)
        {
            if (heldWhy == null || !Settled(sim)) return;
            Send(sim, heldWhy, heldOps.ToList());
            heldOps.Clear();
            heldWhy = null;
        }

        private static void Send(SimGameState sim, string why, List<byte> ops)
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
            };
            status.SectionHashes.AddRange(CompanyFingerprint.SectionHashes(sections));
            ReadEntry(sim.FinancialReportNotification, out status.FinanceCost, out status.FinancePaid);
            ReadEntry(sim.Starmap?.TravelOrder, out status.TravelOrderCost, out status.TravelOrderPaid);
            status.Ops.AddRange(ops);
            foreach (var kv in pilots)
            {
                basePilots.TryGetValue(kv.Key, out var before);
                status.Pilots[kv.Key] = StatDelta.Between(before, kv.Value);
            }
            if (!status.HasChanges && status.Fingerprint == baseFingerprint) return;
            session.SendGame(Channel, status.Encode());
            baseCompany = company;
            basePilots = pilots;
            baseFingerprint = status.Fingerprint;
            Log.Info($"LIVE host: sent {why}: {status.Ops.Count} queue steps, {status.Company.Set.Count + status.Company.Removed.Count} company stats, " +
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
                // The timeline's finance countdown and travel order, as the host's (the redraw
                // below refreshes the entries). A finished travel order goes the way the game's
                // own does (Starmap.RemoveCompletedTravelOrder takes it off the timeline). One the
                // guest lacks can't be made here: the fingerprint catches that.
                WriteEntry(sim.FinancialReportNotification, status.FinanceCost, status.FinancePaid);
                var starmap = sim.Starmap;
                if (starmap?.TravelOrder != null)
                {
                    if (status.TravelOrderCost < 0)
                    {
                        starmap.RemoveCompletedTravelOrder();
                        starmap.TravelOrder = null;
                    }
                    else WriteEntry(starmap.TravelOrder, status.TravelOrderCost, status.TravelOrderPaid);
                }
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
            Redraw(sim);
            Log.Info($"LIVE guest: applied {status.Why}: {status.Ops.Count} queue steps, " +
                     $"{status.Company.Set.Count + status.Company.Removed.Count} company stats, {status.Pilots.Count} pilots");

            // Check only when nothing of ours is still on its way to the host or waiting here.
            if (ShipRequests.PendingRequests > 0 || ShipRequests.HasQueuedUpdates) return;
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
                SendHeld(sim);
                if (DateTime.UtcNow < nextCheck) return;
                nextCheck = DateTime.UtcNow + CheckInterval;
                if (heldWhy == null && Settled(sim) && Fingerprint(sim) != baseFingerprint) Send(sim, "company change", new List<byte>());
            }
            // Guest: statuses are applied by ShipRequests.ProcessInbox (every frame).
        }

        // Host: arriving in another star system brings that system's store, hiring hall and
        // contracts, which the host's game generated. Those come with a full ship; the guest
        // loads it once it isn't editing (ShipView).
        internal static void ArrivedInSystem(SimGameState sim, StarSystem system)
        {
            var session = Session;
            if (session == null || session.State != SessionState.Active || session.Role != SessionRole.Host) return;
            if (!session.Players.Any(p => p.Index != 0) || ShipSim() != sim) return;
            ShipView.MarkDirty("arrived in " + (system?.Name ?? "a new system"));
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

    [HarmonyPatch(typeof(SimGameState), "UpdateInjuries")]
    public static class InjuriesPatch
    {
        static void Prefix() => CompanyLive.RecordOp(CompanyStatus.OpInjuries);
    }

    [HarmonyPatch(typeof(SimGameState), "SetCurrentSystem")]
    public static class SetCurrentSystemPatch
    {
        static void Postfix(SimGameState __instance, StarSystem system)
        {
            try { CompanyLive.ArrivedInSystem(__instance, system); }
            catch (Exception e) { Log.Error("LIVE: arrival", e); }
        }
    }
}
