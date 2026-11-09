using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BattletechCoop.Sync;

// Headless checks of the split-lance rules (COOP-PLAN.md "Feature: split lance").
static class Program
{
    static int passed, failed;

    static void Test(string name, Action body)
    {
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }

    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    // Stand-ins shaped like the game's invocations (property names as in build 20387569).
    class Move { public string ActorGUID { get; set; } }
    class Melee { public string MechGUID { get; set; } public string TargetGUID { get; set; } }
    class Attack { public string SourceGUID { get; private set; } public Attack(string s) { SourceGUID = s; } }
    class Reserve { public string targetGUID { get; set; } }
    class SubAttack { public string targetGUID = "enemy"; }       // a field, not the acting unit
    class TeamLevel { public int Round { get; set; } }

    static readonly string[] Slots = { "pilot_a", "pilot_b", "pilot_c", "pilot_d" };

    static LanceOwnership TwoPlayers(params string[] slots) => LanceOwnership.FromSlots(slots,
        new[] { KV("u1", "pilot_a"), KV("u2", "pilot_b"), KV("u3", "pilot_c"), KV("u4", "pilot_d") }
            .Where(a => slots.Contains(a.Value)), 2);

    static KeyValuePair<string, string> KV(string k, string v) => new KeyValuePair<string, string>(k, v);
    static string Name(byte p) => p == 0 ? "Nines" : "HCASE";
    static LancePick P(string m, string p) => new LancePick { MechId = m, PilotId = p };

    static PickOffer Offer()
    {
        var o = new PickOffer { Open = true, Contract = "Smash and Grab", HostName = "Nines", HostReady = true, GuestSlots = 2 };
        o.HostPicks.Add(new PickChoice { Id = "m9", Name = "Hunchback HBK-4G", Detail = "Behemoth" });
        foreach (var i in new[] { 1, 2, 3 })
        {
            o.Mechs.Add(new PickChoice { Id = "m" + i, Name = "Mech " + i, Detail = "50 t" });
            o.Pilots.Add(new PickChoice { Id = "p" + i, Name = "Pilot " + i });
        }
        return o;
    }

