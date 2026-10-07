// =========================================================================
// BotDeathManager.cs — death is real now (IDEAS 3.1).
//
// UO's most iconic experience, end to end:
//
//   DIE      Tier-scaled retreat thresholds (AdventurerBehavior) mean
//            novices misjudge fights and sometimes don't make it out.
//   HAUNT    The ghost lingers at the corpse a while (GhostBehavior),
//            drifting and moaning OoOoOo at passers-by.
//   HELP     Anyone living with the skill can raise it on the spot —
//            Resurrection off a mage, bandages off a healer or a
//            grandmaster dexxer (BotResurrectAid).
//   CLIMB    Died underground? The ghost walks OUT (GhostExitBehavior):
//            nearest up-stair on this floor, ride it, repeat, surface.
//   WALK     Then the ghost walks — really walks — to the nearest healer
//            or shrine (a Traveler trip while dead; the shrines we placed
//            finally have their true job).
//   RES      At a REAL ankh or a REAL healer NPC standing there. Sparkle
//            + sound, death robe, half health. Nothing resurrects out of
//            thin air any more; the only exception is the long stranding
//            net at the very bottom of this file.
//   CORPSE   Then the corpse run: travel back to the death spot hoping
//   RUN      the loot's still there. ReclaimCorpse takes the gear back
//            (vanilla self-loot is AOS-only — see the note on it). If the
//            corpse rotted or was looted: "WHO LOOTED MY CORPSE" — and a
//            fresh kit, because a naked bot forever is a bug, not a story.
//
// The flow spans several behaviors (Ghost → GhostExit → Traveler-as-ghost
// → CorpseReclaim → back to normal life); this manager holds the shared
// steps and the decisions between them.
// =========================================================================

