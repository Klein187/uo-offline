// =========================================================================
// BotTravelGate.cs — the ephemeral gate pair a bot's Gate Travel opens.
//
// MagicTravel used plain Moongate items with an in-memory 30s delete
// timer. Timers don't survive restarts and world saves don't know the
// gates are ephemeral — so every restart (or save) that caught a pair
// mid-linger orphaned two PERMANENT moongates wherever the caster stood
// (the "random gate in the Britain stables" reports). This subclass
// exists so the shard can tell bot gates apart from everything else:
// on world load every BotTravelGate deletes itself, because by
// definition none should outlive a 30-second window.
//
// The load sweep also removes exact-type plain Moongate strays once —
// the orphans created before this class existed. PublicMoongates (the
// real city gates) derive from Item, not Moongate, and are untouched.
// =========================================================================

using System;
using System.Collections.Generic;
using ModernUO.Serialization;
using Server;
using Server.Items;

namespace Server.CustomBots
{
    [SerializationGenerator(0, false)]
    public partial class BotTravelGate : Moongate
    {
        [Constructible]
        public BotTravelGate() : this(Point3D.Zero, null)
        {
        }

        [Constructible]
        public BotTravelGate(Point3D target, Map map) : base(target, map)
        {
            Dispellable = true;
        }

        // World load: any surviving bot gate is an orphan from a restart
        // that interrupted its linger window — remove it. Also sweep the
        // legacy plain-Moongate orphans (exact type only; subclasses and
        // PublicMoongates untouched).
        public static void Initialize()
        {
            var strays = new List<Item>();
            foreach (var item in World.Items.Values)
            {
                if (item is BotTravelGate ||
                    item.GetType() == typeof(Moongate))
                {
                    strays.Add(item);
                }
            }

            foreach (var s in strays)
            {
                Console.WriteLine(
                    $"[BotTravelGate] removing stray gate at " +
                    $"({s.X},{s.Y},{s.Z}) on {s.Map}");
                s.Delete();
            }

            if (strays.Count > 0)
            {
                Console.WriteLine(
                    $"[BotTravelGate] {strays.Count} orphaned travel gate(s) cleaned up.");
            }
        }
    }
}