    static int Main()
    {
        Test("two players: slots 1-2 are the host's, 3-4 the guest's", () =>
            Check(Enumerable.Range(0, 4).Select(s => LanceOwnership.SlotOwner(s, 4, 2)).SequenceEqual(new byte[] { 0, 0, 1, 1 }), "split"));

        Test("alone, the host owns every slot; three players split 2-1-1", () =>
        {
            Check(Enumerable.Range(0, 4).All(s => LanceOwnership.SlotOwner(s, 4, 1) == 0), "solo");
            Check(Enumerable.Range(0, 4).Select(s => LanceOwnership.SlotOwner(s, 4, 3)).SequenceEqual(new byte[] { 0, 0, 1, 2 }), "three");
        });

        Test("units follow their pilot's slot", () =>
        {
            var o = TwoPlayers(Slots);
            Check(o.Owner("u1") == 0 && o.Owner("u2") == 0 && o.Owner("u3") == 1 && o.Owner("u4") == 1, o.Describe(g => g, Name));
        });

        Test("an empty host slot doesn't shift the guest's units onto the host", () =>
        {
            var o = TwoPlayers("pilot_a", null, "pilot_c", "pilot_d");
            Check(o.Owner("u1") == 0 && o.Owner("u3") == 1 && o.Owner("u4") == 1 && o.Owner("u2") == null, o.Describe(g => g, Name));
        });

        Test("a unit whose pilot isn't in a slot goes to the host", () =>
        {
            var o = LanceOwnership.FromSlots(Slots, new[] { KV("u9", "pilot_zz") }, 2);
            Check(o.Owner("u9") == 0, "stray unit");
        });

        Test("each player commands only their own units while both are connected", () =>
        {
            var o = TwoPlayers(Slots);
            Func<byte, bool> both = p => true;
            Check(o.Check(0, "u1", both, Name) == null && o.Check(1, "u3", both, Name) == null, "own units refused");
            Check(o.Check(1, "u1", both, Name) == "that 'Mech is Nines's to command", o.Check(1, "u1", both, Name) ?? "allowed");
            Check(o.Check(0, "u4", both, Name) == "that 'Mech is HCASE's to command", o.Check(0, "u4", both, Name) ?? "allowed");
        });

        Test("the host takes over a partner who left; a guest never takes over the host's", () =>
        {
            var o = TwoPlayers(Slots);
            Func<byte, bool> hostOnly = p => p == 0;
            Check(o.Check(0, "u3", hostOnly, Name) == null, "host can't command the absent guest's unit");
            Check(!LanceOwnership.MayCommand(1, 0, p => p == 1), "guest commanded an absent host's unit");
        });

        Test("orders for units outside the table (enemies, no unit) aren't restricted", () =>
        {
            var o = TwoPlayers(Slots);
            Check(o.Check(1, "enemy_1", p => true, Name) == null && o.Check(1, null, p => true, Name) == null, "restricted");
        });

        Test("the acting unit is read from each invocation shape; a melee's target isn't mistaken for it", () =>
        {
            Check(LanceOwnership.ActorOf(new Move { ActorGUID = "u1" }) == "u1", "move");
            Check(LanceOwnership.ActorOf(new Melee { MechGUID = "u3", TargetGUID = "enemy" }) == "u3", "melee");
            Check(LanceOwnership.ActorOf(new Attack("u2")) == "u2", "attack (private setter)");
            Check(LanceOwnership.ActorOf(new Reserve { targetGUID = "u4" }) == "u4", "reserve / done");
            Check(LanceOwnership.ActorOf(new SubAttack()) == null, "sub-attack field taken as the actor");
            Check(LanceOwnership.ActorOf(new TeamLevel()) == null && LanceOwnership.ActorOf(null) == null, "team-level");
        });

        Test("the table survives the wire", () =>
        {
            var o = TwoPlayers(Slots);
            var stream = new MemoryStream();
            var w = new BinaryWriter(stream); o.Write(w); w.Flush();
            var back = LanceOwnership.Read(new BinaryReader(new MemoryStream(stream.ToArray())));
            Check(back.Count == 4 && new[] { "u1", "u2", "u3", "u4" }.All(u => back.Owner(u) == o.Owner(u)), "changed");
        });

        // ---- slice 2: picking in order ----
        Test("ownership follows the guest's picks when there was a picking step", () =>
        {
            var actors = new[] { KV("u1", "pilot_a"), KV("u2", "pilot_b"), KV("u3", "pilot_c"), KV("u4", "pilot_d") };
            var o = LanceOwnership.FromPicks(actors, new Dictionary<string, byte> { { "pilot_b", 1 }, { "pilot_d", 1 } });
            Check(o.Owner("u1") == 0 && o.Owner("u2") == 1 && o.Owner("u3") == 0 && o.Owner("u4") == 1, o.Describe(g => g, Name));
        });

        Test("the guest's slots are 3-4, fewer for a smaller drop, none for a two-slot drop", () =>
        {
            Check(PickRules.GuestSlotIndices(4).SequenceEqual(new[] { 2, 3 }), "four slots");
            Check(PickRules.GuestSlotIndices(3).SequenceEqual(new[] { 2 }), "three slots");
            Check(PickRules.GuestSlotIndices(2).Length == 0, "two slots");
        });

        Test("the host has picked once both its slots hold a 'Mech, or it has nothing left to field", () =>
        {
            Check(!PickRules.HostReady(1, 2, 5), "one of two");
            Check(PickRules.HostReady(2, 2, 5) && PickRules.HostReady(1, 2, 0), "ready");
        });

        Test("the guest can't pick before the host has, or more than its slots, or the same 'Mech twice", () =>
        {
            var offer = Offer();
            offer.HostReady = false;
            Check(PickRules.Validate(new[] { P("m1", "p1") }, offer) == "Nines hasn't picked yet", "host first");
            offer.HostReady = true;
            Check(PickRules.Validate(new[] { P("m1", "p1"), P("m2", "p2") }, offer) == null, "valid pair refused");
            Check(PickRules.Validate(new[] { P("m1", "p1"), P("m2", "p2"), P("m3", "p3") }, offer) != null, "three accepted");
            Check(PickRules.Validate(new[] { P("m1", "p1"), P("m1", "p2") }, offer) == "the same 'Mech twice", "duplicate 'Mech");
            Check(PickRules.Validate(new[] { P("m1", "") }, offer) == "each 'Mech needs a pilot", "no pilot");
            Check(PickRules.Validate(new[] { P("m9", "p1") }, offer) == "that 'Mech isn't available any more", "host's 'Mech");
            Check(PickRules.Validate(new LancePick[0], offer) == null, "'none for me' refused");
        });

        Test("the guest may pick its current picks again", () =>
        {
            var offer = Offer();
            offer.GuestPicks.Add(P("m7", "p7"));
            Check(PickRules.Validate(new[] { P("m7", "p7") }, offer) == null, "own pick refused");
        });

        Test("launch waits for the guest's picks only while a guest is there", () =>
        {
            Check(PickRules.LaunchBlock(true, false, "HCASE") == "Waiting for HCASE to pick their 'Mechs", "not blocked");
            Check(PickRules.LaunchBlock(true, true, "HCASE") == null && PickRules.LaunchBlock(false, false, "HCASE") == null, "blocked");
        });

        Test("the offer survives the wire, and its key changes when the host swaps a 'Mech", () =>
        {
            var offer = Offer();
            offer.GuestPicks.Add(P("m7", "p7"));
            offer.GuestConfirmed = true;
            var s = new MemoryStream(); var w = new BinaryWriter(s); offer.Write(w); w.Flush();
            var back = PickOffer.Read(new BinaryReader(new MemoryStream(s.ToArray())));
            Check(back.Key() == offer.Key() && back.Mechs.Count == 3 && back.HostPicks[0].Name == "Hunchback HBK-4G"
                  && back.GuestPicks[0].PilotId == "p7" && back.GuestConfirmed && back.Contract == "Smash and Grab", "changed");
            var swapped = Offer(); swapped.HostPicks[0].Name = "Centurion CN9-A";
            Check(swapped.Key() != Offer().Key(), "a swap doesn't change the key");
        });

        Console.WriteLine($"{passed}/{passed + failed} passed");
        return failed == 0 ? 0 : 1;
    }
}
