// =========================================================================
// BotDeathManager.cs — death is real now (IDEAS 3.1).
//
// UO's most iconic experience, end to end:
//
//   DIE      Tier-scaled retreat thresholds (AdventurerBehavior) mean
//            novices misjudge fights and sometimes don't make it out.
//   HAUNT    The ghost lingers at the corpse a while (GhostBehavior),
//            drifting and moaning OoOoOo at passers-by.
//   WALK     Surface deaths: the ghost walks — really walks — to the
//            nearest healer or shrine (a Traveler trip while dead; the
//            shrines we placed finally have their true job). Dungeon
//            deaths, or deaths with no reachable res point, get found
//            by "a wandering healer" after the haunt (res in place).
//   RES      Sparkle + sound, death robe, half health.
//   CORPSE   Then the corpse run: travel back to the death spot hoping
//   RUN      the loot's still there. Vanilla self-loot (Corpse.Open)
//            re-equips everything. If the corpse rotted or was looted:
//            "WHO LOOTED MY CORPSE" — and a fresh kit, because a naked
//            bot forever is a bug, not a story.
//
// The flow spans several behaviors (Ghost → Traveler-as-ghost →
// CorpseReclaim → back to normal life); this manager holds the shared
// steps and the decisions between them.
// =========================================================================

