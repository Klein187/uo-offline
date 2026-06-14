// =========================================================================
// HpaCommands.cs — Build & inspect the HPA* long-range graph.
//
//   [buildhpa            Build (or rebuild) the whole-Felucca abstract graph.
//                        Reports node/edge counts and build time to the GM
//                        and server console.
//   [hpainfo            Summary: node count, edge count, rough memory.
//   [testroute <dest>   Abstract A* from your location to the named
//                        destination; reports the node-path length and the
//                        first few hops. Proves routing before wiring the
//                        Traveler.
// =========================================================================

using System;
using System.Linq;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class HpaCommands
    {
        public static void Configure()
        {
            CommandSystem.Register("buildhpa",  AccessLevel.Administrator, BuildHpa_OnCommand);
            CommandSystem.Register("hpainfo",   AccessLevel.GameMaster,    HpaInfo_OnCommand);
            CommandSystem.Register("testroute", AccessLevel.GameMaster,    TestRoute_OnCommand);
        }

        [Usage("buildhpa")]
        [Description("Force rebuild the HPA* graph and save to cache (ignores existing cache).")]
        private static void BuildHpa_OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            void Both(string s) { from.SendMessage(s); Console.WriteLine("[hpa] " + s); }

            Both("Rebuilding HPA* graph for Felucca (forced)... may take a minute.");
            var (nodes, edges, ms) = HpaGraph.Build(Map.Felucca);
            bool saved = HpaGraph.SaveToDisk();
            Both($"HPA* built: {nodes:n0} nodes, {edges:n0} edges, in {ms:0} ms " +
                 $"({(saved ? "cached" : "CACHE SAVE FAILED")}).");
            Both("Subsequent boots will load from cache in well under a second.");
        }

        [Usage("hpainfo")]
        [Description("Report HPA* graph size.")]
        private static void HpaInfo_OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (HpaGraph.NodeCount == 0)
            {
                from.SendMessage("HPA* graph empty. Run [buildhpa first.");
                return;
            }
            from.SendMessage($"HPA*: {HpaGraph.NodeCount:n0} nodes, {HpaGraph.EdgeCount:n0} edges.");
        }

        [Usage("testroute <destination name>")]
        [Description("Abstract A* from your location to the named destination.")]
        private static void TestRoute_OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            void Both(string s) { from.SendMessage(s); Console.WriteLine("[hpa] " + s); }

            if (HpaGraph.NodeCount == 0) { Both("Graph empty — [buildhpa first."); return; }

            string name = e.ArgString?.Trim() ?? "";
            if (name.Length == 0) { Both("Usage: [testroute <destination name>"); return; }

            var dest = DestinationCatalog.GetByName(name);
            if (dest == null) { Both($"No destination '{name}'."); return; }

            string startNode = HpaGraph.Nearest(from.Location);
            string goalNode  = HpaGraph.Nearest(dest.Location);
            if (startNode == null || goalNode == null) { Both("No nearby graph nodes."); return; }

            var t = DateTime.UtcNow;
            var path = HpaGraph.FindPath(startNode, goalNode);
            var ms = (DateTime.UtcNow - t).TotalMilliseconds;

            if (path.Count == 0)
            {
                Both($"NO ROUTE from {startNode} to {goalNode}. " +
                     "Clusters may be disconnected (water/mountain barrier).");
                return;
            }

            Both($"Route to '{dest.Name}': {path.Count} hops, found in {ms:0.0} ms.");
            var head = path.Take(4).Select(n =>
            {
                var node = HpaGraph.Get(n);
                return node != null ? $"({node.Location.X},{node.Location.Y})" : n;
            });
            Both("  start: " + string.Join(" -> ", head) + (path.Count > 4 ? " -> ..." : ""));
        }
    }
}
