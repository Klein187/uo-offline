// =========================================================================
// FixDestinationsCommand.cs — [fixdestinations
//
// One-time repair pass for destinations whose stored coordinate points at a
// non-standable tile (wall corner, vendor counter, signpost). For each such
// destination, spiral-searches outward for the nearest standable tile and
// rewrites destinations.json with the corrected X/Y/Z.
//
//   [fixdestinations          DRY RUN — reports what it WOULD change, writes
//                             nothing. Output goes to the server console so
//                             it's copyable.
//   [fixdestinations apply    Writes the corrections to destinations.json
//                             (after backing up the original), then you
//                             [rebuildfields to pick them up.
//
// Search radius is capped: a destination with no standable tile within the
// cap is left alone and reported as UNRESOLVED — those are genuinely bad
// coords (off-map / deep in void) that need manual attention.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class FixDestinationsCommand
    {
        // How far to spiral-search for a standable tile before giving up.
        private const int SearchRadius = 12;

        public static void Configure()
        {
            CommandSystem.Register("fixdestinations", AccessLevel.Administrator, OnCommand);
        }

        [Usage("fixdestinations [apply]")]
        [Description("Repair destination coords that point at non-standable tiles. Dry-run unless 'apply'.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            bool apply = e.ArgString?.Trim().Equals("apply", StringComparison.OrdinalIgnoreCase) == true;
            var map = Map.Felucca;

            void Both(string s) { from.SendMessage(s); Console.WriteLine("[fixdest] " + s); }

            string path = Path.Combine(Core.BaseDirectory, "Data", "Destinations", "destinations.json");
            if (!File.Exists(path)) { Both($"destinations.json not found at {path}"); return; }

            string text = File.ReadAllText(path);
            JsonNode root;
            try { root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
            catch (Exception ex) { Both($"parse error: {ex.Message}"); return; }

            var arr = root?["Destinations"]?.AsArray();
            if (arr == null) { Both("no 'Destinations' array"); return; }

            int examined = 0, fixable = 0, unresolved = 0;

            foreach (var node in arr)
            {
                if (node == null) continue;
                examined++;

                string name = node["Name"]?.GetValue<string>() ?? "(unnamed)";
                int x = node["X"]?.GetValue<int>() ?? 0;
                int y = node["Y"]?.GetValue<int>() ?? 0;
                int z = node["Z"]?.GetValue<int>() ?? 0;

                // Already standable? Leave it.
                if (Walkable.TryFindSeedZ(map, x, y, z, out _))
                    continue;

                // Search for the nearest standable tile.
                if (Walkable.NearestStandable(map, x, y, SearchRadius,
                                              out int nx, out int ny, out int nz))
                {
                    int dist = Math.Max(Math.Abs(nx - x), Math.Abs(ny - y));
                    fixable++;
                    Both($"{name}: ({x},{y},{z}) -> ({nx},{ny},{nz})  [{dist} tiles away]");

                    if (apply)
                    {
                        node["X"] = nx;
                        node["Y"] = ny;
                        node["Z"] = nz;
                    }
                }
                else
                {
                    unresolved++;
                    Both($"{name}: ({x},{y},{z}) UNRESOLVED — no standable tile within {SearchRadius}. Manual fix needed.");
                }
            }

            Both($"--- examined {examined}, fixable {fixable}, unresolved {unresolved} ---");

            if (!apply)
            {
                Both("DRY RUN. Re-run [fixdestinations apply to write these changes.");
                return;
            }

            // Back up the original, then write.
            try
            {
                string backup = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(path, backup, overwrite: false);

                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, root.ToJsonString(opts), Encoding.UTF8);

                Both($"Wrote {fixable} correction(s). Backup: {Path.GetFileName(backup)}");
                Both("Run [rebuildfields then [fieldinfo to confirm.");
            }
            catch (Exception ex)
            {
                Both($"WRITE FAILED: {ex.Message}");
            }
        }
    }
}
