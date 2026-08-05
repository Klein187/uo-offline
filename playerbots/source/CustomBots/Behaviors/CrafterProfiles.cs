// =========================================================================
// CrafterProfiles.cs — the per-subtype DATA for the crafter engine.
//
// The crafter system is "one engine, many data-defined subtypes" (see
// DESIGN-PLAN.md §2). CrafterBehavior + CrafterProduction are the engine;
// this file is the data each subtype plugs in:
//   - the work animation + craft sound
//   - whether output takes a maker's mark (Exceptional + "crafted by")
//   - three loot bands (Common / Minor / Rare) — items the bot "makes"
//   - starter props seeded at spawn so a fresh crafter already looks the part
//
// Loot entries are plain `new Item()` factories (NOT reflection): this file
// lives in UOContent alongside every item type, so construction is fully
// compile-checked. A factory takes the bot only because a few items need it
// (sea finds want the bot's Map).
//
// Adding a subtype later = add a CrafterType value + a CrafterProfile here.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Items;

namespace Server.CustomBots
{
    public sealed class CrafterProfile
    {
        public BotClass Type;

        // Humanoid animation index + craft sound id (0 = silent). Humanoid
        // bodies have no true "crafting" anims, so these are the closest fits.
        public int Action;
        public int Sound;

        // Smith/Tailor goods carry UO's real maker's mark on GM output;
        // fisherman output (fish/maps) is never marked.
        public bool Markable;

        // The tool held in-hand for the work look (smith's hammer, fishing
        // pole). Null when the trade has no iconic hand-held tool — tailoring's
        // sewing kit / scissors are pack items, so a tailor's Tool is null and
        // its kit rides in StarterProps instead. Must be a Layer-wearable item.
        public Func<PlayerBot, Item> Tool;

        // The three production bands. Common dominates at every tier; Minor
        // and Rare only come onto the table at higher tiers (see
        // CrafterProduction.BandWeights). A band may be empty — the engine
        // falls back to Common when it rolls an empty band.
        public Func<PlayerBot, Item>[] Common  = Array.Empty<Func<PlayerBot, Item>>();
        public Func<PlayerBot, Item>[] Minor   = Array.Empty<Func<PlayerBot, Item>>();
        public Func<PlayerBot, Item>[] Rare    = Array.Empty<Func<PlayerBot, Item>>();

        // Seeded once at spawn so a freshly-seen crafter already has a few
        // pieces of their trade in their pack.
        public Func<PlayerBot, Item>[] StarterProps = Array.Empty<Func<PlayerBot, Item>>();

        // ---- Materials (the real side of the economy) ----
        //
        // Item types that count as this trade's working stock (iron ingots,
        // cloth/leather, boards). Empty = the trade doesn't consume
        // materials (fisherman). Production CONSUMES stock: no materials,
        // no output — the bot asks around for more instead.
        public Type[] Materials = Array.Empty<Type>();

        // Build a stack of the trade's staple material (used for starter
        // stock, refining a bought raw haul, and vendor restocks).
        public Func<int, Item> MakeMaterial;

        // Materials consumed per finished piece, rolled in [CostMin,CostMax].
        public int CostMin = 6;
        public int CostMax = 16;

        // The RAW gatherer good this trade buys off a hauler (iron ore for
        // the smith, logs for the carpenter). Null = nobody's haul feeds
        // this trade; it restocks from the shop instead.
        public Type RawGood;

        // How the material reads in chat/status ("iron ingots", "cloth").
        public string MaterialNoun = "materials";
    }

    public static class CrafterProfiles
    {
        // Build a stackable item at a random amount within [min,max].
        private static Item Stack(Item item, int min, int max)
        {
            if (item.Stackable)
            {
                item.Amount = Utility.RandomMinMax(min, max);
            }
            return item;
        }

