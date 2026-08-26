// =========================================================================
// BankSitterBehavior.cs — the bank crowd, in its era-true variety.
//
// A T2A bank was never a uniform chatting crowd. It was:
//   - REGULARS  — the talkers: trade chatter, LFG, watching the room
//   - HAWKERS   — the sellers: WTS/WTB spam every few seconds, all day
//   - AFK       — statues; said "afk" once an hour if that
//   - MACROERS  — the immortal bank scene: someone casting curse on
//     himself over and over for Resist, someone blinking in and out of
//     Hiding, someone creeping around at a crawl training Stealth
//
// Each fixture bot rolls a role when it settles in, so every bank gets a
// mix. Traveling bots that stop at a bank roll one too — a warrior
// squeezing in resist macros during a ten-minute bank stop is exactly
// how it worked. All roles keep the shoved-off-home walk-back.
// =========================================================================

using System;
using Server;
using Server.Items;
using Server.Network;
using Server.Spells;
using Server.Spells.First;
using Server.Spells.Fourth;

namespace Server.CustomBots
{
    public class BankSitterBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "BankSitter";

        // What this member of the crowd spends its day doing.
        public enum BankRole
        {
            Regular,      // trade chatter + people-watching (the old behavior)
            Hawker,       // WTS/WTB spam — actively trying to sell
            Afk,          // does nothing at all
            ResistMacro,  // self-casts weak debuffs in a loop
            HidingMacro,  // hides, reappears, hides again
            StealthMacro, // creeps around near home, hidden
        }

        public BankRole Role { get; private set; } = BankRole.Regular;
        private bool _roleRolled;

        public override string GetStatusLine(PlayerBot bot) => Role switch
        {
            BankRole.Hawker       => "hawking wares at the bank",
            BankRole.Afk          => "afk at the bank",
            BankRole.ResistMacro  => "macroing resist spell at the bank",
            BankRole.HidingMacro  => "macroing hiding at the bank",
            BankRole.StealthMacro => "sneaking about the bank",
            _                     => "loitering at the bank",
        };

        // How far the bot is allowed to drift from home before walking
        // back. 1 tile = "I got shoved" tolerance.
        public int HomeRadius { get; set; } = 1;

        public Point3D Home { get; private set; }
        public Map HomeMap   { get; private set; }

        // ---- macro timers ----
        private DateTime _nextMacroAt = DateTime.MinValue;
        private DateTime _revealAt = DateTime.MinValue;   // hiding: when to pop back
        private DateTime _nextSneakStep = DateTime.MinValue;

        // Resist macro cast tracking: we launched a REAL spell and are
        // waiting for the engine to finish the chant and hand us the
        // target cursor (which we aim at ourselves).
        private bool _resistCastPending;
        private DateTime _resistCastStartedAt = DateTime.MinValue;

        // Enough Magery to work the resist macro at all. Below this the
        // role re-rolls — a pure warrior couldn't self-cast in 1999
        // either; he paid a mage friend or stood in a fire field.
        private const double ResistMacroMinMagery = 25.0;

        public BankSitterBehavior()
        {
            // Bank-crowd chatter: everything trade-related plus small talk.
            // bank_actions are short ("bank", "withdraw 1000") so they land
            // hard; WTS/WTB are the meat; LFG appears here because banks
            // were historically where you'd find groups.
            // No "wts" here on purpose. A regular is holding nothing, and
            // a WTS from a bot with an empty pack is the lie this whole
            // system exists to stop telling. Selling belongs to the Hawker
            // role below, which shouts its REAL stock. WTB stays — wanting
            // to buy promises nothing.
            ChatCategories = new[]
            {
                "small_talk",
                "bank_actions",
                "wtb",
                "lfg"
            };

            // Higher chat chance and faster cooldown than the wandering
            // archetype — banks are loud places.
            ChatChance      = 0.25;
            MinChatCooldown = TimeSpan.FromSeconds(15);
            MaxChatCooldown = TimeSpan.FromSeconds(45);
        }

