using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using BattleTech;
using HBS;
using BattletechCoop.Net;
using Steamworks;
using UnityEngine;

namespace BattletechCoop
{
    // Owns the co-op session for the lifetime of the game process: starts it from the
    // control window, from launch arguments or from a Steam invite, pumps it every frame,
    // and shows a small status overlay. Ctrl+Shift+C toggles the window.
    //
    // Launch arguments (for development and automated tests):
    //   -coop-host-tcp <port>       host over TCP (loopback only unless -coop-lan)
    //   -coop-join-tcp <host:port>  join over TCP
    //   -coop-lan                   bind the TCP host to all interfaces
    //   -coop-name <name>           player name (default: Steam persona name)
    //   -coop-test-message          once connected, send a test game message to the other side
    //   -coop-autoplay              (host) co-op combats make Player 1 AI-controlled too (bot test)
    //   -coop-autoplay-shared       (host) same, but the Player 1 AI alternates between the guest and
    //                               the host each activation (tests guest order requests)
    //   -coop-autoskirmish [map]    (host) once a guest has joined, launch a fixed skirmish
    //   -coop-timescale <x>         Unity time scale during combat (speeds up bot tests)
    //   -coop-inject-desync <seq>   (guest) corrupt state before that stream item, to test desync detection
    //   -coop-no-saves              never write saves (test copies share the owner's Steam Cloud saves)
    //   -coop-test-shipview         (host) once a guest is in its menu, load the newest campaign save
    //   -coop-test-requests         (both) make scripted ship changes once the ship is showing
    //   -coop-test-mission          (host) after the ship went out, launch the first ordinary contract
    //   +connect_lobby <id>         join a Steam lobby (Steam passes this when launching from an invite)
    public class CoopController : MonoBehaviour
    {
        public const ushort TestChannel = 1;
        private const int DefaultTcpPort = 47637;

        public static CoopController Instance { get; private set; }
        public CoopSession Session { get; private set; }
        public Sync.AutoplayMode Autoplay { get; private set; }

        private bool autoSkirmish;
        private string autoSkirmishMap;
        private float autoSkirmishAt = -1;
        private bool autoSkirmishDone;
        private float combatTimeScale = 1f;

        private string modsDirectory;
        private string manifest;
        private Callback<GameLobbyJoinRequested_t> lobbyJoinRequested;
        private Callback<GameRichPresenceJoinRequested_t> richPresenceJoinRequested;
        private bool steamReady;
        private ulong pendingLobby;
        private string pendingName;
        private string lobbyField = "";
        private bool showWindow;
        private Rect windowRect = new Rect(40, 40, 420, 360);
        private string joinAddress = "127.0.0.1:" + DefaultTcpPort;
        private string hostPort = DefaultTcpPort.ToString();
        private bool sendTestOnConnect;
        private bool testSent;
        private string lastEvent = "";

        public static void Create(string modDirectory)
        {
            var go = new GameObject("BattletechCoop");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<CoopController>();
            Instance.modsDirectory = Path.GetDirectoryName(modDirectory.TrimEnd('\\', '/'));
        }

        private void Start()
        {
            manifest = ModManifest.Compute(modsDirectory);
            Log.Info($"Mod manifest: {manifest}");
            HandleLaunchArguments();
        }

        // ModTek starts mods before the game initializes Steam, so Steam callbacks are registered
        // on the first frame where SteamManager reports ready, not in Start().
        private void EnsureSteam()
        {
            if (steamReady || !SteamManager.Initialized) return;
            steamReady = true;
            lobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
            richPresenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichPresenceJoinRequested);
            Log.Info($"Steam ready as '{SteamFriends.GetPersonaName()}'; listening for invites");
            if (pendingLobby != 0)
            {
                var lobby = pendingLobby;
                pendingLobby = 0;
                JoinSteam(lobby, pendingName);
            }
        }

