// Data-only stand-ins for the game types SettingsSync touches, so the production file runs
// without Unity. Method bodies copy the order of the real ones in build 20387569:
//   ActiveOrDefaultSettings.SaveUserSettings: UserSettings.Save() if HasSaveManager, then
//     CachedSettings.Settings = userSettings (-> SaveSettingsToPlayerPrefs) always.
//   GameSettingsModule.InitSettings: playerSettings = ActiveOrDefaultSettings.Settings;
//     original<X> = cloud value; current<X> = original<X>.
//   GameSettingsModule.SaveSettings: every current<X> -> playerSettings.CloudSettings.
// Harmony isn't present, so Patched.Run stands in for it: prefix, original, postfix, and
// (like Harmony 1.2, which has no finalizers) no postfix when the original throws.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Harmony
{
    [AttributeUsage(AttributeTargets.Class)]
    public class HarmonyPatch : Attribute
    {
        public Type Type; public string Method;
        public HarmonyPatch(Type type, string method) { Type = type; Method = method; }
    }
    public static class AccessTools
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        public static FieldInfo Field(Type type, string name) => type.GetField(name, All);
    }
}

namespace BattleTech.Save
{
    public class CloudUserSettings
    {
        public bool speedUpCombat, useAttackCam, useTargetCam, autoCenterOnSelection;
        public float moveFollow, sprintFollow, autoZoomChance, cinematicZoomChance, enemyCinematic, meleeCinematic, jumpCinematic;
        public CloudUserSettings Copy() => (CloudUserSettings)MemberwiseClone();
    }

    public class UserSettings
    {
        public static readonly List<CloudUserSettings> CloudFile = new List<CloudUserSettings>(); // settings_cloud.sav
        public static int FailNextSaves;
        public bool HasSaveManager = true;
        public CloudUserSettings CloudSettings { get; set; } = new CloudUserSettings();
        public void Save() => Patched.Run("UserSettings.Save", this, () =>
        {
            if (FailNextSaves > 0) { FailNextSaves--; throw new System.IO.IOException("disk full"); }
            CloudFile.Add(CloudSettings.Copy()); // Serializer.Serialize(CloudSettings), then SaveBytes
        });
    }

    public static class CachedSettings
    {
        public static readonly List<CloudUserSettings> PlayerPrefs = new List<CloudUserSettings>(); // "CachedSettings"
        public static UserSettings Settings { set { SaveSettingsToPlayerPrefs(value); } }
        static void SaveSettingsToPlayerPrefs(UserSettings settings) =>
            Patched.Run("CachedSettings.SaveSettingsToPlayerPrefs", settings, () => PlayerPrefs.Add(settings.CloudSettings.Copy()));
    }

    public static class ActiveOrDefaultSettings
    {
        public static UserSettings Settings = new UserSettings();
        public static CloudUserSettings CloudSettings => Settings.CloudSettings;
        public static void SaveUserSettings()
        {
            if (Settings.HasSaveManager) Settings.Save();
            CachedSettings.Settings = Settings;
        }
    }
}

namespace BattleTech.UI
{
    using BattleTech.Save;

    public class GameSettingsModule
    {
        private bool originalSpeedUpCombat, currentSpeedUpCombat, originalAttackCam, currentAttackCam;
        private bool originalOverShoulderCam, currentOverShoulderCam, originalAutoCenter, currentAutoCenter;
        private float originalMoveFollow, currentMoveFollow, originalSprintFollow, currentSprintFollow;
        private float originalAutoZoomChance, currentAutoZoomChance, originalCinematicZoomChance, currentCinematicZoomChance;
        private float originalEnemyCinematic, currentEnemyCinematic, originalMeleeCinematic, currentMeleeCinematic;
        private float originalJumpCinematic, currentJumpCinematic;
        private UserSettings playerSettings;

        public void InitSettings()
        {
            playerSettings = ActiveOrDefaultSettings.Settings;
            var c = playerSettings.CloudSettings;
            currentSpeedUpCombat = originalSpeedUpCombat = c.speedUpCombat;
            currentAttackCam = originalAttackCam = c.useAttackCam;
            currentOverShoulderCam = originalOverShoulderCam = c.useTargetCam;
            currentAutoCenter = originalAutoCenter = c.autoCenterOnSelection;
            currentMoveFollow = originalMoveFollow = c.moveFollow;
            currentSprintFollow = originalSprintFollow = c.sprintFollow;
            currentAutoZoomChance = originalAutoZoomChance = c.autoZoomChance;
            currentCinematicZoomChance = originalCinematicZoomChance = c.cinematicZoomChance;
            currentEnemyCinematic = originalEnemyCinematic = c.enemyCinematic;
            currentMeleeCinematic = originalMeleeCinematic = c.meleeCinematic;
            currentJumpCinematic = originalJumpCinematic = c.jumpCinematic;
        }

        public void SaveSettings() => Patched.Run("GameSettingsModule.SaveSettings", this, () =>
        {
            var c = playerSettings.CloudSettings;
            c.speedUpCombat = currentSpeedUpCombat; c.useAttackCam = currentAttackCam;
            c.useTargetCam = currentOverShoulderCam; c.autoCenterOnSelection = currentAutoCenter;
            c.moveFollow = currentMoveFollow; c.sprintFollow = currentSprintFollow;
            c.autoZoomChance = currentAutoZoomChance; c.cinematicZoomChance = currentCinematicZoomChance;
            c.enemyCinematic = currentEnemyCinematic; c.meleeCinematic = currentMeleeCinematic;
            c.jumpCinematic = currentJumpCinematic;
        });

        // A slider or toggle moved (the UI's OnToggleChanged/UpdateSlider set current<X>).
        public void Edit(string suffix, object value) =>
            GetType().GetField("current" + suffix, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(this, value);
    }
}

namespace BattletechCoop.Net
{
    public enum SessionRole { Host, Guest }
    public class CoopSession
    {
        public SessionRole Role;
        public bool IsOpen = true;
        public readonly List<(ushort Channel, byte[] Payload, byte? Target)> Sent = new List<(ushort, byte[], byte?)>();
        public void SendGame(ushort channel, byte[] payload, byte? target = null) => Sent.Add((channel, payload, target));
    }
}

namespace BattletechCoop
{
    public class CoopController
    {
        public static CoopController Instance { get; set; }
        public Net.CoopSession Session { get; set; }
        public readonly List<string> Notices = new List<string>();
        internal void Notice(string message) => Notices.Add(message);
    }
    internal static class Log
    {
        public static readonly List<string> Errors = new List<string>();
        public static void Info(string message) { }
        public static void Error(string message, Exception e = null) => Errors.Add(message);
    }
}

// Harmony stand-in: runs the production patch class for a target around the original body.
public static class Patched
{
    public static bool Enabled = true;
    static readonly Dictionary<string, Type> Patches = new Dictionary<string, Type>();
    public static void Register(string target, Type patch) => Patches[target] = patch;

    public static void Run(string target, object arg, Action original)
    {
        Type patch;
        if (!Enabled || !Patches.TryGetValue(target, out patch)) { original(); return; }
        Call(patch, "Prefix", arg);
        original();
        Call(patch, "Postfix", arg);
    }

    static void Call(Type patch, string name, object arg) =>
        patch.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, new[] { arg });
}
