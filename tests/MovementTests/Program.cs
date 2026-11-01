using System;
using System.Reflection;
using BattleTech;
using BattleTech.UI;
using BattletechCoop.Sync;
using BattletechCoop.Patches;

static class Program
{
    static int passed, failed;
    static CombatGameState combat;
    static AbstractActor actor;
    const string Unit = "ebc06b6a-c5a8-4165-95ce-480bcf335570.0";
    static MechMovementInvocation Walk() => new MechMovementInvocation { ActorGUID = Unit };
    static MechJumpInvocation Jump() => new MechJumpInvocation { MechGUID = Unit };
    static MechMeleeInvocation Melee() => new MechMeleeInvocation { MechGUID = Unit, TargetGUID = "enemy" };
    static MechDFAInvocation Dfa() => new MechDFAInvocation { SourceGUID = Unit, TargetGUID = "enemy" };
    static void Check(bool value, string detail) { if (!value) throw new Exception(detail); }
    static string Validate(InvocationMessage order, bool guest = true) => MovementOrderGuard.Validate(combat, order, guest);
    static void Allowed(InvocationMessage order) => Check(Validate(order) == null, "Unexpected rejection: " + Validate(order));
    static void Blocked(InvocationMessage order) => Check(Validate(order) != null, "Extra movement accepted");
    static void Test(string name, Action body)
    {
        combat = new CombatGameState();
        actor = new AbstractActor { GUID = Unit, team = new Team { GUID = CombatSync.Player1TeamGuid } };
        combat.Actors.Add(Unit, actor);
        CombatSync.Current = null;
        CombatSync.HostActive = CombatSync.GuestActive = false;
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static bool Route(InvocationMessage order) => (bool)typeof(InvocationLogPatch)
        .GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { order });

