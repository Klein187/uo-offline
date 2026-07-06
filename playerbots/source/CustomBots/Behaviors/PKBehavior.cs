// =========================================================================
// PKBehavior.cs — the player-killer bot.
//
// A predatory bot that patrols the roads hunting players and other bots,
// goes criminal/red as it kills, and is hunted by guards in return.
//
// LIFECYCLE
//   PATROL   — wander the waypoint graph (road-weighted) scanning for a
//              victim every step.
//   HUNT     — a victim was scored and chosen; close distance and attack.
//   FLEE     — the fight turned bad (low HP, or guards/help arrived);
//              break off and run.
//   LOOT     — victim is dead; step onto the corpse and grab gold.
//
// VICTIM SCORING — a PK is an opportunist, not an omniscient killer. It
// scores candidates and prefers the easy mark: isolated, wounded,
// lower-tier, outside a guard zone. It never targets other PKs or anything
// a guard would protect.
//
// NOTORIETY — PKs are NOT exempt from UO's criminal/murderer system.
// Attacking an innocent flags them criminal; kills accrue toward red.
// Guards handle reds in town. We don't code that — we just don't bypass it.
//
// GANGS — a PK can belong to a gang (set by the spawner). Gang members
// share aggro: when one engages a victim, nearby gang-mates converge on
// the same target.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Items;
using Server.Mobiles;

namespace Server.CustomBots
{
    public class PKBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "PK";

        // ---- Tunables ----------------------------------------------------

        // How far the PK scans for victims.
        private const int SightRange = 12;

        // Flee when HP drops below this fraction of max.
        private const double FleeHealthPct = 0.35;

        // A victim is "isolated" (preferred) if fewer than this many other
        // players/bots are near them.
        private const int IsolationThreshold = 2;

        // ---- Gang --------------------------------------------------------
        // Gang id; 0 = solo. Gang-mates share the same non-zero id. Set by
        // the spawner at creation.
        public int GangId { get; set; }

        // How far a gang-mate will be pulled in to converge on a victim.
        private const int GangConvergeRange = 20;

        // ---- State -------------------------------------------------------
        private enum Phase { Patrol, Hunt, Flee, Loot }
        private Phase _phase = Phase.Patrol;

        private Mobile _victim;        // current hunt/loot target
        private Corpse _lootCorpse;    // corpse being looted
        private DateTime _nextScan = DateTime.MinValue;
        private DateTime _fleeUntil = DateTime.MinValue;
        private DateTime _nextTauntAt = DateTime.MinValue;

        // The patrol uses a Traveler under the hood for road navigation.
        private TravelerBehavior _patrol;

        private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan FleeDuration = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan TauntCooldown = TimeSpan.FromSeconds(8);

        public override string GetStatusLine(PlayerBot bot)
        {
            var gang = GangId != 0 ? $" (gang {GangId})" : "";
            return _phase switch
            {
                Phase.Hunt when _victim != null && !_victim.Deleted =>
                    $"PK — hunting {_victim.Name}{gang}",
                Phase.Hunt => $"PK — hunting{gang}",
                Phase.Flee => $"PK — fleeing{gang}",
                Phase.Loot => $"PK — looting a kill{gang}",
                _          => $"PK — prowling for prey{gang}",
            };
        }

        public PKBehavior()
        {
            ChatCategories  = new[] { "pk_taunt" };
            ChatChance      = 0.0;   // taunts are fired explicitly, not ambient
            MinChatCooldown = TimeSpan.FromSeconds(8);
            MaxChatCooldown = TimeSpan.FromSeconds(30);
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            // A spawned PK is a career murderer, not a first-timer: enough
            // murder counts to be RED from the start (red = Kills >= 5), so
            // the roads carry visible red hunters immediately instead of
            // greys that only redden after their first few murders.
            if (bot.Kills < 5)
            {
                bot.Kills = Utility.RandomMinMax(5, 20);
            }
            // The camp anchor: dungeon-spawned reds hold their hall.
            _camp = bot.Location;

            // Patrol via an internal Traveler — reuse all the road
            // navigation, waypoint following, and fluid movement.
            _patrol = new TravelerBehavior { AvoidTowns = true };
            _patrol.OnAttached(bot);
            _phase = Phase.Patrol;
        }

