// =========================================================================
// EquipmentTable.cs — Random outfit generation for PlayerBots.
//
// At construction, every bot rolls one of six outfit archetypes:
//   Peasant      25%   - tunic, kilt/skirt, simple hat or no hat
//   Mage         20%   - dyed robe, wizard's hat, staff, sandals
//   Warrior      20%   - plate or chain torso, boots, sword
//   Adventurer   15%   - leather armor, boots, dagger or club
//   Merchant     10%   - fancy shirt, doublet, fancy hat
//   Wanderer     10%   - drab robe, sandals (the v1 default look)
//
// Outfits aren't tied to behavior yet — a warrior-looking bot might
// wander, a mage-looking bot might sit at a bank. That's actually
// realistic; players didn't dress to match what they were doing.
//
// Each archetype is gender-aware where it matters (e.g. FemaleLeather
// vs LeatherChest, Skirt vs Kilt).
//
// To add a new archetype: write a private RollX() method that calls
// Add(bot, new Item(), hue), then weight it in RollOutfit().
// =========================================================================

using System;
using Server;
using Server.Items;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class EquipmentTable
    {
        // -------------------------------------------------------------------
        // Public entry point. Called from PlayerBot's constructor.
        // -------------------------------------------------------------------
        public static void RollOutfit(PlayerBot bot)
        {
            // Weighted pick. The numbers add to 100 for readability.
            int roll = Utility.Random(100);
            if      (roll < 25) RollPeasant(bot);    // 25%
            else if (roll < 45) RollMage(bot);       // 20%
            else if (roll < 65) RollWarrior(bot);    // 20%
            else if (roll < 80) RollAdventurer(bot); // 15%
            else if (roll < 90) RollMerchant(bot);   // 10%
            else                RollWanderer(bot);   // 10%
        }

        // ---------------- Archetypes ----------------

        // Working folk in plain clothing. Most common; the "bank
        // bystander" type. Tunic + pants/skirt, sometimes a hat.
        private static void RollPeasant(PlayerBot bot)
        {
            int neutral = Utility.RandomNeutralHue();

            Add(bot, new Shirt(),  neutral);

            if (bot.Female)
            {
                Add(bot, new Skirt(), Utility.RandomNeutralHue());
            }
            else
            {
                // 50/50 kilt or long pants for variety
                if (Utility.RandomBool())
                    Add(bot, new Kilt(),      Utility.RandomNeutralHue());
                else
                    Add(bot, new LongPants(), Utility.RandomNeutralHue());
            }

            Add(bot, new Sandals(), 0);   // default sandals

            // 30% of peasants wear a hat
            if (Utility.RandomDouble() < 0.30)
            {
                Add(bot, new FloppyHat(), Utility.RandomNeutralHue());
            }
        }

        // Blue robes, wizard's hat, staff. The "mage at the bank" look.
        private static void RollMage(PlayerBot bot)
        {
            // Robes go jewel-tone. RandomBlueHue and RandomDyedHue both
            // look mage-ish; mix for variety.
            int robeHue = Utility.RandomBool()
                ? Utility.RandomBlueHue()
                : Utility.RandomDyedHue();

            Add(bot, new Robe(robeHue),         0);
            Add(bot, new WizardsHat(robeHue),   0);
            Add(bot, new Sandals(),             0);

            // Staff in hand — variety across the three staff types.
            int staffPick = Utility.Random(3);
            BaseStaff staff = staffPick switch
            {
                0 => new GnarledStaff(),
                1 => new BlackStaff(),
                _ => new QuarterStaff()
            };
            Add(bot, staff, 0);
        }

        // Plate or chain torso, plate legs, sword. The "tank" look.
        private static void RollWarrior(PlayerBot bot)
        {
            // Plate vs chain — plate is more visually striking but
            // chain breaks up the silhouette. 60/40 toward plate.
            bool plate = Utility.RandomDouble() < 0.60;

            int armorHue = 0; // un-hued metal — looks normal

            if (plate)
            {
                Add(bot, bot.Female ? new FemalePlateChest() : new PlateChest(), armorHue);
                Add(bot, new PlateLegs(),   armorHue);
                Add(bot, new PlateArms(),   armorHue);
                Add(bot, new PlateGloves(), armorHue);
                Add(bot, new PlateGorget(), armorHue);
            }
            else
            {
                // Chain doesn't have a female-specific chest in our checks,
                // but the regular ChainChest renders fine on either body.
                Add(bot, new ChainChest(), armorHue);
                Add(bot, new ChainLegs(),  armorHue);
                Add(bot, new ChainCoif(),  armorHue);
            }

            Add(bot, new Boots(), 0);

            // Pick a sword
            BaseWeapon weapon = Utility.Random(4) switch
            {
                0 => new Longsword(),
                1 => new Broadsword(),
                2 => new VikingSword(),
                _ => new Katana()
            };
            Add(bot, weapon, 0);
        }

        // Leather armor, boots, dagger or club. "On the move" look —
        // someone hunting in the woods rather than guarding a town.
        private static void RollAdventurer(PlayerBot bot)
        {
            int hue = 0; // un-dyed leather

            Add(bot, bot.Female ? new FemaleLeatherChest() : new LeatherChest(), hue);
            Add(bot, new LeatherLegs(),   hue);
            Add(bot, new LeatherArms(),   hue);
            Add(bot, new LeatherGloves(), hue);
            Add(bot, new LeatherGorget(), hue);
            Add(bot, new Boots(),         Utility.RandomNeutralHue());

            // 30% have a bandana, 70% bare-headed
            if (Utility.RandomDouble() < 0.30)
            {
                Add(bot, new Bandana(), Utility.RandomNeutralHue());
            }

            // Lighter weapon — these aren't main-tank warriors
            BaseWeapon weapon = Utility.Random(3) switch
            {
                0 => new Dagger(),
                1 => new Kryss(),
                _ => new Club()
            };
            Add(bot, weapon, 0);
        }

        // Fancy shirt + doublet, fancy hat, dyed clothing. The
        // "merchant at the bank" or "bard" look.
        private static void RollMerchant(PlayerBot bot)
        {
            int dyed1 = Utility.RandomDyedHue();
            int dyed2 = Utility.RandomDyedHue();

            Add(bot, new FancyShirt(dyed1), 0);
            Add(bot, new Doublet(dyed2),   0);

            if (bot.Female)
            {
                Add(bot, new Skirt(Utility.RandomDyedHue()), 0);
            }
            else
            {
                Add(bot, new LongPants(Utility.RandomDyedHue()), 0);
            }

            Add(bot, new Boots(), 0);

            // 60% wear a fancy hat — merchants like to show off
            if (Utility.RandomDouble() < 0.60)
            {
                Add(bot, new FloppyHat(Utility.RandomDyedHue()), 0);
            }
        }

        // The v1 default look: drab robe + boots. Kept around for
        // variety — some bots are just travelers in plain clothes.
        private static void RollWanderer(PlayerBot bot)
        {
            Add(bot, new Robe(Utility.RandomNeutralHue()), 0);
            Add(bot, new Boots(),                          0);
        }

        // ---------------- Helpers ----------------

        // Set hue if non-zero, then equip on the bot. If a previous slot
        // is occupied (shouldn't happen in our flow but defensive), the
        // item goes to the backpack instead of crashing.
        private static void Add(PlayerBot bot, Item item, int hue)
        {
            if (hue != 0)
            {
                item.Hue = hue;
            }
            bot.AddItem(item);
        }
    }
}