    static int Main()
    {
        Test("recorded seq 47-50: first move, reject two extra moves, allow fire", () => {
            Allowed(Walk());
            actor.HasMovedThisRound = true; // OrderSequence.OnAdded in the real game
            Blocked(Walk()); Blocked(Walk()); Allowed(new AttackInvocation());
            Check(!actor.HasFiredThisRound && !actor.HasActivatedThisRound, "Guard spent firing or ended the turn");
        });
        Test("walk then jump cannot buy a second move", () => { actor.HasMovedThisRound = true; Blocked(Jump()); });
        Test("jump then fire remains legal", () => { Allowed(Jump()); actor.HasMovedThisRound = true; Allowed(new AttackInvocation()); Blocked(Walk()); });
        Test("sprint cannot follow a walk", () => { actor.HasMovedThisRound = true; Blocked(new MechMovementInvocation { ActorGUID = Unit, MoveType = MoveType.Sprinting }); });
        Test("first sprint is allowed", () => Allowed(new MechMovementInvocation { ActorGUID = Unit, MoveType = MoveType.Sprinting }));
        Test("melee cannot buy extra movement after a walk", () => { actor.HasMovedThisRound = true; Blocked(Melee()); });
        Test("first melee uses attacker rather than target", () => Allowed(Melee()));
        Test("base movement invocation is guarded too", () => { actor.HasMovedThisRound = true; Blocked(new AbstractActorMovementInvocation { ActorGUID = Unit }); });
        Test("finished activation cannot move", () => { actor.HasActivatedThisRound = true; Blocked(Walk()); });
        Test("finishing activation cannot move", () => { actor.IsCompletingActivation = true; Blocked(Walk()); });
        Test("unavailable unit cannot move", () => { actor.CanMove = false; Blocked(Walk()); });
        Test("ordinary pilot cannot move after firing", () => { actor.HasFiredThisRound = true; Blocked(Walk()); });
        Test("Ace Pilot can fire then walk once", () => {
            actor.HasFiredThisRound = actor.CanMoveAfterShooting = true;
            Allowed(Walk()); actor.HasMovedThisRound = true; Blocked(Walk());
        });
        Test("Ace Pilot can fire then jump", () => { actor.HasFiredThisRound = actor.CanMoveAfterShooting = true; Allowed(Jump()); });
        Test("Ace Pilot cannot fire then melee", () => { actor.HasFiredThisRound = actor.CanMoveAfterShooting = true; Blocked(Melee()); });
        Test("jump invocation with a DFA target also spends firing", () => { actor.HasFiredThisRound = actor.CanMoveAfterShooting = true; var jump = Jump(); jump.DFATarget = new object(); Blocked(jump); });
        Test("DFA is recognized as movement", () => Check(MovementOrderGuard.IsMovement(Dfa()), "DFA bypasses movement guard"));
        Test("DFA actor is SourceGUID, not target", () => Check(MovementOrderGuard.ActorGuid(Dfa()) == Unit, "Wrong DFA actor"));
        Test("first DFA remains allowed", () => Allowed(Dfa()));
        Test("recorded seq 19-20: jump then DFA cannot buy extra movement", () => {
            Allowed(Jump()); actor.HasMovedThisRound = true;
            Blocked(Dfa()); Allowed(new AttackInvocation());
            Check(!actor.HasFiredThisRound && !actor.HasActivatedThisRound, "Rejection spent firing or activation");
        });
        Test("Ace Pilot cannot fire then a separate DFA", () => {
            actor.HasFiredThisRound = actor.CanMoveAfterShooting = true; Blocked(Dfa());
        });
        Test("finished activation cannot DFA", () => { actor.HasActivatedThisRound = true; Blocked(Dfa()); });
        Test("DFA with missing source is rejected", () => Blocked(new MechDFAInvocation { TargetGUID = Unit }));
        Test("DFA with unknown source is rejected", () => Blocked(new MechDFAInvocation { SourceGUID = "missing", TargetGUID = Unit }));
        Test("guest cannot DFA with enemy unit", () => { actor.team.GUID = "enemy"; Blocked(Dfa()); });
        Test("host player's second move cannot be DFA", () => {
            actor.HasMovedThisRound = true; Check(Validate(Dfa(), false) != null, "Host second move accepted");
        });
        Test("host enemy AI DFA is unaffected", () => {
            actor.team.GUID = "enemy"; actor.HasMovedThisRound = true;
            Check(Validate(Dfa(), false) == null, "Enemy AI blocked");
        });
        Test("authoritative guest DFA replay bypasses guard", () => {
            CombatSync.GuestActive = true; CombatSync.Current = new CombatSync { Allow = false };
            var order = Dfa(); order.WasNetRouted = true;
            Check(Route(order) && CombatSync.Current.Requests == 0 && CombatSync.Current.HostChecks == 0, "DFA replay blocked");
        });
        Test("melee target embedded in a walk still spends firing", () => { actor.HasFiredThisRound = actor.CanMoveAfterShooting = true; var move = Walk(); move.MeleeTargetGUID = "enemy"; Blocked(move); });
        Test("another unit retains its movement", () => {
            actor.HasMovedThisRound = true;
            combat.Actors.Add("second", new AbstractActor { GUID = "second", team = actor.team });
            Allowed(new MechMovementInvocation { ActorGUID = "second" });
        });
        Test("phase change does not grant a second movement", () => { actor.HasMovedThisRound = true; combat.TurnDirector.CurrentPhase--; Blocked(Walk()); });
        Test("game resets movement for the next round", () => {
            actor.HasMovedThisRound = true; Blocked(Walk()); combat.TurnDirector.CurrentRound++;
            actor.HasMovedThisRound = false; Allowed(Walk());
        });
        Test("free movement follows the game's round reset", () => {
            combat.TurnDirector.IsInterleaved = false; Allowed(Walk()); actor.HasMovedThisRound = true;
            Blocked(Walk()); actor.HasMovedThisRound = false; combat.TurnDirector.CurrentRound++; Allowed(Walk());
        });
        Test("entering combat respects movement already spent", () => { actor.HasMovedThisRound = true; combat.TurnDirector.IsInterleaved = true; Blocked(Walk()); });
        Test("stand and reserve remain outside movement guard", () => { actor.HasMovedThisRound = true; Allowed(new MechStandInvocation()); Allowed(new ReserveActorInvocation()); });
        Test("unknown unit is rejected", () => Blocked(new MechMovementInvocation { ActorGUID = "missing" }));
        Test("missing movement GUID is rejected", () => Blocked(new MechMovementInvocation()));
        Test("guest cannot move an enemy", () => { actor.team.GUID = "enemy"; Blocked(Walk()); });
        Test("host enemy AI is outside this player guard", () => { actor.team.GUID = "enemy"; actor.HasMovedThisRound = true; Check(Validate(Walk(), false) == null, "Enemy AI was blocked"); });
        Test("host player is guarded as well as guest", () => { actor.HasMovedThisRound = true; Check(Validate(Walk(), false) != null, "Host extra move accepted"); });
        Test("guest local order is requested and not locally executed", () => {
            CombatSync.GuestActive = true; CombatSync.Current = new CombatSync();
            Check(!Route(Walk()) && CombatSync.Current.Requests == 1, "Local guest routing failed");
        });
        Test("authoritative guest replay bypasses request and movement guard", () => {
            CombatSync.GuestActive = true; CombatSync.Current = new CombatSync { Allow = false };
            var order = Walk(); order.WasNetRouted = true;
            Check(Route(order) && CombatSync.Current.Requests == 0 && CombatSync.Current.HostChecks == 0, "Replay was blocked or re-requested");
        });
        Test("host rejection prevents original GenericInvoke", () => {
            CombatSync.HostActive = true; CombatSync.Current = new CombatSync { Allow = false };
            Check(!Route(Walk()) && CombatSync.Current.HostChecks == 1, "Host rejection bypassed");
        });
        Test("host accepted order reaches original GenericInvoke", () => {
            CombatSync.HostActive = true; CombatSync.Current = new CombatSync();
            Check(Route(Walk()) && CombatSync.Current.HostChecks == 1, "Allowed host order blocked");
        });
        Test("offline invocation unaffected", () => Check(Route(Walk()), "Offline order was blocked"));
        Test("delayed replay supplies the orders needed for UI completion", () => {
            var pending = new PendingSelectionOrders(); var state = new SelectionState(); var sequence = new TestSequence();
            pending.Track("G1.9", state);
            Check(!state.IsComplete && state.Orders == null, "Completed before replay");
            Check(pending.Bind("G1.9", sequence) && state.Orders == sequence, "Replay did not attach");
            Check(!state.IsComplete, "Completed before animation"); sequence.Complete = true;
            Check(state.IsComplete, "Selection cannot advance to fire");
            Check(!pending.Bind("G1.9", new TestSequence()), "Duplicate replay overwrote orders");
        });
        Test("host and other guest replay cannot attach to our selection", () => {
            var pending = new PendingSelectionOrders(); var state = new SelectionState(); pending.Track("G1.9", state);
            Check(!pending.Bind("P1TDG.9", new TestSequence()) && !pending.Bind("G2.9", new TestSequence()), "Wrong sender bound");
            Check(state.Orders == null && pending.Contains(state), "Pending selection lost");
        });
        Test("request IDs remain distinct across players and rejected requests", () => {
            Check(PendingSelectionOrders.InvocationId(1, 10) == "G1.10", "Wrong request correlation");
            Check(PendingSelectionOrders.InvocationId(2, 10) != PendingSelectionOrders.InvocationId(1, 10), "Players collide");
            Check(PendingSelectionOrders.InvocationId(1, 12) != PendingSelectionOrders.InvocationId(1, 10), "Request gap collides");
        });
        Test("two pending selections bind by ID even with replies out of order", () => {
            var pending = new PendingSelectionOrders(); var first = new SelectionState(); var second = new SelectionState();
            var s1 = new TestSequence(); var s2 = new TestSequence(); pending.Track("G1.1", first); pending.Track("G1.2", second);
            pending.Bind("G1.2", s2); pending.Bind("G1.1", s1);
            Check(first.Orders == s1 && second.Orders == s2, "Wrong sequence attached");
        });
        Test("rejected order recovers current selection without spending an action", () => {
            var pending = new PendingSelectionOrders(); var state = new SelectionState { HUD = new CombatHUD(), SelectedActor = actor };
            state.HUD.SelectionHandler.ActiveState = state; pending.Track("G1.3", state); pending.Reject("G1.3");
            Check(state.HUD.SelectionHandler.Resets == 0, "Reset happened within original button callback");
            pending.RecoverRejected();
            Check(state.HUD.SelectionHandler.Resets == 1 && state.HUD.SelectionHandler.Reselected == actor, "Selection not recovered");
            Check(state.Orders == null && !actor.HasMovedThisRound && !actor.HasFiredThisRound, "Rejection spent an action");
            Check(!pending.Bind("G1.3", new TestSequence()), "Rejected selection retained");
        });
        Test("duplicate click cannot tear down a pending selection", () => {
            var pending = new PendingSelectionOrders(); var state = new SelectionState { HUD = new CombatHUD() };
            state.HUD.SelectionHandler.ActiveState = state; pending.Track("G1.1", state); pending.RejectLocal(state); pending.RecoverRejected();
            Check(pending.Contains(state) && state.HUD.SelectionHandler.Resets == 0, "Pending action discarded");
        });
        Test("late rejection does not reset another selected unit", () => {
            var pending = new PendingSelectionOrders(); var hud = new CombatHUD();
            var old = new SelectionState { HUD = hud }; var current = new SelectionState { HUD = hud };
            hud.SelectionHandler.ActiveState = current; pending.RejectLocal(old); pending.RecoverRejected();
            Check(hud.SelectionHandler.ActiveState == current && hud.SelectionHandler.Resets == 0, "New selection reset");
        });
        Test("rejection recovery does not discard an attached sequence", () => {
            var pending = new PendingSelectionOrders(); var state = new SelectionState { HUD = new CombatHUD(), Orders = new TestSequence() };
            state.HUD.SelectionHandler.ActiveState = state; pending.RejectLocal(state); pending.RecoverRejected();
            Check(state.HUD.SelectionHandler.Resets == 0, "Running sequence discarded");
        });
        Test("guest selection patch captures its originating state", () => {
            CombatSync.GuestActive = true; CombatSync.Current = new CombatSync(); var state = new SelectionState();
            var result = (bool)typeof(GuestSelectionOrderPatch).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { state, Walk() });
            Check(!result && CombatSync.Current.RequestedSelection == state && CombatSync.Current.Requests == 1, "Selection was not captured");
        });
        Test("host selection rejection schedules recovery", () => {
            CombatSync.HostActive = true; CombatSync.Current = new CombatSync { Allow = false };
            var result = (bool)typeof(GuestSelectionOrderPatch).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { new SelectionState(), Walk() });
            Check(!result && CombatSync.Current.Recoveries == 1, "Host rejection left selection stranded");
        });
        Test("sequence patch binds only guest replay", () => {
            CombatSync.GuestActive = true; CombatSync.Current = new CombatSync(); var order = Walk();
            var method = typeof(GuestSelectionSequencePatch).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            method.Invoke(null, new object[] { new TestSequence(), order });
            Check(CombatSync.Current.Bindings == 0, "Local sequence bound"); order.WasNetRouted = true;
            method.Invoke(null, new object[] { new TestSequence(), order });
            Check(CombatSync.Current.Bindings == 1, "Replay sequence not bound");
        });
        Console.WriteLine($"{passed}/{passed + failed} passed");
        return failed;
    }
}
