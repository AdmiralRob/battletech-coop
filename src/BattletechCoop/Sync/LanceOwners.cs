using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BattleTech;
using BattletechCoop.Net;

namespace BattletechCoop.Sync
{
    // The split lance in battle (COOP-PLAN.md "Feature: split lance"): the host works out who
    // owns each player-lance unit from the lance configuration's slots (LanceOwnership), sends
    // the table to the guests, and every order is checked against it on the machine that gives
    // it and again on the host.
    public static class LanceOwners
    {
        public const ushort Channel = 32;

        private static LanceOwnership table;
        private static uint tableCombat;
        private static readonly HashSet<byte> takenOver = new HashSet<byte>();

        internal static LanceOwnership Table(CombatSync sync) =>
            sync != null && tableCombat == sync.CombatId ? table : null;

        public static void Reset()
        {
            table = null;
            tableCombat = 0;
            takenOver.Clear();
        }

        // Host, every frame of a co-op battle: build and send the table once the player lance
        // has spawned (it has by the briefing). Autoplay tests let the AI drive the player team,
        // so they keep shared control.
        internal static void UpdateHost(CombatSync sync, CombatGameState combat)
        {
            if (sync.Autoplay) return;
            var session = sync.Session;
            if (Table(sync) == null) Build(sync, combat, session);
            if (Table(sync) == null) return;
            foreach (var owner in table.Units.Select(u => u.Value).Distinct())
            {
                if (owner == 0 || Connected(session, owner) || !takenOver.Add(owner)) continue;
                Log.Info($"LANCE host: player {owner} left; the host now commands their units");
                sync.ShowNotice("Your co-op partner left: you now command their 'Mechs");
            }
        }

        private static void Build(CombatSync sync, CombatGameState combat, CoopSession session)
        {
            var team = combat.Teams?.FirstOrDefault(t => t.GUID == CombatSync.Player1TeamGuid);
            if (team == null || team.units == null || team.units.Count == 0) return;
            var slots = combat.ActiveContract?.Lances?.GetLanceUnitsIncludeEmptySlots(CombatSync.Player1TeamGuid)
                        ?? new SpawnableUnit[0];
            var slotPilots = slots.Select(u => u?.PilotId).ToList();
            // A vanilla lance has four slots; a configuration listing fewer still splits 1-2 / 3-4.
            while (slotPilots.Count < 4) slotPilots.Add(null);
            var players = Math.Max(1, session.Players.Count());
            var actors = team.units.Select(a => new KeyValuePair<string, string>(a.GUID, a.GetPilot()?.pilotDef?.Description?.Id)).ToList();
            // Picked on the lance screen (LancePicker), else the slot rule (skirmish, or no picking).
            var picks = LancePicker.LastAssignment;
            table = picks != null ? LanceOwnership.FromPicks(actors, picks) : LanceOwnership.FromSlots(slotPilots, actors, players);
            Log.Info($"LANCE host: owners from {(picks != null ? "the guest's picks" : "the lance slots")}");
            tableCombat = sync.CombatId;
            Log.Info($"LANCE host: {players} players; slots {string.Join(", ", slotPilots.Select(p => p ?? "-").ToArray())}; " +
                     table.Describe(g => UnitName(combat, g), p => PlayerName(session, p)));
            session.SendGame(Channel, Build(w => { w.Write(sync.CombatId); table.Write(w); }));
        }

        // Guest: the host's table for this battle.
        public static void HandleGameMessage(CoopSession session, byte sender, byte[] payload)
        {
            if (session.Role != SessionRole.Guest || sender != 0) return;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
                {
                    var combatId = r.ReadUInt32();
                    var received = LanceOwnership.Read(r);
                    if (CombatSync.Current == null || CombatSync.Current.CombatId != combatId)
                    {
                        Log.Info($"LANCE guest: ignored the unit owners of combat {combatId} (not this battle)");
                        return;
                    }
                    table = received;
                    tableCombat = combatId;
                    var mine = table.Units.Count(u => u.Value == session.LocalIndex);
                    Log.Info($"LANCE guest: {table.Count} player units, {mine} of them mine");
                    CombatSync.Current.ShowNotice($"You command {mine} of the lance's 'Mechs this mission");
                }
            }
            catch (Exception e) { Log.Error("LANCE guest: could not read the unit owners", e); }
        }

        // Whether `player` may give this order now; null when allowed, else the reason. Orders for
        // units outside the table (enemies, allies, AI-driven tests) are not restricted.
        internal static string Check(CombatSync sync, byte player, InvocationMessage invocation)
        {
            var t = Table(sync);
            if (t == null || invocation == null) return null;
            var session = sync.Session;
            return t.Check(player, LanceOwnership.ActorOf(invocation), p => Connected(session, p), p => PlayerName(session, p));
        }

        private static bool Connected(CoopSession session, byte player) =>
            session != null && session.State == SessionState.Active && session.Players.Any(p => p.Index == player);

        private static string PlayerName(CoopSession session, byte player) =>
            session?.Players.FirstOrDefault(p => p.Index == player)?.Name ?? (player == 0 ? "the host" : $"player {player}");

        private static string UnitName(CombatGameState combat, string guid)
        {
            var actor = combat.FindActorByGUID(guid);
            return actor == null ? guid : $"{actor.DisplayName} ({actor.GetPilot()?.Name})";
        }

        private static byte[] Build(Action<BinaryWriter> body)
        {
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream, Encoding.UTF8))
            {
                body(w);
                w.Flush();
                return stream.ToArray();
            }
        }
    }
}
