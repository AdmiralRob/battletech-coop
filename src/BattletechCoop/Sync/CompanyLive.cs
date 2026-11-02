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

        // Guest
        private static readonly Queue<byte[]> incoming = new Queue<byte[]>();
        private static bool replaying;

        public static void Reset()
        {
            baseCompany = null;
            basePilots = null;
            inDay = false;
            dayOps.Clear();
            incoming.Clear();
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

        // Everything the two copies must agree on, as text: the same on every PC.
        internal static string FingerprintText(SimGameState sim)
        {
            var sb = new StringBuilder();
            sb.Append("day ").Append(sim.DaysPassed).Append(" quarter ").Append(sim.DayRemainingInQuarter).Append('\n');
            foreach (var kv in Snapshot(sim.CompanyStats).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append("c ").Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            foreach (var p in Pilots(sim).OrderBy(p => p.GUID, StringComparer.Ordinal))
            {
                sb.Append("pilot ").Append(p.GUID).Append(' ').Append(p.Callsign).Append('\n');
                foreach (var kv in Snapshot(p.StatCollection).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    sb.Append("p ").Append(p.GUID).Append(' ').Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            }
            foreach (var kv in sim.ActiveMechs.OrderBy(kv => kv.Key))
                sb.Append("bay ").Append(kv.Key).Append(' ').Append(kv.Value.GUID).Append(" '").Append(kv.Value.Name).Append("'\n");
            foreach (var e in sim.MechLabQueue)
                sb.Append("mechlab ").Append(e.GetType().Name).Append(' ').Append((e as WorkOrderEntry_MechLab)?.MechID)
                  .Append(' ').Append(e.GetRemainingCost().ToString(CultureInfo.InvariantCulture)).Append('\n');
            // The MedBay queue is one work order whose sub-entries are the pilots healing.
            var medbay = sim.MedBayQueue;
            if (medbay != null)
                foreach (var e in medbay.SubEntries)
                    sb.Append("medbay ").Append(e.GetType().Name).Append(' ')
                      .Append(e.GetRemainingCost().ToString(CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        internal static uint Fingerprint(SimGameState sim) => Fnv.Hash(FingerprintText(sim));

        // ---- Host ----

        // ShipView just sent the whole ship: that is what the guests have now.
        public static void ShipSent(SimGameState sim)
        {
            baseCompany = Snapshot(sim.CompanyStats);
            basePilots = SnapshotPilots(sim);
            baseFingerprint = Fingerprint(sim);
            nextCheck = DateTime.UtcNow + CheckInterval;
        }

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
            Send(sim, "day " + sim.DaysPassed, dayOps.ToList());
            dayOps.Clear();
        }

        private static void Send(SimGameState sim, string why, List<byte> ops)
        {
            if (!HostLive(out var session, out var live) || !ReferenceEquals(live, sim)) return;
            var company = Snapshot(sim.CompanyStats);
            var pilots = SnapshotPilots(sim);
            var status = new CompanyStatus
            {
                Why = why,
                DaysPassed = sim.DaysPassed,
                DayRemainingInQuarter = sim.DayRemainingInQuarter,
                Company = StatDelta.Between(baseCompany, company),
                Fingerprint = Fingerprint(sim),
            };
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
            if (session.Role != SessionRole.Guest || sender != 0) return;
            incoming.Enqueue(payload);
        }

        // A full ship supersedes the statuses sent before it.
        public static void ShipSaveReceived() => incoming.Clear();

        private static void ApplyNext(SimGameState sim)
        {
            CompanyStatus status;
            try { status = CompanyStatus.Decode(incoming.Dequeue()); }
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
            if (ShipRequests.PendingRequests > 0 || ShipRequests.HasQueuedUpdates || incoming.Count > 0) return;
            var text = FingerprintText(sim);
            var mine = Fnv.Hash(text);
            if (mine == status.Fingerprint) return;
            Log.Info($"LIVE guest: company differs from the host's after {status.Why} (host {status.Fingerprint:x8}, " +
                     $"here {mine:x8}{(missing.Count > 0 ? ", missing pilots " + string.Join(",", missing.ToArray()) : "")}); this copy:");
            foreach (var line in text.Split('\n').Where(l => l.Length > 0 && !l.StartsWith("c Item.") && !l.StartsWith("p "))) Log.Info("LIVE guest fp: " + line);
            ShipRequests.WantHeal("company changed on the host after " + status.Why);
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
                if (DateTime.UtcNow < nextCheck) return;
                nextCheck = DateTime.UtcNow + CheckInterval;
                if (HostLive(out _, out var sim) && Fingerprint(sim) != baseFingerprint) Send(sim, "company change", new List<byte>());
                return;
            }
            var guestSim = ShipSim();
            while (incoming.Count > 0 && guestSim != null && ShipView.Ready && !ShipView.Busy && !ShipRequests.GuestEditing())
            {
                try { ApplyNext(guestSim); }
                catch (Exception e)
                {
                    Log.Error("LIVE guest: could not apply a company update", e);
                    ShipRequests.WantHeal("a company update failed here");
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

    [HarmonyPatch(typeof(SimGameState), "UpdateInjuries")]
    public static class InjuriesPatch
    {
        static void Prefix() => CompanyLive.RecordOp(CompanyStatus.OpInjuries);
    }
}
