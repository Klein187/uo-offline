// =========================================================================
// BotMountHelper.cs — Mount bots on horses, ostards, or llamas. Adds
// visual variety and the classic UO "mounted player" feel.
//
// Used by:
//   - PlayerBot constructor to mount on creation
//   - PlayerBot.OnDeath to dismount + delete the mount
//   - PlayerBot.OnAfterDelete to clean up if the bot is removed
//
// Reflection-based mount type selection: if a specific creature class
// isn't in this ModernUO build, we just try the next one in the list.
// =========================================================================

using System;
using Server;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotMountHelper
    {
        // Pool of mount type names tried in random order. Mixed for visual
        // variety in towns. Resolved via reflection so missing types don't
        // fail compile.
        private static readonly string[] _mountTypes = new[]
        {
            // Common horses (multiple variants for hue variety)
            "Server.Mobiles.Horse",
            "Server.Mobiles.Horse",   // double weight — most common mount
            "Server.Mobiles.Horse",
            "Server.Mobiles.Horse",
            "Server.Mobiles.PackHorse",

            // Ostards — exotic but classic
            "Server.Mobiles.ForestOstard",
            "Server.Mobiles.DesertOstard",
            "Server.Mobiles.FrenziedOstard",

            // Llamas — funny, common in UO
            "Server.Mobiles.Llama",
            "Server.Mobiles.PackLlama",
        };

        // Realistic horse coat colors. Most horses stay at their default
        // hue (brown). A minority get a Utility.RandomNeutralHue which
        // produces muted browns, grays, and tans — natural variation
        // without electric greens or jet blacks that read as "magical".
        // -------------------------------------------------------------------
        private static int RollHorseHue()
        {
            // 75% default brown, 25% neutral variation.
            if (Utility.RandomDouble() < 0.75) return 0;
            return Utility.RandomNeutralHue();
        }

        // -------------------------------------------------------------------
        // Try to mount the bot on a randomly-rolled creature. If something
        // goes wrong (mount types unavailable, mount fails to instantiate)
        // we just leave the bot un-mounted. Never throws.
        // -------------------------------------------------------------------
        public static void TryMountRandom(PlayerBot bot)
        {
            if (bot == null || bot.Deleted) return;
            if (bot.Mounted) return; // already mounted

            // Up to 5 tries — each picks a random type, tries to instantiate.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                string typeName = _mountTypes[Utility.Random(_mountTypes.Length)];
                var mount = TryCreateMount(typeName);
                if (mount == null) continue;

                // Apply a hue variation if it's a plain Horse (other mount
                // types have their own natural colors).
                if (typeName.EndsWith("Horse"))
                {
                    int hue = RollHorseHue();
                    if (hue != 0) mount.Hue = hue;
                }

                // Place the mount at the bot's location so it can be mounted
                // (Rider= requires the mount to be in the world on the same map).
                mount.MoveToWorld(bot.Location, bot.Map);

                // Mount up. The Rider property is defined on BaseMount.
                try
                {
                    mount.Rider = bot;
                    return;
                }
                catch (Exception ex)
                {
                    // If mounting failed for any reason, clean up the
                    // mount we created so it doesn't litter the world.
                    Console.WriteLine($"[BotMountHelper] mount failed for {bot.Name}: {ex.Message}");
                    mount.Delete();
                }
            }
        }

        // -------------------------------------------------------------------
        // Dismount + delete the bot's mount. Safe to call when not mounted.
        // -------------------------------------------------------------------
        public static void DismountAndDelete(PlayerBot bot)
        {
            if (bot == null) return;

            var mount = bot.Mount;
            if (mount == null) return;

            try
            {
                // BaseMount is the concrete type for Horse/Llama/Ostard/etc.
                // It implements IMount, but Rider lives on BaseMount itself.
                if (mount is BaseMount bmMount)
                {
                    bmMount.Rider = null;
                    if (!bmMount.Deleted) bmMount.Delete();
                }
                else
                {
                    // IMount but not BaseMount (e.g. ethereal) — fall back
                    // to the interface-level Rider setter.
                    mount.Rider = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotMountHelper] dismount failed for {bot.Name}: {ex.Message}");
            }
        }

        // -------------------------------------------------------------------
        // Reflection-based mount creation. Returns null if the type isn't
        // available in this build or can't be instantiated as a BaseMount.
        // -------------------------------------------------------------------
        private static BaseMount TryCreateMount(string typeName)
        {
            try
            {
                var t = Type.GetType(typeName + ", UOContent")
                     ?? Type.GetType(typeName);
                if (t == null) return null;
                if (!typeof(BaseMount).IsAssignableFrom(t)) return null;
                return Activator.CreateInstance(t) as BaseMount;
            }
            catch
            {
                return null;
            }
        }
    }
}
