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

        // ---- Pack + crowd rules ------------------------------------------
        // A red is a coward alone: it only HUNTS with at least one fellow
        // red nearby (a pack of 2+), and it breaks off when blues gather —
        // a mob of blues is how lone reds die on a busy road.
        private const int PackRange = 26;         // fellow reds this close = pack
        private const int CrowdRange = 12;        // blues this close = the crowd
        private const int CrowdRetreatCount = 3;  // this many blues (or more
                                                  // than the pack) = back off

        // The editor-drawn leash. When set, the PK prowls ONLY inside this
        // area and never walks toward a town.
        private PKSpawnDef _hunt;
        private DateTime _nextRoamPick;
        private Point3D _roamTarget;

        // ---- State -------------------------------------------------------
        private enum Phase { Patrol, Hunt, Flee, Loot }
        private Phase _phase = Phase.Patrol;

        private Mobile _victim;        // current hunt/loot target
        private Corpse _lootCorpse;    // corpse being looted
        private DateTime _nextScan = DateTime.MinValue;
        private DateTime _fleeUntil = DateTime.MinValue;
        private DateTime _nextTauntAt = DateTime.MinValue;

        // ---- Combat kit (Red Mage casting, dexxer self-care) ----
        private DateTime _nextCastAt = DateTime.MinValue;
        private DateTime _nextCareAt = DateTime.MinValue;

        // ---- Dungeon-mouth ambush ----
        // Every so often a roaming crew walks to a dungeon entrance and
        // lurks it — jumping travelers coming out with their loot. The
        // field PK's whole reason for carrying Tracking.
        private Point3D _ambushSpot;
        private string _ambushName;
        private DateTime _ambushDeadline;   // give up if the walk drags
        private DateTime _lurkUntil;        // set on arrival
        private bool _atAmbush;
        private DateTime _nextAmbushRoll = DateTime.MinValue;

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
                _ when _atAmbush => $"PK — ambushing {_ambushName}{gang}",
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

            // Pick up the editor-drawn hunt leash for this spawn point, if
            // any — a leashed PK never uses the road patrol at all.
            _hunt = PKSpawnData.HuntAreaFor(bot.Location);

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
            // Something (wildlife, a dungeon-mouth guardian) jumped us
            // mid-patrol — turn and fight it with the full kit instead of
            // walking on and getting ground down. The engine swings at
            // Combatant; we add the spells and the bandages.
            if (bot.Combatant is Mobile threat && threat.Alive &&
                !threat.Deleted && threat.Map == bot.Map)
            {
                TryCombatCare(bot);
                TryCombatMagic(bot, threat);
                if (bot.Spell != null)
                {
                    return;
                }
                RearmWeapon(bot);
                if (!bot.InRange(threat.Location, 1) &&
                    bot.Skills[SkillName.Archery].Base < 50.0)
                {
                    var td = bot.GetDirectionTo(threat);
                    if (bot.Direction != td) bot.Direction = td;
                    bot.Move(td);
                }
                return;
            }

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
                    TryBeginPackHunt(bot);
                }
                return;
            }

            // Leashed to an editor-drawn hunt area: prowl inside the
            // polygon, never toward a town. Replaces the road patrol
            // entirely for these reds.
            if (_hunt != null)
            {
                TickHuntAreaRoam(bot);
                if (Core.Now >= _nextScan)
                {
                    _nextScan = Core.Now + ScanInterval;
                    TryBeginPackHunt(bot);
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

            // Dungeon-mouth ambush: either lurking one now (TickAmbush
            // owns the tick), walking to one (fall through — the patrol
            // Traveler is driving the trip), or rolling whether to start.
            if (_ambushSpot != Point3D.Zero)
            {
                if (TickAmbush(bot))
                {
                    return;
                }
            }
            else if (Core.Now >= _nextAmbushRoll)
            {
                _nextAmbushRoll = Core.Now +
                    TimeSpan.FromMinutes(Utility.RandomMinMax(6, 12));
                if (Utility.RandomDouble() < 0.35)
                {
                    TryBeginAmbush(bot);
                }
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

            TryBeginPackHunt(bot);
        }

        // Prowl the editor-drawn hunt polygon: pick a random interior point
        // and walk to it, staying inside the leash. Reds bunch up (pack
        // cohesion) so a drawn area holds a gang, not scattered singles.
        private void TickHuntAreaRoam(PlayerBot bot)
        {
            // Outside the leash (spawn scatter, a knockback) — walk back in.
            if (!_hunt.Contains(bot.X, bot.Y))
            {
                var home = bot.GetDirectionTo(_hunt.Centroid());
                bot.Direction = home;
                bot.Move(home);
                bot.Move(home);
                return;
            }

            // Close ranks with a nearby packmate first.
            if (Core.Now >= _nextPackMove)
            {
                _nextPackMove = Core.Now + TimeSpan.FromSeconds(8);
                var mate = NearestPackmate(bot);
                if (mate != null && _hunt.Contains(mate.X, mate.Y) &&
                    !bot.InRange(mate.Location, 6))
                {
                    var dir = bot.GetDirectionTo(mate.Location);
                    bot.Direction = dir;
                    bot.Move(dir);
                    bot.Move(dir);
                    return;
                }
            }

            if (Core.Now >= _nextRoamPick || _roamTarget == Point3D.Zero ||
                bot.InRange(_roamTarget, 3))
            {
                _roamTarget = RandomPointInHunt(bot);
                _nextRoamPick = Core.Now +
                    TimeSpan.FromSeconds(Utility.RandomMinMax(6, 14));
            }
            if (_roamTarget != Point3D.Zero && !bot.InRange(_roamTarget, 2))
            {
                var dir = bot.GetDirectionTo(_roamTarget);
                bot.Direction = dir;
                bot.Move(dir);
            }
        }

        private Point3D RandomPointInHunt(PlayerBot bot)
        {
            if (_hunt.Hunt == null || _hunt.Hunt.Length < 3)
            {
                return _hunt.Location;
            }
            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;
            foreach (var p in _hunt.Hunt)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            for (int i = 0; i < 12; i++)
            {
                int x = Utility.RandomMinMax(minX, maxX);
                int y = Utility.RandomMinMax(minY, maxY);
                if (_hunt.Contains(x, y))
                {
                    return new Point3D(x, y, bot.Z);
                }
            }
            return _hunt.Centroid();
        }

        // ---- Dungeon-mouth ambush ----

        // Returns true when this tick was consumed by lurking. While the
        // crew is still WALKING to the mouth, returns false so the patrol
        // Traveler keeps driving the trip.
        private bool TickAmbush(PlayerBot bot)
        {
            if (!_atAmbush)
            {
                if (bot.InRange(_ambushSpot, 12))
                {
                    _atAmbush = true;
                    _lurkUntil = Core.Now +
                        TimeSpan.FromMinutes(Utility.RandomMinMax(4, 8));
                    Console.WriteLine(
                        $"[pk] {bot.Name} lurking the mouth of '{_ambushName}'");
                }
                else if (Core.Now >= _ambushDeadline)
                {
                    // The walk dragged (bad route, a running fight on the
                    // way) — give it up and go back to prowling.
                    ClearAmbush(bot);
                }
                return false;
            }

            if (Core.Now >= _lurkUntil)
            {
                ClearAmbush(bot);
                return false;
            }

            // Lurking the mouth: shuffle like the dungeon camp, keep
            // scanning for prey stepping out with its loot.
            if (Core.Now >= _nextCampShuffle)
            {
                _nextCampShuffle = Core.Now +
                    TimeSpan.FromSeconds(Utility.RandomMinMax(4, 10));
                if (bot.InRange(_ambushSpot, 8))
                {
                    var dir = (Direction)Utility.Random(8);
                    bot.Direction = dir;
                    bot.Move(dir);
                }
                else
                {
                    var back = bot.GetDirectionTo(_ambushSpot);
                    bot.Direction = back;
                    bot.Move(back);
                    bot.Move(back);
                }
            }

            if (Core.Now >= _nextScan)
            {
                _nextScan = Core.Now + ScanInterval;
                TryBeginPackHunt(bot);
            }
            return true;
        }

        // Pick a nearby dungeon entrance and send the crew. One red
        // initiates; same-gang mates in earshot adopt the same spot, so
        // the whole gang marches together.
        private void TryBeginAmbush(PlayerBot bot)
        {
            BotDestination pick = null;
            int bestRank = int.MaxValue;
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type != DestinationType.DungeonEntrance)
                {
                    continue;
                }
                // Prefer near mouths, with jitter so a crew doesn't camp
                // the same one forever.
                int rank = (int)bot.GetDistanceToSqrt(d.Location) +
                           Utility.Random(400);
                if (rank < bestRank)
                {
                    bestRank = rank;
                    pick = d;
                }
            }
            if (pick == null)
            {
                return;
            }

            BeginAmbushAt(bot, pick);
            int crew = 1;
            foreach (var m in bot.GetMobilesInRange(30))
            {
                if (m is PlayerBot mate && mate != bot &&
                    mate.Behavior is PKBehavior pk &&
                    pk.GangId == GangId && pk._phase == Phase.Patrol &&
                    pk._ambushSpot == Point3D.Zero && pk._hunt == null &&
                    !DungeonRegistry.IsInDungeon(mate))
                {
                    pk.BeginAmbushAt(mate, pick);
                    crew++;
                }
            }
            Console.WriteLine(
                $"[pk] {bot.Name} leads {crew} red(s) to ambush '{pick.Name}'");
        }

        private void BeginAmbushAt(PlayerBot bot, BotDestination dest)
        {
            _ambushSpot = dest.Location;
            _ambushName = dest.Name;
            _atAmbush = false;
            _ambushDeadline = Core.Now + TimeSpan.FromMinutes(12);

            // Re-aim the patrol at the mouth. The Traveler handles the
            // whole trip (roads, recall if it owns the magic, rescue).
            _patrol?.OnDetached(bot);
            _patrol = new TravelerBehavior
            {
                AvoidTowns = true,
                DestinationName = dest.Name,
            };
            _patrol.OnAttached(bot);
        }

        private void ClearAmbush(PlayerBot bot)
        {
            _ambushSpot = Point3D.Zero;
            _ambushName = null;
            _atAmbush = false;

            _patrol?.OnDetached(bot);
            _patrol = new TravelerBehavior { AvoidTowns = true };
            _patrol.OnAttached(bot);
        }

        // A red only commits to a hunt with a pack (2+ reds) and only when
        // it isn't swamped by blues. This is the "roam in groups, don't
        // suicide into a crowd" rule.
        //
        // Underground it is the wrong rule. A road is open and a lone red on
        // one is a lone red for as long as the fight lasts, which is why he
        // waits for company. A dungeon red is sitting in his own hall on the
        // only way through it, which is the whole reason he is there, and
        // whoever walks in has walked into him — the ambush IS the pack. The
        // rule held every solo dungeon spawn frozen: a red camped a corridor
        // among a dozen blues and never once drew, because no second red was
        // ever going to arrive. The crowd rule below still applies, so he
        // takes on the ones and twos and lets a war party walk past.
        private void TryBeginPackHunt(PlayerBot bot)
        {
            if (PackSize(bot) < 2 && !DungeonRegistry.IsInDungeon(bot))
            {
                return; // lone red on the roads — prowls, won't start a fight
            }
            if (BlueCrowd(bot) >= Math.Max(CrowdRetreatCount, PackSize(bot) + 1))
            {
                return; // too many blues about — bide
            }
            var victim = FindVictim(bot);
            if (victim != null)
            {
                BeginHunt(bot, victim);
            }
        }

        // Fellow reds within pack range, counting this bot.
        //
        // The test is "is this bot red", not "is this bot running the PK
        // brain". They are usually the same bots and were treated as the
        // same thing here, which is wrong in both directions: a red who
        // picked up another brain went uncounted as a packmate, and was
        // then counted as one of the BLUES below, so one broken bot both
        // shrank the pack and swelled the crowd it was measured against.
        private static int PackSize(PlayerBot bot)
        {
            int n = 1;
            foreach (var m in bot.GetMobilesInRange(PackRange))
            {
                if (m != bot && m is PlayerBot other && !other.Deleted &&
                    other.Alive && RedTerritory.IsRed(other))
                {
                    n++;
                }
            }
            return n;
        }

        // Blues (players + non-red bots) close enough to gang up.
        private static int BlueCrowd(PlayerBot bot)
        {
            int n = 0;
            foreach (var m in bot.GetMobilesInRange(CrowdRange))
            {
                if (m == bot || m.Deleted || !m.Alive)
                {
                    continue;
                }
                if (m is PlayerBot pb)
                {
                    if (!RedTerritory.IsRed(pb))
                    {
                        n++;
                    }
                }
                else if (m.Player && m is PlayerMobile && !m.Murderer)
                {
                    // A red player is not part of the crowd a red backs off
                    // from. He has the same problem this bot has.
                    n++;
                }
            }
            return n;
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
                    other.Alive && RedTerritory.IsRed(other))
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

            // Pull in the crew — reds gank, they don't duel.
            AlertGang(bot, victim);
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

            // Keep the pressure on. The engine swings the weapon at
            // Combatant on its own; the upgrades below add the rest of a
            // real killer's game.
            bot.Combatant = victim;
            Taunt(bot);

            // Mid-fight care — bandage under the swings like every real
            // dexxer, pots when it's dire.
            TryCombatCare(bot);

            // The Red Mage throws real spells: Paralyze the runner,
            // e-bolt/explosion the rest. Casting pockets the weapon
            // (pre-AOS ClearHands) — re-arm it between casts, the same
            // tank-mage rhythm the blues use.
            TryCombatMagic(bot, victim);
            if (bot.Spell != null)
            {
                return; // committed to the cast — stand and deliver
            }
            RearmWeapon(bot);

            // Positioning: archers hold their range band; everyone else
            // closes to swing.
            if (bot.Skills[SkillName.Archery].Base >= 50.0)
            {
                int adist = (int)bot.GetDistanceToSqrt(victim.Location);
                if (adist < 3)
                {
                    var away = Opposite(bot.GetDirectionTo(victim));
                    if (bot.Direction != away) bot.Direction = away;
                    bot.Move(away);
                }
                else if (adist > 7)
                {
                    var din = bot.GetDirectionTo(victim);
                    if (bot.Direction != din) bot.Direction = din;
                    bot.Move(din);
                }
                return;
            }

            if (!bot.InRange(victim.Location, 1))
            {
                var d = bot.GetDirectionTo(victim);
                if (bot.Direction != d) bot.Direction = d;
                bot.Move(d);
            }
        }

        // Drink/bandage on a short cadence while fighting or fleeing.
        private void TryCombatCare(PlayerBot bot)
        {
            if (Core.Now < _nextCareAt || bot.HitsMax <= 0)
            {
                return;
            }
            double frac = (double)bot.Hits / bot.HitsMax;
            if (frac >= 0.65 && !bot.Poisoned)
            {
                return;
            }
            _nextCareAt = Core.Now + TimeSpan.FromSeconds(4);

            if (bot.Poisoned && AdventurerBehavior.DrinkCurePotion(bot))
            {
                return;
            }
            if (frac < 0.40 && AdventurerBehavior.DrinkHealPotion(bot))
            {
                return;
            }
            if (bot.Skills[SkillName.Healing].Base >= 50.0)
            {
                AdventurerBehavior.StartBandageSelf(bot);
            }
        }

        // Real ModernUO casting for mage-skilled reds. Launch here; the
        // NEXT tick delivers the target cursor onto the victim (era casts
        // resolved over seconds anyway).
        private void TryCombatMagic(PlayerBot bot, Mobile victim)
        {
            double magery = bot.Skills[SkillName.Magery].Base;
            if (magery < 50.0)
            {
                return;
            }

            // A cursor is up from the last cast — deliver it.
            if (bot.Target != null)
            {
                try { bot.Target.Invoke(bot, victim); } catch { }
                _nextCastAt = Core.Now +
                    TimeSpan.FromSeconds(2.5 + Utility.RandomDouble() * 2.0);
                return;
            }
            if (bot.Spell != null || Core.Now < _nextCastAt)
            {
                return;
            }
            if (!bot.InRange(victim.Location, 10) || !bot.InLOS(victim))
            {
                return;
            }

            string spell;
            if (magery >= 65.0 && bot.Mana >= 14 && !victim.Paralyzed &&
                Utility.RandomDouble() < 0.30)
            {
                spell = "Server.Spells.Fifth.ParalyzeSpell"; // the PK opener
            }
            else if (magery >= 85.0 && bot.Mana >= 20)
            {
                spell = Utility.RandomDouble() < 0.35
                    ? "Server.Spells.Sixth.ExplosionSpell"
                    : "Server.Spells.Sixth.EnergyBoltSpell";
            }
            else if (magery >= 55.0 && bot.Mana >= 11)
            {
                spell = "Server.Spells.Fourth.LightningSpell";
            }
            else if (bot.Mana >= 9)
            {
                spell = "Server.Spells.Third.FireballSpell";
            }
            else
            {
                return; // winded — swing the weapon till the pool refills
            }

            var s = AdventurerBehavior.CreateSpell(spell, bot);
            if (s == null)
            {
                return;
            }
            var face = bot.GetDirectionTo(victim);
            if (bot.Direction != face) bot.Direction = face;
            try
            {
                if (!s.Cast())
                {
                    return;
                }
            }
            catch
            {
                return;
            }
            // Cooldown is stamped when the cursor is delivered.
        }

        // Casting pocketed the weapon (pre-AOS ClearHands) — put it back
        // in hand once the hands are free.
        private static void RearmWeapon(PlayerBot bot)
        {
            if (bot.Spell != null)
            {
                return;
            }
            if (bot.FindItemOnLayer(Layer.TwoHanded) is BaseWeapon ||
                bot.FindItemOnLayer(Layer.OneHanded) is BaseWeapon)
            {
                return;
            }
            var pack = bot.Backpack;
            if (pack == null)
            {
                return;
            }
            foreach (var item in pack.Items)
            {
                if (item is BaseWeapon w && w.Skill != SkillName.Wrestling &&
                    bot.Skills[w.Skill].Base >= 45.0)
                {
                    bot.EquipItem(w);
                    return;
                }
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

            // Chug and bandage WHILE running — a red that flees at 30%
            // and comes back at 70% is how gank crews reset a fight.
            TryCombatCare(bot);

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

            // Never attack another red BOT — professional courtesy, no
            // infighting. Asked of the bot's notoriety rather than its
            // brain, so a red who is running some other routine is still
            // not prey. Deliberately says nothing about red PLAYERS: a red
            // player is still hunted, because whether PKs leave a red
            // player alone is a gameplay call, not a bug fix.
            if (m is PlayerBot pb && RedTerritory.IsRed(pb)) return false;

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
        // Same-gang reds ALWAYS converge on the victim; any other red on
        // patrol nearby usually piles in too — reds have no loyalty, just
        // appetite. (The phase check stops the pull from ping-ponging.)
        private void AlertGang(PlayerBot bot, Mobile victim)
        {
            foreach (var m in bot.Map.GetMobilesInRange(
                         bot.Location, GangConvergeRange))
            {
                if (m is not PlayerBot mate || mate == bot) continue;
                if (mate.Behavior is not PKBehavior pk) continue;
                if (pk._phase != Phase.Patrol) continue;

                bool sameGang = GangId != 0 && pk.GangId == GangId;
                if (sameGang || Utility.RandomDouble() < 0.5)
                {
                    pk.BeginHunt(mate, victim);
                }
            }
        }

        // ---- HELPERS -----------------------------------------------------
        private bool ShouldFlee(PlayerBot bot)
        {
            if (bot.HitsMax > 0 &&
                (double)bot.Hits / bot.HitsMax < FleeHealthPct)
            {
                return true;
            }
            // Blues gathered mid-fight — a mob is death for a red. Break off
            // if the crowd swelled past the pack.
            if (BlueCrowd(bot) >= Math.Max(CrowdRetreatCount, PackSize(bot) + 1))
            {
                return true;
            }
            return false;
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
