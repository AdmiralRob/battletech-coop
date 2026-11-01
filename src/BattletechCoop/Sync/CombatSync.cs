using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BattleTech;
using BattleTech.UI;
using BattletechCoop.Net;
using Harmony;
using HBS;

namespace BattletechCoop.Sync
{
    // Test modes: Host = the AI plays Player 1 on the host; Shared = the Player 1 AI thinks on the
    // guest on even activations and on the host on odd ones, exercising guest order requests.
    public enum AutoplayMode : byte { Off = 0, Host = 1, Shared = 2 }

    // Lockstep for one combat (see docs/DESIGN.md "M1 protocol" and "M3 shared control"). The host
    // runs the game and streams every invocation, at the moment it executes, plus every AI
    // "activation complete", stamped with the turn-activation count. The guest replays the stream
    // in order, one item at a time, only when its game is idle and at the same activation, and
    // compares a canonical state text before each invocation. Orders the guest gives are sent to
    // the host as requests; once accepted they come back through the stream like any other.
    public class CombatSync
    {
        public const ushort ChannelCombatStart = 10;
        public const ushort ChannelStream = 11;
        public const ushort ChannelGuestStatus = 12;
        public const ushort ChannelDesync = 13;
        public const ushort ChannelMenuReady = 14;
        public const ushort ChannelOrderRequest = 15;
        public const ushort ChannelOrderResult = 16;
        public const ushort ChannelCombatPending = 17;
        public const ushort ChannelCombatSave = 18;

        // Guest: a combat the host started before this game reached its main menu.
        private static byte[] pendingStart;

        // Host: guests (by player index) that last reported being in their main menu.
        public static readonly HashSet<byte> GuestsInMenu = new HashSet<byte>();

        public const string Player1TeamGuid = "bf40fd39-ccf9-47c4-94a6-061809681140";

        private enum ItemKind : byte { Invocation = 1, AIComplete = 2 }

        private class Item
        {
            public uint Seq;
            public uint Activation;
            public ItemKind Kind;
            public string TypeName;
            public byte[] Data;
            public string State;
            public string TeamGuid;
        }

        private enum RequestKind : byte { Invocation = 1, AIComplete = 2 }

        private class Request
        {
            public byte Sender;
            public uint Id;
            public uint GuestSeq;
            public RequestKind Kind;
            public byte[] Data;
            public string TeamGuid;
            public uint Activation;
        }

        public static CombatSync Current { get; private set; }

        public bool IsHost { get; }
        public AutoplayMode Mode { get; }
        public bool Autoplay => Mode != AutoplayMode.Off;
        public string Notice { get; private set; } = "";
        public float NoticeUntil { get; private set; }
        public uint CombatId { get; }
        public int Desyncs { get; private set; }
        public bool AnyDesync => Desyncs > 0 || guestDesyncs > 0;
        public uint Activation { get; private set; }
        public uint LastSeq => seq;
        public int Pending => incoming.Count;
        public string Status => IsHost
            ? $"host: sent {seq}, guest applied {guestApplied}, guest desyncs {guestDesyncs}"
            : $"guest: applied {seq}, queued {incoming.Count}, desyncs {Desyncs}";

        private readonly CoopSession session;
        private readonly Queue<Item> incoming = new Queue<Item>();
        private readonly HashSet<string> streamedInvocations = new HashSet<string>();
        private readonly HashSet<string> reportedComplete = new HashSet<string>();
        private uint seq;
        private uint guestApplied;
        private int guestDesyncs;
        private Item applying;
        private bool missionOverLogged;
        private bool begunLogged;
        private float nextStatusTime;

        // Host: guest requests waiting for an idle moment. Guest: the one request in flight.
        private readonly Queue<Request> requests = new Queue<Request>();
        private readonly PendingSelectionOrders selectionOrders = new PendingSelectionOrders();
        private uint nextRequestId;
        private uint outstandingRequest;
        private bool completeRequested;

        // M2: the guest gets this combat as a combat save instead of a contract (DESIGN.md). Since
        // 0.0.13 the save is taken at the briefing and loaded as a combat restart, so both sides
        // launch the mission fresh from it: intro, dropship and opening dialogue included.
        public bool ViaSave { get; private set; }
        public bool SaveSent => saveSent;
        public bool SaveLoadStarted => saveLoadStarted;
        // Host: between sending the combat save and resuming from it (it reloads the same save, so
        // both sides run identical post-load state; see SendSave).
        public bool Reloading { get; private set; }
        private bool saveSent;
        private byte[] guestSave;
        private uint saveSeq;
        private uint saveActivation;
        private bool saveLoadStarted;
        private bool saveLoaded;

