// =========================================================================
// DestinationType.cs — Categories of "places of interest" in the world,
// plus per-class weights that drive destination picking.
//
// When a Traveler rolls a new destination, the system iterates all
// destinations and weights each by (DestinationType, BotClass). Different
// classes are drawn to different types. A Mage strongly prefers Moongates
// and Mage Shops; a Crafter strongly prefers Smithies.
//
// Add a new type? Update the enum, add a row to ClassWeights, and start
// referencing it in destinations.json. The weight is `Default` if the
// bot's class isn't explicitly listed for that type.
// =========================================================================

using System.Collections.Generic;

namespace Server.CustomBots
{
    public enum DestinationType
    {
        Bank,
        Tavern,
        VendorSmith,
        VendorMage,
        VendorTailor,
        VendorCarpenter,
        VendorBowyer,
        VendorAlchemist,
        VendorWeaponer,
        VendorProvisioner,
        Healer,
        Library,
        Graveyard,
        Moongate,
        Crossroads,
        Bridge,
        Stables,
        CityCenter,
        Dungeon,
        Inn,
        Forge,   // a forge fixture — smith crafters station here
        Dock,    // a pier fixture — fisherman crafters station here
    }

    public static class DestinationWeights
    {
        // Lookup: type -> {class name -> weight}.
        // Weights > 1 favor; weights < 1 disfavor; the "Default" key
        // applies when the bot's class isn't explicitly listed.
        //
        // Class names match BotClass.ToString() exactly (Warrior, Mage,
        // Fencer, Archer, Tamer, Crafter, Healer, Thief, Bard, Ranger).
        private static readonly Dictionary<DestinationType, Dictionary<string, double>> ClassWeights = new()
        {
            [DestinationType.Bank] = new()
            {
                ["Default"]  = 1.0,
                ["Crafter"]  = 1.5, // crafters bank ingots / lumber
                ["Bard"]     = 0.7, // bards bank less
            },

            [DestinationType.Tavern] = new()
            {
                ["Default"]  = 1.0,
                ["Bard"]     = 5.0, // bards live in taverns
                ["Warrior"]  = 2.0,
                ["Fencer"]   = 1.8,
                ["Ranger"]   = 1.5,
                ["Mage"]     = 0.5,
                ["Crafter"]  = 0.5,
                ["Healer"]   = 0.7,
            },

            [DestinationType.VendorSmith] = new()
            {
                ["Default"]  = 0.5,
                ["Crafter"]  = 5.0,
                ["Warrior"]  = 2.5,
                ["Fencer"]   = 2.0,
                ["Ranger"]   = 1.5,
                ["Archer"]   = 1.5,
            },

            [DestinationType.VendorMage] = new()
            {
                ["Default"]  = 0.5,
                ["Mage"]     = 5.0,
                ["Healer"]   = 1.8,
                ["Tamer"]    = 1.8,
            },

            [DestinationType.VendorTailor] = new()
            {
                ["Default"]  = 0.8,
                ["Crafter"]  = 3.0,
                ["Bard"]     = 2.0,
            },

            [DestinationType.Forge] = new()
            {
                // A forge is a crafter station — smiths belong here, and
                // almost no one else has a reason to stand at a forge.
                ["Default"]  = 0.2,
                ["Crafter"]  = 4.0,
            },

            [DestinationType.Dock] = new()
            {
                // A dock is the fisherman's station. Crafters strongly
                // weighted here (specifically Fishermen, who StationFor at
                // Dock); almost nobody else has reason to stand on a pier.
                ["Default"]  = 0.2,
                ["Crafter"]  = 4.0,
            },

            [DestinationType.VendorCarpenter] = new()
            {
                ["Default"]  = 0.6,
                ["Crafter"]  = 3.0,
            },

            [DestinationType.VendorBowyer] = new()
            {
                ["Default"]  = 0.5,
                ["Archer"]   = 4.0,
                ["Ranger"]   = 3.5,
                ["Crafter"]  = 1.5,
            },

            [DestinationType.VendorAlchemist] = new()
            {
                ["Default"]  = 0.6,
                ["Healer"]   = 3.0,
                ["Mage"]     = 2.0,
                ["Crafter"]  = 1.5,
            },

            [DestinationType.VendorWeaponer] = new()
            {
                // General weapon shops — swords, axes, maces, polearms.
                // Combat classes shop here; crafters less so since they
                // make their own.
                ["Default"]  = 0.5,
                ["Warrior"]  = 4.5,
                ["Fencer"]   = 4.0,
                ["Ranger"]   = 2.0,
                ["Archer"]   = 1.5, // archers prefer Bowyer but browse here too
                ["Thief"]    = 1.5, // daggers, hidden blades
                ["Crafter"]  = 1.0,
            },

            [DestinationType.VendorProvisioner] = new()
            {
                // Provisioners — bandages, torches, food, candles, ropes,
                // general adventuring supplies. Useful to most classes.
                ["Default"]  = 1.5,
                ["Ranger"]   = 3.5, // big restock before wilderness trips
                ["Warrior"]  = 2.5, // bandages and food before dungeons
                ["Fencer"]   = 2.5,
                ["Archer"]   = 2.5,
                ["Tamer"]    = 2.0,
                ["Thief"]    = 2.0, // torches, lockpicks
                ["Healer"]   = 2.5, // bandages
                ["Mage"]     = 1.2,
                ["Crafter"]  = 0.8,
            },

            [DestinationType.Healer] = new()
            {
                ["Default"]  = 0.7,
                ["Healer"]   = 4.0,
            },

            [DestinationType.Library] = new()
            {
                ["Default"]  = 0.6,
                ["Mage"]     = 3.0,
                ["Bard"]     = 1.5,
            },

            [DestinationType.Graveyard] = new()
            {
                ["Default"]  = 0.5,
                ["Healer"]   = 1.8, // last rites
                ["Mage"]     = 1.5, // necromantic interest
                ["Thief"]    = 2.0, // shady haunts
            },

            [DestinationType.Moongate] = new()
            {
                ["Default"]  = 1.5,
                ["Mage"]     = 2.5,
                ["Ranger"]   = 2.0,
                ["Tamer"]    = 1.8,
            },

            [DestinationType.Crossroads] = new()
            {
                ["Default"]  = 1.0,
                ["Ranger"]   = 2.5,
            },

            [DestinationType.Bridge] = new()
            {
                ["Default"]  = 0.8,
                ["Ranger"]   = 1.5,
            },

            [DestinationType.Stables] = new()
            {
                ["Default"]  = 0.7,
                ["Tamer"]    = 5.0,
                ["Ranger"]   = 2.0,
            },

            [DestinationType.CityCenter] = new()
            {
                ["Default"]  = 1.0, // catch-all utility hub
            },

            [DestinationType.Dungeon] = new()
            {
                ["Default"]  = 0.3, // most classes avoid; bards/crafters/healers rarely venture
                ["Warrior"]  = 5.0,
                ["Fencer"]   = 4.0,
                ["Archer"]   = 4.0,
                ["Ranger"]   = 3.5,
                ["Mage"]     = 4.5,
                ["Tamer"]    = 3.0, // tamers bring pets to dungeons
                ["Thief"]    = 2.5, // looting parties
                ["Healer"]   = 1.5, // following an adventurer party
            },

            [DestinationType.Inn] = new()
            {
                // Inns are for resting/sleeping; everyone uses them at
                // some point. Slightly broader appeal than taverns since
                // even non-drinkers need a bed.
                ["Default"]  = 1.5,
                ["Bard"]     = 3.5, // bards entertain at inns too
                ["Ranger"]   = 2.5, // weary from the road
                ["Warrior"]  = 2.0,
                ["Fencer"]   = 1.8,
                ["Archer"]   = 1.8,
                ["Thief"]    = 1.5, // staking out marks
                ["Mage"]     = 1.0,
                ["Crafter"]  = 0.7,
            },
        };

        // Look up a class's weight for a destination type. Falls back to
        // "Default" entry; falls back to 1.0 if neither exists.
        public static double GetWeight(DestinationType type, BotClass cls)
        {
            // Hard exclusion: trader classes never travel to dangerous
            // destinations. A Crafter is a shopkeeper/artisan — it has no
            // business wandering into a dungeon or a graveyard and getting
            // killed. Weight 0 removes it from the roll entirely.
            if (cls == BotClass.Crafter &&
                (type == DestinationType.Dungeon ||
                 type == DestinationType.Graveyard))
            {
                return 0.0;
            }

            if (!ClassWeights.TryGetValue(type, out var classMap))
                return 1.0;

            string className = cls.ToString();
            if (classMap.TryGetValue(className, out var w)) return w;
            if (classMap.TryGetValue("Default", out var def)) return def;
            return 1.0;
        }
    }
}
