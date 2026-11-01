using System.Collections.Generic;
using BattleTech;
using BattleTech.UI;
using Harmony;

namespace BattletechCoop.Sync
{
    // SelectionState.PublishInvocation subscribes only during its synchronous PublishMessage
    // call. Co-op replies arrive later, so explicitly associate the replay's sequence with
    // the originating selection. The game can then perform its normal move -> fire transition.
    internal sealed class PendingSelectionOrders
    {
        private readonly Dictionary<string, SelectionState> pending = new Dictionary<string, SelectionState>();
        private readonly Queue<SelectionState> rejected = new Queue<SelectionState>();

        public static string InvocationId(byte sender, uint request) => $"G{sender}.{request}";
        public bool Contains(SelectionState state) => state != null && pending.ContainsValue(state);
        public void Track(string id, SelectionState state) { if (state != null) pending.Add(id, state); }

        public bool Bind(string id, IStackSequence sequence)
        {
            SelectionState state;
            if (id == null || sequence == null || !pending.TryGetValue(id, out state)) return false;
            pending.Remove(id);
            state.AssignOrders(sequence);
            return true;
        }

        public void Reject(string id)
        {
            SelectionState state;
            if (!pending.TryGetValue(id, out state)) return;
            pending.Remove(id);
            RejectLocal(state);
        }

        public void RejectLocal(SelectionState state)
        {
            // A second click must not tear down the selection waiting for its first reply.
            if (state != null && !Contains(state)) rejected.Enqueue(state);
        }

        public void RecoverRejected()
        {
            // Run next Update, after the original button handler has finished. Never reset
            // another selection if the user has already switched units or backed out.
            while (rejected.Count > 0)
            {
                var state = rejected.Dequeue();
                var hud = (CombatHUD)AccessTools.Property(typeof(SelectionState), "HUD").GetValue(state, null);
                var handler = hud?.SelectionHandler;
                if (handler == null || handler.ActiveState != state || state.Orders != null || Contains(state)) continue;
                var actor = state.SelectedActor;
                handler.DeselectActor(actor);
                handler.TrySelectActor(actor, false);
            }
        }
    }
}
