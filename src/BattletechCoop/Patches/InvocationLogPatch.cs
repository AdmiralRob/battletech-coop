using System.Reflection;
using BattleTech;
using BattletechCoop.Sync;
using Harmony;

namespace BattletechCoop.Patches
{
    // GenericInvoke is the single entry point for every invocation (player HUD, AI, and replayed
    // stream items); it also sees every other MessageCenter message. Confirmed in game on
    // 2026-10-16: player and AI actions both pass through here (see docs/DESIGN.md).
    [HarmonyPatch]
    public static class InvocationLogPatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(InvocationHandler), "GenericInvoke");

        static bool Prefix(MessageCenterMessage message)
        {
            var invocation = message as InvocationMessage;
            if (invocation == null) return true;

            // Host reloading the combat save it just sent: the old combat must not change any more.
            if (CombatSync.HostActive && CombatSync.Current.Reloading)
            {
                Log.Info($"SYNC host: dropped {invocation.GetType().Name} while reloading the combat save");
                return false;
            }

            // A guest only executes invocations replayed from the host's stream (WasNetRouted).
            // Anything it creates itself becomes a request to the host (shared control), which
            // runs it and streams it back.
            if (CombatSync.GuestActive && !message.WasNetRouted)
            {
                CombatSync.Current.RequestOrder(invocation);
                return false;
            }

            if (CombatSync.HostActive && !CombatSync.Current.AllowHostMovement(invocation))
                return false;

            if (Main.Settings.LogInvocations && CombatSync.Current == null)
                Log.Info($"GenericInvoke {invocation.GetType().Name} id={invocation.InvocationID} seed={invocation.RandomSeed} netRouted={message.IsNetRouted}/{message.WasNetRouted}");
            return true;
        }
    }
}
