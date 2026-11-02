using System;
using System.Collections.Generic;
using System.Linq;
using BattletechCoop.Sync;

// The Unity-free half of live company updates: what changed between two stat snapshots, and
// the CompanyStatus wire format. The game-facing half (StatCollection, SimGameState) is checked
// against the real game DLL; see docs/COOP-PLAN.md.
// Model of the game's Mech Bay queue for the ordering tests: a day pays 1 (MechTech skill)
// against the first order; a ship update can add an order.
sealed class MechBay
{
    public static readonly byte[] Day = { 0 };
    public static byte[] AddRefit(byte cost) => new byte[] { 1, cost };
    public List<int> Queue = new List<int>();
    public string Describe() => string.Join(",", Queue);
    public int Process(GuestInbox inbox, bool editing, bool force = false, bool ready = true) =>
        inbox.Process(ready, !editing, force, update => Queue.Add(update[1]), status => { if (Queue.Count > 0) Queue[0] -= 1; });
}

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
    static List<KeyValuePair<string, string>> Sections(string system = "starsystemdef_Claybrooke", int travelState = 0, int travelTime = 0,
                                                       string[] mechLab = null, int day = 42, Dictionary<string, StatValue> company = null,
                                                       int financeLeft = 29, int travelOrderLeft = -1) =>
        CompanyFingerprint.Sections(day, 10, system, travelState, travelTime,
            company ?? Stats(("Funds", StatValue.Of(1000000))),
            new[] { new CompanyFingerprint.PilotEntry { Guid = "SGRef_12", Callsign = "Nines", Stats = Stats(("ExperienceUnspent", StatValue.Of(500))) } },
            new[] { "0 mech1 'Blackjack'" }, mechLab ?? new[] { "WorkOrderEntry_MechLab mech1 3" }, new string[0], financeLeft, travelOrderLeft);
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

        // ---- Order of host changes on the guest (GuestInbox), from the PR #3 review ----
        // A tiny model of the game's Mech Bay queue: UpdateMechLabWorkQueue(true) pays the
        // MechTech skill (1 here) against the first order, whenever it was added.
        Test("review repro: a held day doesn't give progress to a refit the host added later", () =>
        {
            var guest = new MechBay();
            var inbox = new GuestInbox();
            inbox.Add(GuestInbox.Kind.CompanyStatus, MechBay.Day);        // host: day passes, no refit yet
            guest.Process(inbox, editing: true);                          // guest has the MechLab open: held
            inbox.Add(GuestInbox.Kind.ShipUpdate, MechBay.AddRefit(3));    // host: queues a refit afterwards
            guest.Process(inbox, editing: true);                          // must wait behind the held day
            Check(guest.Queue.Count == 0, "the refit jumped ahead of the held day");
            guest.Process(inbox, editing: false);                         // MechLab closed
            Check(guest.Queue.Count == 1 && guest.Queue[0] == 3, $"refit got progress for a day before it existed: {guest.Describe()}");
        });

        Test("review repro: a refit the guest confirms lands after the days held before it", () =>
        {
            var guest = new MechBay();
            var inbox = new GuestInbox();
            inbox.Add(GuestInbox.Kind.CompanyStatus, MechBay.Day);
            guest.Process(inbox, editing: true);                          // held while in the MechLab
            guest.Process(inbox, editing: true, force: true);             // ShipRequests.CaptureSession, before the game adds the order
            guest.Queue.Add(3);                                           // the guest's own refit
            guest.Process(inbox, editing: false);
            Check(guest.Queue.Count == 1 && guest.Queue[0] == 3, $"held day replayed onto the guest's new refit: {guest.Describe()}");
        });

        Test("control: a refit that existed before the day still gets that day's progress", () =>
        {
            var guest = new MechBay { Queue = { 3 } };
            var inbox = new GuestInbox();
            inbox.Add(GuestInbox.Kind.CompanyStatus, MechBay.Day);
            guest.Process(inbox, editing: true);
            guest.Process(inbox, editing: false);
            Check(guest.Queue[0] == 2, guest.Describe());
        });

        Test("with nothing held, host ship updates still apply at once, even while editing", () =>
        {
            var guest = new MechBay();
            var inbox = new GuestInbox();
            inbox.Add(GuestInbox.Kind.ShipUpdate, MechBay.AddRefit(3));
            Check(guest.Process(inbox, editing: true) == 1 && guest.Queue.Count == 1, "update waited for nothing");
        });

        Test("while the ship loads nothing applies; afterwards everything applies in order", () =>
        {
            var guest = new MechBay { Queue = { 2 } };
            var inbox = new GuestInbox();
            inbox.Add(GuestInbox.Kind.CompanyStatus, MechBay.Day);
            inbox.Add(GuestInbox.Kind.ShipUpdate, MechBay.AddRefit(3));
            inbox.Add(GuestInbox.Kind.CompanyStatus, MechBay.Day);
            Check(guest.Process(inbox, editing: false, ready: false) == 0 && inbox.Count == 3, "applied while loading");
            guest.Process(inbox, editing: false);
            // day: 2->1; refit 3 added; day: 1->0 (the first order is paid first)
            Check(guest.Describe() == "0,3", guest.Describe());
        });

        Test("a new ship drops everything sent before it", () =>
        {
            var inbox = new GuestInbox();
            inbox.Add(GuestInbox.Kind.CompanyStatus, MechBay.Day);
            inbox.Add(GuestInbox.Kind.ShipUpdate, MechBay.AddRefit(3));
            inbox.Clear();
            Check(inbox.Count == 0, "not cleared");
        });

        Test("applying an item can't start applying the next one inside it", () =>
        {
            var inbox = new GuestInbox();
            var order = new List<string>();
            inbox.Add(GuestInbox.Kind.ShipUpdate, new byte[] { 1 });
            inbox.Add(GuestInbox.Kind.ShipUpdate, new byte[] { 2 });
            inbox.Process(true, true, false, p =>
            {
                order.Add("start " + p[0]);
                inbox.Process(true, true, true, q => order.Add("nested " + q[0]), q => { }); // a capture inside a replay
                order.Add("end " + p[0]);
            }, p => { });
            Check(string.Join(",", order) == "start 1,end 1,start 2,end 2", string.Join(",", order));
        });

        // ---- Fingerprint (CompanyFingerprint), from the PR #3 review ----
        Test("review repro: systems differ while everything else matches -> fingerprints differ", () =>
        {
            Func<string, uint> fp = system => CompanyFingerprint.Hash(Sections(system: system));
            Check(fp("starsystemdef_Claybrooke") == fp("starsystemdef_Claybrooke"), "same state, different fingerprints");
            Check(fp("starsystemdef_Claybrooke") != fp("starsystemdef_Detroit"), "a guest left in the previous system matches the host");
            Check(fp(null) != fp("starsystemdef_Claybrooke"), "no system matches a system");
        });

        Test("fingerprint: pilot order and stat order don't matter, values do", () =>
        {
            var a = new CompanyFingerprint.PilotEntry { Guid = "A", Callsign = "a", Stats = Stats(("x", StatValue.Of(1)), ("y", StatValue.Of(2))) };
            var b = new CompanyFingerprint.PilotEntry { Guid = "B", Callsign = "b", Stats = Stats(("y", StatValue.Of(2)), ("x", StatValue.Of(1))) };
            var none = new string[0];
            Func<CompanyFingerprint.PilotEntry[], string> text = ps => CompanyFingerprint.Text(CompanyFingerprint.Sections(1, 1, "s", 0, 0, day1, ps, none, none, none));
            var t1 = text(new[] { a, b });
            Check(t1 == text(new[] { b, a }), "order changed the fingerprint");
            b.Stats["x"] = StatValue.Of(9);
            Check(text(new[] { a, b }) != t1, "a pilot stat change was missed");
        });

        // ---- Live test 2026-11-01: guest still "in space" after the host docked ----
        Test("live test: docked vs still in transit -> fingerprints differ, in 'travel' only", () =>
        {
            var host = Sections(travelState: 0 /* IN_SYSTEM */, travelTime: 0);
            var guest = Sections(travelState: 4 /* TRANSIT_FROM_JUMP */, travelTime: 4);
            Check(CompanyFingerprint.Hash(host) != CompanyFingerprint.Hash(guest), "a guest still in transit matches a docked host");
            var diff = CompanyFingerprint.Differing(CompanyFingerprint.SectionHashes(host), CompanyFingerprint.SectionHashes(guest));
            Check(diff.Count == 1 && diff[0] == "travel", "wrong sections: " + string.Join(",", diff));
        });

        Test("mismatch report names exactly the sections that differ", () =>
        {
            var a = Sections();
            var b = Sections(mechLab: new[] { "WorkOrderEntry_MechLab mech1 2" }, day: 43);
            var diff = CompanyFingerprint.Differing(CompanyFingerprint.SectionHashes(a), CompanyFingerprint.SectionHashes(b));
            Check(string.Join(",", diff) == "day,mechlab", string.Join(",", diff));
            Check(CompanyFingerprint.Differing(new List<uint> { 1, 2 }, CompanyFingerprint.SectionHashes(a)).Count == CompanyFingerprint.SectionNames.Length,
                  "an older host's status without section hashes should list every section");
        });

        Test("travel state, travel time and section hashes survive the wire", () =>
        {
            var s = new CompanyStatus { TravelState = 4, TravelTime = 3 };
            s.SectionHashes.AddRange(CompanyFingerprint.SectionHashes(Sections()));
            var back = CompanyStatus.Decode(s.Encode());
            Check(back.TravelState == 4 && back.TravelTime == 3 && back.SectionHashes.SequenceEqual(s.SectionHashes), "travel or section hashes changed");
            Check(back.SectionHashes.Count == CompanyFingerprint.SectionNames.Length, "one hash per section");
        });

        // ---- PR #3 review 2: timeline entries and local presentation flags ----
        Test("review repro: guest browses a room the host hasn't, then an unrelated host change -> no catch-up", () =>
        {
            var host = Stats(("Funds", StatValue.Of(1000000)), ("COMPANY_HasVisitedRoom_MechBay", StatValue.Of(false)));
            var guest = new Dictionary<string, StatValue>(host) { ["COMPANY_HasVisitedRoom_MechBay"] = StatValue.Of(true) }; // EnterRoom, guest only
            var hostAfter = new Dictionary<string, StatValue>(host) { ["Funds"] = StatValue.Of(987500) };
            StatDelta.Between(host, hostAfter).ApplyTo(guest);                // the status: only Funds changed on the host
            Check(CompanyFingerprint.Hash(Sections(company: hostAfter)) == CompanyFingerprint.Hash(Sections(company: guest)),
                  "a room visit on the guest alone asks for a reload");
            guest["Funds"] = StatValue.Of(1);
            Check(CompanyFingerprint.Hash(Sections(company: hostAfter)) != CompanyFingerprint.Hash(Sections(company: guest)), "real company state no longer checked");
        });

        Test("only the verified local flags are left out of the comparison", () =>
        {
            foreach (var local in new[] { "COMPANY_HasVisitedRoom_Barracks", "COMPANY_HasVisitedRoom_Shop", "COMPANY_HasSeenTutorial_MechLab", "COMPANY_HasSeenECMReview_MechLab" })
                Check(CompanyFingerprint.IsLocalPresentation(local), local + " should be local");
            foreach (var shared in new[] { "COMPANY_NotificationViewed_LowFunds", "HasSeenFlashpointNotification", "HasSeenNewStarmapTechNotification", "COMPANY_MechWarriorsHired", "Funds" })
                Check(!CompanyFingerprint.IsLocalPresentation(shared), shared + " is set by the day simulation and must stay checked");
        });

        Test("review repro: day applied but the finance countdown still shows the old value -> detected in 'day'", () =>
        {
            var host = Sections(financeLeft: 29);
            var guest = Sections(financeLeft: 30); // DayRemainingInQuarter set, entry not paid
            var diff = CompanyFingerprint.Differing(CompanyFingerprint.SectionHashes(host), CompanyFingerprint.SectionHashes(guest));
            Check(diff.Count == 1 && diff[0] == "day", string.Join(",", diff));
        });

        Test("review repro: travel time copied but the travel order still shows the old value -> detected in 'travel'", () =>
        {
            var diff = CompanyFingerprint.Differing(CompanyFingerprint.SectionHashes(Sections(travelTime: 5, travelOrderLeft: 5)),
                                                    CompanyFingerprint.SectionHashes(Sections(travelTime: 5, travelOrderLeft: 6)));
            Check(diff.Count == 1 && diff[0] == "travel", string.Join(",", diff));
            Check(CompanyFingerprint.Hash(Sections(travelOrderLeft: -1)) != CompanyFingerprint.Hash(Sections(travelOrderLeft: 0)), "no order vs finished order");
        });

        Test("timeline entries survive the wire; none is -1", () =>
        {
            var s = new CompanyStatus { FinanceCost = 90, FinancePaid = 61, TravelOrderCost = 6, TravelOrderPaid = 1 };
            var back = CompanyStatus.Decode(s.Encode());
            Check(back.FinanceCost == 90 && back.FinancePaid == 61 && back.TravelOrderCost == 6 && back.TravelOrderPaid == 1, "entries changed");
            var none = CompanyStatus.Decode(new CompanyStatus().Encode());
            Check(none.FinanceCost == -1 && none.TravelOrderCost == -1, "missing entries not -1");
        });

        Console.WriteLine($"{passed}/{passed + failed} passed");
        return failed == 0 ? 0 : 1;
    }
}
