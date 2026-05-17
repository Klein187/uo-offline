// =========================================================================
// ChatLibrary.cs — Loads ambient chat lines from text files.
//
// On server startup, scans Distribution/Data/PlayerBotChat/ for .txt
// files. Each file becomes one named category (the filename without
// extension). Lines in the file become the pool; '#' comments and blank
// lines are skipped.
//
// To add new lines or categories, just edit the .txt files and restart
// the server. No recompile needed.
//
// Behaviors call ChatLibrary.PickRandom(categoryNames) to get a random
// line from the union of those categories. Returns null if no lines are
// available (e.g. all categories empty or unknown).
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using Server;

namespace Server.CustomBots
{
    public static class ChatLibrary
    {
        // Resolved at runtime so we don't bake an absolute path at compile.
        // Mirrors how ModernUO's own code locates Data files (see e.g.
        // SkillsInfo.cs: Path.Combine(Core.BaseDirectory, "Data/...")).
        private static readonly string ChatDir =
            Path.Combine(Core.BaseDirectory, "Data", "PlayerBotChat");

        // category name (lowercased) -> lines
        private static readonly Dictionary<string, List<string>> _categories =
            new(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;

        // -------------------------------------------------------------------
        // Configure — discovered and called by ModernUO at startup.
        // -------------------------------------------------------------------
        public static void Configure()
        {
            Load();
        }

        // Reload at runtime (e.g. an admin command later). Idempotent.
        public static void Load()
        {
            _categories.Clear();

            if (!Directory.Exists(ChatDir))
            {
                Console.WriteLine($"ChatLibrary: directory not found at {ChatDir}. Bots will be silent.");
                _loaded = true;
                return;
            }

            int totalLines = 0;
            int totalCats = 0;

            foreach (var path in Directory.EnumerateFiles(ChatDir, "*.txt"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var lines = ParseFile(path);
                if (lines.Count == 0)
                {
                    continue;
                }
                _categories[name] = lines;
                totalLines += lines.Count;
                totalCats++;
            }

            _loaded = true;
            Console.WriteLine($"ChatLibrary: loaded {totalLines} lines across {totalCats} categories from {ChatDir}");
        }

        private static List<string> ParseFile(string path)
        {
            var result = new List<string>();
            try
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }
                    if (line.StartsWith('#'))
                    {
                        continue;
                    }
                    result.Add(line);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ChatLibrary: failed to read {path}: {ex.Message}");
            }
            return result;
        }

        // -------------------------------------------------------------------
        // PickRandom — pick a line from the union of the given categories.
        // Categories that don't exist or are empty are skipped.
        // -------------------------------------------------------------------
        public static string PickRandom(params string[] categories)
        {
            if (!_loaded || categories == null || categories.Length == 0)
            {
                return null;
            }

            // Pick a weighted-by-category choice: each category gets equal
            // chance, then a random line within. This avoids huge files
            // dominating just because they have more lines.
            //
            // If you want size-weighted instead, flatten all lines and pick
            // from the combined list. Either is reasonable; equal-weight
            // keeps small categories (like bank_actions) audible alongside
            // larger ones (like wts).

            // Filter to categories that actually have content.
            List<List<string>> nonEmpty = null;
            for (int i = 0; i < categories.Length; i++)
            {
                if (_categories.TryGetValue(categories[i], out var list) && list.Count > 0)
                {
                    nonEmpty ??= new List<List<string>>();
                    nonEmpty.Add(list);
                }
            }

            if (nonEmpty == null)
            {
                return null;
            }

            var pool = nonEmpty[Utility.Random(nonEmpty.Count)];
            return pool[Utility.Random(pool.Count)];
        }

        public static int CategoryCount(string category)
        {
            return _categories.TryGetValue(category, out var list) ? list.Count : 0;
        }

        public static IEnumerable<string> KnownCategories => _categories.Keys;
    }
}
