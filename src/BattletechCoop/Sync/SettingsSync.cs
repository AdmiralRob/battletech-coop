using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BattleTech.Save;
using BattleTech.UI;
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
    // The guest's own values must never be stored over. CloudUserSettings is written in exactly
    // two places, both synchronous and both patched below:
    //   UserSettings.Save                         -> Steam Cloud (settings_cloud.sav)
    //   CachedSettings.SaveSettingsToPlayerPrefs  -> the PlayerPrefs "CachedSettings" copy
    // (ActiveOrDefaultSettings.SaveUserSettings calls the first only with a SaveManager, then
    // the second always; settings load calls the second too.) Around each write the guest's own
    // values are put in, and the host's back afterwards.
    //
    // Guest edits are taken from the Options screen itself: GameSettingsModule.InitSettings sets
    // original<X> and current<X> from the active settings when the screen opens, and SaveSettings
    // writes every current<X> back. So current != original is exactly what the guest changed, and
    // a host update that arrives while the screen is open can't be mistaken for one.
    public static class SettingsSync
    {
        public const ushort Channel = 35; // host -> guests: combat pacing settings

        // CloudUserSettings field, and the GameSettingsModule field suffix that edits it
        // (original<suffix>, current<suffix>). Checked against build 20387569.
        private static readonly string[] Names =
        {
            "speedUpCombat", "useAttackCam", "useTargetCam", "moveFollow", "sprintFollow",
            "autoZoomChance", "cinematicZoomChance", "enemyCinematic", "meleeCinematic", "jumpCinematic",
        };
        private static readonly string[] MenuNames =
        {
            "SpeedUpCombat", "AttackCam", "OverShoulderCam", "MoveFollow", "SprintFollow",
            "AutoZoomChance", "CinematicZoomChance", "EnemyCinematic", "MeleeCinematic", "JumpCinematic",
        };

        private static FieldInfo[] fields, menuOriginal, menuCurrent;
        private static FieldInfo menuPlayerSettings;
        private static float[] lastSent; // host: the values the guests have
        private static float[] own;      // guest: its own values, kept while the host's apply
        private static float[] applied;  // guest: the host's values currently in effect
        private static CloudUserSettings target; // guest: the settings object holding the host's values
        private static int writeDepth;   // guest: inside a write of target (nested writes count once)

        private static CoopSession Session => CoopController.Instance?.Session;
        private static bool IsHost => Session != null && Session.IsOpen && Session.Role == SessionRole.Host;

        internal static bool Ready()
        {
            if (fields != null) return fields.Length == Names.Length;
            fields = Names.Select(n => AccessTools.Field(typeof(CloudUserSettings), n)).ToArray();
            menuOriginal = MenuNames.Select(n => AccessTools.Field(typeof(GameSettingsModule), "original" + n)).ToArray();
            menuCurrent = MenuNames.Select(n => AccessTools.Field(typeof(GameSettingsModule), "current" + n)).ToArray();
            menuPlayerSettings = AccessTools.Field(typeof(GameSettingsModule), "playerSettings");
            for (var i = 0; i < Names.Length; i++)
            {
                var type = fields[i]?.FieldType;
                if ((type == typeof(bool) || type == typeof(float)) &&
                    menuOriginal[i]?.FieldType == type && menuCurrent[i]?.FieldType == type) continue;
                Log.Error($"SETTINGS: '{Names[i]}' doesn't match this game version; settings sync is off");
                fields = new FieldInfo[0];
                return false;
            }
            return true;
        }

        private static float Get(FieldInfo f, object o) =>
            f.FieldType == typeof(bool) ? ((bool)f.GetValue(o) ? 1f : 0f) : (float)f.GetValue(o);

        private static void Set(FieldInfo f, object o, float v)
        {
            if (f.FieldType == typeof(bool)) f.SetValue(o, v != 0f);
            else f.SetValue(o, v);
        }

        private static float[] Read(CloudUserSettings s) => fields.Select(f => Get(f, s)).ToArray();

        private static void Write(CloudUserSettings s, float[] values)
        {
            for (var i = 0; i < fields.Length; i++) Set(fields[i], s, values[i]);
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

        // Host: settings were just stored; send any change to the guests.
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
            if (own == null || !ReferenceEquals(cloud, target)) own = Read(cloud);
            target = cloud;
            applied = host;
            Write(cloud, host);
            Log.Info($"SETTINGS guest: using the host's combat pacing for this session: {Describe(host)}");
            CoopController.Instance?.Notice("Using the host's combat pacing and cinematic settings for this session");
        }

        // Guest, before the Options screen writes its values: record what the guest changed there.
        internal static void BeforeMenuSave(GameSettingsModule menu)
        {
            if (applied == null || own == null || menu == null || !Ready()) return;
            var changed = false;
            for (var i = 0; i < fields.Length; i++)
            {
                var current = Get(menuCurrent[i], menu);
                if (current == Get(menuOriginal[i], menu)) continue;
                own[i] = current;
                changed = true;
            }
            if (!changed) return;
            Log.Info($"SETTINGS guest: kept your changes for after the session: {Describe(own)}");
            CoopController.Instance?.Notice("Combat pacing changes saved; the host's apply until the session ends");
        }

        // Guest, after the Options screen wrote its values: the host's are in effect again (the
        // screen also writes back values it showed before a later host update).
        internal static void AfterMenuSave(GameSettingsModule menu)
        {
            if (applied == null || target == null || !Ready()) return;
            var edited = menu == null ? null : (menuPlayerSettings?.GetValue(menu) as UserSettings)?.CloudSettings;
            if (edited != null && !ReferenceEquals(edited, target))
                Log.Info("SETTINGS guest: the Options screen edited a different settings object than the active one");
            Write(target, applied);
        }

        // Around each of the two settings writes: the guest's own values go into storage. Only the
        // object that holds the host's values is touched (the cache writer also stores temporary
        // settings objects while settings load), and nothing here calls back into the game.
        internal static void BeginWrite(CloudUserSettings cloud)
        {
            if (applied == null || own == null || cloud == null || !ReferenceEquals(cloud, target)) return;
            if (writeDepth++ == 0) Write(cloud, own);
        }

        internal static void EndWrite(CloudUserSettings cloud)
        {
            if (cloud != null && ReferenceEquals(cloud, target) && writeDepth > 0 && --writeDepth == 0 && applied != null)
                Write(cloud, applied);
            AfterHostSave(cloud);
        }

        // Every frame while in a session. Writes are synchronous, so a write still open here means
        // it threw before its postfix ran: put the host's values back.
        public static void Update()
        {
            if (writeDepth == 0) return;
            Log.Error("SETTINGS guest: a settings save failed part-way; using the host's values again");
            writeDepth = 0;
            if (applied != null && target != null) Write(target, applied);
        }

        // Session ended: the guest gets its own values back.
        public static void Reset()
        {
            lastSent = null;
            writeDepth = 0;
            if (own != null && target != null)
            {
                Write(target, own);
                Log.Info($"SETTINGS guest: restored your own combat pacing: {Describe(own)}");
            }
            own = null;
            applied = null;
            target = null;
        }

        internal static byte[] Encode(float[] values)
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

        internal static float[] Decode(byte[] payload)
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

    // Steam Cloud copy: Serializer.Serialize(CloudSettings), then SaveManager.SaveBytes.
    [HarmonyPatch(typeof(UserSettings), "Save")]
    public static class UserSettingsSavePatch
    {
        static void Prefix(UserSettings __instance)
        {
            try { SettingsSync.BeginWrite(__instance.CloudSettings); }
            catch (Exception e) { Log.Error("SETTINGS: before cloud save failed", e); }
        }

        static void Postfix(UserSettings __instance)
        {
            try { SettingsSync.EndWrite(__instance.CloudSettings); }
            catch (Exception e) { Log.Error("SETTINGS: after cloud save failed", e); }
        }
    }

    // PlayerPrefs copy: Serializer.Serialize(settings.CloudSettings) into "CachedSettings".
    [HarmonyPatch(typeof(CachedSettings), "SaveSettingsToPlayerPrefs")]
    public static class CachedSettingsSavePatch
    {
        static void Prefix(UserSettings settings)
        {
            try { SettingsSync.BeginWrite(settings?.CloudSettings); }
            catch (Exception e) { Log.Error("SETTINGS: before cache save failed", e); }
        }

        static void Postfix(UserSettings settings)
        {
            try { SettingsSync.EndWrite(settings?.CloudSettings); }
            catch (Exception e) { Log.Error("SETTINGS: after cache save failed", e); }
        }
    }

    // The Options > Game screen: its current/original values identify the guest's real edits.
    [HarmonyPatch(typeof(GameSettingsModule), "SaveSettings")]
    public static class GameSettingsSavePatch
    {
        static void Prefix(GameSettingsModule __instance)
        {
            try { SettingsSync.BeforeMenuSave(__instance); }
            catch (Exception e) { Log.Error("SETTINGS: before Options save failed", e); }
        }

        static void Postfix(GameSettingsModule __instance)
        {
            try { SettingsSync.AfterMenuSave(__instance); }
            catch (Exception e) { Log.Error("SETTINGS: after Options save failed", e); }
        }
    }
}