        private static readonly Dictionary<BotClass, CrafterProfile> Profiles = new()
        {
            // ---------------------------------------------------------------
            // SMITH — weapons & armor at the forge. Maker's-markable.
            // Action 9 = one-handed swing (reads as hammering); 0x2A anvil ring.
            // ---------------------------------------------------------------
            [BotClass.Smith] = new CrafterProfile
            {
                Type     = BotClass.Smith,
                Action   = 9,
                Sound    = 0x2A,
                Markable = true,
                Tool     = _ => new SmithHammer(),
                Common = new Func<PlayerBot, Item>[]
                {
                    _ => new Dagger(),
                    _ => new Mace(),
                    _ => new RingmailGloves(),
                    _ => new PlateGorget(),
                },
                Minor = new Func<PlayerBot, Item>[]
                {
                    _ => new Katana(),
                    _ => new Broadsword(),
                    _ => new PlateGloves(),
                },
                Rare = new Func<PlayerBot, Item>[]
                {
                    _ => new Halberd(),
                    _ => new Bardiche(),
                    _ => new PlateChest(),
                },
                StarterProps = new Func<PlayerBot, Item>[]
                {
                    _ => new Dagger(),
                    _ => new RingmailGloves(),
                    // Working stock + a purse to buy the miners' ore with.
                    _ => new IronIngot(Utility.RandomMinMax(40, 90)),
                    _ => new Gold(Utility.RandomMinMax(150, 400)),
                },
                Materials     = new[] { typeof(IronIngot) },
                MakeMaterial  = amt => new IronIngot(amt),
                CostMin       = 8,
                CostMax       = 20,
                RawGood       = typeof(IronOre),
                MaterialNoun  = "iron ingots",
            },

            // ---------------------------------------------------------------
            // TAILOR — cloth & leather goods. Maker's-markable (clothing &
            // leather/studded armor both carry the mark on GM output).
            // Action 9 reads as working cloth; 0x248 scissors snip.
            // ---------------------------------------------------------------
            [BotClass.Tailor] = new CrafterProfile
            {
                Type     = BotClass.Tailor,
                Action   = 9,
                Sound    = 0x248,
                Markable = true,
                Tool     = null, // sewing kit / scissors are pack items (see StarterProps)
                Common = new Func<PlayerBot, Item>[]
                {
                    _ => new Shirt(),
                    _ => new ShortPants(),
                    _ => new LongPants(),
                    _ => new Cap(),
                    _ => new Bandana(),
                },
                Minor = new Func<PlayerBot, Item>[]
                {
                    _ => new FancyShirt(),
                    _ => new FloppyHat(),
                    _ => new LeafChest(),   // leather
                },
                Rare = new Func<PlayerBot, Item>[]
                {
                    _ => new WizardsHat(),
                    _ => new HideChest(),   // studded
                },
                StarterProps = new Func<PlayerBot, Item>[]
                {
                    _ => new Shirt(),
                    _ => new Cap(),
                    _ => new SewingKit(),   // the tailor's trade tools ride in the pack
                    _ => new Scissors(),
                    // Working stock: bolts' worth of cloth plus some cured
                    // leather, and coin for restocking at the shop.
                    _ => new Cloth(Utility.RandomMinMax(40, 80)),
                    _ => new Leather(Utility.RandomMinMax(15, 35)),
                    _ => new Gold(Utility.RandomMinMax(120, 300)),
                },
                Materials     = new[] { typeof(Cloth), typeof(Leather) },
                MakeMaterial  = amt => new Cloth(amt),
                CostMin       = 6,
                CostMax       = 14,
                RawGood       = null, // no gatherer hauls cloth — shop restocks
                MaterialNoun  = "cloth",
            },

            // ---------------------------------------------------------------
            // CARPENTER — staves, furniture and instruments from the
            // lumberjacks' logs. The lumber supply line's in-town buyer.
            // Action 9 reads as working the bench; 0x23D is the saw rasp.
            // Weapons (staves/clubs) take the maker's mark; furniture just
            // ignores the stamp.
            // ---------------------------------------------------------------
            [BotClass.Carpenter] = new CrafterProfile
            {
                Type     = BotClass.Carpenter,
                Action   = 9,
                Sound    = 0x23D,
                Markable = true,
                Tool     = null, // saw / smoothing plane are pack tools (see StarterProps)
                Common = new Func<PlayerBot, Item>[]
                {
                    _ => new Club(),
                    _ => new QuarterStaff(),
                    _ => new GnarledStaff(),
                },
                Minor = new Func<PlayerBot, Item>[]
                {
                    _ => new Stool(),
                    _ => new WoodenChair(),
                    _ => new WoodenBox(),
                },
                Rare = new Func<PlayerBot, Item>[]
                {
                    _ => new Lute(),
                    _ => new LapHarp(),
                },
                StarterProps = new Func<PlayerBot, Item>[]
                {
                    _ => new Saw(),
                    _ => new SmoothingPlane(),
                    _ => new Club(),
                    // Working stock + coin for the lumberjacks' hauls.
                    _ => new Board(Utility.RandomMinMax(40, 90)),
                    _ => new Gold(Utility.RandomMinMax(120, 300)),
                },
                Materials     = new[] { typeof(Board) },
                MakeMaterial  = amt => new Board(amt),
                CostMin       = 8,
                CostMax       = 18,
                RawGood       = typeof(Log),
                MaterialNoun  = "boards",
            },

            // ---------------------------------------------------------------
            // FISHERMAN — fish + rare sea finds. NEVER maker's-marked.
            // Action 12 is UO's real fishing-cast animation (the exact action
            // the Fishing harvest system plays); 0x364 is the water splash.
            // ---------------------------------------------------------------
            [BotClass.Fisherman] = new CrafterProfile
            {
                Type     = BotClass.Fisherman,
                Action   = 12,
                Sound    = 0x364,
                Markable = false,
                Tool     = _ => new FishingPole(),
                Common = new Func<PlayerBot, Item>[]
                {
                    _ => Stack(new Fish(), 1, 3),
                    _ => Stack(new RawFishSteak(), 1, 2),
                },
                Minor = new Func<PlayerBot, Item>[]
                {
                    _ => new BigFish(),
                    _ => Stack(new FishSteak(), 1, 3),
                },
                Rare = new Func<PlayerBot, Item>[]
                {
                    b => new MessageInABottle(b.Map),
                    b => new SOS(b.Map),
                    b => new TreasureMap(Utility.RandomMinMax(1, 4), b.Map),
                    _ => new PrizedFish(),
                    _ => new WondrousFish(),
                    _ => new TrulyRareFish(),
                    _ => new WaterloggedBoots(),
                },
                StarterProps = new Func<PlayerBot, Item>[]
                {
                    _ => Stack(new Fish(), 2, 5),
                },
            },
        };

        // Look up an artisan class's profile. Falls back to Smith so a bot
        // with an unexpected class still behaves rather than throwing.
        public static CrafterProfile For(BotClass cls) =>
            Profiles.TryGetValue(cls, out var p) ? p : Profiles[BotClass.Smith];
    }
}