        private CombatSync(CoopSession session, bool isHost, uint combatId, AutoplayMode mode)
        {
            this.session = session;
            IsHost = isHost;
            CombatId = combatId;
            Mode = mode;
        }

        // Which machine makes the decisions for an AI team right now. Normally the host does; in
        // the Shared test the Player 1 AI alternates by activation (the count is identical on both).
        public bool ThinksHere(AITeam team)
        {
            if (Mode != AutoplayMode.Shared || team.GUID != Player1TeamGuid) return IsHost;
            // With a combat save, the guest has no battle before the save point; both sides know its
            // activation (host: when sent, guest: when received), so they agree on this.
            if (ViaSave && (!(IsHost ? saveSent : guestSave != null) || Activation <= saveActivation)) return IsHost;
            var guestThinks = Activation % 2 == 0;
            return IsHost ? !guestThinks : guestThinks;
        }

        private void ShowNotice(string text)
        {
            Notice = text;
            NoticeUntil = UnityEngine.Time.realtimeSinceStartup + 6f;
            Log.Info("SYNC notice: " + text);
        }

        public static bool HostActive => Current != null && Current.IsHost;
        public static bool GuestActive => Current != null && !Current.IsHost;

        // ---- combat start ----

        // Host, from the LaunchContract prefix: send the contract to every guest before launching locally.
        public static void BeginAsHost(CoopSession session, Contract contract, AutoplayMode mode, bool viaSave)
        {
            var combatId = (uint)Environment.TickCount;
            if (viaSave)
            {
                // The guest gets a combat save once the briefing is up here (OnBriefingReady).
                session.SendGame(ChannelCombatPending, Build(w => { w.Write(combatId); w.Write((byte)mode); }));
                Current = new CombatSync(session, true, combatId, mode) { ViaSave = true };
                Log.Info($"SYNC host: combat {combatId} started; the guest gets a combat save at the briefing, autoplay={mode}");
                return;
            }
            var data = GameSerializer.SerializeContract(contract);
            session.SendGame(ChannelCombatStart, Build(w =>
            {
                w.Write(combatId);
                w.Write((byte)mode);
                w.Write(data.Length);
                w.Write(data);
            }));
            Current = new CombatSync(session, true, combatId, mode);
            Log.Info($"SYNC host: combat {combatId} started, contract {data.Length} bytes sent, autoplay={mode}");
        }

        private static void BeginAsGuest(CoopSession session, byte[] payload)
        {
            if (!AutoSkirmish.MainMenuShowing)
            {
                if (UnityGameInstance.BattleTechGame?.Combat != null)
                {
                    Log.Error("SYNC guest: host started a combat but we are already in one; ignoring");
                    return;
                }
                pendingStart = payload;
                Log.Info("SYNC guest: host started a combat; launching it once our main menu is up");
                return;
            }
            pendingStart = null;
            var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            var combatId = r.ReadUInt32();
            var mode = (AutoplayMode)r.ReadByte();
            var data = r.ReadBytes(r.ReadInt32());
            var contract = GameSerializer.DeserializeContract(data);
            Current = new CombatSync(session, false, combatId, mode);
            Log.Info($"SYNC guest: launching host's combat {combatId} ({data.Length} bytes), autoplay={mode}");

            GuestInHostGame = true;
            LanceLoader.PrepareGuestLance(contract, () => ContractLauncher.LaunchFromMenu(contract));
        }

        private static void BeginPendingAsGuest(CoopSession session, BinaryReader r)
        {
            var combatId = r.ReadUInt32();
            var mode = (AutoplayMode)r.ReadByte();
            pendingStart = null;
            Current = new CombatSync(session, false, combatId, mode) { ViaSave = true };
            Current.ShowNotice("The host is starting a mission; it loads here in a moment");
        }

        private void ReceiveSave(BinaryReader r)
        {
            saveSeq = r.ReadUInt32();
            saveActivation = r.ReadUInt32();
            guestSave = r.ReadBytes(r.ReadInt32());
            LancePins.Pending = LancePins.Read(r);
            Log.Info($"SYNC guest: the host's launch picked {LancePins.Describe(LancePins.Pending)}");
            // Stream items up to the save are already part of its state.
            var dropped = 0;
            while (incoming.Count > 0 && incoming.Peek().Seq <= saveSeq) { incoming.Dequeue(); dropped++; }
            seq = saveSeq;
            Log.Info($"SYNC guest: combat save received ({guestSave.Length} bytes) at seq {saveSeq}, activation {saveActivation}; dropped {dropped} earlier items");
        }

