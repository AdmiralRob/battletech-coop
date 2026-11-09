using BattleTech;
using BattleTech.UI;
using BattletechCoop.Sync;
using Harmony;

namespace BattletechCoop.Patches
{
    [HarmonyPatch(typeof(SelectionState), "PublishInvocation")]
    public static class GuestSelectionOrderPatch
    {
        static bool Prefix(SelectionState __instance, MessageCenterMessage invocation)
        {
            var order = invocation as InvocationMessage;
            if (order == null || order.WasNetRouted) return true;
            if (CombatSync.HostActive && !CombatSync.Current.AllowHostOrder(order))
            {
                CombatSync.Current.RecoverSelection(__instance);
                return false;
            }
            if (!CombatSync.GuestActive) return true;
            CombatSync.Current.RequestOrder(order, __instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(InvocationMessage), "PublishStackSequence")]
    public static class GuestSelectionSequencePatch
    {
        static void Postfix(IStackSequence stackSequence, InvocationMessage triggeringInvocationMessage)
        {
            if (CombatSync.GuestActive && triggeringInvocationMessage.WasNetRouted)
                CombatSync.Current.BindGuestSelection(triggeringInvocationMessage, stackSequence);
        }
    }
}
