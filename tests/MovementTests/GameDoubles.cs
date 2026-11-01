// Minimal data-only substitutes for running the production guard and routing prefix
// without Unity. These do not emulate animations, networking, or game action execution.
using System;
using System.Collections.Generic;
using System.Reflection;

public class MessageCenterMessage
{
    public bool WasNetRouted, IsNetRouted;
}
namespace BattleTech
{
    public interface IStackSequence { bool IsComplete { get; } }
    public class TestSequence : IStackSequence { public bool Complete; public bool IsComplete => Complete; }
    public class InvocationMessage : MessageCenterMessage { public string InvocationID; public int RandomSeed; }
    public class AbstractActorMovementInvocation : InvocationMessage { public string ActorGUID, MeleeTargetGUID; public MoveType MoveType; }
    public class MechMovementInvocation : AbstractActorMovementInvocation { }
    public enum MoveType { Walking, Sprinting }
    public class MechJumpInvocation : InvocationMessage { public string MechGUID; public object DFATarget; }
    public class MechDFAInvocation : InvocationMessage { public string SourceGUID, TargetGUID; }
    public class MechMeleeInvocation : InvocationMessage { public string MechGUID, TargetGUID; }
    public class AttackInvocation : InvocationMessage { }
    public class ReserveActorInvocation : InvocationMessage { }
    public class MechStandInvocation : InvocationMessage { }
    public class Team { public string GUID; }
    public class AbstractActor
    {
        public string GUID;
        public Team team;
        public bool HasMovedThisRound, HasFiredThisRound, HasActivatedThisRound, IsCompletingActivation;
        public bool CanMove = true, CanMoveAfterShooting;
    }
    public class TurnDirector { public int CurrentRound = 1, CurrentPhase = 3; public bool IsInterleaved = true; }
    public class CombatGameState
    {
        public TurnDirector TurnDirector = new TurnDirector();
        public Dictionary<string, AbstractActor> Actors = new Dictionary<string, AbstractActor>();
        public AbstractActor FindActorByGUID(string guid) { AbstractActor actor; return Actors.TryGetValue(guid, out actor) ? actor : null; }
    }
    public class InvocationHandler { public void GenericInvoke(MessageCenterMessage message) { } }
}
namespace Harmony
{
    public class HarmonyPatch : Attribute { public HarmonyPatch() { } public HarmonyPatch(Type type, string method) { } }
    public static class AccessTools
    {
        public static MethodInfo Method(Type type, string name) => type.GetMethod(name);
        public static PropertyInfo Property(Type type, string name) => type.GetProperty(name);
    }
}
namespace BattleTech.UI
{
    public class SelectionState
    {
        public CombatHUD HUD { get; set; }
        public AbstractActor SelectedActor;
        public IStackSequence Orders;
        public bool IsComplete => Orders != null && Orders.IsComplete;
        public void AssignOrders(IStackSequence sequence) { Orders = sequence; }
    }
    public class CombatHUD { public CombatSelectionHandler SelectionHandler = new CombatSelectionHandler(); }
    public class CombatSelectionHandler
    {
        public SelectionState ActiveState;
        public int Resets;
        public AbstractActor Reselected;
        public void DeselectActor(AbstractActor actor) { Resets++; ActiveState = null; }
        public bool TrySelectActor(AbstractActor actor, bool manual) { Reselected = actor; return true; }
    }
}
namespace BattletechCoop
{
    public static class Log { public static void Info(string message) { } }
    public static class Main { public static Settings Settings = new Settings(); }
    public class Settings { public bool LogInvocations; }
}
namespace BattletechCoop.Sync
{
    // A routing spy: asserts which path the REAL InvocationLogPatch selects.
    public class CombatSync
    {
        public const string Player1TeamGuid = "bf40fd39-ccf9-47c4-94a6-061809681140";
        public static CombatSync Current;
        public static bool HostActive, GuestActive;
        public bool Reloading, Allow = true;
        public int Requests, HostChecks, Recoveries, Bindings;
        public BattleTech.UI.SelectionState RequestedSelection;
        public void RequestOrder(BattleTech.InvocationMessage invocation, BattleTech.UI.SelectionState selection = null) { Requests++; RequestedSelection = selection; }
        public bool AllowHostMovement(BattleTech.InvocationMessage invocation) { HostChecks++; return Allow; }
        public void RecoverSelection(BattleTech.UI.SelectionState state) { Recoveries++; }
        public void BindGuestSelection(BattleTech.InvocationMessage invocation, BattleTech.IStackSequence sequence) { Bindings++; }
    }
}
