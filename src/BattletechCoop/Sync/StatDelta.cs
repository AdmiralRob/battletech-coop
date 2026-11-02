using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BattletechCoop.Sync
{
    // A game statistic's value: the four types a BattleTech Variant can hold.
    // Unity-free, so the delta logic is covered by tests/CompanyLiveTests.
    internal struct StatValue : IEquatable<StatValue>
    {
        public const byte Bool = 0, Int = 1, Float = 2, String = 3;
        public byte Type;
        public bool B;
        public int I;
        public float F;
        public string S;

        public static StatValue Of(bool v) => new StatValue { Type = Bool, B = v };
        public static StatValue Of(int v) => new StatValue { Type = Int, I = v };
        public static StatValue Of(float v) => new StatValue { Type = Float, F = v };
        public static StatValue Of(string v) => new StatValue { Type = String, S = v ?? "" };

        public bool Equals(StatValue o) =>
            Type == o.Type && (Type == Bool ? B == o.B : Type == Int ? I == o.I :
                               Type == Float ? F.Equals(o.F) : string.Equals(S, o.S, StringComparison.Ordinal));
        public override bool Equals(object obj) => obj is StatValue v && Equals(v);
        public override int GetHashCode() => Type;

        // The same text on every PC (invariant culture, round-trip floats), for fingerprints.
        public override string ToString() =>
            Type == Bool ? (B ? "true" : "false") : Type == Int ? I.ToString(CultureInfo.InvariantCulture) :
            Type == Float ? F.ToString("R", CultureInfo.InvariantCulture) : "\"" + S + "\"";

        public void Write(BinaryWriter w)
        {
            w.Write(Type);
            switch (Type)
            {
                case Bool: w.Write(B); break;
                case Int: w.Write(I); break;
                case Float: w.Write(F); break;
                default: w.Write(S ?? ""); break;
            }
        }

        public static StatValue Read(BinaryReader r)
        {
            var type = r.ReadByte();
            switch (type)
            {
                case Bool: return Of(r.ReadBoolean());
                case Int: return Of(r.ReadInt32());
                case Float: return Of(r.ReadSingle());
                case String: return Of(r.ReadString());
                default: throw new InvalidDataException("unknown stat type " + type);
            }
        }
    }

    // What changed in one stat collection between two snapshots: values to set (new or changed)
    // and names that are gone.
    internal sealed class StatDelta
    {
        public readonly SortedDictionary<string, StatValue> Set = new SortedDictionary<string, StatValue>(StringComparer.Ordinal);
        public readonly SortedSet<string> Removed = new SortedSet<string>(StringComparer.Ordinal);
        public bool IsEmpty => Set.Count == 0 && Removed.Count == 0;

        public static StatDelta Between(IDictionary<string, StatValue> before, IDictionary<string, StatValue> now)
        {
            var d = new StatDelta();
            foreach (var kv in now)
                if (before == null || !before.TryGetValue(kv.Key, out var old) || !old.Equals(kv.Value))
                    d.Set[kv.Key] = kv.Value;
            if (before != null)
                foreach (var name in before.Keys)
                    if (!now.ContainsKey(name)) d.Removed.Add(name);
            return d;
        }

        // Applies to a plain snapshot (the host's record of what the guests have; also tests).
        public void ApplyTo(IDictionary<string, StatValue> stats)
        {
            foreach (var name in Removed) stats.Remove(name);
            foreach (var kv in Set) stats[kv.Key] = kv.Value;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Set.Count);
            foreach (var kv in Set) { w.Write(kv.Key); kv.Value.Write(w); }
            w.Write(Removed.Count);
            foreach (var name in Removed) w.Write(name);
        }

        public static StatDelta Read(BinaryReader r)
        {
            var d = new StatDelta();
            var set = r.ReadInt32();
            for (var i = 0; i < set; i++) { var name = r.ReadString(); d.Set[name] = StatValue.Read(r); }
            var removed = r.ReadInt32();
            for (var i = 0; i < removed; i++) d.Removed.Add(r.ReadString());
            return d;
        }
    }

    // One live company update from the host: the day's work-queue steps to replay, then the
    // company's and each pilot's changed stats, the day counters, and a fingerprint to check.
    internal sealed class CompanyStatus
    {
        public const byte OpMechLabDay = 1, OpMechLabNoDay = 2, OpInjuries = 3;

        public string Why = "";
        public readonly List<byte> Ops = new List<byte>();
        public int DaysPassed, DayRemainingInQuarter;
        public int TravelState, TravelTime;
        // The quarterly finance report's timeline entry (WorkOrderEntry Cost/PaidCost), or -1
        // when the host has none.
        public int FinanceCost = -1, FinancePaid = -1;
        // The travel order with its legs (null when the host isn't travelling).
        public TravelOrderData TravelOrder;
        // Sent before the host settled (a jump animation playing, a new system's shops still
        // loading): the guest applies the day's steps and stats but not travel or the market, and
        // doesn't compare fingerprints. A settled status always follows (PR #3 review 3).
        public bool Partial;
        // Where the host is and where it's going: the current system, the route (system IDs of
        // Starmap.ActivePath) and how far along it is (Starmap travelIndex).
        public string SystemId = "";
        public int TravelIndex;
        public readonly List<string> Route = new List<string>();
        // The current system's store stock and pilots for hire, only when they changed.
        public MarketData Market;
        public readonly List<uint> SectionHashes = new List<uint>();
        public StatDelta Company = new StatDelta();
        public readonly SortedDictionary<string, StatDelta> Pilots = new SortedDictionary<string, StatDelta>(StringComparer.Ordinal);
        public uint Fingerprint;

        public bool HasChanges => Ops.Count > 0 || !Company.IsEmpty || Pilots.Values.Any(p => !p.IsEmpty);

        public byte[] Encode()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Why);
                w.Write(Ops.Count);
                foreach (var op in Ops) w.Write(op);
                w.Write(DaysPassed);
                w.Write(DayRemainingInQuarter);
                Company.Write(w);
                var changed = Pilots.Where(kv => !kv.Value.IsEmpty).ToList();
                w.Write(changed.Count);
                foreach (var kv in changed) { w.Write(kv.Key); kv.Value.Write(w); }
                w.Write(Fingerprint);
                w.Write(TravelState);
                w.Write(TravelTime);
                w.Write((byte)SectionHashes.Count);
                foreach (var h in SectionHashes) w.Write(h);
                w.Write(FinanceCost); w.Write(FinancePaid);
                w.Write(TravelOrder != null);
                TravelOrder?.Write(w);
                w.Write(SystemId ?? "");
                w.Write(TravelIndex);
                w.Write(Route.Count);
                foreach (var id in Route) w.Write(id);
                w.Write(Market != null);
                Market?.Write(w);
                w.Write(Partial);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static CompanyStatus Decode(byte[] payload)
        {
            using (var r = new BinaryReader(new MemoryStream(payload)))
            {
                var s = new CompanyStatus { Why = r.ReadString() };
                var ops = r.ReadInt32();
                for (var i = 0; i < ops; i++) s.Ops.Add(r.ReadByte());
                s.DaysPassed = r.ReadInt32();
                s.DayRemainingInQuarter = r.ReadInt32();
                s.Company = StatDelta.Read(r);
                var pilots = r.ReadInt32();
                for (var i = 0; i < pilots; i++) { var guid = r.ReadString(); s.Pilots[guid] = StatDelta.Read(r); }
                s.Fingerprint = r.ReadUInt32();
                s.TravelState = r.ReadInt32();
                s.TravelTime = r.ReadInt32();
                var sections = r.ReadByte();
                for (var i = 0; i < sections; i++) s.SectionHashes.Add(r.ReadUInt32());
                s.FinanceCost = r.ReadInt32(); s.FinancePaid = r.ReadInt32();
                if (r.ReadBoolean()) s.TravelOrder = TravelOrderData.Read(r);
                s.SystemId = r.ReadString();
                s.TravelIndex = r.ReadInt32();
                var route = r.ReadInt32();
                for (var i = 0; i < route; i++) s.Route.Add(r.ReadString());
                if (r.ReadBoolean()) s.Market = MarketData.Read(r);
                s.Partial = r.ReadBoolean();
                return s;
            }
        }
    }

    // Everything the host's and the guest's copies of the company must agree on, as text that is
    // the same on every PC, in named sections. CompanyLive gathers the parts from the game; this
    // only lays them out. A status carries a hash per section, so when the copies differ both
    // sides can log exactly the part that differs.
    internal static class CompanyFingerprint
    {
        internal sealed class PilotEntry
        {
            public string Guid, Callsign;
            public IDictionary<string, StatValue> Stats;
        }

        public static readonly string[] SectionNames = { "day", "system", "travel", "company", "pilots", "bays", "mechlab", "medbay", "market" };

        // Company stats that each game sets for its own player's screens, so they legitimately
        // differ between copies and are left out of the comparison (they still sync from the
        // host). Checked in build 20387569: COMPANY_HasVisitedRoom_<room> is set only by that
        // room's EnterRoom (and PlayEnterVO); COMPANY_HasSeenTutorial_MechLab and
        // COMPANY_HasSeenECMReview_MechLab only by MechLabPanel. The other "viewed"/"seen" stats
        // (NotificationViewed_*, HasSeenFlashpointNotification...) are set by the ship's day
        // simulation and stay in (PR #3 review).
        public static bool IsLocalPresentation(string stat) =>
            stat.StartsWith("COMPANY_HasVisitedRoom_", StringComparison.Ordinal) ||
            stat.StartsWith("COMPANY_HasSeen", StringComparison.Ordinal);

        // systemId: the current star system, kept apart from the company's stats (CurSystem).
        // travelState/travelTime: SGTravelManager.TravelState (in the system, at the jump point,
        // in transit...) and SimGameState.TravelTime. Neither is a company stat, so without them a
        // guest left in the previous system, or still in transit after the host docked, would
        // match (PR #3 review; live test 2026-11-01).
        // financeLeft / travelOrderLeft: what the timeline shows for the quarterly finance report
        // and the travel order (WorkOrderEntry.GetRemainingCost), or -1 when there is none.
        // They are timeline objects of their own, not the day counters (PR #3 review).
        public static List<KeyValuePair<string, string>> Sections(
            int daysPassed, int dayRemainingInQuarter, string systemId, int travelState, int travelTime,
            IDictionary<string, StatValue> company, IEnumerable<PilotEntry> pilots,
            IEnumerable<string> bays, IEnumerable<string> mechLab, IEnumerable<string> medBay,
            int financeLeft = -1, int travelOrderLeft = -1,
            IEnumerable<string> route = null, int travelIndex = 0, IEnumerable<string> market = null,
            IEnumerable<string> travelOrder = null)
        {
            var list = new List<KeyValuePair<string, string>>();
            void Add(string name, System.Text.StringBuilder text) => list.Add(new KeyValuePair<string, string>(name, text.ToString()));
            System.Text.StringBuilder New() => new System.Text.StringBuilder();

            var sb = New().Append("day ").Append(daysPassed).Append(" quarter ").Append(dayRemainingInQuarter)
                          .Append(" finance ").Append(financeLeft).Append('\n');
            Add("day", sb);
            Add("system", New().Append("system ").Append(systemId ?? "(none)").Append('\n'));
            Add("travel", New().Append("travel ").Append(travelState).Append(" time ").Append(travelTime)
                               .Append(" order ").Append(travelOrderLeft).Append(" index ").Append(travelIndex)
                               .Append(" route ").Append(string.Join(">", (route ?? Enumerable.Empty<string>()).ToArray())).Append('\n')
                               .Append(string.Concat((travelOrder ?? Enumerable.Empty<string>()).Select(l => l + "\n").ToArray())));
            sb = New();
            foreach (var kv in company.Where(kv => !IsLocalPresentation(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append("c ").Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            Add("company", sb);
            sb = New();
            foreach (var p in pilots.OrderBy(p => p.Guid, StringComparer.Ordinal))
            {
                sb.Append("pilot ").Append(p.Guid).Append(' ').Append(p.Callsign).Append('\n');
                foreach (var kv in p.Stats.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    sb.Append("p ").Append(p.Guid).Append(' ').Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            }
            Add("pilots", sb);
            sb = New(); foreach (var line in bays) sb.Append("bay ").Append(line).Append('\n'); Add("bays", sb);
            sb = New(); foreach (var line in mechLab) sb.Append("mechlab ").Append(line).Append('\n'); Add("mechlab", sb);
            sb = New(); foreach (var line in medBay) sb.Append("medbay ").Append(line).Append('\n'); Add("medbay", sb);
            // The current system's store stock and pilots for hire, generated by the host's game.
            sb = New(); foreach (var line in market ?? Enumerable.Empty<string>()) sb.Append(line).Append('\n'); Add("market", sb);
            return list;
        }

        public static string Text(List<KeyValuePair<string, string>> sections) => string.Concat(sections.Select(s => s.Value));
        public static uint Hash(List<KeyValuePair<string, string>> sections) => Fnv.Hash(Text(sections));
        public static List<uint> SectionHashes(List<KeyValuePair<string, string>> sections) => sections.Select(s => Fnv.Hash(s.Value)).ToList();

        // The names of the sections whose hashes differ (all of them if the lists don't line up).
        public static List<string> Differing(IList<uint> host, IList<uint> mine)
        {
            if (host == null || mine == null || host.Count != mine.Count || host.Count != SectionNames.Length)
                return SectionNames.ToList();
            return SectionNames.Where((n, i) => host[i] != mine[i]).ToList();
        }
    }

    // The host's travel order: Starmap.OnPathfindingComplete makes a WorkOrderEntry_TravelGeneric
    // (cost 0) with one leg per stage as SubEntries (TravelToJumpPoint, TravelJumping,
    // TravelToSystem). The game's PayCost pays the legs first and GetRemainingCost sums them, so
    // a day's travel moves a leg's PaidCost, not the parent's (PR #3 review 3). Type is the
    // WorkOrderType (30-33).
    internal sealed class TravelOrderData
    {
        public sealed class Entry
        {
            public int Type, Cost, Paid;
            public string Id = "", Description = "";
        }

        public readonly Entry Root = new Entry();
        public readonly List<Entry> Legs = new List<Entry>();

        // For the fingerprint: types and payments, not the (localized) descriptions.
        public IEnumerable<string> Lines()
        {
            yield return $"order {Root.Type} {Root.Cost} {Root.Paid}";
            for (var i = 0; i < Legs.Count; i++) yield return $"leg {i} {Legs[i].Type} {Legs[i].Cost} {Legs[i].Paid}";
        }

        // Same entries in the same order, whatever has been paid.
        public bool SameShape(int rootType, IList<int> legTypes) =>
            rootType == Root.Type && legTypes.Count == Legs.Count && Legs.Select(l => l.Type).SequenceEqual(legTypes);

        private static void WriteEntry(BinaryWriter w, Entry e)
        {
            w.Write(e.Type); w.Write(e.Id ?? ""); w.Write(e.Description ?? ""); w.Write(e.Cost); w.Write(e.Paid);
        }

        private static Entry ReadEntry(BinaryReader r) =>
            new Entry { Type = r.ReadInt32(), Id = r.ReadString(), Description = r.ReadString(), Cost = r.ReadInt32(), Paid = r.ReadInt32() };

        public void Write(BinaryWriter w)
        {
            WriteEntry(w, Root);
            w.Write(Legs.Count);
            foreach (var leg in Legs) WriteEntry(w, leg);
        }

        public static TravelOrderData Read(BinaryReader r)
        {
            var t = new TravelOrderData();
            var root = ReadEntry(r);
            t.Root.Type = root.Type; t.Root.Id = root.Id; t.Root.Description = root.Description; t.Root.Cost = root.Cost; t.Root.Paid = root.Paid;
            var n = r.ReadInt32();
            for (var i = 0; i < n; i++) t.Legs.Add(ReadEntry(r));
            return t;
        }
    }

    // Host: when company statuses go out (PR #3 review 3). A day's status is sent as soon as the
    // day ends, in order with the host's other ship messages, so nothing sent later (a refit the
    // host accepts, its own changes) can overtake it. If the host isn't settled yet, that status
    // is partial and a settled one is owed as soon as it is.
    internal sealed class HostStatusGate
    {
        public bool Owed { get; private set; }

        // Returns whether the day's status is partial.
        public bool DayEnded(bool settled)
        {
            if (!settled) Owed = true;
            return !settled;
        }

        public bool CheckDue(bool settled, bool changed) => settled && (Owed || changed);
        public void SentSettled() => Owed = false;
        public void Reset() => Owed = false;
    }

    // A system's store stock (system, faction and black-market shops) and pilots for hire, as the
    // game's own JSON (ShopDefItem.ToJSON, PilotDef.ToJSON). The host's game generates them
    // (Shop.RefreshShop, StarSystem.GeneratePilots), with randomness, so the guest takes the
    // host's instead of making its own.
    internal sealed class MarketData
    {
        public sealed class ShopData
        {
            public int LastRefreshDay;
            public readonly List<string> Items = new List<string>();
        }

        public string SystemId = "";
        public readonly ShopData[] Shops = new ShopData[3]; // system, faction, black market; null = none
        public int PilotsRefreshDay;
        public readonly List<string> Pilots = new List<string>();

        public void Write(BinaryWriter w)
        {
            w.Write(SystemId ?? "");
            foreach (var shop in Shops)
            {
                w.Write(shop != null);
                if (shop == null) continue;
                w.Write(shop.LastRefreshDay);
                w.Write(shop.Items.Count);
                foreach (var json in shop.Items) w.Write(json);
            }
            w.Write(PilotsRefreshDay);
            w.Write(Pilots.Count);
            foreach (var json in Pilots) w.Write(json);
        }

        public static MarketData Read(BinaryReader r)
        {
            var m = new MarketData { SystemId = r.ReadString() };
            for (var i = 0; i < m.Shops.Length; i++)
            {
                if (!r.ReadBoolean()) continue;
                var shop = new ShopData { LastRefreshDay = r.ReadInt32() };
                var n = r.ReadInt32();
                for (var j = 0; j < n; j++) shop.Items.Add(r.ReadString());
                m.Shops[i] = shop;
            }
            m.PilotsRefreshDay = r.ReadInt32();
            var pilots = r.ReadInt32();
            for (var i = 0; i < pilots; i++) m.Pilots.Add(r.ReadString());
            return m;
        }
    }

    // FNV-1a over the fingerprint text: the same on every PC.
    internal static class Fnv
    {
        public static uint Hash(string text)
        {
            var hash = 2166136261u;
            foreach (var ch in text) { hash ^= ch; hash *= 16777619u; }
            return hash;
        }
    }
}
