// =========================================================================
// CrafterBehavior.cs — a crafter bot working at its craft station.
//
// This is the ENGINE; per-subtype data lives in CrafterProfiles and the
// "crafting system" (two-roll production + maker's mark) lives in
// CrafterProduction. The behavior itself does three things:
//
//   1. Anchor — hold the station tile, drift back if shoved (like BankSitter).
//   2. Loop   — on a cadence, play the subtype's work animation + sound and
//               run one production attempt (most cycles make nothing).
//   3. Sell-off — periodically items the bot made just "sell to the shop" and
//               vanish, so the pack stays bounded and believable.
//
// The bot's CrafterType (Smith / Tailor / Fisherman) selects the profile;
// destination routing to the station is handled by the Traveler + the
// crafter-handoff in TravelerBehavior — this behavior runs once stationed.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Engines.Harvest;
using Server.Items;
using Server.Targeting;

namespace Server.CustomBots
{
    public class CrafterBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Crafter";

        public override string GetStatusLine(PlayerBot bot)
        {
            string doing = bot.Class switch
            {
                BotClass.Smith     => "smithing at the forge",
                BotClass.Tailor    => "sewing at the tailor shop",
                BotClass.Carpenter => "working the carpenter bench",
                BotClass.Fisherman => "fishing off the dock",
                _                  => "working the shop",
            };

            var profile = CrafterProfiles.For(bot.Class);
            if (profile.Materials.Length == 0)
            {
                return doing;
            }
            int stock = CrafterStock.Count(bot, profile);
            return stock < profile.CostMax
                ? $"{doing} — out of {profile.MaterialNoun}"
                : $"{doing} ({stock} {profile.MaterialNoun})";
        }

        // Shoved-off-tile tolerance, same as BankSitter.
        public int HomeRadius { get; set; } = 1;

        public Point3D Home { get; private set; }
        public Map HomeMap   { get; private set; }

        // Work-cycle cadence: animation plays and a production roll happens
        // each cycle (production rarely succeeds — see CrafterProduction).
        private DateTime _nextCraftAt = DateTime.MinValue;
        private static readonly TimeSpan CraftMin = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan CraftMax = TimeSpan.FromSeconds(14);

        // Sell-off cadence — made items periodically vanish ("sold").
        private DateTime _nextSellAt = DateTime.MinValue;
        private static readonly TimeSpan SellMin = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan SellMax = TimeSpan.FromSeconds(120);

        // Dry spell — the shop is out of materials. The bot stops producing,
        // asks around for stock, and after a while restocks over the counter
        // if no gatherer's haul has come through (and the purse allows).
        private DateTime? _drySince;
        private DateTime _nextNeedLineAt = DateTime.MinValue;
        private static readonly TimeSpan DryRestockAfter = TimeSpan.FromMinutes(3);
        private const int RestockUnitPrice = 2; // gp per unit over the counter

        // Hard cap on tracked made-items so a pack never balloons even before
        // the sell-off timer fires.
        private const int PackCap = 12;

        // Items this bot has produced (newest last). Transient — not
        // serialized; after a reload the bot simply starts tracking fresh.
        private readonly List<Item> _made = new();

        // Direction -> (dx, dy) for the eight facings, used to scan for water.
        private static readonly (Direction dir, int dx, int dy)[] Facings =
        {
            (Direction.North, 0, -1),
            (Direction.Right, 1, -1),
            (Direction.East,  1,  0),
            (Direction.Down,  1,  1),
            (Direction.South, 0,  1),
            (Direction.Left, -1,  1),
            (Direction.West, -1,  0),
            (Direction.Up,   -1, -1),
        };

        public CrafterBehavior()
        {
            ChatCategories = CraftChat;

            // Moderate chatter — busier than a wanderer, calmer than a bank
            // crowd; a crafter is focused on the work.
            ChatChance      = 0.18;
            MinChatCooldown = TimeSpan.FromSeconds(20);
            MaxChatCooldown = TimeSpan.FromSeconds(50);
        }

        // Per-class ambient chatter — a smith talks like a smith, a tailor
        // like a tailor. The shared craft_talk pool carries the handful of
        // any-trade lines; each class file carries the trade's own patter.
        private static readonly string[] FisherChat    = { "fishing", "small_talk" };
        private static readonly string[] SmithChat     = { "smith_talk", "craft_talk" };
        private static readonly string[] TailorChat    = { "tailor_talk", "craft_talk" };
        private static readonly string[] CarpenterChat = { "carpenter_talk", "craft_talk" };
        private static readonly string[] CraftChat     = { "craft_talk" };