        // Guest: load the host's combat save once we're out of combat (main menu or SimGame).
        private void TryLoadSave()
        {
            if (guestSave == null || saveLoadStarted) return;
            if (UnityGameInstance.BattleTechGame?.Combat != null) return;
            if (ShipView.Busy) return; // finish loading the host's ship first
            saveLoadStarted = true;
            try
            {
                var save = SaveTransfer.Deserialize(guestSave);
                MarkRestart(save);
                Log.Info($"SYNC guest: loading the host's combat save as a fresh start (skirmish={save.IsSkirmish})");
                GuestInHostGame = true;
                // LoadTransitioning.Load takes a save slot and reads it from disk; FileReadComplete is
                // where that read hands over the GameInstanceSave, so the in-memory save takes the
                // same path from there (combat-restart check, curtain, scene switch, GameInstance.Load).
                Traverse.Create(LazySingletonBehavior<BattleTech.Save.LoadTransitioning>.Instance)
                    .Method("FileReadComplete", new object[] { true, save }).GetValue();
            }
            catch (Exception e)
            {
                Log.Error("SYNC guest: could not load the host's combat save", e);
                ShowNotice("Could not load the host's mission; see BattletechCoop.log");
            }
        }

        // Guest, every frame while connected and not in a co-op combat: launch a deferred combat,
        // and tell the host whether we're in the main menu (auto-skirmish waits for that).
        private static float nextMenuReport;
        private static bool lastMenuReported;

        public static void UpdateIdleGuest(CoopSession session)
        {
            if (session.Role != SessionRole.Guest || session.State != SessionState.Active || Current != null) return;
            var inMenu = AutoSkirmish.MainMenuShowing;
            if (inMenu && pendingStart != null)
            {
                BeginAsGuest(session, pendingStart);
                return;
            }
            if (inMenu != lastMenuReported || UnityEngine.Time.realtimeSinceStartup >= nextMenuReport)
            {
                lastMenuReported = inMenu;
                nextMenuReport = UnityEngine.Time.realtimeSinceStartup + 2f;
                session.SendGame(ChannelMenuReady, Build(w => w.Write(inMenu)));
            }
        }

        // Guest: playing a battle or campaign that came from the host (skirmish launch or combat
        // save), which makes no sense to keep playing once the host is gone.
        public static bool GuestInHostGame;

        // The session ended (left, kicked, host gone): drop everything tied to it, so a new session
        // starts clean. Before this, a lobby ended mid-battle left a CombatSync bound to the dead
        // session behind, and the next launch was skipped as "already in co-op combat".
        public static void ResetForSessionEnd()
        {
            if (Current != null) Log.Info($"SYNC end (session closed): {Current.Status}");
            Current = null;
            pendingStart = null;
            GuestsInMenu.Clear();
            lastMenuReported = false;
        }

        public static void End(string reason)
        {
            if (Current == null) return;
            Log.Info($"SYNC end ({reason}): {Current.Status}");
            Current = null;
        }

        // ---- messages from the session ----

