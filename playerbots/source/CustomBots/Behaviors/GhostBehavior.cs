// =========================================================================
// GhostBehavior.cs — a freshly dead bot haunting its corpse (IDEAS 3.1).
//
// The first act of the death story: the ghost drifts around the death
// spot for a minute or two, moaning OoOoOo at anyone nearby (the client
// garbles ghost speech for the living — exactly right). Then:
//
//   - Surface, res point in walking range → becomes a Traveler (while
//     dead) and WALKS to the healer/shrine. BotDeathManager's arrival
//     hook resurrects it there and starts the corpse run.
//   - Dungeon, or nowhere to walk → "found by a wandering healer" /
//     "used the level's ankh": res in place after the haunt, corpse
//     conveniently underfoot.
//
// Ghosts don't fight, don't mount, don't shop. They drift.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    public class GhostBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Ghost";

        public override string GetStatusLine(PlayerBot bot) => "dead — a ghost seeking resurrection";

        private const int HauntMinSeconds = 45;
        private const int HauntMaxSeconds = 120;

        // How far the ghost drifts from the corpse while haunting.
        private const int DriftRadius = 6;

        // Res-in-place waits for hostiles to clear the area, up to this
        // much extra haunting past the normal window.
        private static readonly TimeSpan HauntHostileGrace = TimeSpan.FromMinutes(3);

        private const int HostileCheckRange = 8;

        private DateTime _hauntUntil = DateTime.MinValue;
        private Point3D _anchor;

        public GhostBehavior()
        {
            ChatCategories  = new[] { "ghost" };
            ChatChance      = 0.30;
            MinChatCooldown = TimeSpan.FromSeconds(15);
            MaxChatCooldown = TimeSpan.FromSeconds(45);
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            _anchor = bot.Location;
            _hauntUntil = Core.Now + TimeSpan.FromSeconds(
                Utility.RandomMinMax(HauntMinSeconds, HauntMaxSeconds));
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal || bot.Deleted)
            {
                return;
            }

            // Somehow alive again (an admin, a player with a res spell) —
            // skip straight to the corpse run.
            if (bot.Alive)
            {
                bot.CorpseRunPending = true;
                bot.Behavior = new CorpseReclaimBehavior();
                return;
            }

            TrySpeak(bot);

            if (Core.Now >= _hauntUntil)
            {
                var destName = BotDeathManager.PickResDestination(bot);
                if (destName != null)
                {
                    Console.WriteLine(
                        $"[death] {bot.Name}'s ghost sets off for '{destName}'");
                    bot.Behavior = new TravelerBehavior { DestinationName = destName };
                }
                else
                {
                    // Nobody within walking range — a wandering healer
                    // finds them (or, in a dungeon, they find the ankh).
                    // But NOT into a monster's face: ressing at half
                    // health beside the respawned room is an instant
                    // re-kill. Keep haunting until the room clears (or
                    // the hard limit passes — monsters wander).
                    if (HostileNearby(bot) &&
                        Core.Now < _hauntUntil + HauntHostileGrace)
                    {
                        return;
                    }
                    BotDeathManager.ResurrectBot(bot, "wandering healer");
                }
                return;
            }

            Drift(bot);
        }

        // Same "actual monster" filter the combat code uses: deeply
        // negative karma + a fight mode. Wildlife and townsfolk don't
        // delay a res.
        private static bool HostileNearby(PlayerBot bot)
        {
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, HostileCheckRange))
            {
                if (m is Server.Mobiles.BaseCreature bc &&
                    bc.Alive && !bc.Deleted &&
                    bc.Karma < 0 &&
                    bc.FightMode != Server.Mobiles.FightMode.None &&
                    !bc.Controlled && !bc.Summoned)
                {
                    return true;
                }
            }
            return false;
        }

        private void Drift(PlayerBot bot)
        {
            // Drift: aimless one-tile floats around the corpse.
            if (Utility.RandomDouble() < 0.5)
            {
                Direction dir;
                if (Math.Max(Math.Abs(bot.X - _anchor.X), Math.Abs(bot.Y - _anchor.Y))
                    > DriftRadius)
                {
                    dir = bot.GetDirectionTo(_anchor);
                }
                else
                {
                    dir = (Direction)Utility.Random(8);
                }
                bot.Direction = dir;
                bot.Move(dir);
            }
        }
    }
}
