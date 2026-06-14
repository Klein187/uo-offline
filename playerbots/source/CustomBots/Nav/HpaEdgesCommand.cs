// =========================================================================
// HpaEdgesCommand.cs — [hpaedges
//
// Inspects the HPA graph connectivity near the GM's location to find WHY
// it's fragmented. Reports, for the nearest node: its coordinates, how many
// edges it has, and the coords of each neighbor. If a border node has only
// intra-cluster neighbors (all within ~96 tiles on one side) and no neighbor
// across the cluster boundary, the inter-cluster crossing link is missing —
// that's the fragmentation cause.
// =========================================================================

using System;
using System.Linq;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class HpaEdgesCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("hpaedges", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("hpaedges")]
        [Description("Inspect HPA edges of the node nearest the GM.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            void Both(string s) { from.SendMessage(s); Console.WriteLine("[hpa] " + s); }

            string nn = HpaGraph.Nearest(from.Location);
            if (nn == null) { Both("No nearest node."); return; }
            var node = HpaGraph.Get(nn);
            Both($"Nearest node {nn} @ ({node.Location.X},{node.Location.Y}) has {node.Edges.Count} edges:");

            int cs = HpaGraph.ClusterSize;
            int myCx = node.Location.X / cs, myCy = node.Location.Y / cs;
            int sameCluster = 0, crossCluster = 0;

            foreach (var (nbName, cost) in node.Edges.OrderBy(kv => kv.Value).Take(12))
            {
                var nb = HpaGraph.Get(nbName);
                if (nb == null) continue;
                int nbCx = nb.Location.X / cs, nbCy = nb.Location.Y / cs;
                bool cross = (nbCx != myCx || nbCy != myCy);
                if (cross) crossCluster++; else sameCluster++;
                Both($"   -> ({nb.Location.X},{nb.Location.Y}) cost {cost:0.0} {(cross ? "[CROSS-CLUSTER]" : "[same]")}");
            }
            Both($"Summary: {sameCluster} same-cluster, {crossCluster} cross-cluster edges shown.");
            Both(crossCluster == 0
                ? "=> NO cross-cluster edges! Inter-cluster links are missing — that's the fragmentation."
                : "=> Has cross-cluster edges; fragmentation is elsewhere.");
        }
    }
}