using System;
using Server;
using Server.Items;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotDeathManager
    {
        // ---- Knobs ----

        public static bool Enabled = true;

        // How far (straight-line) a ghost is willing to walk for a res.
        // Beyond this a wandering healer "finds them" instead.
        public const int MaxResWalkDistance = 500;

        // Corpse-run handoff: when a corpse-bound Traveler gets this close
        // to the death spot, it stops riding waypoints and walks straight
        // at the corpse.
        public const int CorpseApproachRange = 30;

        // Hard ceiling on total ghost time. A ghost whose res walk wedges
        // (stuck route, blocked gate — first soak: a ghost looping at
        // Trinsic's WP 212 forever) gets found by a wandering healer ON
        // THE SPOT. The death story must never strand a bot permanently.
        public static readonly TimeSpan GhostRescueAfter = TimeSpan.FromMinutes(10);

        // Called by TravelerBehavior's tick while dead. True = rescued
        // (resurrected in place; behavior swapped — caller returns).
        public static bool CheckGhostRescue(PlayerBot bot)
        {
            if (bot.Alive ||
                bot.LastDeathAt == DateTime.MinValue ||
                Core.Now - bot.LastDeathAt < GhostRescueAfter)
            {
                return false;
            }
            ResurrectBot(bot, "wandering healer found the wedged ghost");
            return true;
        }

        // -------------------------------------------------------------------
        // OnBotDeath — called from PlayerBot.OnDeath after the journal
        // entry. Starts the ghost flow.
        // -------------------------------------------------------------------
        public static void OnBotDeath(PlayerBot bot, Mobile killer)
        {
            if (!Enabled || bot == null || bot.Deleted)
            {
                return;
            }

            // Remember what the bot WAS, so a red that dies comes back a
            // red and a mid-crawl death can resume the dive.
            bot.PreDeathBehaviorName = bot.Behavior?.SerializableName ?? "Traveler";
            bot.CorpseRunPending = false;

            // Death-spiral counter (decays after a quiet hour).
            if (Core.Now - bot.LastDeathAt > TimeSpan.FromMinutes(60))
            {
                bot.RecentDeaths = 0;
            }
            bot.RecentDeaths++;
            bot.LastDeathAt = Core.Now;

            Console.WriteLine(
                $"[death] {bot.Name} was killed by {killer?.Name ?? "something"} " +
                $"at ({bot.X},{bot.Y}) — ghost rises");

            bot.Behavior = new GhostBehavior();
        }

        // Nearest place a red can come back that the guards do not watch.
        // Shrines first (era-correct for a murderer), then any ungarded res
        // point, then nothing — the caller resurrects in place rather than
        // leave a ghost standing forever.
        private static BotDestination NearestRefuge(PlayerBot bot)
        {
            BotDestination best = null;
            int bestDist = int.MaxValue;

            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type != DestinationType.Shrine)
                {
                    continue;
                }
                if (RedTerritory.IsGuardedPlace(d, bot.Map))
                {
                    continue;
                }

                int dist = Math.Max(Math.Abs(d.Location.X - bot.X),
                                    Math.Abs(d.Location.Y - bot.Y));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = d;
                }
            }

            return best;
        }

        // -------------------------------------------------------------------
        // After the haunt: where does this ghost get resurrected?
        // Returns a destination name to ghost-walk to, or null for a
        // res-in-place ("a wandering healer found them" / dungeon ankh).
        // -------------------------------------------------------------------
        public static string PickResDestination(PlayerBot bot)
        {
            if (DungeonRegistry.IsInDungeon(bot))
            {
                return null; // dungeons res in place (the level's ankh)
            }

            var graph = WaypointRegistry.Graph;
            var botNode = graph.FindNearestNode(bot.Location);
            int botComp = botNode != null ? graph.ComponentOf(botNode.Name) : -1;

            BotDestination best = null;
            int bestDist = int.MaxValue;
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type != DestinationType.Healer && d.Type != DestinationType.Shrine)
                {
                    continue;
                }

                // Town healers stand inside guarded towns, so walking a red
                // ghost to one only feeds it back to the guards. The era
                // agrees: a murderer's res was the shrines, not the healer
                // on the corner.
                if (!RedTerritory.MayGoTo(bot, d))
                {
                    continue;
                }

                int dist = Math.Max(Math.Abs(d.Location.X - bot.X),
                                    Math.Abs(d.Location.Y - bot.Y));
                if (dist >= bestDist || dist > MaxResWalkDistance)
                {
                    continue;
                }

                // Must be on the ghost's own landmass — a ghost drifting
                // into a MAROONED rescue-teleport reads as a bug.
                if (botComp >= 0)
                {
                    var wp = d.NearestWaypoint;
                    if (string.IsNullOrEmpty(wp) || graph.Get(wp) == null)
                    {
                        wp = graph.FindNearestNode(d.Location)?.Name;
                    }
                    if (wp != null && graph.ComponentOf(wp) != botComp)
                    {
                        continue;
                    }
                }

                best = d;
                bestDist = dist;
            }

            return best?.Name;
        }

        // -------------------------------------------------------------------
        // Resurrect — sparkle, sound, robe (PlayerMobile.Resurrect), half
        // health, a shaky line. Then decide: corpse run or straight back
        // to life (corpse already underfoot / gone).
        // -------------------------------------------------------------------
        public static void ResurrectBot(PlayerBot bot, string how)
        {
            if (bot == null || bot.Deleted || bot.Alive)
            {
                return;
            }

            // Never stand a murderer back up inside a guarded town. That
            // res WAS the death loop: guards cut the red down, a wandering
            // healer put it on its feet on the same tile, the guards cut it
            // down again — thirteen times for one bot in one evening.
            //
            // It is moved to a shrine rather than refused, because this is
            // also the ten-minute safety net for a wedged ghost and the
            // death story must never strand a bot. The era lands in the same
            // place: a murderer's res was the shrines.
            if (RedTerritory.IsRed(bot) &&
                RedTerritory.IsGuardedPlace(bot.Location, bot.Map))
            {
                var refuge = NearestRefuge(bot);
                if (refuge != null)
                {
                    Console.WriteLine(
                        $"[death] {bot.Name} is a murderer — no res under the " +
                        $"guards; carried to '{refuge.Name}'");
                    bot.MoveToWorld(refuge.ArrivalPoint ?? refuge.Location, bot.Map);
                }
            }

            bot.FixedEffect(0x376A, 10, 16);
            bot.PlaySound(0x214);
            bot.Resurrect();
            bot.Hits = Math.Max(10, (int)(bot.HitsMax * 0.55));

            Console.WriteLine($"[death] {bot.Name} resurrected ({how}) at ({bot.X},{bot.Y})");

            var line = ChatLibrary.PickRandom("death_res");
            if (!string.IsNullOrEmpty(line))
            {
                bot.Say(line);
            }

            BeginCorpseRun(bot);
        }

        // -------------------------------------------------------------------
        // BeginCorpseRun — freshly ressed; go get the stuff back.
        // -------------------------------------------------------------------
        private static void BeginCorpseRun(PlayerBot bot)
        {
            if (bot.Corpse is not Corpse corpse || corpse.Deleted)
            {
                // Nothing left to run to.
                GiveUpCorpse(bot, "corpse already gone at res time");
                return;
            }

            var corpseLoc = corpse.GetWorldLocation();
            int dist = Math.Max(Math.Abs(corpseLoc.X - bot.X),
                                Math.Abs(corpseLoc.Y - bot.Y));

            bot.CorpseRunPending = true;

            if (dist <= CorpseApproachRange)
            {
                // Ressed at/near the death spot (dungeon ankh, wandering
                // healer) — walk straight to the body.
                bot.Behavior = new CorpseReclaimBehavior();
                return;
            }

            // Long run: ride the waypoint graph toward the destination
            // nearest the corpse; TravelerBehavior's corpse-approach check
            // breaks off for the last stretch.
            var destName = NearestDestinationTo(corpseLoc);
            if (destName == null)
            {
                bot.Behavior = new CorpseReclaimBehavior(); // hail mary walk
                return;
            }

            Console.WriteLine(
                $"[death] {bot.Name} starts the corpse run " +
                $"(~{dist} tiles, via '{destName}')");
            bot.Behavior = new TravelerBehavior { DestinationName = destName };
        }

        private static string NearestDestinationTo(Point3D loc)
        {
            BotDestination best = null;
            int bestDist = int.MaxValue;
            foreach (var d in DestinationCatalog.All)
            {
                // Interior points aren't Traveler-routable.
                if (d.Type is DestinationType.DungeonRoom
                          or DestinationType.DungeonDescend
                          or DestinationType.DungeonAscend)
                {
                    continue;
                }
                int dist = Math.Max(Math.Abs(d.Location.X - loc.X),
                                    Math.Abs(d.Location.Y - loc.Y));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = d;
                }
            }
            return best?.Name;
        }

        // -------------------------------------------------------------------
        // Traveler hooks.
        // -------------------------------------------------------------------

        // Called at the top of TravelerBehavior.HandleArrival. A DEAD
        // traveler arriving anywhere is a ghost completing its res walk.
        public static bool OnTravelerArrival(PlayerBot bot)
        {
            if (!bot.Alive)
            {
                ResurrectBot(bot, "reached a healer");
                return true; // behavior was swapped by the corpse-run start
            }
            return false;
        }

        // Called each Traveler tick while CorpseRunPending: break off the
        // waypoint route once the corpse is close and walk straight at it.
        // Returns true if the behavior was swapped (caller must return).
        public static bool TryCorpseApproach(PlayerBot bot)
        {
            if (bot.Corpse is not Corpse corpse || corpse.Deleted)
            {
                GiveUpCorpse(bot, "corpse decayed mid-run");
                return false; // keep traveling; life goes on
            }

            var loc = corpse.GetWorldLocation();
            int dist = Math.Max(Math.Abs(loc.X - bot.X), Math.Abs(loc.Y - bot.Y));
            if (dist > CorpseApproachRange)
            {
                return false;
            }

            bot.Behavior = new CorpseReclaimBehavior();
            return true;
        }

        // -------------------------------------------------------------------
        // GiveUpCorpse — looted, rotted, or unreachable. Grumble (the era
        // demands it), shed the death robe, and re-kit so the bot doesn't
        // wander naked forever.
        // -------------------------------------------------------------------
        public static void GiveUpCorpse(PlayerBot bot, string reason)
        {
            bot.CorpseRunPending = false;

            Console.WriteLine($"[death] {bot.Name} lost their gear ({reason})");

            var line = ChatLibrary.PickRandom("death_looted");
            if (!string.IsNullOrEmpty(line))
            {
                bot.Say(line);
            }

            if (bot.FindItemOnLayer(Layer.OuterTorso) is DeathRobe robe)
            {
                robe.Delete();
            }
            EquipmentTable.RollOutfit(bot, bot.Class, bot.SkillTier);
            bot.EquipFactionShield(); // allegiance survives a looted corpse
        }

        // -------------------------------------------------------------------
        // ResumeLife — the death story is over (gear reclaimed or re-kitted).
        // Return to what the bot was doing before it died.
        // -------------------------------------------------------------------
        public static void ResumeLife(PlayerBot bot)
        {
            bot.CorpseRunPending = false;

            if (DungeonRegistry.IsInDungeon(bot))
            {
                // Death-spiral brake: a bot that has died TWICE recently is
                // done with this place — a crawler ressed at half health in
                // a respawning room otherwise dies to the same scorpion
                // forever (observed in the first soak: 5 deaths in 15 min).
                // Very human, too: two deaths in Despise and you go home.
                if (bot.RecentDeaths >= 2 && EvacuateDungeon(bot))
                {
                    return;
                }
                // A red stays a red. This line used to hand a dungeon death
                // the crawler brain no matter what the bot was, so a PK who
                // died once in his own hall came back a monster hunter and
                // never hunted a player again. He keeps his murder counts
                // either way, so the result was a permanent murderer running
                // a civilian's routine. BotLifecycleManager refuses to
                // re-brain a PK for that exact reason; the death flow was
                // doing it anyway.
                bot.Behavior = ResumeBrain(bot, () => new DungeonCrawlerBehavior());
                return;
            }

            bot.Behavior = ResumeBrain(bot, () => BehaviorRegistry.Create("Traveler"));
        }

        // What the bot goes back to being. PK first, always; otherwise
        // whatever this spot in the death flow would normally hand out.
        private static PlayerBotBehavior ResumeBrain(
            PlayerBot bot, Func<PlayerBotBehavior> otherwise) =>
            bot.PreDeathBehaviorName == "PK"
                ? BehaviorRegistry.Create("PK")
                : otherwise();

        // -------------------------------------------------------------------
        // EvacuateDungeon — move a twice-dead bot to its dungeon's surface
        // entrance (offset off the pad so it doesn't step straight back in)
        // and send it traveling. Matches the entrance by dungeon tag; room
        // tags can carry authoring suffixes ("Despise lvl1 ratmen"), so the
        // match is prefix-based both ways.
        // -------------------------------------------------------------------
        private static bool EvacuateDungeon(PlayerBot bot)
        {
            var interior = DungeonRegistry.NearestPoint(bot.Location, 200);
            if (interior == null || string.IsNullOrEmpty(interior.Dungeon))
            {
                return false;
            }

            BotDestination entrance = null;
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type != DestinationType.DungeonEntrance ||
                    string.IsNullOrEmpty(d.Dungeon))
                {
                    continue;
                }
                if (interior.Dungeon.StartsWith(d.Dungeon, StringComparison.OrdinalIgnoreCase) ||
                    d.Dungeon.StartsWith(interior.Dungeon, StringComparison.OrdinalIgnoreCase))
                {
                    entrance = d;
                    break;
                }
            }
            if (entrance == null)
            {
                return false;
            }

            // A few tiles off the pad, on a standable tile.
            var map = Map.Felucca;
            var pad = entrance.Location;
            Point3D spot = pad;
            foreach (var (dx, dy) in new[] { (5, 0), (0, 5), (-5, 0), (0, -5), (4, 4), (-4, -4) })
            {
                int x = pad.X + dx, y = pad.Y + dy;
                int z = map.GetAverageZ(x, y);
                if (map.CanFit(x, y, z, 16, false, false))
                {
                    spot = new Point3D(x, y, z);
                    break;
                }
            }

            Console.WriteLine(
                $"[death] {bot.Name} has died {bot.RecentDeaths} times — " +
                $"leaving {interior.Dungeon} for the surface");
            bot.MoveToWorld(spot, map);
            // Same rule as ResumeLife: a red leaves the dungeon still a red.
            // A murderer on a Traveler brain walks into guarded towns on
            // errands and dies to the guards on repeat. A PK put down at a
            // dungeon mouth prowls it, which is a thing PKs already do.
            bot.Behavior = ResumeBrain(bot, () => new TravelerBehavior());
            return true;
        }
    }
}
