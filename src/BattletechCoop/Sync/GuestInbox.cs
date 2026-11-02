using System;
using System.Collections.Generic;

namespace BattletechCoop.Sync
{
    // Guest: everything the host sends about its ship after the full ship (ordinary ship updates
    // on channel 23, company statuses on channel 36) goes through this one queue, and is applied
    // strictly in the order the host sent it. A held item holds everything behind it.
    //
    // Why (PR #3 review): a day's work-queue steps must run against the queue as it was on the
    // host that day. With separate queues, a refit added after a held day was applied first, and
    // the replayed day then gave it progress for a day it didn't exist on the host.
    //
    // - Ship updates need the host's ship showing (ready).
    // - Statuses also wait while the player is editing (MechLab open, unconfirmed training), so
    //   nothing changes underneath them.
    // - force: the player is about to make a change of their own (a refit, readying a 'Mech...).
    //   Everything held is applied first, so the local change lands after the host's earlier
    //   days, as on the host.
    // Unity-free, so the ordering is covered by tests/CompanyLiveTests.
    internal sealed class GuestInbox
    {
        public enum Kind : byte { ShipUpdate, CompanyStatus }

        private readonly Queue<KeyValuePair<Kind, byte[]>> items = new Queue<KeyValuePair<Kind, byte[]>>();
        private bool processing;

        public int Count => items.Count;

        public void Add(Kind kind, byte[] payload) => items.Enqueue(new KeyValuePair<Kind, byte[]>(kind, payload));

        // A full ship supersedes everything sent before it.
        public void Clear() => items.Clear();

        // Applies items from the front while they may be applied; returns how many were.
        public int Process(bool ready, bool statusesMayApply, bool force,
                           Action<byte[]> applyUpdate, Action<byte[]> applyStatus)
        {
            if (processing || !ready) return 0; // applying an item can't start applying the next
            processing = true;
            var applied = 0;
            try
            {
                while (items.Count > 0)
                {
                    var head = items.Peek();
                    if (head.Key == Kind.CompanyStatus && !statusesMayApply && !force) break;
                    items.Dequeue();
                    if (head.Key == Kind.ShipUpdate) applyUpdate(head.Value);
                    else applyStatus(head.Value);
                    applied++;
                }
            }
            finally { processing = false; }
            return applied;
        }
    }
}
