using System;
using System.Collections.Generic;
using System.Linq;
using BattletechCoop.Sync;

// The Unity-free half of live company updates: what changed between two stat snapshots, and
// the CompanyStatus wire format. The game-facing half (StatCollection, SimGameState) is checked
// against the real game DLL; see docs/COOP-PLAN.md.
static class Program
{
    static int passed, failed;
    static void Check(bool value, string detail) { if (!value) throw new Exception(detail); }
    static void Test(string name, Action body)
    {
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static Dictionary<string, StatValue> Stats(params (string, StatValue)[] items) =>
        items.ToDictionary(i => i.Item1, i => i.Item2, StringComparer.Ordinal);
    static bool Same(IDictionary<string, StatValue> a, IDictionary<string, StatValue> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v.Equals(kv.Value));

    static int Main()
    {
        var day1 = Stats(("Funds", StatValue.Of(1000000)), ("Morale", StatValue.Of(25)), ("Item.HeatSink", StatValue.Of(3)),
                         ("ArgoUpgrade", StatValue.Of(true)), ("MechTechSkill", StatValue.Of(4.5f)), ("Faction", StatValue.Of("Liao")));

        Test("no change gives an empty delta", () => Check(StatDelta.Between(day1, new Dictionary<string, StatValue>(day1)).IsEmpty, "not empty"));

        Test("changed, new and removed stats are all found", () =>
        {
            var day2 = new Dictionary<string, StatValue>(day1);
            day2["Funds"] = StatValue.Of(987500);       // daily costs
            day2["Item.Laser"] = StatValue.Of(1);       // new item from an event
            day2.Remove("Item.HeatSink");               // used up
            var d = StatDelta.Between(day1, day2);
            Check(d.Set.Count == 2 && d.Set["Funds"].I == 987500 && d.Set["Item.Laser"].I == 1, "set wrong");
            Check(d.Removed.SetEquals(new[] { "Item.HeatSink" }), "removed wrong");
            var guest = new Dictionary<string, StatValue>(day1);
            d.ApplyTo(guest);
            Check(Same(guest, day2), "applying the delta doesn't reproduce the host's stats");
        });

        Test("a type change counts as a change", () =>
        {
            var now = new Dictionary<string, StatValue>(day1) { ["Morale"] = StatValue.Of(25f) };
            Check(StatDelta.Between(day1, now).Set.ContainsKey("Morale"), "int 25 vs float 25 not seen");
        });

        Test("no previous snapshot sends everything (a pilot hired since)", () =>
            Check(StatDelta.Between(null, day1).Set.Count == day1.Count, "not everything"));

        Test("every stat type survives the wire exactly", () =>
        {
            var weird = Stats(("f", StatValue.Of(0.1f)), ("nan", StatValue.Of(float.NaN)), ("neg", StatValue.Of(-0f)),
                              ("big", StatValue.Of(int.MinValue)), ("s", StatValue.Of("Ünïcode \"quoted\"")), ("empty", StatValue.Of("")), ("b", StatValue.Of(false)));
            var s = new CompanyStatus { Why = "day 7", DaysPassed = 7, DayRemainingInQuarter = 23, Company = StatDelta.Between(null, weird), Fingerprint = 0xdeadbeef };
            var back = CompanyStatus.Decode(s.Encode());
            Check(back.Company.Set.Count == weird.Count && weird.All(kv => back.Company.Set[kv.Key].Equals(kv.Value)), "values changed on the wire");
            Check(back.Why == "day 7" && back.DaysPassed == 7 && back.DayRemainingInQuarter == 23 && back.Fingerprint == 0xdeadbeef, "header changed");
        });

        Test("queue steps keep their order, pilot deltas keep their pilot", () =>
        {
            var s = new CompanyStatus();
            s.Ops.AddRange(new[] { CompanyStatus.OpMechLabDay, CompanyStatus.OpInjuries, CompanyStatus.OpMechLabNoDay });
            s.Pilots["SGRef_12"] = StatDelta.Between(Stats(("ExperienceUnspent", StatValue.Of(500))), Stats(("ExperienceUnspent", StatValue.Of(1300))));
            s.Pilots["SGRef_13"] = new StatDelta(); // unchanged: not sent
            var back = CompanyStatus.Decode(s.Encode());
            Check(back.Ops.SequenceEqual(s.Ops), "ops reordered");
            Check(back.Pilots.Count == 1 && back.Pilots["SGRef_12"].Set["ExperienceUnspent"].I == 1300, "pilot delta wrong");
        });

        Test("a status with only a day change still has changes", () =>
        {
            var s = new CompanyStatus(); s.Ops.Add(CompanyStatus.OpMechLabDay);
            Check(s.HasChanges && !new CompanyStatus().HasChanges, "HasChanges wrong");
        });

        Test("fingerprint text is culture-proof and hashes the same everywhere", () =>
        {
            var old = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Check(StatValue.Of(4.5f).ToString() == "4.5", "float uses the PC's culture: " + StatValue.Of(4.5f));
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = old; }
            Check(Fnv.Hash("day 3\n") == Fnv.Hash("day 3\n") && Fnv.Hash("day 3\n") != Fnv.Hash("day 4\n"), "hash");
            Check(Fnv.Hash("") == 2166136261u, "FNV basis changed (must match ShipRequests digests)");
        });

        Test("a truncated status is rejected, not half-applied", () =>
        {
            var bytes = new CompanyStatus { Company = StatDelta.Between(null, day1) }.Encode();
            var threw = false;
            try { CompanyStatus.Decode(bytes.Take(bytes.Length - 6).ToArray()); } catch (System.IO.EndOfStreamException) { threw = true; }
            Check(threw, "decoded a cut-off payload");
        });

        Console.WriteLine($"{passed}/{passed + failed} passed");
        return failed == 0 ? 0 : 1;
    }
}