        public override void OnDetached(PlayerBot bot)
        {
            _patrol?.OnDetached(bot);
            base.OnDetached(bot);
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal) return;
            if (!bot.Alive) return;

            switch (_phase)
            {
                case Phase.Patrol: TickPatrol(bot); break;
                case Phase.Hunt:   TickHunt(bot);   break;
                case Phase.Flee:   TickFlee(bot);   break;
                case Phase.Loot:   TickLoot(bot);   break;
            }
        }

        // Pack cohesion + dungeon camping state.
        private Point3D _camp;
        private DateTime _nextPackMove;
        private DateTime _nextCampShuffle;

        // ---- PATROL ------------------------------------------------------
        private void TickPatrol(PlayerBot bot)
        {
            // Dungeon reds CAMP their hall instead of road-patrolling: the
            // patrol Traveler's surface destination rolls would churn
            // (everything is unreachable from a dungeon component) and its
            // maroon-rescue would teleport the red OUT of the dungeon.
            // Camping the spawn room and jumping whoever walks in is
            // exactly what dungeon PKs did anyway.
            if (DungeonRegistry.IsInDungeon(bot))
            {
                TickDungeonCamp(bot);
                if (Core.Now >= _nextScan)
                {
                    _nextScan = Core.Now + ScanInterval;
                    var mark = FindVictim(bot);
                    if (mark != null)
                    {
                        BeginHunt(bot, mark);
                    }
                }
                return;
            }

            // Guard-aware patrol: if the PK has wandered into a guarded
            // region (the town-spanning waypoint graph can route it
            // through Britain etc.), abandon the current route and head
            // back out to the wilds. A PK never loiters in town — that's
            // where it gets guard-whacked, and it can't hunt there anyway.
            if (IsGuarded(bot))
            {
                RetreatFromTown(bot);
                return;
            }

            // Pack cohesion: reds roam in gangs. When a fellow red is
            // nearby but drifting away, close ranks instead of walking the
            // route this tick — packs stay packs, and a gang of three is a
            // lot harder for a group of blues to mob than a lone red.
            if (Core.Now >= _nextPackMove)
            {
                _nextPackMove = Core.Now + TimeSpan.FromSeconds(8);
                var mate = NearestPackmate(bot);
                if (mate != null && !bot.InRange(mate.Location, 12))
                {
                    var dir = bot.GetDirectionTo(mate.Location);
                    bot.Direction = dir;
                    bot.Move(dir);
                    bot.Move(dir);
                    return;
                }
            }

            // Drive the underlying Traveler so the PK keeps moving.
            _patrol?.Tick(bot);

            // Scan for a victim on a cadence (scanning every tick is waste).
            if (Core.Now < _nextScan) return;
            _nextScan = Core.Now + ScanInterval;

            var victim = FindVictim(bot);
            if (victim != null)
            {
                BeginHunt(bot, victim);
            }
        }

        // Dungeon camp: shuffle around the spawn room like a bored sentry.
        // No Traveler, no destination rolls — the red owns this hall until
        // something walks in.
        private void TickDungeonCamp(PlayerBot bot)
        {
            // The halls bite: fight the room's spawn back. A red that
            // ignores the monsters gets ground down and journals
            // embarrassing self-attributed deaths. With Combatant set the
            // bot's normal auto-combat does the rest.
            if (bot.Combatant is not Mobile cur || !cur.Alive || cur.Deleted)
            {
                foreach (var m in bot.GetMobilesInRange(8))
                {
                    if (m is BaseCreature bc && bc.Alive && !bc.Deleted &&
                        bc.Combatant == bot)
                    {
                        bot.Combatant = bc;
                        break;
                    }
                }
            }

            if (Core.Now < _nextCampShuffle)
            {
                return;
            }
            _nextCampShuffle = Core.Now +
                TimeSpan.FromSeconds(Utility.RandomMinMax(4, 10));

            if (bot.InRange(_camp, 8))
            {
                var dir = (Direction)Utility.Random(8);
                bot.Direction = dir;
                bot.Move(dir);
            }
            else
            {
                var back = bot.GetDirectionTo(_camp);
                bot.Direction = back;
                bot.Move(back);
                bot.Move(back);
            }
        }

        // The nearest fellow red in sight — the gang to close ranks with.
        private static PlayerBot NearestPackmate(PlayerBot bot)
        {
            PlayerBot best = null;
            int bestD = int.MaxValue;
            foreach (var m in bot.GetMobilesInRange(30))
            {
                if (m is PlayerBot other && other != bot && !other.Deleted &&
                    other.Alive && other.Behavior is PKBehavior)
                {
                    int d = (int)bot.GetDistanceToSqrt(other.Location);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = other;
                    }
                }
            }
            return best;
        }

        // PK is in a guarded region — walk straight out. Step toward the
        // nearest non-guarded tile until clear, then normal patrol resumes.
        private void RetreatFromTown(PlayerBot bot)
        {
            // Find a direction that leads out of the guarded region. Sample
            // the eight directions a few tiles out; head toward the first
            // that's unguarded.
            Direction? escape = FindUnguardedDirection(bot);
            if (escape.HasValue)
            {
                if (bot.Direction != escape.Value) bot.Direction = escape.Value;
                bot.Move(escape.Value);
                bot.Move(escape.Value);  // double-step — leave promptly
            }
            else
            {
                // Surrounded by guard zone (shouldn't happen on the road
                // graph) — just nudge the Traveler and hope it routes out.
                _patrol?.Tick(bot);
            }
        }

        // Probe the 8 compass directions; return the first whose tile a
        // few steps out is NOT in a guarded region.
        private static Direction? FindUnguardedDirection(PlayerBot bot)
        {
            Direction[] dirs =
            {
                Direction.North, Direction.East, Direction.South, Direction.West,
                Direction.Right, Direction.Down, Direction.Left, Direction.Up,
            };
            const int probe = 6;
            foreach (var d in dirs)
            {
                int nx = bot.X, ny = bot.Y;
                switch (d)
                {
                    case Direction.North: ny -= probe; break;
                    case Direction.South: ny += probe; break;
                    case Direction.East:  nx += probe; break;
                    case Direction.West:  nx -= probe; break;
                    case Direction.Right: nx += probe; ny -= probe; break;
                    case Direction.Left:  nx -= probe; ny += probe; break;
                    case Direction.Up:    nx -= probe; ny -= probe; break;
                    case Direction.Down:  nx += probe; ny += probe; break;
                }
                var region = Region.Find(new Point3D(nx, ny, bot.Z), bot.Map);
                if (region == null ||
                    !region.IsPartOf<Server.Regions.GuardedRegion>())
                {
                    return d;
                }
            }
            return null;
        }

        // ---- HUNT --------------------------------------------------------
        private void BeginHunt(PlayerBot bot, Mobile victim)
        {
            _victim = victim;
            _phase  = Phase.Hunt;
            bot.Combatant = victim;
            Taunt(bot);

            // Pull in gang-mates: any PK with the same GangId nearby drops
            // what it's doing and converges on this victim.
            if (GangId != 0)
            {
                AlertGang(bot, victim);
            }
        }

        private void TickHunt(PlayerBot bot)
        {
            var victim = _victim;

            // Victim gone — dead, fled far, or zoned out?
            if (victim == null || victim.Deleted || victim.Map != bot.Map)
            {
                EndHunt(bot);
                return;
            }
            if (!victim.Alive)
            {
                // Kill (or someone else's kill) — go loot the corpse.
                BeginLoot(bot, victim);
                return;
            }
            if (!bot.InRange(victim.Location, SightRange + 6))
            {
                // Victim escaped our sight — give up the hunt.
                EndHunt(bot);
                return;
            }

            // Fight turning bad? Flee.
            if (ShouldFlee(bot))
            {
                BeginFlee(bot, victim);
                return;
            }

            // Keep the pressure on. The combat system attacks Combatant
            // each weapon/spell tick; we just keep Combatant set and close
            // distance if we've drifted out of reach.
            bot.Combatant = victim;
            Taunt(bot);

            if (!bot.InRange(victim.Location, 1))
            {
                var d = bot.GetDirectionTo(victim);
                if (bot.Direction != d) bot.Direction = d;
                bot.Move(d);
            }
        }

        private void EndHunt(PlayerBot bot)
        {
            _victim = null;
            bot.Combatant = null;
            _phase = Phase.Patrol;
        }

        // ---- FLEE --------------------------------------------------------
        private void BeginFlee(PlayerBot bot, Mobile threat)
        {
            _phase = Phase.Flee;
            _fleeUntil = Core.Now + FleeDuration;
            _victim = threat;       // remembered only as "what to run from"
            bot.Combatant = null;
        }

        private void TickFlee(PlayerBot bot)
        {
            if (Core.Now >= _fleeUntil || _victim == null ||
                _victim.Deleted || _victim.Map != bot.Map)
            {
                _victim = null;
                _phase = Phase.Patrol;
                return;
            }

            // Run directly away from the threat. Double-step for a real
            // gap, like the v47 flee.
            var away = Opposite(bot.GetDirectionTo(_victim));
            if (bot.Direction != away) bot.Direction = away;
            bot.Move(away);
            bot.Move(away);
        }

        // ---- LOOT --------------------------------------------------------
        private void BeginLoot(PlayerBot bot, Mobile deadVictim)
        {
            _phase = Phase.Loot;
            bot.Combatant = null;
            _victim = null;
            _lootCorpse = FindCorpse(bot, deadVictim);

            if (_lootCorpse == null)
            {
                // No corpse found — nothing to loot, resume patrol.
                _phase = Phase.Patrol;
            }
        }

        private void TickLoot(PlayerBot bot)
        {
            var corpse = _lootCorpse;
            if (corpse == null || corpse.Deleted || corpse.Map != bot.Map)
            {
                _lootCorpse = null;
                _phase = Phase.Patrol;
                return;
            }

            // Walk to the corpse.
            if (!bot.InRange(corpse.Location, 1))
            {
                var d = bot.GetDirectionTo(corpse);
                if (bot.Direction != d) bot.Direction = d;
                bot.Move(d);
                return;
            }

            // On the corpse — grab the gold and go.
            GrabGold(bot, corpse);
            _lootCorpse = null;
            _phase = Phase.Patrol;
        }

        // ---- VICTIM SCANNING --------------------------------------------
        //
        // Score every candidate; the highest score is the chosen victim.
        // Returns null if nothing is worth attacking.
        private Mobile FindVictim(PlayerBot bot)
        {
            // A PK is not suicidal — don't hunt while standing in a guarded
            // region (a victim killed here brings guards instantly).
            if (IsGuarded(bot)) return null;

            Mobile best = null;
            double bestScore = double.MinValue;

            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, SightRange))
            {
                if (!IsValidVictim(bot, m)) continue;

                double score = ScoreVictim(bot, m);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = m;
                }
            }

            return best;
        }

        // Is this mobile something a PK would attack at all?
        private bool IsValidVictim(PlayerBot bot, Mobile m)
        {
            if (m == bot || m.Deleted || !m.Alive) return false;
            if (m.Map != bot.Map) return false;

            // Targets are players and player-bots only — not monsters,
            // not vendors/guards.
            bool isPlayerLike =
                (m is PlayerBot) || (m.Player && m is PlayerMobile);
            if (!isPlayerLike) return false;

            // Never attack another PK — professional courtesy, no infighting.
            if (m is PlayerBot pb && pb.Behavior is PKBehavior) return false;

            // Don't attack a victim standing in a guarded zone — a kill
            // there brings guards down on the PK instantly.
            if (m.Region != null &&
                m.Region.IsPartOf<Server.Regions.GuardedRegion>())
            {
                return false;
            }

            return true;
        }

        // Higher score = more attractive victim. A PK prefers the easy
        // mark: isolated, wounded, lower-tier, and close.
        private double ScoreVictim(PlayerBot bot, Mobile m)
        {
            double score = 100.0;

            // Distance — nearer is better. Up to -50 at max range.
            int dist = (int)bot.GetDistanceToSqrt(m.Location);
            score -= dist * 4.0;

            // Wounded — a hurt victim is an easy kill. Up to +60.
            if (m.HitsMax > 0)
            {
                double missingPct = 1.0 - (double)m.Hits / m.HitsMax;
                score += missingPct * 60.0;
            }

            // Isolation — a lone traveler is far better prey than someone
            // in a crowd. +40 if isolated, big penalty if surrounded.
            int nearbyAllies = CountNearbyPlayers(bot, m);
            if (nearbyAllies < IsolationThreshold) score += 40.0;
            else                                   score -= nearbyAllies * 15.0;

            // Lower-tier — a PK picks on the weak. A bot victim's SkillTier
            // tells us; real players we can't read this way, treat neutral.
            if (m is PlayerBot vb)
            {
                // Lower tier index = weaker = better prey.
                score += (5 - (int)vb.SkillTier) * 6.0;
            }

            return score;
        }

        // Count players/bots near a candidate (excludes the candidate and
        // this PK) — used for the isolation score.
        private int CountNearbyPlayers(PlayerBot bot, Mobile candidate)
        {
            int n = 0;
            foreach (var m in candidate.Map.GetMobilesInRange(
                         candidate.Location, 6))
            {
                if (m == candidate || m == bot) continue;
                if (m.Deleted || !m.Alive) continue;
                if ((m is PlayerBot) || (m.Player && m is PlayerMobile))
                    n++;
            }
            return n;
        }

        // ---- GANG --------------------------------------------------------
        private void AlertGang(PlayerBot bot, Mobile victim)
        {
            foreach (var m in bot.Map.GetMobilesInRange(
                         bot.Location, GangConvergeRange))
            {
                if (m is not PlayerBot mate || mate == bot) continue;
                if (mate.Behavior is not PKBehavior pk) continue;
                if (pk.GangId != GangId) continue;

                // Pull a gang-mate that's just patrolling onto this victim.
                if (pk._phase == Phase.Patrol)
                {
                    pk.BeginHunt(mate, victim);
                }
            }
        }

        // ---- HELPERS -----------------------------------------------------
        private bool ShouldFlee(PlayerBot bot)
        {
            if (bot.HitsMax <= 0) return false;
            return (double)bot.Hits / bot.HitsMax < FleeHealthPct;
        }

        private void Taunt(PlayerBot bot)
        {
            if (Core.Now < _nextTauntAt) return;
            _nextTauntAt = Core.Now + TauntCooldown;

            string line = ChatLibrary.PickRandom(new[] { "pk_taunt" });
            if (!string.IsNullOrEmpty(line))
            {
                bot.Say(line);
            }
        }

        private static bool IsGuarded(Mobile m)
        {
            return m.Region != null &&
                   m.Region.IsPartOf<Server.Regions.GuardedRegion>();
        }

        private static Corpse FindCorpse(PlayerBot bot, Mobile deadVictim)
        {
            // The victim's corpse should be at or near where they died.
            foreach (var item in bot.Map.GetItemsInRange(
                         deadVictim.Location, 2))
            {
                if (item is Corpse c && c.Owner == deadVictim)
                    return c;
            }
            return null;
        }

        private static void GrabGold(PlayerBot bot, Corpse corpse)
        {
            // Take gold from the corpse into the PK's pack. Keep it simple
            // and safe — gold only; full item looting is noisy and the
            // bots are transient anyway.
            var loot = new List<Item>();
            foreach (var item in corpse.Items)
            {
                if (item is Gold) loot.Add(item);
            }
            foreach (var gold in loot)
            {
                try { bot.AddToBackpack(gold); } catch { }
            }
            if (loot.Count > 0)
            {
                try { bot.Say("*loots the corpse*"); } catch { }
            }
        }

        private static Direction Opposite(Direction d)
        {
            return d switch
            {
                Direction.North => Direction.South,
                Direction.South => Direction.North,
                Direction.East  => Direction.West,
                Direction.West  => Direction.East,
                Direction.Up    => Direction.Down,
                Direction.Down  => Direction.Up,
                Direction.Left  => Direction.Right,
                Direction.Right => Direction.Left,
                _ => d,
            };
        }
    }
}
