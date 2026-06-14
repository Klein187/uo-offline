// =========================================================================
// HpaComponentsCommand.cs — [hpacomponents
//
// Flood-fills the abstract HPA graph and reports how many CONNECTED
// COMPONENTS exist and the sizes of the largest few. This is the decisive
// diagnostic for "FindPath returns 0 for everything": if the graph is one
// big component, routing should work and the bug is elsewhere; if it's
// shattered into thousands of tiny components, intra-cluster linking failed
// and clusters don't connect to each other.
// =========================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class HpaComponentsCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("hpacomponents", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("hpacomponents")]
        [Description("Report connected components of the HPA graph.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            void Both(string s) { from.SendMessage(s); Console.WriteLine("[hpa] " + s); }

            if (HpaGraph.NodeCount == 0) { Both("Graph empty."); return; }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sizes = new List<int>();

            foreach (var name in HpaGraph.AllNames.ToList())
            {
                if (seen.Contains(name)) continue;
                int sz = 0;
                var stack = new Stack<string>();
                stack.Push(name); seen.Add(name);
                while (stack.Count > 0)
                {
                    var c = stack.Pop(); sz++;
                    var node = HpaGraph.Get(c);
                    if (node == null) continue;
                    foreach (var nb in node.Edges.Keys)
                        if (seen.Add(nb)) stack.Push(nb);
                }
                sizes.Add(sz);
            }

            sizes.Sort(); sizes.Reverse();
            int show = Math.Min(10, sizes.Count);
            Both($"HPA components: {sizes.Count} total. " +
                 $"Largest {show}: {string.Join(", ", sizes.Take(show))}");
            Both($"Nodes in largest component: {sizes[0]} of {HpaGraph.NodeCount} " +
                 $"({100.0 * sizes[0] / HpaGraph.NodeCount:0.0}%)");
        }
    }
}