        // A crafter's late-night mutter is about the SHOP, not dungeon runs
        // — the generic night_talk pool belongs to adventurers.
        protected override string NightTalkCategory => "craft_night";

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            Home    = bot.Location;
            HomeMap = bot.Map;

            // Per-class chatter. Fishermen walk themselves to the water's edge
            // each tick (see FishermanWork) rather than anchoring to a Home.
            ChatCategories = bot.Class switch
            {
                BotClass.Fisherman => FisherChat,
                BotClass.Smith     => SmithChat,
                BotClass.Tailor    => TailorChat,
                BotClass.Carpenter => CarpenterChat,
                _                  => CraftChat,
            };

            // A smith works AT the anvil — face it. (Without this the
            // hammer swings at whatever direction the walk-in happened to
            // end on, sometimes with the anvil at the smith's back.)
            if (bot.Class == BotClass.Smith)
            {
                FaceNearestAnvil(bot);
            }

            ScheduleNextCraft();
            ScheduleNextSell();
        }

        // Anvils are statics 0x0FAF/0x0FB0; forges are 0x0FB1 plus the
        // large multi-tile forge range. Face the nearest one within a few
        // tiles of the station; keep the current facing if none is found.
        private static void FaceNearestAnvil(PlayerBot bot)
        {
            var map = bot.Map;
            if (map == null || map == Map.Internal)
            {
                return;
            }

            Point3D? best = null;
            int bestDist = int.MaxValue;
            for (int dx = -3; dx <= 3; dx++)
            {
                for (int dy = -3; dy <= 3; dy++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }
                    int x = bot.X + dx, y = bot.Y + dy;
                    foreach (var st in map.Tiles.GetStaticTiles(x, y))
                    {
                        int id = st.ID & TileData.MaxItemValue;
                        bool workFace = id is 0x0FAF or 0x0FB0 or 0x0FB1 ||
                                        (id >= 0x197A && id <= 0x19A9);
                        if (!workFace)
                        {
                            continue;
                        }
                        int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            best = new Point3D(x, y, st.Z);
                        }
                    }
                }
            }

            if (best != null)
            {
                var d = bot.GetDirectionTo(best.Value);
                if (bot.Direction != d)
                {
                    bot.Direction = d;
                }
            }
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            // Timed destination visit — return to traveling when up.
            if (CheckVisitExpired(bot)) return;

            TrySpeak(bot);

            // Periodically "sell" made goods so the pack stays bounded. Runs
            // regardless of whether the bot is settled on its tile this tick.
            if (Core.Now >= _nextSellAt)
            {
                SellOff(bot);
                ScheduleNextSell();
            }

            // Fishermen walk to the water's edge and cast only when open water
            // is directly adjacent — so the line never crosses the dock.
            if (bot.Class == BotClass.Fisherman)
            {
                FishermanWork(bot);
                return;
            }

            // Drift back to the station tile if shoved.
            var dx = bot.Location.X - Home.X;
            var dy = bot.Location.Y - Home.Y;
            bool atHome = (dx == 0 && dy == 0);
            if (!atHome && dx * dx + dy * dy > HomeRadius * HomeRadius)
            {
                var d = bot.GetDirectionTo(Home);
                if (bot.Direction != d) bot.Direction = d;
                bot.Move(d);
                return;  // moving this tick; work on a tick we're settled
            }

            // Settled at the station — work on cadence. With materials in
            // stock, that's the animation + a production attempt; dry, the
            // hammer stays down and the bot hunts for stock instead.
            if (Core.Now >= _nextCraftAt)
            {
                var profile = CrafterProfiles.For(bot.Class);
                if (profile.Materials.Length > 0 &&
                    CrafterStock.Count(bot, profile) < profile.CostMax)
                {
                    DrySpell(bot, profile);
                }
                else
                {
                    _drySince = null;
                    WorkCycle(bot);
                }
                ScheduleNextCraft();
            }
        }

        // -------------------------------------------------------------------
        // DrySpell — no materials to work. Ask around for stock (era-real
        // "wtb iron ingots" lines), and if no hauler shows up for a while,
        // restock over the counter with real gold. A broke, dry crafter
        // just waits — the next sale or delivery gets the shop moving again.
        // -------------------------------------------------------------------
        private void DrySpell(PlayerBot bot, CrafterProfile profile)
        {
            _drySince ??= Core.Now;

            if (Core.Now >= _nextNeedLineAt && IsPlayerNearby(bot))
            {
                var line = BotScene.Pick("craft_need", "{mat}", profile.MaterialNoun);
                if (!string.IsNullOrEmpty(line))
                {
                    bot.Say(line);
                }
                _nextNeedLineAt = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(45, 100));
            }

            if (Core.Now - _drySince.Value < DryRestockAfter)
            {
                return;
            }

            int units = Utility.RandomMinMax(40, 80);
            int price = units * RestockUnitPrice;
            if (!CrafterStock.SpendGold(bot, price))
            {
                return; // purse can't cover it — keep waiting for a haul
            }

            CrafterStock.Add(bot, profile, units);
            _drySince = null;
            Console.WriteLine(
                $"[crafter] {bot.Name} restocked {units} {profile.MaterialNoun} " +
                $"over the counter for {price}gp");
        }

        public override void OnDetached(PlayerBot bot)
        {
            base.OnDetached(bot);
            // Stop tracking made items; they remain in the pack and the
            // bot's next behavior owns them. Avoids dangling references.
            _made.Clear();
        }

        // -------------------------------------------------------------------
        // Fisherman loop: walk to the water's edge, then cast only when OPEN
        // water (water beside the pier, not under it) is directly adjacent —
        // so the line goes out to sea, never down through the dock boards.
        // -------------------------------------------------------------------
        private void FishermanWork(PlayerBot bot)
        {
            var water = NearestOpenWater(bot, out int steps);
            if (water == null)
            {
                return; // no reachable open water nearby — just stand
            }

            var dir = bot.GetDirectionTo(water.Value);
            if (bot.Direction != dir)
            {
                bot.Direction = dir;
            }

            if (steps > 1)
            {
                // Not at the edge yet — take a step toward the water.
                bot.Move(dir);
                return;
            }

            // At the edge, facing open water — cast on cadence.
            if (Core.Now >= _nextCraftAt)
            {
                TryRealFish(bot);
                ScheduleNextCraft();
            }
        }

        // -------------------------------------------------------------------
        // One work cycle for Smith/Tailor: play the "making" animation + sound,
        // then run the two-roll production. The animation plays every cycle;
        // an item appears only on the rare cycle the rolls succeed.
        // -------------------------------------------------------------------
        private void WorkCycle(PlayerBot bot)
        {
            var profile = CrafterProfiles.For(bot.Class);

            // Face the work, then animate. Animate signature:
            //   Animate(action, frameCount, repeatCount, forward, repeat, delay)
            try
            {
                bot.Animate(profile.Action, 5, 1, true, false, 0);
            }
            catch
            {
                // Some bodies reject some actions — animation is cosmetic,
                // never let it break the tick.
            }

            if (profile.Sound > 0)
            {
                try { bot.PlaySound(profile.Sound); } catch { }
            }

            // Production roll. Most cycles return null.
            var made = CrafterProduction.TryProduce(bot, profile);
            if (made != null)
            {
                Track(made);

                // A GM's exceptional piece is worth announcing — the
                // maker's-mark moment made visible. (Quality is only ever
                // stamped Exceptional by the GM maker's mark, so this fires
                // exactly on those.)
                bool exceptional = made switch
                {
                    BaseWeapon w   => w.Quality == WeaponQuality.Exceptional,
                    BaseArmor a    => a.Quality == ArmorQuality.Exceptional,
                    BaseClothing c => c.Quality == ClothingQuality.Exceptional,
                    _              => false,
                };
                if (exceptional && Utility.RandomDouble() < 0.60)
                {
                    var line = ChatLibrary.PickRandom("craft_masterwork");
                    if (!string.IsNullOrEmpty(line))
                    {
                        bot.Say(line);
                    }
                }
            }
        }

        // Cast a real fishing line at the water the bot is facing, using UO's
        // harvest system. It plays the genuine cast + splash + sound, rolls
        // the bot's Fishing skill, and drops actual catches (fish, boots,
        // SOS, message-in-a-bottle, ...) into the pack. If a harvest is
        // already running the system's own concurrency lock no-ops this.
        private static void TryRealFish(PlayerBot bot)
        {
            if (bot.FindItemOnLayer(Layer.TwoHanded) is not FishingPole pole)
            {
                return; // not holding the pole (shouldn't happen — equipped at spawn)
            }

            var target = FindWaterTargetInFront(bot);
            if (target == null)
            {
                return;
            }

            try
            {
                Fishing.System.StartHarvesting(bot, pole, target);
            }
            catch
            {
                // Never let a harvest hiccup break the behavior tick.
            }
        }

        // Is (x,y) water? Checks BOTH the land tile (open sea/rivers) and any
        // static tiles (dockside shallow water is often static water items,
        // not wet land).
        private static bool IsWet(Map map, int x, int y)
        {
            var landId = map.Tiles.GetLandTile(x, y).ID;
            if ((TileData.LandTable[landId].Flags & TileFlag.Wet) != 0)
            {
                return true;
            }

            foreach (var st in map.Tiles.GetStaticTiles(x, y))
            {
                if ((TileData.ItemTable[st.ID & TileData.MaxItemValue].Flags & TileFlag.Wet) != 0)
                {
                    return true;
                }
            }
            return false;
        }

        // Does (x,y) have a standable surface static — i.e. a dock plank/floor?
        // This is how we tell the pier APART from the water it's built over,
        // WITHOUT depending on the bot's exact Z (CanFit at a guessed Z was
        // unreliable and made the bot fish through the boards).
        private static bool HasStandableStatic(Map map, int x, int y)
        {
            foreach (var st in map.Tiles.GetStaticTiles(x, y))
            {
                var flags = TileData.ItemTable[st.ID & TileData.MaxItemValue].Flags;
                if ((flags & TileFlag.Surface) != 0 && (flags & TileFlag.Impassable) == 0)
                {
                    return true;
                }
            }
            return false;
        }

        // OPEN water = wet AND NOT covered by a standable plank. This is the
        // water beside the pier (where you cast), not the water under the dock
        // (wet, but it has a plank on top, so it's excluded).
        private static bool IsOpenWater(Map map, int x, int y) =>
            IsWet(map, x, y) && !HasStandableStatic(map, x, y);

        // Nearest open-water tile to the bot, within a wide radius. `steps` is
        // the Chebyshev (king-move) distance to it, so the caller can tell
        // "adjacent" (cast) from "still a few tiles off" (walk closer). Null if
        // no open water is in range.
        private static Point3D? NearestOpenWater(PlayerBot bot, out int steps)
        {
            steps = int.MaxValue;
            var map = bot.Map;
            if (map == null || map == Map.Internal)
            {
                return null;
            }

            const int scan = 14;
            Point3D? best = null;
            int bestSq = int.MaxValue;

            for (int dx = -scan; dx <= scan; dx++)
            {
                for (int dy = -scan; dy <= scan; dy++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }
                    int x = bot.X + dx, y = bot.Y + dy;
                    if (!IsOpenWater(map, x, y))
                    {
                        continue;
                    }
                    int sq = dx * dx + dy * dy;
                    if (sq < bestSq)
                    {
                        bestSq = sq;
                        best = new Point3D(x, y, bot.Z);
                        steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    }
                }
            }
            return best;
        }

        // Build the harvest target for the open water directly in front of the
        // bot (it casts only when adjacent, so this looks 1-2 tiles out).
        // Returns the correct target TYPE for the fishing system: a LandTarget
        // for open sea, a StaticTarget for dockside static water. Null if none.
        private static object FindWaterTargetInFront(PlayerBot bot)
        {
            var map = bot.Map;
            if (map == null || map == Map.Internal)
            {
                return null;
            }

            var facing = (Direction)((int)bot.Direction & 0x7);
            int dx = 0, dy = 0;
            foreach (var (d, fx, fy) in Facings)
            {
                if (d == facing)
                {
                    dx = fx;
                    dy = fy;
                    break;
                }
            }

            for (int dist = 1; dist <= 2; dist++)
            {
                int x = bot.X + dx * dist, y = bot.Y + dy * dist;
                if (!IsOpenWater(map, x, y))
                {
                    continue;
                }

                // Open sea — wet land tile -> LandTarget.
                var landId = map.Tiles.GetLandTile(x, y).ID;
                if ((TileData.LandTable[landId].Flags & TileFlag.Wet) != 0)
                {
                    return new LandTarget(new Point3D(x, y, map.GetAverageZ(x, y)), map);
                }

                // Dockside static water -> StaticTarget.
                foreach (var st in map.Tiles.GetStaticTiles(x, y))
                {
                    if ((TileData.ItemTable[st.ID & TileData.MaxItemValue].Flags & TileFlag.Wet) != 0)
                    {
                        return new StaticTarget(new Point3D(x, y, st.Z), st.ID);
                    }
                }
            }
            return null;
        }

        // Hand one finished piece to a buyer — the purchase scenes in
        // BotEconomy call this so a "sold!" actually moves a real item into
        // the buyer's pack. Returns null when there's nothing on the shelf.
        public Item TakeMadeItem()
        {
            while (_made.Count > 0)
            {
                var item = _made[^1];
                _made.RemoveAt(_made.Count - 1);
                if (item is { Deleted: false })
                {
                    return item;
                }
            }
            return null;
        }

        // Record a produced item and enforce the hard pack cap by selling the
        // oldest overflow immediately.
        private void Track(Item item)
        {
            _made.Add(item);
            while (_made.Count > PackCap)
            {
                Vanish(_made[0]);
                _made.RemoveAt(0);
            }
        }

        // "Sell" a few goods so the pack stays bounded, as if the artisan
        // offloaded them to a shop.
        //   - Smith/Tailor: faked production tracks its output in _made.
        //   - Fisherman: real catches land directly in the pack, so trim the
        //     backpack's oldest non-gold items instead.
        private void SellOff(PlayerBot bot)
        {
            if (bot.Class == BotClass.Fisherman)
            {
                TrimCatch(bot);
                return;
            }

            // Keep a couple of finished pieces ON the shelf — that's what a
            // browsing bot actually buys in the purchase scenes. Only the
            // surplus goes over the counter to the shop.
            if (_made.Count <= 2)
            {
                return;
            }

            int sell = Math.Min(_made.Count - 2, Utility.RandomMinMax(1, 2));
            for (int i = 0; i < sell; i++)
            {
                Vanish(_made[0]);
                _made.RemoveAt(0);
            }

            // The batch goes over the counter to the shop's NPC vendor —
            // turn to face them for the hand-off when one's in range.
            FaceNearestVendor(bot);

            // The shop PAYS for the batch — the crafter's purse grows with
            // its work (and that gold buys the next ore haul).
            int paid = sell * Utility.RandomMinMax(15, 40);
            bot.AddToBackpack(new Gold(paid));
            Console.WriteLine($"[crafter] {bot.Name} sold {sell} finished piece(s) to the shop for {paid}gp");
        }

        // Face the shop's NPC vendor for the counter hand-off, if one is
        // within conversational range.
        private static void FaceNearestVendor(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, 8))
            {
                if (m is Server.Mobiles.BaseVendor vendor && !vendor.Deleted)
                {
                    var d = bot.GetDirectionTo(vendor.Location);
                    if (bot.Direction != d)
                    {
                        bot.Direction = d;
                    }
                    return;
                }
            }
        }

        // Sell off a fisherman's real catch so the pack never fills. Deletes
        // the oldest non-gold items; when the pack is getting full it sells
        // harder. Gold (the bot's purse) is left alone.
        private static void TrimCatch(PlayerBot bot)
        {
            var pack = bot.Backpack;
            if (pack == null)
            {
                return;
            }

            int count = pack.Items.Count;
            if (count == 0)
            {
                return;
            }

            int sell = count > 15 ? count - 12 : Utility.RandomMinMax(1, 2);
            for (int i = 0; i < sell; i++)
            {
                Item oldest = null;
                foreach (var it in pack.Items)
                {
                    if (it != null && !it.Deleted && it is not Gold)
                    {
                        oldest = it;
                        break;
                    }
                }
                if (oldest == null)
                {
                    break;
                }
                oldest.Delete();
            }
        }

        // Delete an item if it still exists; drop the reference either way.
        private static void Vanish(Item item)
        {
            if (item != null && !item.Deleted)
            {
                item.Delete();
            }
        }

        private void ScheduleNextCraft()
        {
            int ms = Utility.RandomMinMax(
                (int)CraftMin.TotalMilliseconds,
                (int)CraftMax.TotalMilliseconds);
            _nextCraftAt = Core.Now + TimeSpan.FromMilliseconds(ms);
        }

        private void ScheduleNextSell()
        {
            int ms = Utility.RandomMinMax(
                (int)SellMin.TotalMilliseconds,
                (int)SellMax.TotalMilliseconds);
            _nextSellAt = Core.Now + TimeSpan.FromMilliseconds(ms);
        }
    }
}
