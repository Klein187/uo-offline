// =========================================================================
// CorpseReclaimBehavior.cs — the last act of the corpse run (IDEAS 3.1).
//
// The bot is alive again and within ~30 tiles of its corpse. This
// behavior walks it straight to the body (own fast step timer +
// PathFollower, same pattern as the teleporter pad-walk) and then
// SELF-LOOTS via the vanilla flow — Corpse.Open(bot, checkSelfLoot:true)
// is exactly what double-clicking your own corpse does: everything
// returns to your pack/layers and the death robe vanishes.
//
// If the corpse decayed or someone emptied it: "WHO LOOTED MY CORPSE",
// and a fresh kit from EquipmentTable so nobody haunts the roads naked.
// Either way life resumes (BotDeathManager.ResumeLife).
// =========================================================================

using System;
using Server;
using Server.Items;

namespace Server.CustomBots
{
    public class CorpseReclaimBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "CorpseReclaim";

        // Give up if the walk hasn't reached the corpse in this long
        // (wedged behind terrain, corpse on an unreachable ledge...).
        private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(90);

        private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(400);

        private PathFollower _follower;
        private Timer _stepTimer;
        private DateTime _startedAt;
        private Point3D _corpseLoc;

        public override string GetStatusLine(PlayerBot bot) =>
            $"corpse run → ({_corpseLoc.X},{_corpseLoc.Y})";

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            _startedAt = Core.Now;

            if (bot.Corpse is Corpse corpse && !corpse.Deleted)
            {
                _corpseLoc = corpse.GetWorldLocation();
            }
        }

        public override void OnDetached(PlayerBot bot)
        {
            StopStepping();
            base.OnDetached(bot);
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal || bot.Deleted)
            {
                StopStepping();
                return;
            }

            if (bot.Corpse is not Corpse corpse || corpse.Deleted)
            {
                StopStepping();
                BotDeathManager.GiveUpCorpse(bot, "corpse gone before reclaim");
                BotDeathManager.ResumeLife(bot);
                return;
            }

            _corpseLoc = corpse.GetWorldLocation();

            // There. Vanilla self-loot needs range 2.
            if (bot.InRange(_corpseLoc, 2))
            {
                StopStepping();
                corpse.Open(bot, checkSelfLoot: true);

                Console.WriteLine($"[death] {bot.Name} reclaimed their corpse at ({_corpseLoc.X},{_corpseLoc.Y})");
                var line = ChatLibrary.PickRandom("death_reclaim");
                if (!string.IsNullOrEmpty(line))
                {
                    bot.Say(line);
                }

                BotDeathManager.ResumeLife(bot);
                return;
            }

            if (Core.Now - _startedAt > WalkTimeout)
            {
                StopStepping();
                BotDeathManager.GiveUpCorpse(bot, "couldn't reach the corpse");
                BotDeathManager.ResumeLife(bot);
                return;
            }

            EnsureStepping(bot);
        }

        private void EnsureStepping(PlayerBot bot)
        {
            if (_stepTimer?.Running == true)
            {
                return;
            }
            _stepTimer = Timer.DelayCall(TimeSpan.Zero, StepInterval, 0, () =>
            {
                if (bot.Deleted || bot.Behavior != this)
                {
                    StopStepping();
                    return;
                }
                _follower ??= new PathFollower(bot, _corpseLoc);
                _follower.Follow(range: 1);
            });
        }

        private void StopStepping()
        {
            _stepTimer?.Stop();
            _stepTimer = null;
            _follower = null;
        }
    }
}
