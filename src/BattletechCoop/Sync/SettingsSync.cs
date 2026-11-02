using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BattleTech.Save;
using BattletechCoop.Net;
using Harmony;

namespace BattletechCoop.Sync
{
    // The host's combat pacing and cinematic settings (Options > Game) apply on every guest for
    // the session, so both screens play attacks, cinematics and follow cameras the same way and
    // stay in step. Camera speeds and personal controls are left alone.
    //
    // These settings are not part of the lockstep state: the cinematic chances are rolled with
    // UnityEngine.Random in CameraControl.ShowAttackCam, and speedUpCombat only changes timing
    // (StackManager.Update, ActorMovementSequence.PlayMeleeAnim). Different values can't desync.
    //
    // They live in CloudUserSettings, which UserSettings.Save writes to the player's Steam Cloud.
    // The guest therefore never saves the host's values: UserSettings.Save serializes the guest's
    // own values (keeping any change the guest made during the session) and then puts the host's
    // back. The guest's own values return when the session ends.
    public static class SettingsSync
    {
        public const ushort Channel = 35; // host -> guests: combat pacing settings

        // CloudUserSettings fields (bool or float), checked against build 20387569.
        private static readonly string[] Names =
        {
            "speedUpCombat", "useAttackCam", "useTargetCam", "moveFollow", "sprintFollow",
            "autoZoomChance", "cinematicZoomChance", "enemyCinematic", "meleeCinematic", "jumpCinematic",
        };

        private static FieldInfo[] fields;
        private static float[] lastSent; // host: the values the guests have
        private static float[] own;      // guest: its own values, kept while the host's apply
        private static float[] applied;  // guest: the host's values currently in effect

        private static CoopSession Session => CoopController.Instance?.Session;
        private static bool IsHost => Session != null && Session.IsOpen && Session.Role == SessionRole.Host;

        private static bool Ready()
        {
            if (fields != null) return fields.Length == Names.Length;
            fields = Names.Select(n => AccessTools.Field(typeof(CloudUserSettings), n)).ToArray();
            if (fields.Any(f => f == null || (f.FieldType != typeof(bool) && f.FieldType != typeof(float))))
            {
                Log.Error("SETTINGS: a combat pacing setting is missing from CloudUserSettings; settings sync is off");
                fields = new FieldInfo[0];
                return false;
            }
            return true;
        }

        private static float[] Read(CloudUserSettings s) =>
            fields.Select(f => f.FieldType == typeof(bool) ? ((bool)f.GetValue(s) ? 1f : 0f) : (float)f.GetValue(s)).ToArray();

        private static void Write(CloudUserSettings s, float[] values)
        {
            for (var i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType == typeof(bool)) fields[i].SetValue(s, values[i] != 0f);
                else fields[i].SetValue(s, values[i]);
            }
        }

        private static string Describe(float[] values) =>
            string.Join(" ", Names.Select((n, i) => n + "=" + values[i].ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());

        // Host: a guest joined; send it the current values.
        public static void OnGuestJoined(CoopSession session, byte index)
        {
            if (session.Role != SessionRole.Host || !Ready()) return;
            var cloud = ActiveOrDefaultSettings.CloudSettings;
            if (cloud == null) return;
            lastSent = Read(cloud);
            session.SendGame(Channel, Encode(lastSent), index);
            Log.Info($"SETTINGS host: sent combat pacing to player {index}: {Describe(lastSent)}");
        }

        // Host: after the game saved settings (the options screen does this); send any change.
        internal static void AfterHostSave(CloudUserSettings cloud)
        {
            if (!IsHost || cloud == null || !Ready()) return;
            var now = Read(cloud);
            if (lastSent != null && now.SequenceEqual(lastSent)) return;
            lastSent = now;
            Session.SendGame(Channel, Encode(now));
            Log.Info($"SETTINGS host: combat pacing changed, sent to the guests: {Describe(now)}");
        }

        // Guest: the host's values arrived.
        public static void HandleGameMessage(CoopSession session, byte sender, byte[] payload)
        {
            if (session.Role != SessionRole.Guest || sender != 0 || !Ready()) return;
            var cloud = ActiveOrDefaultSettings.CloudSettings;
            if (cloud == null) { Log.Info("SETTINGS guest: no user settings yet; ignored the host's"); return; }
            float[] host;
            try { host = Decode(payload); }
            catch (Exception e) { Log.Error("SETTINGS guest: could not read the host's settings", e); return; }
            if (own == null) own = Read(cloud);
            applied = host;
            Write(cloud, host);
            Log.Info($"SETTINGS guest: using the host's combat pacing for this session: {Describe(host)}");
            CoopController.Instance?.Notice("Using the host's combat pacing and cinematic settings for this session");
        }

        // Guest, before UserSettings.Save serializes: keep what the guest changed, save its own values.
        internal static void BeforeSave(CloudUserSettings cloud)
        {
            if (applied == null || own == null || cloud == null) return;
            var now = Read(cloud);
            var changed = false;
            for (var i = 0; i < now.Length; i++)
            {
                if (now[i] == applied[i]) continue;
                own[i] = now[i];
                changed = true;
            }
            if (changed)
            {
                Log.Info($"SETTINGS guest: kept your changes for after the session: {Describe(own)}");
                CoopController.Instance?.Notice("Combat pacing changes saved; the host's apply until the session ends");
            }
            Write(cloud, own);
        }

        // Guest, after UserSettings.Save: the host's values again.
        internal static void AfterGuestSave(CloudUserSettings cloud)
        {
            if (applied == null || cloud == null) return;
            Write(cloud, applied);
        }

        // Session ended: the guest gets its own values back.
        public static void Reset()
        {
            lastSent = null;
            if (own != null)
            {
                var cloud = ActiveOrDefaultSettings.CloudSettings;
                if (cloud != null)
                {
                    Write(cloud, own);
                    Log.Info($"SETTINGS guest: restored your own combat pacing: {Describe(own)}");
                }
            }
            own = null;
            applied = null;
        }

        private static byte[] Encode(float[] values)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((byte)values.Length);
                foreach (var v in values) w.Write(v);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static float[] Decode(byte[] payload)
        {
            using (var r = new BinaryReader(new MemoryStream(payload)))
            {
                var count = r.ReadByte();
                if (count != Names.Length) throw new InvalidDataException($"expected {Names.Length} settings, got {count}");
                var values = new float[count];
                for (var i = 0; i < count; i++) values[i] = r.ReadSingle();
                return values;
            }
        }
    }

    // UserSettings.Save serializes CloudSettings synchronously (Serializer.Serialize, then
    // SaveManager.SaveBytes), so swapping values around the call controls what is written.
    [HarmonyPatch(typeof(UserSettings), "Save")]
    public static class UserSettingsSavePatch
    {
        static void Prefix(UserSettings __instance)
        {
            try { SettingsSync.BeforeSave(__instance.CloudSettings); }
            catch (Exception e) { Log.Error("SETTINGS: before save failed", e); }
        }

        static void Postfix(UserSettings __instance)
        {
            try
            {
                SettingsSync.AfterGuestSave(__instance.CloudSettings);
                SettingsSync.AfterHostSave(__instance.CloudSettings);
            }
            catch (Exception e) { Log.Error("SETTINGS: after save failed", e); }
        }
    }
}
