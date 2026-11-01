using BattleTech;

namespace BattletechCoop.Sync
{
    // Validate new movement orders, never authoritative guest replays. Use the game's own
    // round state: OrderSequence.OnAdded spends movement and OnNewRound restores it. The
    // co-op activation counter identifies a TEAM activation, not an individual unit's budget.
    internal static class MovementOrderGuard
    {
        public static string ActorGuid(InvocationMessage invocation)
        {
            // MechMovementInvocation derives from AbstractActorMovementInvocation (also sprint).
            var move = invocation as AbstractActorMovementInvocation;
            if (move != null) return move.ActorGUID;
            var jump = invocation as MechJumpInvocation;
            if (jump != null) return jump.MechGUID;
            var melee = invocation as MechMeleeInvocation;
            if (melee != null) return melee.MechGUID;
            // DFA is a separate InvocationMessage, not a MechJumpInvocation.
            var dfa = invocation as MechDFAInvocation;
            if (dfa != null) return dfa.SourceGUID;
            return null;
        }

        public static bool IsMovement(InvocationMessage invocation) =>
            invocation is AbstractActorMovementInvocation || invocation is MechJumpInvocation ||
            invocation is MechMeleeInvocation || invocation is MechDFAInvocation;

        public static string Validate(CombatGameState combat, InvocationMessage invocation, bool guestRequest)
        {
            // In particular, AttackInvocation after a walk/jump must pass unchanged. This
            // guard does not spend firing, end an activation, or change any actor state.
            if (!IsMovement(invocation)) return null;
            var guid = ActorGuid(invocation);
            var actor = string.IsNullOrEmpty(guid) ? null : combat.FindActorByGUID(guid);
            if (actor == null) return "the moving unit could not be found";
            if (actor.team == null || actor.team.GUID != CombatSync.Player1TeamGuid)
                return guestRequest ? "that unit is not on the player team" : null;

            if (actor.HasActivatedThisRound || actor.IsCompletingActivation)
                return "that unit has finished its activation";
            if (actor.HasMovedThisRound) return "that unit has already moved this round; fire or finish its turn";
            if (!actor.CanMove) return "that unit cannot move right now";
            if (actor.HasFiredThisRound && !actor.CanMoveAfterShooting)
                return "that unit cannot move after firing";
            // Melee/DFA also spend firing. Ace Pilot permits movement after shooting,
            // not a second attack disguised as a movement order.
            var move = invocation as AbstractActorMovementInvocation;
            var jump = invocation as MechJumpInvocation;
            if (actor.HasFiredThisRound && (invocation is MechMeleeInvocation || invocation is MechDFAInvocation ||
                (move != null && !string.IsNullOrEmpty(move.MeleeTargetGUID)) ||
                (jump != null && jump.DFATarget != null)))
                return "that unit has already fired this round";
            return null;
        }

        public static string Describe(CombatGameState combat, InvocationMessage invocation)
        {
            var guid = ActorGuid(invocation);
            var actor = string.IsNullOrEmpty(guid) ? null : combat.FindActorByGUID(guid);
            var td = combat.TurnDirector;
            return $"{invocation.GetType().Name} actor={guid ?? "?"} round={td.CurrentRound} phase={td.CurrentPhase}" +
                $" interleaved={td.IsInterleaved} moved={actor?.HasMovedThisRound} fired={actor?.HasFiredThisRound}" +
                $" activated={actor?.HasActivatedThisRound} completing={actor?.IsCompletingActivation}";
        }
    }
}
