using System;
using System.Linq;
using System.Reflection;
using BattleTech.Save;
using BattleTech.UI;
using BattletechCoop;
using BattletechCoop.Net;
using BattletechCoop.Sync;

// Settings sync: the guest uses the host's combat pacing for the session, and the guest's own
// values are the only ones ever stored (Steam Cloud file and PlayerPrefs cache), including
// edits it makes mid-session. Covers the cases from the PR #2 review.
static class Program
{
    static int passed, failed;
    static CoopSession session;
    static CoopController controller;

    // Order: speedUpCombat, useAttackCam, useTargetCam, moveFollow, sprintFollow,
    //        autoZoomChance, cinematicZoomChance, enemyCinematic, meleeCinematic, jumpCinematic
    static readonly float[] Host = { 1, 0, 1, 1.0f, 0.9f, 0.2f, 0.3f, 0.4f, 0.6f, 0f };

    static CloudUserSettings Live => ActiveOrDefaultSettings.CloudSettings;
    static CloudUserSettings LastCloudFile => UserSettings.CloudFile.Last();
    static CloudUserSettings LastCache => CachedSettings.PlayerPrefs.Last();

    static void Check(bool value, string detail) { if (!value) throw new Exception(detail); }

    // The guest's own preferences, different from Host in every field.
    static bool IsGuestOwn(CloudUserSettings c, float autoZoom = 0.1f, float jump = 0.5f) =>
        !c.speedUpCombat && c.useAttackCam && !c.useTargetCam && c.moveFollow == 0.25f && c.sprintFollow == 0.5f &&
        c.autoZoomChance == autoZoom && c.cinematicZoomChance == 0.7f && c.enemyCinematic == 0.8f &&
        c.meleeCinematic == 0.1f && c.jumpCinematic == jump;

    static bool IsHost(CloudUserSettings c, float autoZoom = 0.2f) =>
        c.speedUpCombat && !c.useAttackCam && c.useTargetCam && c.moveFollow == 1.0f && c.sprintFollow == 0.9f &&
        c.autoZoomChance == autoZoom && c.cinematicZoomChance == 0.3f && c.enemyCinematic == 0.4f &&
        c.meleeCinematic == 0.6f && c.jumpCinematic == 0f;