using System;
using Server;
using Server.Collections;
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

        // Hard ceiling on total ghost time. A ghost whose route wedges
        // (blocked stairs, a gate that never fires — first soak: a ghost
        // looping at Trinsic's WP 212 forever) has to end up somewhere.
        // The death story must never strand a bot permanently.
        //
        // Twenty minutes, not ten, because a ghost now has real work to
        // do first: climb out of a dungeon, then cross the map on foot to
        // a healer. Ten minutes fired in the middle of an honest climb.
        public static readonly TimeSpan GhostRescueAfter = TimeSpan.FromMinutes(20);

        // Called by TravelerBehavior's tick while dead, and by the ghost
        // behaviors. True = handled (behavior swapped — caller returns).
        //
        // This no longer stands the bot up out of nowhere. It carries the
        // ghost to the nearest res point it could not reach on its own and
        // raises it THERE, so even the rescue puts the bot at an ankh
        // instead of in the middle of a field.
        public static bool CheckGhostRescue(PlayerBot bot)
        {
            if (bot.Alive ||
                bot.LastDeathAt == DateTime.MinValue ||
                Core.Now - bot.LastDeathAt < GhostRescueAfter)
            {
                return false;
            }

            CarryToResPoint(bot, "never made it");
            return true;
        }

        // The net's own move: to the nearest res point, raised there.
        public static void CarryToResPoint(PlayerBot bot, string why)
        {
            var refuge = NearestResPoint(bot);
            if (refuge != null)
            {
                Console.WriteLine(
                    $"[death] {bot.Name}'s ghost {why} — carried to " +
                    $"'{refuge.Name}'");
                bot.MoveToWorld(refuge.ArrivalPoint ?? refuge.Location, bot.Map);
            }

            ResurrectBot(bot, "wedged ghost, carried to a res point");
        }

        // -------------------------------------------------------------------
        // Real resurrection sites — an ANKH you can touch and a HEALER who
        // is actually standing there. This is what replaced the off-screen
        // "wandering healer": nothing raises a bot unless one of these (or
        // another bot with the skill) is genuinely within reach.
        // -------------------------------------------------------------------

        // Ankhs.ResurrectRange. A healer NPC offers at 4 (BaseHealer's
        // OnMovement check), so the ghost only has to get near.
        public const int AnkhResRange   = 2;
        public const int HealerResRange = 4;

        // How far a ghost will look around for one of the above once it has
        // arrived somewhere that ought to have one.
        public const int ResSiteSearchRange = 24;

        // A real healer NPC willing to raise THIS bot, or null. The engine's
        // own CheckResurrect does the deciding, so a murderer gets told
        // "thou'rt not a decent and good person" and stays a ghost — which
        // is exactly why reds are routed to shrines instead.
        public static BaseHealer FindHealerNpc(PlayerBot bot, int range)
        {
            if (bot?.Map == null || bot.Map == Map.Internal)
            {
                return null;
            }

            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, range))
            {
                if (m is BaseHealer h && h.Alive && !h.Deleted &&
                    h.CheckResurrect(bot))
                {
                    return h;
                }
            }
            return null;
        }

        // A real ankh item in range. Rejuvination ankhs are a different
        // class and don't resurrect, so they can't match here.
        public static Item FindAnkh(PlayerBot bot, int range)
        {
            if (bot?.Map == null || bot.Map == Map.Internal)
            {
                return null;
            }

            foreach (var item in bot.Map.GetItemsInRange(bot.Location, range))
            {
                if (item is AnkhNorth or AnkhWest && !item.Deleted)
                {
                    return item;
                }
            }
            return null;
        }

        // Something in reach RIGHT NOW that can raise this ghost. Returns a
        // description for the log line, or null.
        public static string ResurrectorInReach(PlayerBot bot)
        {
            if (FindAnkh(bot, AnkhResRange) != null)
            {
                return "touched the ankh";
            }

            var healer = FindHealerNpc(bot, HealerResRange);
            return healer != null ? $"{healer.Name} the healer" : null;
        }

        // The tile a ghost should float the last few steps to. Null when
        // there's nothing worth walking to nearby.
        public static Point3D? FindResSite(PlayerBot bot, int range = ResSiteSearchRange)
        {
            var ankh = FindAnkh(bot, range);
            if (ankh != null)
            {
                return ankh.GetWorldLocation();
            }

            var healer = FindHealerNpc(bot, range);
            return healer?.Location;
        }

        // Nearest destination that is supposed to HAVE a res site, used by
        // the stranding net. Shrines and healers only; red rules apply.
        private static BotDestination NearestResPoint(PlayerBot bot)
        {
            BotDestination best = null;
            int bestDist = int.MaxValue;

            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type != DestinationType.Healer && d.Type != DestinationType.Shrine)
                {
                    continue;
                }
                if (!RedTerritory.MayGoTo(bot, d))
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
        // OnBotDeath — called from PlayerBot.OnDeath after the journal
        // entry. Starts the ghost flow.
        // -------------------------------------------------------------------
        public static void OnBotDeath(PlayerBot bot, Mobile killer, string how = null)
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
            bot.LastKiller = killer;

            Console.WriteLine(
                $"[death] {bot.Name} was killed by {how ?? killer?.Name ?? "something"} " +
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
            // Underground has no answer of its own — dungeons have almost
            // no ankhs and no healers. GhostExitBehavior walks the ghost
            // up and out first, and only then is this asked.
            if (DungeonRegistry.IsInDungeon(bot))
            {
                return null;
            }

            // Two passes. The first keeps the walk sane; the second drops
            // the distance cap when nothing at all qualified, because
            // "nowhere to go" leaves the ghost standing in a field until
            // the stranding net. That is the murderers' case in
            // particular: the healer on every town corner refuses them
            // (BaseHealer.CheckResurrect), so a red's only res is the
            // shrines, and the shrines are deliberately far from
            // everywhere. A red walking half the map to Compassion is the
            // era working, not a bug.
            return PickResDestination(bot, MaxResWalkDistance)
                ?? PickResDestination(bot, int.MaxValue);
        }

        private static string PickResDestination(PlayerBot bot, int maxDist)
        {
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
                if (dist >= bestDist || dist > maxDist)
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
        //
        // Arriving is no longer the same thing as being raised. The
        // waypoint puts the ghost at the door; the ankh or the healer is
        // the last few tiles past it, and the ghost has to reach one. Only
        // when there is genuinely nothing here does it fall back to
        // standing up on the spot — which now shows in the log as a fault,
        // because it means the destination is mis-authored.
        public static bool OnTravelerArrival(PlayerBot bot)
        {
            if (bot.Alive)
            {
                return false;
            }

            var how = ResurrectorInReach(bot);
            if (how != null)
            {
                ResurrectBot(bot, how);
                return true; // behavior was swapped by the corpse-run start
            }

            var site = FindResSite(bot);
            if (site.HasValue)
            {
                Console.WriteLine(
                    $"[death] {bot.Name}'s ghost is at the door — closing on " +
                    $"the ankh/healer at {site.Value}");
                bot.Behavior = new GhostBehavior { SeekSite = site };
                return true;
            }

            Console.WriteLine(
                $"[death] {bot.Name} arrived at a res point with no ankh and no " +
                $"healer in {ResSiteSearchRange} tiles — check the destination");
            StuckTelemetry.Record(bot, "res_site_missing",
                $"no ankh/healer at ({bot.X},{bot.Y})");
            ResurrectBot(bot, "no res site here");
            return true;
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
        // ReclaimCorpse — take the gear back off the body.
        //
        // Corpse.Open(bot, checkSelfLoot: true) is the vanilla "you quickly
        // gather all of your belongings", and it is an AOS feature twice
        // over. Corpse.Create only records restore info when Core.AOS, and
        // Open then skips every item that has none:
        //
        //     if (owner.Player && Core.AOS)          // Corpse.Create
        //         c.SetRestoreInfo(item, item.Location);
        //
        //     if (... || !GetRestoreInfo(item, ref loc))   // Corpse.Open
        //         continue;
        //
        // This shard is T2A, so Core.AOS is false and there is never any
        // restore info. The call walked the bot all the way to its body,
        // moved nothing at all, set the SelfLooted flag and left it standing
        // there naked. Vanilla is right about the era — T2A had no gather-
        // your-belongings, you dragged your things out of the corpse by hand
        // — but a bot has no hands, and it still logged the reclaim as a
        // success. A bot that FAILED to reach its corpse got a fresh outfit
        // out of GiveUpCorpse, so failing was strictly better than
        // succeeding.
        //
        // So: use vanilla where vanilla works, and do the dragging by hand
        // where it doesn't. Everything movable goes back to the pack, then
        // the bot wears what it can.
        // -------------------------------------------------------------------
        public static void ReclaimCorpse(PlayerBot bot, Corpse corpse)
        {
            if (Core.AOS)
            {
                corpse.Open(bot, checkSelfLoot: true);
                return;
            }

            var pack = bot.Backpack;
            if (pack == null)
            {
                GiveUpCorpse(bot, "no pack to reclaim into");
                return;
            }

            // The robe goes first. It holds the OuterTorso layer that the
            // bot's own chest piece is about to want back.
            if (bot.FindItemOnLayer(Layer.OuterTorso) is DeathRobe robe)
            {
                robe.Delete();
            }

            // Copy the list before moving anything: pack.AddItem mutates
            // corpse.Items underneath the loop.
            using var onBody = PooledRefList<Item>.Create(128);
            onBody.AddRange(corpse.Items);

            int taken = 0, left = 0;

            for (var i = 0; i < onBody.Count; i++)
            {
                var item = onBody[i];

                // Hair and beards are part of how the corpse looks. They are
                // not loot and the bot must not walk off carrying its own
                // hair.
                if (item.Layer is Layer.Hair or Layer.FacialHair)
                {
                    continue;
                }

                if (!item.Movable || pack.CheckHold(bot, item, false, true) != true)
                {
                    left++;
                    continue;
                }

                pack.AddItem(item);
                taken++;
            }

            // Nothing came back — somebody had already emptied it. Same
            // outcome as a corpse that rotted, so say so and re-kit rather
            // than walk away naked and call it a success.
            if (taken == 0)
            {
                GiveUpCorpse(bot, left > 0 ? "corpse held nothing it could carry"
                                           : "corpse was already empty");
                return;
            }

            // Dress again out of what the bot was ACTUALLY wearing when it
            // died. corpse.EquipItems is that record, and using it is the
            // difference between putting the armour back on and putting the
            // spellbook in the sword hand: a weapon, a shield and a
            // spellbook all compete for the same two layers, and "equip
            // whatever comes out of the pack first" picks between them by
            // accident. This is the loop ModernUO's own DuelContext.Refresh
            // uses to hand duellists their kit back, including the
            // IsChildOf check — an item still lying in the corpse because
            // the pack was full must not be equipped out of thin air.
            var worn = corpse.EquipItems;
            int dressed = 0;

            for (var i = 0; worn != null && i < worn.Count; i++)
            {
                var item = worn[i];

                if (item.Movable &&
                    item.Layer is not Layer.Hair and not Layer.FacialHair &&
                    item.IsChildOf(pack) &&
                    bot.EquipItem(item))
                {
                    dressed++;
                }
            }

            bot.EquipFactionShield(); // allegiance survives a death

            // The worn count is the one that matters. "Took back 92" only
            // says the pack filled up; a bot standing in its armour is the
            // thing that was broken.
            Console.WriteLine(
                $"[death] {bot.Name} took back {taken} item(s) from their " +
                $"corpse and put {dressed} back on" +
                (left > 0 ? $" ({left} left behind)" : ""));
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
