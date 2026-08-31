// =========================================================================
// BotPathing.cs — the bots' run/walk animation flag.
//
// Until 2026-08-30, PathFollower.Follow took a `run` argument and passed it
// to GetDirectionTo, which OR'd Direction.Running onto the step. That is how
// every bot on a run-paced step timer told the client to animate a run.
//
// ModernUO #2599 removed that argument. Upstream derives the bit from the
// AI's step pace instead, in BaseAI.DoMoveImpl — which is the right answer
// for creatures, and reaches none of the bots: a bot drives a PathFollower
// directly off its own step timer and never goes through BaseAI. With no
// Mover set, PathFollower falls back to a plain Mobile.Move and nothing
// stamps the bit at all, so a bot stepping every 200 ms would go out flagged
// as walking.
//
// That mismatch is the exact defect #2599 set out to fix. The client renders
// a walk step over 400 ms on foot, queues five of them, and drops the sixth;
// feed it run-paced steps flagged as walks and the queue backs up until the
// mobile snaps forward. So the bots keep stamping the bit, from the same
// pace their step timer already runs at.
// =========================================================================

using System;

namespace Server.CustomBots
{
    public static class BotPathing
    {
        /// <summary>
        /// A PathFollower.Mover that stamps Direction.Running from the caller's
        /// own pace. `running` is read per step, not captured once, so a bot
        /// that drops from a run to a walk mid-leg is animated correctly from
        /// the next step on.
        /// </summary>
        public static MoveMethod Paced(Mobile m, Func<bool> running) =>
            (d, _) =>
            {
                d = (d & Direction.Mask) | (running() ? Direction.Running : 0);

                // Matches PathFollower's own Mover-less fallback, plus the bit.
                return m.Move(d) ? MoveResult.Success : MoveResult.Blocked;
            };
    }
}