    static void Test(string name, SessionRole role, Action body)
    {
        SettingsSync.Reset();
        UserSettings.CloudFile.Clear(); CachedSettings.PlayerPrefs.Clear(); UserSettings.FailNextSaves = 0;
        Log.Errors.Clear();
        ActiveOrDefaultSettings.Settings = new UserSettings
        {
            CloudSettings = new CloudUserSettings
            {
                speedUpCombat = false, useAttackCam = true, useTargetCam = false, moveFollow = 0.25f, sprintFollow = 0.5f,
                autoZoomChance = 0.1f, cinematicZoomChance = 0.7f, enemyCinematic = 0.8f, meleeCinematic = 0.1f, jumpCinematic = 0.5f,
            },
        };
        session = new CoopSession { Role = role };
        controller = new CoopController { Session = session };
        CoopController.Instance = controller;
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + (e.InnerException ?? e).Message); }
    }

    static void HostSends(float[] values) => SettingsSync.HandleGameMessage(session, 0, SettingsSync.Encode(values));
    static float[] With(int index, float value) { var v = (float[])Host.Clone(); v[index] = value; return v; }

    // Options > Game > Save: the screen writes its values, then the settings are stored.
    static void SaveFromOptions(GameSettingsModule menu) { menu.SaveSettings(); ActiveOrDefaultSettings.SaveUserSettings(); }

    static int Main()
    {
        Patched.Register("UserSettings.Save", typeof(UserSettingsSavePatch));
        Patched.Register("CachedSettings.SaveSettingsToPlayerPrefs", typeof(CachedSettingsSavePatch));
        Patched.Register("GameSettingsModule.SaveSettings", typeof(GameSettingsSavePatch));

        Test("patches target the game's method names", SessionRole.Guest, () =>
        {
            foreach (var p in new[] { typeof(UserSettingsSavePatch), typeof(CachedSettingsSavePatch), typeof(GameSettingsSavePatch) })
            {
                var a = p.GetCustomAttribute<Harmony.HarmonyPatch>();
                Check(a.Type.GetMethod(a.Method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null,
                    $"{p.Name} targets missing {a.Type.Name}.{a.Method}");
            }
            Check(SettingsSync.Ready(), "fields not found");
        });

        Test("guest uses the host's values after joining", SessionRole.Guest, () =>
        {
            HostSends(Host);
            Check(IsHost(Live), "host values not in effect");
            Check(controller.Notices.Count == 1, "no notice");
        });

        Test("normal save: cloud file and cache both store the guest's own values", SessionRole.Guest, () =>
        {
            HostSends(Host);
            ActiveOrDefaultSettings.SaveUserSettings();
            Check(UserSettings.CloudFile.Count == 1 && IsGuestOwn(LastCloudFile), "cloud file has host values");
            Check(CachedSettings.PlayerPrefs.Count == 1 && IsGuestOwn(LastCache), "cache has host values");
            Check(IsHost(Live), "host values not back in effect after saving");
        });

        Test("cache-only save (no SaveManager) stores the guest's own values", SessionRole.Guest, () =>
        {
            HostSends(Host);
            ActiveOrDefaultSettings.Settings.HasSaveManager = false;
            ActiveOrDefaultSettings.SaveUserSettings();
            Check(UserSettings.CloudFile.Count == 0, "unexpected cloud save");
            Check(CachedSettings.PlayerPrefs.Count == 1 && IsGuestOwn(LastCache), "cache has host values");
            Check(IsHost(Live), "host values not back in effect");
        });

        Test("settings-load cache write stores the guest's own values", SessionRole.Guest, () =>
        {
            HostSends(Host);
            CachedSettings.Settings = ActiveOrDefaultSettings.Settings; // LoadUserSettings callback
            Check(IsGuestOwn(LastCache) && IsHost(Live), "cache has host values or host values lost");
        });

        Test("a temporary settings object written to the cache is left alone", SessionRole.Guest, () =>
        {
            HostSends(Host);
            var other = new UserSettings { CloudSettings = new CloudUserSettings { moveFollow = 0.33f } };
            CachedSettings.Settings = other;
            Check(LastCache.moveFollow == 0.33f && other.CloudSettings.moveFollow == 0.33f, "temporary object changed");
            Check(IsHost(Live), "active settings changed");
        });

        Test("review repro: host update while Options is open, then an unrelated save", SessionRole.Guest, () =>
        {
            HostSends(Host);                               // guest's own autoZoom 0.1, host's 0.2
            var menu = new GameSettingsModule(); menu.InitSettings(); // shows 0.2
            HostSends(With(5, 0.9f));                      // host changes autoZoom to 0.9, screen still shows 0.2
            menu.Edit("AutoCenter", true);                 // guest changes something unrelated
            SaveFromOptions(menu);
            Check(IsGuestOwn(LastCloudFile) && LastCloudFile.autoCenterOnSelection, "cloud file lost a guest preference");
            Check(IsGuestOwn(LastCache), "cache lost a guest preference");
            Check(IsHost(Live, autoZoom: 0.9f), "host's latest value not in effect");
            SettingsSync.Reset();
            Check(IsGuestOwn(Live), "after the session the guest's autoZoom is not its own 0.1");
        });

        Test("a real guest edit in Options is kept, stored, and restored after the session", SessionRole.Guest, () =>
        {
            HostSends(Host);
            var menu = new GameSettingsModule(); menu.InitSettings();
            menu.Edit("JumpCinematic", 0.8f);
            SaveFromOptions(menu);
            Check(IsGuestOwn(LastCloudFile, jump: 0.8f) && IsGuestOwn(LastCache, jump: 0.8f), "edit not stored");
            Check(IsHost(Live), "host values not in effect during the session");
            Check(controller.Notices.Any(n => n.Contains("after the session") || n.Contains("until the session ends")), "no notice");
            SettingsSync.Reset();
            Check(IsGuestOwn(Live, jump: 0.8f), "edit not restored after the session");
        });

        Test("saving Options twice keeps the edit and touches nothing else", SessionRole.Guest, () =>
        {
            HostSends(Host);
            var menu = new GameSettingsModule(); menu.InitSettings();
            menu.Edit("MoveFollow", 0.6f);
            SaveFromOptions(menu);
            HostSends(With(5, 0.9f));
            SaveFromOptions(menu);
            var c = LastCloudFile;
            Check(c.moveFollow == 0.6f && c.autoZoomChance == 0.1f && !c.speedUpCombat && c.jumpCinematic == 0.5f, "second save changed own values");
        });

        Test("an edit moved back to its original value is not an edit", SessionRole.Guest, () =>
        {
            HostSends(Host);
            var menu = new GameSettingsModule(); menu.InitSettings();
            menu.Edit("EnemyCinematic", 0.05f); menu.Edit("EnemyCinematic", 0.4f); // back to what it showed
            SaveFromOptions(menu);
            Check(IsGuestOwn(LastCloudFile), "reverted slider changed the guest's own value");
        });

        Test("Options opened before joining: an untouched save keeps the guest's values", SessionRole.Guest, () =>
        {
            var menu = new GameSettingsModule(); menu.InitSettings(); // shows the guest's own values
            HostSends(Host);
            SaveFromOptions(menu);
            Check(IsGuestOwn(LastCloudFile) && IsHost(Live), "stale screen values were taken as edits");
        });

        Test("a second host update doesn't replace the guest's own snapshot", SessionRole.Guest, () =>
        {
            HostSends(Host); HostSends(With(0, 0));
            ActiveOrDefaultSettings.SaveUserSettings();
            Check(IsGuestOwn(LastCloudFile), "own snapshot was taken from host values");
            SettingsSync.Reset();
            Check(IsGuestOwn(Live), "wrong values restored");
        });

        Test("a save that throws: the host's values return on the next update", SessionRole.Guest, () =>
        {
            HostSends(Host);
            UserSettings.FailNextSaves = 1;
            try { ActiveOrDefaultSettings.SaveUserSettings(); } catch (System.IO.IOException) { }
            Check(IsGuestOwn(Live), "precondition: postfix should not have run");
            SettingsSync.Update();
            Check(IsHost(Live), "host values not restored after the failed save");
            ActiveOrDefaultSettings.SaveUserSettings();
            Check(IsGuestOwn(LastCloudFile) && IsHost(Live), "later saves broken");
        });

        Test("outside a session, saves store whatever is set", SessionRole.Guest, () =>
        {
            Live.moveFollow = 0.6f;
            ActiveOrDefaultSettings.SaveUserSettings();
            Check(LastCloudFile.moveFollow == 0.6f && LastCache.moveFollow == 0.6f && Live.moveFollow == 0.6f, "values changed");
        });

        Test("guest ignores settings that don't come from the host", SessionRole.Guest, () =>
        {
            SettingsSync.HandleGameMessage(session, 2, SettingsSync.Encode(Host));
            Check(IsGuestOwn(Live), "applied settings from another guest");
        });

        Test("guest rejects a payload with the wrong number of settings", SessionRole.Guest, () =>
        {
            SettingsSync.HandleGameMessage(session, 0, SettingsSync.Encode(new float[] { 1, 0, 1 }));
            Check(IsGuestOwn(Live) && Log.Errors.Count == 1, "bad payload applied or not reported");
        });

        Test("host sends to a guest that joins, only to that guest", SessionRole.Host, () =>
        {
            SettingsSync.OnGuestJoined(session, 2);
            Check(session.Sent.Count == 1 && session.Sent[0].Channel == SettingsSync.Channel && session.Sent[0].Target == 2, "not sent to the joiner");
            Check(SettingsSync.Decode(session.Sent[0].Payload).SequenceEqual(new float[] { 0, 1, 0, 0.25f, 0.5f, 0.1f, 0.7f, 0.8f, 0.1f, 0.5f }), "wrong values sent");
        });

        Test("host sends a change once, to everyone, and not when nothing changed", SessionRole.Host, () =>
        {
            SettingsSync.OnGuestJoined(session, 1);
            ActiveOrDefaultSettings.SaveUserSettings();
            Check(session.Sent.Count == 1, "unchanged save was sent");
            Live.jumpCinematic = 0.9f;
            ActiveOrDefaultSettings.SaveUserSettings(); // cloud save and cache write: one message
            Check(session.Sent.Count == 2 && session.Sent[1].Target == null, "change not broadcast once");
            Check(SettingsSync.Decode(session.Sent[1].Payload)[9] == 0.9f, "wrong value broadcast");
            Check(LastCloudFile.jumpCinematic == 0.9f, "host's own save altered");
        });

        Test("host ignores the settings channel", SessionRole.Host, () =>
        {
            SettingsSync.HandleGameMessage(session, 1, SettingsSync.Encode(Host));
            Check(IsGuestOwn(Live), "host applied a guest's settings");
        });

        Console.WriteLine($"{passed}/{passed + failed} passed");
        return failed == 0 ? 0 : 1;
    }
}