        private void HandleLaunchArguments()
        {
            var args = Environment.GetCommandLineArgs();
            string name = null;
            for (var i = 0; i < args.Length; i++)
            {
                var hasValue = i + 1 < args.Length;
                switch (args[i])
                {
                    case "-coop-name": if (hasValue) name = args[i + 1]; break;
                    case "-coop-test-message": sendTestOnConnect = true; break;
                    case "-coop-autoplay": Autoplay = Sync.AutoplayMode.Host; break;
                    case "-coop-autoplay-shared": Autoplay = Sync.AutoplayMode.Shared; break;
                    case "-coop-autoskirmish":
                        autoSkirmish = true;
                        if (hasValue && !args[i + 1].StartsWith("-")) autoSkirmishMap = args[i + 1];
                        break;
                    case "-coop-test-rehost": rehostTestRounds = 3; break;
                    case "-coop-test-shipview": shipViewTest = true; break;
                    case "-coop-test-requests": Sync.ShipRequestTest.Enabled = true; break;
                    case "-coop-test-mission": Sync.MissionTest.Enabled = true; break;
                    case "-coop-no-saves": Sync.SaveGuard.NoSavesFlag = true; break;
                    case "-coop-deep-state": Sync.StateText.Deep = true; break;
                    case "-coop-inject-desync":
                        if (hasValue) Sync.CombatSync.InjectDesyncAt = uint.Parse(args[i + 1]);
                        break;
                    case "-coop-timescale":
                        if (hasValue) combatTimeScale = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
                        break;
                }
            }
            var lan = Array.IndexOf(args, "-coop-lan") >= 0;
            for (var i = 0; i + 1 < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-coop-host-tcp": HostTcp(int.Parse(args[i + 1]), lan, name); return;
                    case "-coop-join-tcp": JoinTcp(args[i + 1], name); return;
                    case "+connect_lobby":
                        // Steam isn't initialized yet this early; EnsureSteam() joins once it is.
                        pendingLobby = ulong.Parse(args[i + 1]);
                        pendingName = name;
                        Log.Info($"Launched from a Steam invite for lobby {pendingLobby}; joining once Steam is ready");
                        return;
                }
            }
        }

        private string DefaultName(string requested)
        {
            if (!string.IsNullOrEmpty(requested)) return requested;
            return SteamManager.Initialized ? SteamFriends.GetPersonaName() : "Player";
        }

        private string ModVersion => typeof(CoopController).Assembly.GetName().Version.ToString();

        public void HostTcp(int port, bool lan, string name = null)
        {
            if (!CanStart()) return;
            try
            {
                var transport = TcpTransport.Listen(lan ? IPAddress.Any : IPAddress.Loopback, port);
                Begin(CoopSession.Host(transport, DefaultName(name), ModVersion, manifest, Log.Info));
            }
            catch (Exception e)
            {
                Notify("Could not host on port " + port + ": " + e.Message);
            }
        }

        public void JoinTcp(string address, string name = null)
        {
            if (!CanStart()) return;
            var colon = address.LastIndexOf(':');
            var host = colon > 0 ? address.Substring(0, colon) : address;
            var port = colon > 0 ? int.Parse(address.Substring(colon + 1)) : DefaultTcpPort;
            Begin(CoopSession.Join(TcpTransport.Connect(host, port), DefaultName(name), ModVersion, manifest, Log.Info));
        }

        public void HostSteam(string name = null)
        {
            if (!CanStart()) return;
            if (!SteamManager.Initialized) { Notify("Steam is not running"); return; }
            Begin(CoopSession.Host(SteamTransport.Host(4, Log.Info), DefaultName(name), ModVersion, manifest, Log.Info));
        }

        public void JoinSteam(ulong lobby, string name = null)
        {
            if (!CanStart()) return;
            if (!SteamManager.Initialized) { Notify("Steam is not running"); return; }
            Begin(CoopSession.Join(SteamTransport.Join(lobby, Log.Info), DefaultName(name), ModVersion, manifest, Log.Info));
        }

        public void Leave()
        {
            Session?.Close("left the session");
        }

        private bool CanStart()
        {
            if (Session != null && Session.IsOpen) { Notify("Already in a session; leave it first"); return false; }
            return true;
        }

        private void Begin(CoopSession session)
        {
            Session = session;
            testSent = false;
            session.PlayerJoined += p =>
            {
                Notify($"{p.Name} is player {p.Index}");
                if (session.Role == SessionRole.Host && p.Index != 0)
                {
                    Sync.ShipView.MarkDirty($"{p.Name} joined");
                    Sync.SettingsSync.OnGuestJoined(session, p.Index);
                }
            };
            session.PlayerLeft += (p, reason) => Notify($"{p.Name} left: {reason}");
            session.Closed += reason => OnSessionClosed(session, reason);
            session.GameMessage += OnGameMessage;
        }

        private float returnToMenuAt = -1;

        private void OnSessionClosed(CoopSession session, string reason)
        {
            Notify("Session ended: " + reason);
            Sync.CombatSync.ResetForSessionEnd();
            Sync.ShipView.Reset();
            Sync.SettingsSync.Reset();
            Sync.CompanyLive.Reset();
            Sync.LancePicker.Reset();
            // A guest left inside the host's battle or campaign would keep playing a copy that
            // can't affect anything ("ghost campaign"): go back to the main menu shortly after.
            if (session.Role == SessionRole.Guest && Sync.CombatSync.GuestInHostGame && !Sync.AutoSkirmish.MainMenuShowing)
            {
                Notify("The co-op session ended: returning to the main menu in a few seconds");
                returnToMenuAt = Time.realtimeSinceStartup + 6f;
            }
            Sync.CombatSync.GuestInHostGame = false;
        }

        private void UpdateReturnToMenu()
        {
            if (returnToMenuAt < 0 || Time.realtimeSinceStartup < returnToMenuAt) return;
            returnToMenuAt = -1;
            if (Sync.AutoSkirmish.MainMenuShowing) return;
            if (Session != null && Session.IsOpen) return; // rejoined in the meantime
            try
            {
                Log.Info("Returning to the main menu after the co-op session ended");
                LazySingletonBehavior<BattleTech.Save.LoadTransitioning>.Instance.GoToMainMenu();
            }
            catch (Exception e)
            {
                Log.Error("Could not return to the main menu", e);
            }
        }

        private void OnGameMessage(byte sender, ushort channel, byte[] payload)
        {
            if (channel == Sync.ShipView.ChannelShipSave || channel == Sync.ShipView.ChannelShipRefresh)
            {
                Sync.ShipView.HandleGameMessage(Session, channel, payload);
                return;
            }
            if (Sync.MissionStart.Handles(channel))
            {
                Sync.MissionStart.HandleGameMessage(Session, sender, channel, payload);
                return;
            }
            if (channel == Sync.ShipRequests.ChannelRequest || channel == Sync.ShipRequests.ChannelReply
                || channel == Sync.ShipRequests.ChannelUpdate)
            {
                Sync.ShipRequests.HandleGameMessage(Session, sender, channel, payload);
                return;
            }
            // Must come before the combat range below, which takes every other channel from 10 up.
            if (channel == Sync.SettingsSync.Channel)
            {
                Sync.SettingsSync.HandleGameMessage(Session, sender, payload);
                return;
            }
            if (channel == Sync.LancePicker.Channel)
            {
                Sync.LancePicker.HandleGameMessage(Session, sender, payload);
                return;
            }
            if (channel == Sync.LanceOwners.Channel)
            {
                Sync.LanceOwners.HandleGameMessage(Session, sender, payload);
                return;
            }
            if (channel == Sync.CompanyLive.Channel)
            {
                Sync.CompanyLive.HandleGameMessage(Session, sender, payload);
                return;
            }
            if (channel >= Sync.CombatSync.ChannelCombatStart)
            {
                Sync.CombatSync.HandleGameMessage(Session, sender, channel, payload);
                return;
            }
            if (channel != TestChannel) return;
            // Large test messages are the text followed by zero padding; show only the text.
            var text = Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, 80)).Split('\0')[0];
            Notify($"Test message from player {sender}: {payload.Length} bytes, {text}");
        }

        public void SendTestMessage(int extraBytes = 0)
        {
            if (Session == null || Session.State != SessionState.Active) return;
            var text = $"hello from player {Session.LocalIndex} ({Session.LocalName})";
            var head = Encoding.UTF8.GetBytes(text);
            var payload = new byte[head.Length + extraBytes];
            Buffer.BlockCopy(head, 0, payload, 0, head.Length);
            Session.SendGame(TestChannel, payload);
            Log.Info($"Sent test message ({payload.Length} bytes)");
        }

        private void OnLobbyJoinRequested(GameLobbyJoinRequested_t request)
        {
            Log.Info($"Steam invite accepted: lobby {request.m_steamIDLobby.m_SteamID} from {request.m_steamIDFriend.m_SteamID}");
            JoinSteam(request.m_steamIDLobby.m_SteamID);
        }

        // "Join Game" on a friend in the Steam friends list while our game is running.
        private void OnRichPresenceJoinRequested(GameRichPresenceJoinRequested_t request)
        {
            Log.Info($"Steam Join Game: '{request.m_rgchConnect}' from {request.m_steamIDFriend.m_SteamID}");
            ulong lobby;
            var parts = (request.m_rgchConnect ?? "").Split(' ');
            if (parts.Length == 2 && parts[0] == "+connect_lobby" && ulong.TryParse(parts[1], out lobby))
                JoinSteam(lobby);
        }

        // For sync modules: the same short notice the overlay shows for session events.
        internal void Notice(string message) => Notify(message);

        private void Notify(string message)
        {
            lastEvent = message;
            Log.Info(message);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.C) && Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftShift))
                showWindow = !showWindow;

            EnsureSteam();
            UpdateRehostTest();
            UpdateReturnToMenu();
            if (Session == null) return;
            try
            {
                Session.Update();
            }
            catch (Exception e)
            {
                Log.Error("Session update failed", e);
            }
            if (sendTestOnConnect && !testSent && Session.State == SessionState.Active
                && (Session.Role == SessionRole.Guest || Session.Players.Count() > 1))
            {
                testSent = true;
                SendTestMessage();
            }

            try
            {
                Sync.CombatSync.Current?.Update();
                Sync.ShipView.Update(Session);
                Sync.ShipRequests.Update();
                Sync.MissionStart.Update();
                Sync.SettingsSync.Update();
                Sync.CompanyLive.Update(Session);
                Sync.LancePicker.Update();
                Sync.CombatSync.UpdateIdleGuest(Session);
                UpdateTestModes();
            }
            catch (Exception e)
            {
                Log.Error("Combat sync update failed", e);
            }
        }

        // -coop-test-rehost: host via Steam, leave, host again, a few times (reported re-host bug).
        private int rehostTestRounds;
        private float rehostNextStep = -1;

        private void UpdateRehostTest()
        {
            if (rehostTestRounds <= 0 || !steamReady || Time.realtimeSinceStartup < rehostNextStep) return;
            var steam = Session?.Transport as SteamTransport;
            if (Session == null || !Session.IsOpen)
            {
                Log.Info($"REHOST TEST: hosting (rounds left {rehostTestRounds})");
                HostSteam();
                rehostNextStep = Time.realtimeSinceStartup + 10f; // lobby must exist by then
                return;
            }
            Log.Info($"REHOST TEST: lobby ready={steam != null && steam.LobbyReady} ({Session.Transport.Name}); leaving");
            Leave();
            rehostTestRounds--;
            rehostNextStep = Time.realtimeSinceStartup + 3f;
            if (rehostTestRounds == 0) Log.Info("REHOST TEST: done");
        }

        // -coop-test-shipview (host): once a guest is in its main menu, load the newest campaign save
        // (read-only with -coop-no-saves); the ship view then sends it to the guest.
        private bool shipViewTest;

        private void UpdateShipViewTest()
        {
            if (!shipViewTest || Session.Role != SessionRole.Host || !Sync.AutoSkirmish.MainMenuShowing) return;
            var guests = Session.Players.Where(p => p.Index != 0).ToList();
            if (guests.Count == 0 || !guests.All(p => Sync.CombatSync.GuestsInMenu.Contains(p.Index))) return;
            shipViewTest = false;
            var slot = UnityGameInstance.BattleTechGame.SaveManager.GameInstanceSaves.MostRecentCampaignSave;
            if (slot == null) { Log.Error("SHIP test: no campaign save to load"); return; }
            Log.Info($"SHIP test: loading the newest campaign save '{slot.CompanyName}'");
            LazySingletonBehavior<BattleTech.Save.LoadTransitioning>.Instance.Load(slot);
        }

        private void UpdateTestModes()
        {
            UpdateShipViewTest();
            Sync.ShipRequestTest.Update();
            Sync.MissionTest.Update();
            // Only with -coop-timescale, so normal play (including the game's own pause) is untouched.
            if (!Mathf.Approximately(combatTimeScale, 1f))
            {
                var inCombat = Sync.CombatSync.Current != null && UnityGameInstance.BattleTechGame?.Combat != null;
                Time.timeScale = inCombat ? combatTimeScale : 1f;
            }

            if (!autoSkirmish || autoSkirmishDone || Session.Role != SessionRole.Host) return;
            var guests = Session.Players.Where(p => p.Index != 0).ToList();
            if (Session.State != SessionState.Active || guests.Count == 0 || !Sync.AutoSkirmish.MainMenuShowing
                || !guests.All(p => Sync.CombatSync.GuestsInMenu.Contains(p.Index)))
            {
                autoSkirmishAt = -1;
                return;
            }
            // Give both games a moment in the main menu before loading.
            if (autoSkirmishAt < 0) autoSkirmishAt = Time.realtimeSinceStartup + 5f;
            if (Time.realtimeSinceStartup < autoSkirmishAt) return;
            autoSkirmishDone = true;
            Sync.AutoSkirmish.Launch(autoSkirmishMap);
        }

        private void OnApplicationQuit()
        {
            Session?.Close("game closed");
        }

        private void OnGUI()
        {
            if (Session != null && Session.IsOpen)
            {
                var label = $"Co-op {Session.Role} · {Session.State} · {Session.Players.Count()} player(s)";
                GUI.Label(new Rect(Screen.width - 330, 8, 320, 22), label);
                if (Session.Role == SessionRole.Guest && Sync.ShipView.GuestViewing && Sync.CombatSync.Current == null
                    && UnityGameInstance.BattleTechGame?.Simulation != null && UnityGameInstance.BattleTechGame.Combat == null)
                    GUI.Label(new Rect(Screen.width / 2 - 300, 4, 600, 22),
                        $"Viewing {Sync.ShipView.GuestCompany} (the host's company). Refits, repairs and pilot changes go to the host; other changes are not kept.");
                if (Time.realtimeSinceStartup < Sync.MissionStart.NoticeUntil)
                    GUI.Label(new Rect(Screen.width / 2 - 300, 48, 600, 44), Sync.MissionStart.Notice);
                if (Time.realtimeSinceStartup < Sync.ShipRequests.NoticeUntil)
                    GUI.Label(new Rect(Screen.width / 2 - 300, 26, 600, 44), Sync.ShipRequests.Notice);
                var sync = Sync.CombatSync.Current;
                if (sync != null)
                {
                    GUI.Label(new Rect(Screen.width - 330, 28, 320, 22), sync.Status);
                    if (sync.AnyDesync) DrawDesyncBanner();
                    if (Time.realtimeSinceStartup < sync.NoticeUntil)
                        GUI.Label(new Rect(Screen.width / 2 - 250, 70, 500, 44), sync.Notice);
                }
            }
            Sync.LancePicker.OnGUI();
            if (showWindow)
                windowRect = GUILayout.Window(0x0B7C00, windowRect, DrawWindow, "BattleTech Co-op (Ctrl+Shift+C)");
        }

        private GUIStyle bannerStyle;

        // A desync means the two games no longer show the same battle; small status text was easy
        // to miss in the first human test, so this is a big banner that stays up.
        private void DrawDesyncBanner()
        {
            if (bannerStyle == null)
            {
                bannerStyle = new GUIStyle(GUI.skin.box) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                bannerStyle.normal.textColor = Color.white;
                var tex = new Texture2D(1, 1);
                tex.SetPixel(0, 0, new Color(0.75f, 0.05f, 0.05f, 0.9f));
                tex.Apply();
                bannerStyle.normal.background = tex;
            }
            GUI.Box(new Rect(Screen.width / 2 - 360, 120, 720, 76),
                "CO-OP DESYNC: the two games no longer match.\nFinish or quit this battle and send both logs.", bannerStyle);
        }

        private void DrawWindow(int id)
        {
            if (Session == null || !Session.IsOpen)
            {
                GUILayout.Label("Not connected.");
                GUILayout.Label(steamReady ? "Steam ready" : "Waiting for Steam...");
                if (GUILayout.Button("Host via Steam")) HostSteam();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Lobby ID", GUILayout.Width(60));
                lobbyField = GUILayout.TextField(lobbyField, GUILayout.Width(170));
                ulong lobbyId;
                if (GUILayout.Button("Join Steam lobby") && ulong.TryParse(lobbyField.Trim(), out lobbyId)) JoinSteam(lobbyId);
                GUILayout.EndHorizontal();
                GUILayout.Space(8);
                GUILayout.Label("Development (TCP):");
                GUILayout.BeginHorizontal();
                GUILayout.Label("Port", GUILayout.Width(40));
                hostPort = GUILayout.TextField(hostPort, GUILayout.Width(70));
                if (GUILayout.Button("Host TCP")) HostTcp(int.Parse(hostPort), false);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                joinAddress = GUILayout.TextField(joinAddress, GUILayout.Width(200));
                if (GUILayout.Button("Join TCP")) JoinTcp(joinAddress);
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label($"{Session.Role} via {Session.Transport.Name}");
                GUILayout.Label($"State: {Session.State}   You are player {Session.LocalIndex}");
                foreach (var p in Session.Players)
                    GUILayout.Label($"  [{p.Index}] {p.Name}   {(p.RttMs < 0 ? "-" : p.RttMs.ToString("0") + " ms")}");
                var steam = Session.Transport as SteamTransport;
                if (steam != null && steam.LobbyReady)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"Lobby {steam.Lobby.m_SteamID}");
                    if (GUILayout.Button("Copy ID", GUILayout.Width(70))) GUIUtility.systemCopyBuffer = steam.Lobby.m_SteamID.ToString();
                    GUILayout.EndHorizontal();
                    if (Session.Role == SessionRole.Host && GUILayout.Button("Invite Steam friends")) steam.InviteFriends();
                }
                if (Session.State != SessionState.Active) GUILayout.Label("Connecting to host...");
                if (Session.Role == SessionRole.Guest && Session.State == SessionState.Active)
                {
                    GUI.enabled = !Sync.ShipView.Busy;
                    if (GUILayout.Button(Sync.ShipView.Busy ? "Loading the host's ship..." : "Refresh the host's ship"))
                        Sync.ShipView.RequestRefresh(Session);
                    GUI.enabled = true;
                }
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Send test message")) SendTestMessage();
                if (GUILayout.Button("Send 2 MB test")) SendTestMessage(2 * 1024 * 1024);
                GUILayout.EndHorizontal();
                if (GUILayout.Button("Leave session")) Leave();
            }
            GUILayout.Space(8);
            GUILayout.Label(lastEvent);
            GUILayout.Label($"Mods: {manifest}");
            GUI.DragWindow();
        }
    }
}
