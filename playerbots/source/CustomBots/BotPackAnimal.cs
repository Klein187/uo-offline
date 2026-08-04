// =========================================================================
// BotPackAnimal.cs — the laden llama walking beside a miner.
//
// A gatherer clocking in for a shift brings a pack beast (miners favor
// llamas, lumberjacks horses). The yield goes into the BEAST's pack, not
// the bot's — twice the haul, and the classic UO picture: a miner
// swinging at the face while a pack llama stands loaded beside them,
// then the pair walking the load back to town.
//
// Ephemeral by definition (same lesson as BotTravelGate: any timer- or
// lifecycle-cleaned world object must be a DISTINCT CLASS that
// self-cleans on load, or restarts orphan it forever):
//   - world load  → every surviving bot pack animal is a stray; delete.
//   - OnThink     → throttled ownerless reaper: master deleted or logged
//                   out (internal map) → delete. Death does NOT trigger
//                   it — a dead miner's llama waits by the corpse.
//   - delivery    → BotEconomy stables (deletes) it after the unload.
//   - bot delete  → PlayerBot.OnAfterDelete releases it.
// =========================================================================

using System;
using ModernUO.Serialization;
using Server;
using Server.Mobiles;

namespace Server.CustomBots
{
    [SerializationGenerator(0)]
    public partial class BotPackHorse : PackHorse
    {
        [Constructible]
        public BotPackHorse()
        {
        }

        private DateTime _nextOwnerCheck;

        public override void OnThink()
        {
            base.OnThink();
            BotPackAnimals.ReapIfOrphaned(this, ref _nextOwnerCheck);
        }

        public static void Initialize()
        {
            BotPackAnimals.SweepStrays<BotPackHorse>();
        }
    }

    [SerializationGenerator(0)]
    public partial class BotPackLlama : PackLlama
    {
        [Constructible]
        public BotPackLlama()
        {
        }

        private DateTime _nextOwnerCheck;

        public override void OnThink()
        {
            base.OnThink();
            BotPackAnimals.ReapIfOrphaned(this, ref _nextOwnerCheck);
        }

        public static void Initialize()
        {
            BotPackAnimals.SweepStrays<BotPackLlama>();
        }
    }

    public static class BotPackAnimals
    {
        // Working animals get working names — a real player renamed the
        // beast the day they bought it, and the rename is what makes the
        // spoken pet commands ("Bessie follow me") read right.
        private static readonly string[] BeastNames =
        {
            "Bessie", "Daisy", "Clyde", "Buck", "Maple", "Nutmeg",
            "Biscuit", "Juniper", "Star", "Willow", "Chester", "Rosie",
            "Patches", "Dusty", "Hazel", "Bramble",
        };

        // Spawn (or return) the bot's pack beast, controlled and following.
        // Miners favor llamas — the mining llama is THE era image — and
        // lumberjacks favor horses. The owner gives the real follow order
        // out loud, exactly the command a player typed.
        public static BaseCreature SpawnFor(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
            {
                return null;
            }
            if (bot.PackAnimal is { Deleted: false } existing)
            {
                return existing;
            }

            bool llama = bot.Class == BotClass.Miner
                ? Utility.RandomDouble() < 0.60
                : Utility.RandomDouble() < 0.30;
            BaseCreature beast = llama ? new BotPackLlama() : new BotPackHorse();
            beast.Name = BeastNames[Utility.Random(BeastNames.Length)];

            beast.MoveToWorld(new Point3D(bot.X + 1, bot.Y + 1, bot.Z), bot.Map);
            if (beast.SetControlMaster(bot))
            {
                beast.ControlTarget = bot;
                beast.ControlOrder = OrderType.Follow;
            }

            bot.PackAnimal = beast;
            BotScene.Play((1.2, bot, $"{beast.Name} follow me"));
            Console.WriteLine(
                $"[gather] {bot.Name} brought a pack " +
                $"{(llama ? "llama" : "horse")} for the shift at ({bot.X},{bot.Y})");
            return beast;
        }

        // Unhitch and remove the beast (delivery done, bot deleted...).
        public static void Release(PlayerBot bot)
        {
            if (bot == null)
            {
                return;
            }
            var beast = bot.PackAnimal;
            bot.PackAnimal = null;
            if (beast is { Deleted: false })
            {
                beast.Delete();
            }
        }

        // Throttled ownerless check, shared by both species. A master
        // that's DELETED or stored on the internal map (logged out) never
        // comes back for the beast; a dead master does (ghost → res →
        // corpse run), so death keeps the llama waiting by the body.
        public static void ReapIfOrphaned(BaseCreature beast, ref DateTime nextCheck)
        {
            if (Core.Now < nextCheck)
            {
                return;
            }
            nextCheck = Core.Now + TimeSpan.FromSeconds(10);

            var master = beast.ControlMaster;
            if (master == null || master.Deleted || master.Map == Map.Internal)
            {
                beast.Delete();
            }
        }

        // World load: any surviving bot pack animal is a stray from a
        // restart that caught a shift/haul mid-flight — remove it. The
        // next shift spawns a fresh one.
        public static void SweepStrays<T>() where T : BaseCreature
        {
            var strays = new System.Collections.Generic.List<Mobile>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is T)
                {
                    strays.Add(m);
                }
            }
            foreach (var s in strays)
            {
                s.Delete();
            }
            if (strays.Count > 0)
            {
                Console.WriteLine(
                    $"[BotPackAnimals] {strays.Count} stray {typeof(T).Name}(s) cleaned up.");
            }
        }
    }
}