        public static void HandleGameMessage(CoopSession session, byte sender, ushort channel, byte[] payload)
        {
            try
            {
                using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
                {
                    switch (channel)
                    {
                        case ChannelCombatStart:
                            if (session.Role == SessionRole.Guest) BeginAsGuest(session, payload);
                            break;
                        case ChannelCombatPending:
                            if (session.Role == SessionRole.Guest) BeginPendingAsGuest(session, r);
                            break;
                        case ChannelCombatSave:
                            if (GuestActive && r.ReadUInt32() == Current.CombatId) Current.ReceiveSave(r);
                            break;
                        case ChannelMenuReady:
                            if (session.Role == SessionRole.Host)
                            {
                                if (r.ReadBoolean()) GuestsInMenu.Add(sender); else GuestsInMenu.Remove(sender);
                            }
                            break;
                        case ChannelStream:
                            if (Current != null && !Current.IsHost && r.ReadUInt32() == Current.CombatId) Current.Enqueue(r);
                            break;
                        case ChannelGuestStatus:
                            if (HostActive && r.ReadUInt32() == Current.CombatId)
                            {
                                Current.guestApplied = r.ReadUInt32();
                                Current.guestDesyncs = r.ReadInt32();
                            }
                            break;
                        case ChannelDesync:
                            if (HostActive && r.ReadUInt32() == Current.CombatId)
                            {
                                Current.guestDesyncs = Math.Max(Current.guestDesyncs, 1);
                                Log.Error($"SYNC host: guest {sender} reports DESYNC at seq {r.ReadUInt32()}:\n{r.ReadString()}");
                            }
                            break;
                        case ChannelOrderRequest:
                            if (HostActive && r.ReadUInt32() == Current.CombatId) Current.ReceiveRequest(sender, r);
                            break;
                        case ChannelOrderResult:
                            if (GuestActive && r.ReadUInt32() == Current.CombatId) Current.ReceiveResult(r);
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error($"SYNC: failed to handle channel {channel} message", e);
            }
        }

        // ---- host side ----

        // Prefix of every InvocationMessage.Invoke override: the invocation is executing now.
        public void OnInvoke(InvocationMessage invocation, CombatGameState combat)
        {
            if (IsHost) StreamInvocation(invocation, combat);
            else CheckInvocation(invocation, combat);
        }

        private void StreamInvocation(InvocationMessage invocation, CombatGameState combat)
        {
            if (!streamedInvocations.Add(invocation.InvocationID)) return; // base Invoke reached through an override
            var state = StateText.Render(combat);
            var data = GameSerializer.SerializeMessage(invocation);
            var item = new Item
            {
                Seq = ++seq, Activation = Activation, Kind = ItemKind.Invocation,
                TypeName = invocation.GetType().FullName, Data = data, State = state,
            };
            Send(item);
            if (Main.Settings.LogInvocations)
                Log.Info($"SYNC host: seq {item.Seq} act {item.Activation} {item.TypeName} id={invocation.InvocationID} actor={SyncDiagnostics.ActorOf(invocation)} ({data.Length} bytes)");
        }

        public void OnAITeamMaybeComplete(AITeam team)
        {
            if (!IsHost || !team.IsComplete || !reportedComplete.Add(team.GUID)) return;
            var item = new Item { Seq = ++seq, Activation = Activation, Kind = ItemKind.AIComplete, TeamGuid = team.GUID };
            Send(item);
            if (Main.Settings.LogInvocations)
                Log.Info($"SYNC host: seq {item.Seq} act {item.Activation} AIComplete {team.DisplayName}");
        }

        public void OnActivation()
        {
            if (!IsHost && ViaSave && !saveLoaded) return; // activations replayed while the save loads
            if (IsHost && Reloading) return;
            Activation++;
            reportedComplete.Clear();
            completeRequested = false;
        }

        private void Send(Item item)
        {
            session.SendGame(ChannelStream, Build(w =>
            {
                w.Write(CombatId);
                w.Write(item.Seq);
                w.Write(item.Activation);
                w.Write((byte)item.Kind);
                if (item.Kind == ItemKind.Invocation)
                {
                    w.Write(item.TypeName);
                    w.Write(item.Data.Length);
                    w.Write(item.Data);
                    w.Write(item.State);
                }
                else
                {
                    w.Write(item.TeamGuid);
                }
            }));
        }

        // ---- guest side ----

        private void Enqueue(BinaryReader r)
        {
            var item = new Item { Seq = r.ReadUInt32(), Activation = r.ReadUInt32(), Kind = (ItemKind)r.ReadByte() };
            if (item.Kind == ItemKind.Invocation)
            {
                item.TypeName = r.ReadString();
                item.Data = r.ReadBytes(r.ReadInt32());
                item.State = r.ReadString();
            }
            else
            {
                item.TeamGuid = r.ReadString();
            }
            incoming.Enqueue(item);
        }

        // Called every frame by the controller.
        public void Update()
        {
            if (!IsHost && ViaSave)
            {
                TryLoadSave();
                if (!saveLoadStarted) return; // any combat still showing is the previous one
            }
            var combat = UnityGameInstance.BattleTechGame?.Combat;
            if (combat == null) return;

            if (!begunLogged && combat.TurnDirector != null && combat.TurnDirector.GameHasBegun)
            {
                begunLogged = true;

                Log.Info($"SYNC combat begun: {combat.AllActors.Count} actors; {Status}");
            }

            if (!missionOverLogged && combat.TurnDirector != null && combat.TurnDirector.IsMissionOver)
            {
                missionOverLogged = true;
                Log.Info($"SYNC mission over: {Status}");
            }

            if (IsHost)
            {
                if (Reloading) return;
                if (SaveDue()) { SendSave(); return; }
                while (requests.Count > 0 && IsIdle(combat))
                    ProcessRequest(combat, requests.Dequeue());
                if (IsIdle(combat)) selectionOrders.RecoverRejected();
                return;
            }
            while (incoming.Count > 0 && CanApply(combat, incoming.Peek()))
                Apply(combat, incoming.Dequeue());

            if (IsIdle(combat)) selectionOrders.RecoverRejected();

            if (UnityEngine.Time.realtimeSinceStartup >= nextStatusTime)
            {
                nextStatusTime = UnityEngine.Time.realtimeSinceStartup + 2f;
                session.SendGame(ChannelGuestStatus, Build(w => { w.Write(CombatId); w.Write(seq); w.Write(Desyncs); }));
            }
        }

        // Combat running, no order playing out, nothing queued in the game's InvocationHandler.
        private static bool IsIdle(CombatGameState combat)
        {
            if (combat.TurnDirector == null || !combat.TurnDirector.GameHasBegun) return false;
            if (combat.StackManager.IsAnyOrderActive) return false;
            var queued = Traverse.Create(combat.InvocationHandler).Field("queuedInvocationMessages").GetValue<System.Collections.IList>();
            return queued == null || queued.Count == 0;
        }

        private bool CanApply(CombatGameState combat, Item item)
        {
            if (ViaSave && !saveLoaded) return false;
            if (!IsIdle(combat)) return false;
            if (item.Activation > Activation) return false;
            if (item.Activation < Activation)
            {
                ReportDesync(item.Seq, $"turn structure diverged: item is for activation {item.Activation}, guest is at {Activation}");
                // Apply anyway so the stream keeps moving; the state check will show the damage.
            }
            return true;
        }

        // Test of the desync detector itself (-coop-inject-desync <seq>): corrupt guest state on purpose.
        public static uint InjectDesyncAt;

        private void Apply(CombatGameState combat, Item item)
        {
            seq = item.Seq;
            if (InjectDesyncAt != 0 && item.Seq == InjectDesyncAt)
            {
                var victim = combat.AllActors.OfType<Mech>().Where(m => !m.IsDead).OrderBy(m => m.GUID, StringComparer.Ordinal).FirstOrDefault();
                if (victim != null)
                {
                    Traverse.Create(victim).Property("_heat").SetValue(victim.CurrentHeat + 7);
                    Log.Info($"SYNC guest: TEST - injected +7 heat on {victim.GUID} before seq {item.Seq}");
                }
            }
            if (item.Kind == ItemKind.AIComplete)
            {
                var team = combat.TurnDirector.TurnActors.OfType<AITeam>().FirstOrDefault(t => t.GUID == item.TeamGuid);
                if (team == null) { ReportDesync(item.Seq, "AIComplete for unknown team " + item.TeamGuid); return; }
                if (combat.TurnDirector.ActiveTurnActor != team)
                    Log.Error($"SYNC guest: seq {item.Seq} AIComplete for {team.DisplayName}, but active turn actor is {combat.TurnDirector.ActiveTurnActor}");
                Traverse.Create(team).Field("isComplete").SetValue(true);
                if (Main.Settings.LogInvocations) Log.Info($"SYNC guest: seq {item.Seq} act {item.Activation} AIComplete {team.DisplayName}");
                return;
            }

            MessageCenterMessage message;
            try
            {
                message = GameSerializer.DeserializeMessage(item.Data);
            }
            catch (Exception e)
            {
                ReportDesync(item.Seq, $"could not deserialize {item.TypeName}: {e.Message}");
                return;
            }
            var invocation = message as InvocationMessage;
            if (invocation == null) { ReportDesync(item.Seq, $"stream item is {message?.GetType().FullName ?? "null"}, expected {item.TypeName}"); return; }

            invocation.IsNetRouted = true;
            invocation.WasNetRouted = true;
            applying = item;
            combat.MessageCenter.PublishMessage(invocation);
            if (applying != null)
            {
                // GenericInvoke queued it instead of running it; the check runs when it executes.
                Log.Info($"SYNC guest: seq {item.Seq} queued by the game before executing");
            }
            if (Main.Settings.LogInvocations) Log.Info($"SYNC guest: seq {item.Seq} act {item.Activation} {item.TypeName} id={invocation.InvocationID} actor={SyncDiagnostics.ActorOf(invocation)}");
            if (seq % 10 == 0) Log.Info($"SYNC applied={seq} desyncs={Desyncs}");
        }

        private void CheckInvocation(InvocationMessage invocation, CombatGameState combat)
        {
            if (applying == null) return; // an Invoke not started by us (dropped earlier) or a base call
            var item = applying;
            applying = null;
            var state = StateText.Render(combat);
            var differences = StateText.Differences(item.State, state);
            var busyHost = item.State.Split('\n').Count(l => l.EndsWith(" busy", StringComparison.Ordinal));
            var busyGuest = state.Split('\n').Count(l => l.EndsWith(" busy", StringComparison.Ordinal));
            if (busyHost + busyGuest > 0)
                Log.Info($"SYNC diag: seq {item.Seq} check skipped units still acting (host {busyHost}, guest {busyGuest})");
            if (differences.Count > 0)
            {
                ReportDesync(item.Seq, $"state differs before {item.TypeName} id={invocation.InvocationID}:\n{StateText.Diff(differences)}");
                if (Desyncs == 1) Log.Info($"SYNC diag: full state at first desync (seq {item.Seq})\n--- host ---\n{item.State}--- guest ---\n{state}");
            }
        }

        // Guest: the loaded combat starts playing when its briefing's "Begin mission" is pressed; from
        // here it continues at the host's activation.
        public void OnBeginPlaying()
        {
            if (IsHost)
            {
                if (!Reloading) return;
                Reloading = false;
                Activation = saveActivation;
                Log.Info($"SYNC host: resumed from its own save at seq {seq}, activation {Activation}");
                return;
            }
            if (!ViaSave || saveLoaded || !saveLoadStarted) return;
            Activation = saveActivation;
            saveLoaded = true;
            Log.Info($"SYNC guest: loaded mission resumed at seq {seq}, activation {Activation}");
        }

        // Host, when the briefing's start button comes on (MissionStart): the mission is loaded and
        // nothing has happened yet, so this is where the guest's save is taken.
        public bool OnBriefingReady()
        {
            if (!IsHost || !ViaSave || saveSent) return false;
            if (briefingReadyAt < 0) briefingReadyAt = UnityEngine.Time.realtimeSinceStartup;
            return true;
        }

        // The reload waits until the loading curtain from the launch is gone: a load started while
        // it is still closing never gets past ProcessLoadedFile (seen 2026-10-25).
        private float briefingReadyAt = -1;

        private bool SaveDue() =>
            ViaSave && !saveSent && briefingReadyAt >= 0 && UnityEngine.Time.realtimeSinceStartup > briefingReadyAt + 1f
            && !LoadingCurtain.IsVisible;

        // Saves loaded with this flag take the game's own "combat restart" path: the campaign state
        // comes from the save, and the saved contract is launched fresh instead of resumed
        // (GameInstance.CreateCombatFromSave). The game's "mission out of date" popup that path
        // normally shows is skipped for these (CombatRestartPopupPatch).
        public static readonly HashSet<BattleTech.Save.GameInstanceSave> RestartSaves = new HashSet<BattleTech.Save.GameInstanceSave>();

        private static void MarkRestart(BattleTech.Save.GameInstanceSave save)
        {
            Traverse.Create(save).Field("flaggedForRestart").SetValue((bool?)true);
            RestartSaves.Add(save);
        }

        // Host, at the briefing: nothing has been streamed yet (seq 0, activation 0).
        private void SendSave()
        {
            saveSent = true;
            var started = System.Diagnostics.Stopwatch.StartNew();
            byte[] bytes;
            try
            {
                bytes = SaveTransfer.Serialize(new BattleTech.Save.GameInstanceSave(UnityGameInstance.BattleTechGame, BattleTech.Save.SaveGameStructure.SaveReason.MANUAL));
            }
            catch (Exception e)
            {
                Log.Error("SYNC host: could not build the combat save for the guest", e);
                ShowNotice("Could not send this mission to the guest; see BattletechCoop.log");
                return;
            }
            var atSeq = seq;
            var atActivation = Activation;
            saveActivation = atActivation;
            var pins = LancePins.Collect(UnityGameInstance.BattleTechGame?.Combat?.ActiveContract);
            Log.Info($"SYNC host: this launch picked {LancePins.Describe(pins)}");
            session.SendGame(ChannelCombatSave, Build(w =>
            {
                w.Write(CombatId);
                w.Write(atSeq);
                w.Write(atActivation);
                w.Write(bytes.Length);
                w.Write(bytes);
                LancePins.Write(w, pins);
            }));
            Log.Info($"SYNC host: combat save sent ({bytes.Length} bytes, built in {started.ElapsedMilliseconds} ms) at seq {atSeq}, activation {atActivation}");

            // No reload here. Before 0.0.13 the guest resumed a mid-battle save, and a resumed battle
            // differs from the live one (component order: a critical hit picked a different
            // component, 2026-10-17), so the host reloaded the same save too. Now the guest launches
            // the mission fresh from the save (a combat restart), as the host launched it from the
            // same campaign state and contract a moment ago; the state checks confirm they match.
            // (A reload from the briefing also stalls in ProcessLoadedFile, 2026-10-25.)
        }

        // ---- guest orders (shared control) ----

        // Guest: may the AI think for this team now? Only when it's this machine's turn to think,
        // the guest has replayed everything and is idle, and no request is in flight.
        public bool GuestMayThink(AITeam team)
        {
            var combat = UnityGameInstance.BattleTechGame?.Combat;
            return ThinksHere(team) && combat != null && incoming.Count == 0 && IsIdle(combat) && outstandingRequest == 0;
        }

        // Guest, from GenericInvoke: an order created on this machine (HUD, or the guest-side AI in
        // the Shared test). It is not run here; the host runs it and streams it back.
        public void RequestOrder(InvocationMessage invocation, SelectionState selection = null)
        {
            if (outstandingRequest != 0 || selectionOrders.Contains(selection))
            {
                ShowNotice($"Waiting for the host to confirm your last order; {invocation.GetType().Name} ignored");
                selectionOrders.RejectLocal(selection);
                return;
            }
            var combat = UnityGameInstance.BattleTechGame?.Combat;
            if (combat != null)
            {
                var reason = MovementOrderGuard.Validate(combat, invocation, true);
                if (reason != null)
                {
                    Log.Info("SYNC guest: blocked movement " + MovementOrderGuard.Describe(combat, invocation));
                    ShowNotice(reason);
                    selectionOrders.RejectLocal(selection);
                    return;
                }
            }
            byte[] data;
            try
            {
                data = GameSerializer.SerializeMessage(invocation);
            }
            catch (Exception e)
            {
                Log.Error($"SYNC guest: could not serialize {invocation.GetType().Name}", e);
                selectionOrders.RejectLocal(selection);
                return;
            }
            outstandingRequest = ++nextRequestId;
            selectionOrders.Track(PendingSelectionOrders.InvocationId(session.LocalIndex, outstandingRequest), selection);
            session.SendGame(ChannelOrderRequest, Build(w =>
            {
                w.Write(CombatId);
                w.Write(outstandingRequest);
                w.Write(seq);
                w.Write((byte)RequestKind.Invocation);
                w.Write(data.Length);
                w.Write(data);
            }));
            Log.Info($"SYNC guest: request {outstandingRequest} {invocation.GetType().Name} at seq {seq}");
        }

        // Guest, after AITeam.think: a guest-side AI never completes locally (see DESIGN.md).
        public void AfterGuestThink(AITeam team)
        {
            if (!team.IsComplete) return;
            Traverse.Create(team).Field("isComplete").SetValue(false);
            if (completeRequested || outstandingRequest != 0) return;
            completeRequested = true;
            outstandingRequest = ++nextRequestId;
            session.SendGame(ChannelOrderRequest, Build(w =>
            {
                w.Write(CombatId);
                w.Write(outstandingRequest);
                w.Write(seq);
                w.Write((byte)RequestKind.AIComplete);
                w.Write(team.GUID);
                w.Write(Activation);
            }));
            Log.Info($"SYNC guest: request {outstandingRequest} AIComplete {team.DisplayName} at act {Activation}");
        }

        private void ReceiveResult(BinaryReader r)
        {
            var id = r.ReadUInt32();
            var accepted = r.ReadBoolean();
            var reason = r.ReadString();
            if (id != outstandingRequest) return;
            outstandingRequest = 0;
            if (!accepted)
            {
                selectionOrders.Reject(PendingSelectionOrders.InvocationId(session.LocalIndex, id));
                completeRequested = false;
                ShowNotice("Order not accepted: " + reason);
            }
        }

        // Host: a request from a guest. Handled in Update when the game is idle.
        private void ReceiveRequest(byte sender, BinaryReader r)
        {
            var request = new Request { Sender = sender, Id = r.ReadUInt32(), GuestSeq = r.ReadUInt32(), Kind = (RequestKind)r.ReadByte() };
            if (request.Kind == RequestKind.Invocation)
            {
                request.Data = r.ReadBytes(r.ReadInt32());
            }
            else
            {
                request.TeamGuid = r.ReadString();
                request.Activation = r.ReadUInt32();
            }
            requests.Enqueue(request);
        }

        private void ProcessRequest(CombatGameState combat, Request request)
        {
            var reason = ValidateRequest(combat, request);
            if (reason == null)
            {
                try
                {
                    if (request.Kind == RequestKind.Invocation) reason = RunGuestOrder(combat, request);
                    else CompleteForGuest(combat, request);
                }
                catch (Exception e)
                {
                    Log.Error($"SYNC host: guest {request.Sender} request {request.Id} failed", e);
                    reason = "the host could not run it (" + e.GetType().Name + ")";
                }
            }
            if (reason != null) Log.Info($"SYNC host: rejected guest {request.Sender} request {request.Id}: {reason}");
            session.SendGame(ChannelOrderResult, Build(w =>
            {
                w.Write(CombatId);
                w.Write(request.Id);
                w.Write(reason == null);
                w.Write(reason ?? "");
            }), request.Sender);
        }

        private string ValidateRequest(CombatGameState combat, Request request)
        {
            if (combat.TurnDirector.IsMissionOver) return "the mission is over";
            if (request.GuestSeq != seq) return "the battle moved on before your order arrived; give it again";
            var active = combat.TurnDirector.ActiveTurnActor as Team;
            if (request.Kind == RequestKind.AIComplete)
            {
                var team = active as AITeam;
                if (team == null || team.GUID != request.TeamGuid) return "that team is not acting";
                if (request.Activation != Activation || ThinksHere(team)) return "not the guest's activation";
                return null;
            }
            if (active == null || active.GUID != Player1TeamGuid) return "it is not the player's turn";
            return null;
        }

        private string RunGuestOrder(CombatGameState combat, Request request)
        {
            var invocation = GameSerializer.DeserializeMessage(request.Data) as InvocationMessage;
            if (invocation == null) throw new InvalidDataException("request did not contain an invocation");
            var reason = MovementOrderGuard.Validate(combat, invocation, true);
            if (MovementOrderGuard.IsMovement(invocation))
                Log.Info($"SYNC host: movement request {request.Id}: {MovementOrderGuard.Describe(combat, invocation)} result={reason ?? "allowed"}");
            if (reason != null) return reason;
            // Namespace by sender and request, so guest HUD selections can correlate their
            // delayed replay even if OrderResult arrives before or after it. Engine-generated
            // IDs alone can collide when two test copies use the same machine prefix.
            // The setter is private on the base class, so write its backing field.
            AccessTools.Field(typeof(InvocationMessage), "<InvocationID>k__BackingField")
                .SetValue(invocation, PendingSelectionOrders.InvocationId(request.Sender, request.Id));
            Log.Info($"SYNC host: accepted guest {request.Sender} request {request.Id} {invocation.GetType().Name} as {invocation.InvocationID}");
            combat.MessageCenter.PublishMessage(invocation);
            return null;
        }

        // Host-local commands and guest requests both pass through GenericInvoke. Guest
        // requests are also checked above so a rejection is returned to their sender.
        public bool AllowHostMovement(InvocationMessage invocation)
        {
            var combat = UnityGameInstance.BattleTechGame?.Combat;
            if (combat == null) return true;
            var reason = MovementOrderGuard.Validate(combat, invocation, false);
            if (reason == null) return true;
            Log.Info("SYNC host: blocked movement " + MovementOrderGuard.Describe(combat, invocation));
            ShowNotice(reason);
            return false;
        }

        public void BindGuestSelection(InvocationMessage invocation, IStackSequence sequence)
        {
            if (selectionOrders.Bind(invocation.InvocationID, sequence))
                Log.Info($"SYNC guest: bound selection to replay {invocation.InvocationID}");
        }

        public void RecoverSelection(SelectionState selection) => selectionOrders.RejectLocal(selection);

        private void CompleteForGuest(CombatGameState combat, Request request)
        {
            var team = (AITeam)combat.TurnDirector.ActiveTurnActor;
            Traverse.Create(team).Field("isComplete").SetValue(true);
            Log.Info($"SYNC host: accepted guest {request.Sender} request {request.Id} AIComplete {team.DisplayName}");
            OnAITeamMaybeComplete(team);
        }

        private void ReportDesync(uint atSeq, string detail)
        {
            Desyncs++;
            Log.Error($"SYNC DESYNC at seq {atSeq}: {detail}");
            session.SendGame(ChannelDesync, Build(w => { w.Write(CombatId); w.Write(atSeq); w.Write(detail.Length > 4000 ? detail.Substring(0, 4000) : detail); }));
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
