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
using Server.Network;

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

        public BankSitterBehavior()
        {
            // Bank-crowd chatter: everything trade-related plus small talk.
            // bank_actions are short ("bank", "withdraw 1000") so they land
            // hard; WTS/WTB are the meat; LFG appears here because banks
            // were historically where you'd find groups.
            ChatCategories = new[]
            {
                "small_talk",
                "bank_actions",
                "wts",
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

            switch (Role)
            {
                case BankRole.Hawker:
                    // A seller talks SHOP, loudly and often — nothing else.
                    ChatCategories  = new[] { "wts", "wtb" };
                    ChatChance      = 0.55;
                    MinChatCooldown = TimeSpan.FromSeconds(10);
                    MaxChatCooldown = TimeSpan.FromSeconds(25);
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

        // ---- Resist macroer: weak self-debuffs on a loop, forever ----
        // The looks-right subset of first-circle debuffs plus Curse. No
        // real spell system involved — words of power, the cast animation,
        // the right particles and sound, a mana dip. When the pool runs
        // dry the bot stands there "meditating" until it refills, exactly
        // like the real macro did.
        private static readonly (string words, int itemID, int speed, int duration,
            int effect, int sound)[] ResistSpells =
        {
            ("Uus Jux",   0x3779, 1, 46, 5002, 0x1DF), // Clumsy
            ("Des Mani",  0x3779, 1, 46, 5009, 0x1E6), // Weaken
            ("Rel Wis",   0x3779, 1, 46, 5004, 0x1E4), // Feeblemind
            ("Des Sanct", 0x374A, 10, 15, 5028, 0x1E1), // Curse
        };

        private void TickResistMacro(PlayerBot bot)
        {
            if (Core.Now < _nextMacroAt)
            {
                return;
            }

            // Out of mana — stand and regenerate like everyone did.
            if (bot.Mana < 10)
            {
                _nextMacroAt = Core.Now + TimeSpan.FromSeconds(15);
                return;
            }

            var s = ResistSpells[Utility.Random(ResistSpells.Length)];
            bot.PublicOverheadMessage(MessageType.Spell, bot.SpeechHue, false, s.words);
            bot.Animate(16, 7, 1, true, false, 0);
            bot.FixedParticles(s.itemID, s.speed, s.duration, s.effect, EffectLayer.Waist);
            bot.PlaySound(s.sound);
            bot.Mana = Math.Max(0, bot.Mana - 6);

            _nextMacroAt = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(8, 15));
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
