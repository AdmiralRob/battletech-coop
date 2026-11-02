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
                return s;
            }
        }
    }

    // Everything the host's and the guest's copies of the company must agree on, as text that is
    // the same on every PC. CompanyLive gathers the parts from the game; this only lays them out.
    internal static class CompanyFingerprint
    {
        internal sealed class PilotEntry
        {
            public string Guid, Callsign;
            public IDictionary<string, StatValue> Stats;
        }

        // systemId: the current star system's ID. It is stored apart from the company's stats
        // (SimGameState.CurSystem, set by SetCurrentSystem), so without it a guest still in the
        // previous system could match after a jump (PR #3 review).
        public static string Text(int daysPassed, int dayRemainingInQuarter, string systemId,
                                  IDictionary<string, StatValue> company, IEnumerable<PilotEntry> pilots,
                                  IEnumerable<string> bays, IEnumerable<string> mechLab, IEnumerable<string> medBay)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("day ").Append(daysPassed).Append(" quarter ").Append(dayRemainingInQuarter).Append('\n');
            sb.Append("system ").Append(systemId ?? "(none)").Append('\n');
            foreach (var kv in company.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append("c ").Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            foreach (var p in pilots.OrderBy(p => p.Guid, StringComparer.Ordinal))
            {
                sb.Append("pilot ").Append(p.Guid).Append(' ').Append(p.Callsign).Append('\n');
                foreach (var kv in p.Stats.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    sb.Append("p ").Append(p.Guid).Append(' ').Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            }
            foreach (var line in bays) sb.Append("bay ").Append(line).Append('\n');
            foreach (var line in mechLab) sb.Append("mechlab ").Append(line).Append('\n');
            foreach (var line in medBay) sb.Append("medbay ").Append(line).Append('\n');
            return sb.ToString();
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
