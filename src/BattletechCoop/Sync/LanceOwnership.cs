using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BattletechCoop.Sync
{
    // Which player commands which unit of the player lance (COOP-PLAN.md "Feature: split lance").
    // Unity-free so the rules can be tested headlessly; LanceOwners applies them in the game.
    //
    // Rules (Rob, 2026-11-06):
    // - The lance's slots are split evenly in slot order: with two players, slots 1-2 are the
    //   host's and slots 3-4 the guest's.
    // - Only a unit's owner commands it.
    // - A unit whose owner has left the session is commanded by the host.
    internal sealed class LanceOwnership
    {
        private readonly Dictionary<string, byte> owners = new Dictionary<string, byte>();

        public int Count => owners.Count;
        public IEnumerable<KeyValuePair<string, byte>> Units => owners;

        // The player who owns a lance slot. Slots split evenly in order, the earlier players
        // taking the larger share when they don't divide (later: 3-4 players).
        public static byte SlotOwner(int slot, int slotCount, int players)
        {
            if (players <= 1 || slotCount <= 0) return 0;
            if (slot < 0) slot = 0;
            if (slot >= slotCount) slot = slotCount - 1;
            return (byte)Math.Min(players - 1, slot * players / slotCount);
        }

        // slotPilots: the lance configuration's pilot per slot, empty slots included (null or "").
        // actorPilots: each player-team unit's GUID and its pilot's ID.
        // Units whose pilot isn't in a slot (shouldn't happen) go to the host.
        public static LanceOwnership FromSlots(IList<string> slotPilots, IEnumerable<KeyValuePair<string, string>> actorPilots, int players)
        {
            var o = new LanceOwnership();
            foreach (var actor in actorPilots)
            {
                var slot = -1;
                for (var i = 0; i < slotPilots.Count; i++)
                    if (!string.IsNullOrEmpty(slotPilots[i]) && slotPilots[i] == actor.Value) { slot = i; break; }
                o.owners[actor.Key] = slot < 0 ? (byte)0 : SlotOwner(slot, slotPilots.Count, players);
            }
            return o;
        }

        // From the picking step (LancePicker): the pilots each guest picked; everyone else is the
        // host's. Preferred over the slot rule when there was a picking step.
        public static LanceOwnership FromPicks(IEnumerable<KeyValuePair<string, string>> actorPilots, IDictionary<string, byte> pickedPilots)
        {
            var o = new LanceOwnership();
            foreach (var actor in actorPilots)
                o.owners[actor.Key] = actor.Value != null && pickedPilots.TryGetValue(actor.Value, out var p) ? p : (byte)0;
            return o;
        }

        public void Set(string actorGuid, byte player) => owners[actorGuid] = player;

        // null: not a player-lance unit (an enemy, or not tracked).
        public byte? Owner(string actorGuid) =>
            actorGuid != null && owners.TryGetValue(actorGuid, out var p) ? p : (byte?)null;

        // Who may command a unit now: its owner; the host too once the owner has left.
        public static bool MayCommand(byte player, byte owner, Func<byte, bool> connected) =>
            player == owner || (player == 0 && !connected(owner));

        // null when allowed, else the reason shown to the player.
        public string Check(byte player, string actorGuid, Func<byte, bool> connected, Func<byte, string> name)
        {
            var owner = Owner(actorGuid);
            if (owner == null || MayCommand(player, owner.Value, connected)) return null;
            return $"that 'Mech is {name(owner.Value)}'s to command";
        }

        public void Write(BinaryWriter w)
        {
            w.Write(owners.Count);
            foreach (var kv in owners.OrderBy(k => k.Key, StringComparer.Ordinal)) { w.Write(kv.Key); w.Write(kv.Value); }
        }

        public static LanceOwnership Read(BinaryReader r)
        {
            var o = new LanceOwnership();
            var n = r.ReadInt32();
            for (var i = 0; i < n; i++) o.owners[r.ReadString()] = r.ReadByte();
            return o;
        }

        public string Describe(Func<string, string> unitName, Func<byte, string> playerName) =>
            string.Join("; ", owners.OrderBy(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal)
                .Select(kv => $"{unitName(kv.Key)} -> {playerName(kv.Value)}"));

        // The unit an order is for. Checked against build 20387569: every InvocationMessage that
        // names a unit carries it in one of these properties (the first present wins: a melee or
        // DFA also names its TargetGUID, an attack its targets, so their own unit comes first).
        //   ActorGUID:  AbstractActorMovementInvocation (MechMovementInvocation, sprint)
        //   MechGUID:   MechJumpInvocation, MechMeleeInvocation, MechStandInvocation, MechStartupInvocation
        //   SourceGUID: AttackInvocation, MechDFAInvocation, SensorLockInvocation, ActiveProbeInvocation,
        //               EjectInvocation, MechMortarInvocation, MoraleDefendInvocation
        //   targetGUID: ReserveActorInvocation (Done / reserve), VentCoolantInvocation,
        //               InspireActorInvocation (the unit being inspired: one's own)
        // Orders naming no unit (SubAttackInvocation, team-level ones) are not restricted.
        public static readonly string[] ActorProperties = { "ActorGUID", "MechGUID", "SourceGUID", "targetGUID" };

        private static readonly Dictionary<Type, PropertyInfo> actorProperty = new Dictionary<Type, PropertyInfo>();

        public static string ActorOf(object invocation)
        {
            if (invocation == null) return null;
            var type = invocation.GetType();
            PropertyInfo p;
            lock (actorProperty)
            {
                if (!actorProperty.TryGetValue(type, out p))
                {
                    p = ActorProperties
                        .Select(n => type.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        .FirstOrDefault(x => x != null && x.PropertyType == typeof(string) && x.GetIndexParameters().Length == 0);
                    actorProperty[type] = p;
                }
            }
            return p?.GetValue(invocation, null) as string;
        }
    }
}