        // How far from the arrival point a bank bot may scatter when it
        // settles in. Bank buildings are large; without this every bot
        // homes on the exact tile it arrived at and the crowd stacks.
        public int ScatterRadius { get; set; } = 5;

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            HomeMap = bot.Map;
            Home    = PickScatteredHome(bot);

            RollRole(bot);

            // Gear progression (IDEAS 4.3): dungeon runs pay. Three
            // survived runs and this bank visit becomes shopping day —
            // tier promotion, fresh skills, visibly better kit. Regulars
            // you keep seeing at the bank get better gear over time.
            if (bot.DungeonRunsSurvived >= 3 &&
                bot.SkillTier < BotSkillTier.Grandmaster)
            {
                bot.DungeonRunsSurvived = 0;
                bot.SkillTier++;
                bot.ReinitializeAsClass(bot.Class);
                bot.EquipFactionShield();
                var line = ChatLibrary.PickRandom("gear_up");
                if (!string.IsNullOrEmpty(line))
                {
                    bot.Say(line);
                }
                Console.WriteLine(
                    $"[gear] {bot.Name} promoted to {bot.SkillTier} (dungeon runs paid off)");
            }
        }

        public override void OnDetached(PlayerBot bot)
        {
            // A macroer leaving the bank must not walk off invisible.
            if (bot != null && !bot.Deleted && bot.Hidden)
            {
                bot.Hidden = false;
            }
            base.OnDetached(bot);
        }

        // -------------------------------------------------------------------
        // Roll what this bot does at the bank, and tune the chat engine to
        // match. Rolled once per attach — a returning visitor may well do
        // something different next trip.
        // -------------------------------------------------------------------
        private void RollRole(PlayerBot bot)
        {
            if (_roleRolled)
            {
                return;
            }
            _roleRolled = true;

            int r = Utility.Random(100);
            Role = r switch
            {
                < 30 => BankRole.Regular,
                < 50 => BankRole.Hawker,
                < 65 => BankRole.Afk,
                < 80 => BankRole.ResistMacro,
                < 90 => BankRole.HidingMacro,
                _    => BankRole.StealthMacro,
            };

            // The resist macro is REAL casting now — no Magery, no macro.
            // A skill-less bot re-rolls into the mundane crowd instead.
            if (Role == BankRole.ResistMacro &&
                bot.Skills[SkillName.Magery].Base < ResistMacroMinMagery)
            {
                int r2 = Utility.Random(100);
                Role = r2 switch
                {
                    < 46 => BankRole.Regular,
                    < 77 => BankRole.Hawker,
                    _    => BankRole.Afk,
                };
            }

            switch (Role)
            {
                case BankRole.Hawker:
                    // A seller talks SHOP, loudly and often — nothing else.
                    // The WTS half comes from BotShop (the real item in the
                    // pack), so only WTB is left in the category list.
                    ChatCategories  = new[] { "wtb" };
                    ChatChance      = 0.55;
                    MinChatCooldown = TimeSpan.FromSeconds(10);
                    MaxChatCooldown = TimeSpan.FromSeconds(25);
                    BotShop.Stock(bot);
                    break;

                case BankRole.Afk:
                    // Statues don't talk. One "afk" on the way out of the
                    // chair is all anyone ever got.
                    ChatChance = 0.0;
                    if (Utility.RandomDouble() < 0.25)
                    {
                        bot.Say("afk");
                    }
                    break;

                case BankRole.ResistMacro:
                case BankRole.HidingMacro:
                case BankRole.StealthMacro:
                    // Macroers were away from the keyboard by definition.
                    ChatChance = 0.0;
                    _nextMacroAt = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(3, 10));
                    break;
            }

            if (Role == BankRole.StealthMacro)
            {
                bot.Hidden = true;
                _nextSneakStep = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(2, 5));
            }
        }

        // Pick a home tile near the arrival point instead of standing
        // exactly where we landed — spreads the bank crowd out across the
        // building. Tries a handful of random nearby tiles, takes the
        // first that the bot can actually stand on; falls back to the
        // arrival tile if none pan out.
        private Point3D PickScatteredHome(PlayerBot bot)
        {
            var arrival = bot.Location;
            var map     = bot.Map;
            if (map == null || map == Map.Internal) return arrival;

            for (int attempt = 0; attempt < 12; attempt++)
            {
                int ox = Utility.RandomMinMax(-ScatterRadius, ScatterRadius);
                int oy = Utility.RandomMinMax(-ScatterRadius, ScatterRadius);
                if (ox == 0 && oy == 0) continue;

                int nx = arrival.X + ox;
                int ny = arrival.Y + oy;
                int nz = arrival.Z;

                // CanFit checks the tile is walkable and the bot's body
                // fits there (no wall, no other blocker).
                if (map.CanFit(nx, ny, nz, 16, false, false))
                {
                    return new Point3D(nx, ny, nz);
                }
            }

            // Nothing walkable found nearby — just stay where we landed.
            return arrival;
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            // If this is a timed destination visit, return to traveling
            // once the visit window is up.
            if (CheckVisitExpired(bot)) return;

            switch (Role)
            {
                case BankRole.Afk:
                    // Nothing. That's the role.
                    break;

                case BankRole.ResistMacro:
                    TickResistMacro(bot);
                    break;

                case BankRole.HidingMacro:
                    TickHidingMacro(bot);
                    break;

                case BankRole.StealthMacro:
                    TickStealthMacro(bot);
                    return; // moves on its own schedule; skip the walk-back

                default:
                    TickTalker(bot);
                    break;
            }

            WalkBackIfShoved(bot);
        }

        // ---- Regular + Hawker: the talking crowd ----
        private void TickTalker(PlayerBot bot)
        {
            // A hawker leads with what it is actually holding. The line is
            // built from the item in its pack, so "WTS GM halberd 5k" means
            // there is a GM halberd in there and 5k buys it. A hawker that
            // sold out (or got looted) restocks and carries on.
            if (Role == BankRole.Hawker)
            {
                var stock = BotShop.StockOf(bot) ?? BotShop.Stock(bot);
                if (stock != null && TrySpeakLine(bot, BotShop.WtsLine(stock), 0.62))
                {
                    FaceNearestPerson(bot);
                    if (Utility.RandomDouble() < 0.40)
                    {
                        bot.Animate(33, 5, 1, true, false, 0);
                    }
                    WalkBackIfShoved(bot);
                    return;
                }
            }

            // Speak first; chatter is the whole point — and you talk TO
            // someone: face the nearest person when a line lands, instead
            // of announcing WTS to a wall.
            if (TrySpeak(bot))
            {
                FaceNearestPerson(bot);
                // A hawker punctuates the pitch — wave the goods around.
                if (Role == BankRole.Hawker && Utility.RandomDouble() < 0.40)
                {
                    bot.Animate(33, 5, 1, true, false, 0);
                }
            }
            else if (Utility.RandomDouble() < 0.03)
            {
                // Idle life between lines: mostly turn to watch whoever's
                // around; now and then check the bank box (the bend-over
                // gesture every bank crowd made all day).
                if (Utility.RandomDouble() < 0.35)
                {
                    bot.Animate(32, 5, 1, true, false, 0);
                }
                else
                {
                    FaceNearestPerson(bot);
                }
            }
        }

        // ---- Resist macroer: REAL self-casts on a loop, forever ----
        // The engine does the whole thing exactly as it did for a player
        // holding down a macro: words of power, the chant animation, the
        // cast delay, then the target cursor — which we aim at ourselves.
        // Mana and REAGENTS are consumed from the pack by the spell
        // system itself; fizzles happen; when the reg pouch runs dry the
        // bot visibly restocks from its bank box (it's standing at the
        // bank — that's where a 1999 macroer kept the stash), and a
        // broke macroer retires into the mundane crowd.

        // Reagents each option burns (era-correct):
        //   Clumsy      = bloodmoss + nightshade
        //   Weaken      = garlic + nightshade
        //   Feeblemind  = nightshade + ginseng
        //   Curse (26+) = garlic + nightshade + sulfurous ash
        private static readonly Type[][] ResistSpellRegs =
        {
            new[] { typeof(Bloodmoss), typeof(Nightshade) },
            new[] { typeof(Garlic), typeof(Nightshade) },
            new[] { typeof(Nightshade), typeof(Ginseng) },
            new[] { typeof(Garlic), typeof(Nightshade), typeof(SulfurousAsh) },
        };

        private void TickResistMacro(PlayerBot bot)
        {
            // The chant finished and the cursor is up — target ourselves.
            // CheckSequence consumes the mana and reagents right here.
            if (_resistCastPending && bot.Target != null)
            {
                _resistCastPending = false;
                bot.Target.Invoke(bot, bot);
                _nextMacroAt = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(8, 15));
                return;
            }

            // Launched but no cursor after a while — the cast got
            // disturbed (shoved mid-chant). Reset and try again later.
            if (_resistCastPending)
            {
                if (Core.Now - _resistCastStartedAt > TimeSpan.FromSeconds(10))
                {
                    _resistCastPending = false;
                    _nextMacroAt = Core.Now + TimeSpan.FromSeconds(6);
                }
                return;
            }

            if (bot.Spell != null || Core.Now < _nextMacroAt)
            {
                return;
            }

            // Out of mana — sit and regenerate like everyone did.
            if (bot.Mana < 12)
            {
                _nextMacroAt = Core.Now + TimeSpan.FromSeconds(15);
                return;
            }

            var spell = PickResistSpell(bot);
            if (spell == null)
            {
                RestockRegsOrRetire(bot);
                return;
            }

            try
            {
                if (!spell.Cast())
                {
                    _nextMacroAt = Core.Now + TimeSpan.FromSeconds(6);
                    return;
                }
            }
            catch
            {
                _nextMacroAt = Core.Now + TimeSpan.FromSeconds(10);
                return;
            }

            _resistCastPending = true;
            _resistCastStartedAt = Core.Now;
        }

        // An option the bot can actually pay for right now, or null when
        // the pouch can't cover ANY of them.
        private Spell PickResistSpell(PlayerBot bot)
        {
            var pack = bot.Backpack;
            if (pack == null)
            {
                return null;
            }

            int options = bot.Skills[SkillName.Magery].Base >= 26 ? 4 : 3;
            int start = Utility.Random(options);
            for (int n = 0; n < options; n++)
            {
                int i = (start + n) % options;
                bool haveRegs = true;
                foreach (var t in ResistSpellRegs[i])
                {
                    if (pack.GetAmount(t) < 1)
                    {
                        haveRegs = false;
                        break;
                    }
                }
                if (!haveRegs)
                {
                    continue;
                }
                return i switch
                {
                    0 => new ClumsySpell(bot),
                    1 => new WeakenSpell(bot),
                    2 => new FeeblemindSpell(bot),
                    _ => new CurseSpell(bot),
                };
            }
            return null;
        }

        // The reg pouch ran dry. A macroer standing AT the bank pulls a
        // fresh batch from the bank box — visible bend, gold deducted —
        // and one who can't afford it gives up the grind and joins the
        // mundane crowd.
        private void RestockRegsOrRetire(PlayerBot bot)
        {
            var pack = bot.Backpack;
            var regTypes = new[]
            {
                typeof(Bloodmoss), typeof(Garlic), typeof(Ginseng),
                typeof(Nightshade), typeof(SulfurousAsh),
            };

            int cost = 0;
            foreach (var t in regTypes)
            {
                cost += Math.Max(0, 30 - pack.GetAmount(t)) * 2;
            }

            if (cost > 0 && pack.GetAmount(typeof(Gold)) >= cost)
            {
                pack.ConsumeTotal(typeof(Gold), cost);
                foreach (var t in regTypes)
                {
                    int add = 30 - pack.GetAmount(t);
                    if (add > 0)
                    {
                        pack.DropItem((Item)Activator.CreateInstance(t, add));
                    }
                }
                bot.Animate(32, 5, 1, true, false, 0); // into the bank box
                Console.WriteLine(
                    $"[macro] {bot.Name} restocked resist reagents from the " +
                    $"bank box (-{cost}gp)");
                _nextMacroAt = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(8, 15));
                return;
            }

            Console.WriteLine(
                $"[macro] {bot.Name} is out of reagents and gold — resist " +
                $"session over, joining the crowd");
            Role = BankRole.Regular;
            ChatChance = 0.25;
        }

        // ---- Hiding macroer: blink out, blink back, repeat all day ----
        private void TickHidingMacro(PlayerBot bot)
        {
            if (bot.Hidden)
            {
                if (Core.Now >= _revealAt)
                {
                    bot.Hidden = false;
                }
                return;
            }

            if (Core.Now >= _nextMacroAt)
            {
                bot.Animate(32, 5, 1, true, false, 0); // crouch over the macro
                bot.Hidden = true;
                _revealAt   = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(6, 14));
                _nextMacroAt = _revealAt + TimeSpan.FromSeconds(Utility.RandomMinMax(4, 10));
            }
        }

        // ---- Stealth macroer: creep in circles near home, hidden ----
        private void TickStealthMacro(PlayerBot bot)
        {
            // The periodic stealth "failure": pop visible for a moment,
            // crouch, vanish again — every bank had one doing exactly this.
            if (!bot.Hidden)
            {
                if (Core.Now >= _nextMacroAt)
                {
                    bot.Animate(32, 5, 1, true, false, 0);
                    bot.Hidden = true;
                }
                return;
            }

            if (Utility.RandomDouble() < 0.01)
            {
                bot.Hidden = false;
                _nextMacroAt = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(4, 8));
                return;
            }

            if (Core.Now < _nextSneakStep)
            {
                return;
            }
            _nextSneakStep = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(2, 5));

            // One slow step, biased back toward home so the creep stays a
            // tight circle instead of drifting into the street.
            Direction d;
            var dx = bot.Location.X - Home.X;
            var dy = bot.Location.Y - Home.Y;
            if (dx * dx + dy * dy > 9)
            {
                d = bot.GetDirectionTo(Home);
            }
            else
            {
                d = (Direction)Utility.Random(8);
            }
            if (bot.Direction != d)
            {
                bot.Direction = d;
            }
            bot.Move(d);
            bot.Hidden = true; // movement must not reveal the act
        }

        // If we got shoved off our home tile, walk back. One step per
        // tick toward home until we're there.
        private void WalkBackIfShoved(PlayerBot bot)
        {
            // A buyer crossing the bank floor to close a deal is not a
            // bot that got shoved. Without this the two pullers fight:
            // BotShopDeal steps it toward the seller, this drags it back
            // to its chair, and it jitters in place until the walk times
            // out and the sale dies.
            if (BotShopDeal.IsDealing(bot))
            {
                return;
            }

            var dx = bot.Location.X - Home.X;
            var dy = bot.Location.Y - Home.Y;
            if (dx == 0 && dy == 0)
            {
                return;
            }

            var distSquared = dx * dx + dy * dy;
            if (distSquared <= HomeRadius * HomeRadius)
            {
                return;
            }

            var d = bot.GetDirectionTo(Home);
            if (bot.Direction != d)
            {
                bot.Direction = d;
            }
            bot.Move(d);
        }

        // Turn toward the nearest other visible person (bot or player) in
        // conversation range — the small thing that makes a standing
        // crowd read as PEOPLE instead of statues.
        private static void FaceNearestPerson(PlayerBot bot)
        {
            Mobile nearest = null;
            int bestDist = int.MaxValue;
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, 6))
            {
                if (m == bot || m.Deleted || !m.Alive || !m.Player || m.Hidden)
                {
                    continue;
                }
                int dx = Math.Abs(m.X - bot.X);
                int dy = Math.Abs(m.Y - bot.Y);
                int dist = dx > dy ? dx : dy;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    nearest = m;
                }
            }

            if (nearest != null)
            {
                var d = bot.GetDirectionTo(nearest);
                if (bot.Direction != d)
                {
                    bot.Direction = d;
                }
            }
        }
    }
}
