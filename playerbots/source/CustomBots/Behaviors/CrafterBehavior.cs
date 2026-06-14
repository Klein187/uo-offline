// =========================================================================
// CrafterBehavior.cs — a crafter bot working at its craft station.
//
// Like BankSitter, the bot holds a fixed home tile and drifts back if
// shoved. On top of that it periodically plays a "making" animation and
// craft sound — a smith swinging at the forge, a tailor working cloth —
// and chats from craft-themed lines.
//
// The bot's CrafterType (Smith / Tailor / Tinker / Inscriptionist /
// Carpenter / Bowcrafter) decides the animation, the sound, and — via
// CrafterTypeHelper.StationFor — which destination type it routes to.
// Destination routing itself is handled by the Traveler + destination
// weights; this behavior is what runs once the bot has ARRIVED and is
// stationed.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    public class CrafterBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Crafter";

        // Shoved-off-tile tolerance, same as BankSitter.
        public int HomeRadius { get; set; } = 1;

        public Point3D Home { get; private set; }
        public Map HomeMap   { get; private set; }

        // When the next craft animation may play.
        private DateTime _nextCraftAt = DateTime.MinValue;

        private static readonly TimeSpan CraftMin = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan CraftMax = TimeSpan.FromSeconds(14);

        public CrafterBehavior()
        {
            // Craft-themed chatter. craft_talk is the new craft line pool;
            // wts/wtb because crafters sell their wares and buy materials;
            // small_talk for ambient color.
            ChatCategories = new[]
            {
                "craft_talk",
                "wts",
                "wtb",
                "small_talk",
            };

            // Moderate chatter — busier than a wanderer, calmer than a
            // bank crowd; a crafter is focused on the work.
            ChatChance      = 0.18;
            MinChatCooldown = TimeSpan.FromSeconds(20);
            MaxChatCooldown = TimeSpan.FromSeconds(50);
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            Home    = bot.Location;
            HomeMap = bot.Map;
            ScheduleNextCraft();
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

            // Drift back to the station tile if shoved.
            var dx = bot.Location.X - Home.X;
            var dy = bot.Location.Y - Home.Y;
            bool atHome = (dx == 0 && dy == 0);
            if (!atHome && dx * dx + dy * dy > HomeRadius * HomeRadius)
            {
                var d = bot.GetDirectionTo(Home);
                if (bot.Direction != d) bot.Direction = d;
                bot.Move(d);
                return;  // moving this tick; craft on a tick we're settled
            }

            // Settled at the station — play the craft animation on cadence.
            if (Core.Now >= _nextCraftAt)
            {
                PlayCraft(bot);
                ScheduleNextCraft();
            }
        }

        // -------------------------------------------------------------------
        // Play the "making" animation + sound for this bot's craft type.
        // -------------------------------------------------------------------
        private void PlayCraft(PlayerBot bot)
        {
            var (action, sound) = CraftMotion(bot.CrafterSpec);

            // Face the work, then animate. Animate signature:
            //   Animate(action, frameCount, repeatCount, forward, repeat, delay)
            try
            {
                bot.Animate(action, 5, 1, true, false, 0);
            }
            catch
            {
                // Some bodies reject some actions — animation is cosmetic,
                // never let it break the tick.
            }

            if (sound > 0)
            {
                try { bot.PlaySound(sound); } catch { }
            }
        }

        // action = humanoid animation index; sound = craft sound id (0 = none).
        //
        // Humanoid bodies have a limited animation set. Action 9 is the
        // one-handed swing — it reads convincingly as hammering or working
        // a station. 11 is a bow-draw-like motion. We pick the closest fit
        // per craft; none are perfect "crafting" anims because UO doesn't
        // have those for player bodies.
        private static (int action, int sound) CraftMotion(CrafterType type)
        {
            return type switch
            {
                // Smith — hammer swing + anvil ring.
                CrafterType.Smith      => (9, 0x2A),
                // Carpenter — same swing motion, sawing/hammering sound.
                CrafterType.Carpenter  => (9, 0x23D),
                // Bowcrafter — bow-draw-like motion, fletching sound.
                CrafterType.Bowcrafter => (11, 0x55),
                // Tailor — gentle swing reads as cloth work, scissors snip.
                CrafterType.Tailor     => (9, 0x248),
                // Tinker — swing motion, tinkering sound.
                CrafterType.Tinker     => (9, 0x241),
                // Inscriptionist — quiet; a bow/salute gesture, no sound.
                CrafterType.Inscriptionist => (11, 0),
                // Fisherman — bow-draw motion reads as casting a line,
                // 0x364 is the water splash. Both feel right for fishing.
                CrafterType.Fisherman  => (11, 0x364),
                _ => (9, 0),
            };
        }

        private void ScheduleNextCraft()
        {
            int ms = Utility.RandomMinMax(
                (int)CraftMin.TotalMilliseconds,
                (int)CraftMax.TotalMilliseconds);
            _nextCraftAt = Core.Now + TimeSpan.FromMilliseconds(ms);
        }
    }
}
