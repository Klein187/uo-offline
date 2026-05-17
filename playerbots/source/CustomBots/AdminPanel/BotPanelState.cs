// =========================================================================
// BotPanelState.cs — Static state for the admin panel.
//
// The admin panel ("BotPanel") needs two pieces of state to survive
// across gump close/reopen cycles:
//
//   1. The DRAFT — the spawner-being-built. List of (behavior, count)
//      pairs the admin has stacked up before committing.
//
//   2. The LOG — last few action messages, so the user can see "Decorate
//      placed 55503 items" inside the panel rather than scrolling chat.
//
// Both are keyed by Mobile.Serial so different admins don't share state.
// On the single-player offline shard there's only one of you, but
// keying by Serial is correct practice and costs nothing.
// =========================================================================

using System.Collections.Generic;
using Server;

namespace Server.CustomBots
{
    public static class BotPanelState
    {
        // ---- DRAFT ----

        // A single (behavior, count) entry in a draft.
        public sealed class DraftEntry
        {
            public string BehaviorName { get; set; } = "BankSitter";
            public int    Count        { get; set; } = 0;
        }

        // Per-admin draft list. Each entry will become a separate
        // PlayerBotSpawner when the user commits.
        private static readonly Dictionary<Serial, List<DraftEntry>> _drafts = new();

        public static List<DraftEntry> GetDraft(Mobile m)
        {
            if (!_drafts.TryGetValue(m.Serial, out var list))
            {
                list = new List<DraftEntry>();
                _drafts[m.Serial] = list;
            }
            return list;
        }

        public static void AddDraftEntry(Mobile m, string behaviorName, int count = 0)
        {
            GetDraft(m).Add(new DraftEntry { BehaviorName = behaviorName, Count = count });
        }

        public static void RemoveDraftEntry(Mobile m, int index)
        {
            var list = GetDraft(m);
            if (index >= 0 && index < list.Count)
            {
                list.RemoveAt(index);
            }
        }

        public static void ClearDraft(Mobile m)
        {
            if (_drafts.TryGetValue(m.Serial, out var list))
            {
                list.Clear();
            }
        }

        // ---- LOG ----

        // Ring buffer of recent action messages. Display in the panel
        // shows newest at the bottom.
        private const int LogMaxLines = 6;

        private static readonly Dictionary<Serial, Queue<string>> _logs = new();

        public static Queue<string> GetLog(Mobile m)
        {
            if (!_logs.TryGetValue(m.Serial, out var q))
            {
                q = new Queue<string>(LogMaxLines);
                _logs[m.Serial] = q;
            }
            return q;
        }

        public static void Log(Mobile m, string message)
        {
            var q = GetLog(m);
            q.Enqueue(message);
            while (q.Count > LogMaxLines)
            {
                q.Dequeue();
            }
        }

        public static void ClearLog(Mobile m)
        {
            if (_logs.TryGetValue(m.Serial, out var q))
            {
                q.Clear();
            }
        }
    }
}
