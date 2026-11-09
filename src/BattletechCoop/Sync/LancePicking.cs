using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BattletechCoop.Sync
{
    // Picking the split lance in order (COOP-PLAN.md "Feature: split lance", slice 2). Unity-free
    // so the rules and the wire format can be tested headlessly; LancePicker drives the game's
    // lance configuration screen on the host and the picker window on the guest.
    //
    // Rob, 2026-11-06 / 11-08: the host picks their own 'Mechs first, then the guest picks theirs;
    // the host may still swap their own afterwards.

    // A 'Mech (Id: the MechDef's GUID, as LanceConfiguratorPanel.LoadLanceConfiguration looks it
    // up) or a pilot (Id: the pilot def's ID), with what the picker shows.
    internal sealed class PickChoice
    {
        public string Id = "", Name = "", Detail = "";

        public void Write(BinaryWriter w) { w.Write(Id ?? ""); w.Write(Name ?? ""); w.Write(Detail ?? ""); }
        public static PickChoice Read(BinaryReader r) => new PickChoice { Id = r.ReadString(), Name = r.ReadString(), Detail = r.ReadString() };
    }

    // One of the guest's slots: a 'Mech and its pilot.
    internal sealed class LancePick
    {
        public string MechId = "", PilotId = "";

        public void Write(BinaryWriter w) { w.Write(MechId ?? ""); w.Write(PilotId ?? ""); }
        public static LancePick Read(BinaryReader r) => new LancePick { MechId = r.ReadString(), PilotId = r.ReadString() };
    }

    // Host -> guest: the host's lance screen as the guest needs it.
    internal sealed class PickOffer
    {
        public bool Open;                 // the host's lance screen is up
        public string Contract = "";
        public string HostName = "";
        public bool HostReady;            // the host has picked (PickRules.HostReady)
        public int GuestSlots;            // how many slots are the guest's (2; fewer for small drops)
        public readonly List<PickChoice> HostPicks = new List<PickChoice>();   // "Mech - pilot" per host slot
        public readonly List<PickChoice> Mechs = new List<PickChoice>();       // free to pick
        public readonly List<PickChoice> Pilots = new List<PickChoice>();
        public readonly List<LancePick> GuestPicks = new List<LancePick>();    // in the guest's slots now
        public bool GuestConfirmed;       // the host has the guest's picks (launch allowed)

        // Everything the guest may choose: the free ones plus its own current picks.
        public bool MechAvailable(string id) =>
            Mechs.Any(m => m.Id == id) || GuestPicks.Any(p => p.MechId == id);

        public bool PilotAvailable(string id) =>
            Pilots.Any(p => p.Id == id) || GuestPicks.Any(p => p.PilotId == id);

        public void Write(BinaryWriter w)
        {
            w.Write(Open); w.Write(Contract ?? ""); w.Write(HostName ?? ""); w.Write(HostReady); w.Write(GuestSlots);
            WriteList(w, HostPicks); WriteList(w, Mechs); WriteList(w, Pilots);
            w.Write(GuestPicks.Count); foreach (var p in GuestPicks) p.Write(w);
            w.Write(GuestConfirmed);
        }

        public static PickOffer Read(BinaryReader r)
        {
            var o = new PickOffer
            {
                Open = r.ReadBoolean(), Contract = r.ReadString(), HostName = r.ReadString(),
                HostReady = r.ReadBoolean(), GuestSlots = r.ReadInt32(),
            };
            ReadList(r, o.HostPicks); ReadList(r, o.Mechs); ReadList(r, o.Pilots);
            var n = r.ReadInt32();
            for (var i = 0; i < n; i++) o.GuestPicks.Add(LancePick.Read(r));
            o.GuestConfirmed = r.ReadBoolean();
            return o;
        }

        // What changed for the guest (the host re-sends only when this differs).
        public string Key()
        {
            var s = new MemoryStream();
            var w = new BinaryWriter(s); Write(w); w.Flush();
            return System.Convert.ToBase64String(s.ToArray());
        }

        private static void WriteList(BinaryWriter w, List<PickChoice> list) { w.Write(list.Count); foreach (var c in list) c.Write(w); }
        private static void ReadList(BinaryReader r, List<PickChoice> list) { var n = r.ReadInt32(); for (var i = 0; i < n; i++) list.Add(PickChoice.Read(r)); }
    }

    internal static class PickRules
    {
        // The host has picked once both its slots hold a 'Mech, or it has no more 'Mechs to field.
        public static bool HostReady(int hostSlotsFilled, int hostSlots, int freeMechs) =>
            hostSlotsFilled >= hostSlots || freeMechs == 0;

        // The guest's slots: the ones after the host's, at most two, within the screen's slots.
        public static int[] GuestSlotIndices(int slotCount, int hostSlots = 2, int guestSlots = 2) =>
            Enumerable.Range(hostSlots, System.Math.Max(0, System.Math.Min(guestSlots, slotCount - hostSlots))).ToArray();

        // null when the guest's picks can be placed, else why not.
        public static string Validate(IList<LancePick> picks, PickOffer offer)
        {
            if (offer == null || !offer.Open) return "the host's lance screen is closed";
            if (!offer.HostReady) return $"{offer.HostName} hasn't picked yet";
            if (picks.Count > offer.GuestSlots) return $"only {offer.GuestSlots} 'Mechs are yours to pick";
            foreach (var p in picks)
            {
                if (string.IsNullOrEmpty(p.MechId) || string.IsNullOrEmpty(p.PilotId)) return "each 'Mech needs a pilot";
                if (!offer.MechAvailable(p.MechId)) return "that 'Mech isn't available any more";
                if (!offer.PilotAvailable(p.PilotId)) return "that pilot isn't available any more";
            }
            if (picks.Select(p => p.MechId).Distinct().Count() != picks.Count) return "the same 'Mech twice";
            if (picks.Select(p => p.PilotId).Distinct().Count() != picks.Count) return "the same pilot twice";
            return null;
        }

        // Why the host can't launch yet, or null. Only while a guest is there to pick.
        public static string LaunchBlock(bool guestConnected, bool guestConfirmed, string guestName) =>
            guestConnected && !guestConfirmed ? $"Waiting for {guestName} to pick their 'Mechs" : null;
    }
}
