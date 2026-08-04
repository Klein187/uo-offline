// =========================================================================
// GathererBehavior.cs — a lumberjack/miner working a wilderness spot
// (IDEAS 1.5 "lumberjack in the middle of nowhere" + the supply side of
// the 4.1 economy loop).
//
// Attached by the Traveler handoff when a gatherer class arrives at a
// GatherSpot. The bot works: swings its tool at the treeline/rock face
// (real animation + the chop/dig sound), accumulates REAL logs/ore in
// its pack, and mutters work chatter. When the shift ends (visit timer)
// it shoulders the load — HaulPending — and travels to town, where
// TravelerBehavior's delivery hook plays the handoff scene at a crafter
// or the bank.
//
// THE SITE IS THE PAINTED POLYGON. A site drawn in the map editor is the
// mine / the grove, and the shift only happens INSIDE it — arriving
// "near" the shape is not arriving. A bot that clocks in outside (the
// last-leg drift is short and wilderness waypoints sit on the road, so it
// often stops short) walks itself in first and swings nothing on the way;
// if it can't get inside, it gives up and travels on rather than mining
// the roadside. Working the face keeps it inside too: the shuffle only
// takes steps that stay within the polygon. Unpainted sites keep the old
// stand-where-you-landed behavior.
//
// If something attacks mid-shift, the tool is a real axe: swap to a
// defender and fight (the classic UO lumberjack). The shift is lost;
// the defender revert sends them traveling and the destination roll
// usually points at another spot.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    public class GathererBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Gatherer";

        // Swing cadence and yield.
        private static readonly TimeSpan SwingInterval   = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan HarvestInterval = TimeSpan.FromSeconds(35);
        private const int MaxCarried = 60; // stop stuffing the pack past this

        // Walking in from wherever the traveler stopped. Own step timer +
        // PathFollower, same pattern as the corpse run.
        private static readonly TimeSpan StepInterval  = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan WalkInTimeout = TimeSpan.FromSeconds(75);

        // How far off a site a bot may be handed off and still be counted
        // as "sent here" when it has forgotten its site name (post-save).
        private const int SiteRecoveryRange = 40;

        private DateTime _nextSwing;
        private DateTime _nextHarvest;
        private Point3D _anchor;

        // The painted work site, and the walk-in state for reaching it.
        private PaintedZone _site;
        private Point3D _walkInGoal;
        private DateTime _walkInStartedAt;
        private bool _clockedIn;
        private bool _shiftStamped;
        private PathFollower _follower;
        private Timer _stepTimer;

        // The destination that sent this bot here. Set by the Traveler
        // handoff; null on a behavior restored from a save (the site is
        // then recovered from the bot's position instead).
        public string SiteName { get; set; }

        public override string GetStatusLine(PlayerBot bot)
        {
            if (_site != null && !_clockedIn)
            {
                return bot.Class == BotClass.Miner
                    ? "walking in to the dig site"
                    : "walking in to the tree line";
            }
            return bot.Class == BotClass.Miner ? "mining a rock face" : "chopping wood";
        }

        public GathererBehavior()
        {
            ChatCategories  = new[] { "gather_talk", "small_talk" };
            ChatChance      = 0.10;
            MinChatCooldown = TimeSpan.FromSeconds(45);
            MaxChatCooldown = TimeSpan.FromSeconds(120);
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            _anchor = bot.Location;
            _nextSwing = Core.Now;
            _nextHarvest = Core.Now + HarvestInterval;

            _site = ResolveSite(bot);
            _clockedIn = _site == null || _site.Contains(bot.X, bot.Y);
            // Already standing in the site: the handoff's window IS the
            // shift, so a later re-entry must not restamp it.
            _shiftStamped = _clockedIn;
            if (_site != null && !_clockedIn)
            {
                _walkInGoal = _site.InteriorGoal(bot.Map, bot.Z);
                _walkInStartedAt = Core.Now;
            }
            else
            {
                // Handed off (or restored) already standing in the site —
                // the ClockIn path never runs for this shift, so kit up
                // here: bring up the pack beast that carries the yield.
                // (Gatherers spawn unmounted now — their only animal is
                // the pack beast — but keep the dismount as a backstop
                // against any odd handoff arriving in the saddle.)
                if (bot.Mounted)
                {
                    BotMountHelper.DismountAndDelete(bot);
                }
                BotPackAnimals.SpawnFor(bot);
            }

            // Organic arrivals get a visit window from the handoff; a
            // directly-attached gatherer (admin, load) stamps its own.
            VisitExpiresAt ??= Core.Now + TimeSpan.FromMinutes(Utility.RandomMinMax(4, 8));
        }

        public override void OnDetached(PlayerBot bot)
        {
            StopStepping();
            base.OnDetached(bot);
        }

        // Which painted site is this? By name when the handoff told us, else
        // by where the bot is standing (a shift restored from a save), else
        // the nearest site — a bot handed off just outside the shape.
        private PaintedZone ResolveSite(PlayerBot bot)
        {
            if (!string.IsNullOrEmpty(SiteName))
            {
                var byName = ZoneRegistry.AreaForDestination(SiteName, bot.Location);
                if (byName != null && byName.IsGatherSite)
                {
                    return byName;
                }
                // Named a site with no polygon painted for it — that site
                // has no shape to stay inside, so work where we landed.
                if (byName == null && DestinationCatalog.GetByName(SiteName) != null)
                {
                    return null;
                }
            }

            return ZoneRegistry.GatherAreaAt(bot.X, bot.Y)
                ?? ZoneRegistry.NearestGatherArea(bot.Location, SiteRecoveryRange);
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal || bot.Deleted || !bot.Alive)
            {
                StopStepping();
                return;
            }

            // Attacked mid-shift: drop the work, raise the axe. The
            // defender's revert-to-Traveler resumes ordinary life.
            if (bot.Combatant is Mobile threat && threat.Alive && !threat.Deleted)
            {
                StopStepping();
                bot.Behavior = new AdventurerBehavior
                {
                    DefenderMode = true,
                    DefenderRetreatHpFraction = 0.45,
                };
                return;
            }

            // Shift over — shoulder the load and head to town. The
            // destination roll sees HaulPending and points at the bank /
            // the crafter who buys this material.
            if (VisitExpiresAt != null && Core.Now >= VisitExpiresAt.Value)
            {
                StopStepping();
                bot.HaulPending = true;
                var line = ChatLibrary.PickRandom("gather_haul");
                if (!string.IsNullOrEmpty(line))
                {
                    bot.Say(line);
                }
                bot.Behavior = BehaviorRegistry.Create("Traveler");
                return;
            }

            // Outside the painted site: no ore comes out of the roadside.
            // Walk in (or give up) — nothing else happens this tick.
            if (_site != null && !_site.Contains(bot.X, bot.Y))
            {
                if (_clockedIn)
                {
                    // Shoved out mid-shift (knockback, a fight that moved
                    // us). Stop working, walk back in on a fresh clock.
                    _clockedIn = false;
                    _walkInGoal = _site.InteriorGoal(bot.Map, bot.Z);
                    _walkInStartedAt = Core.Now;
                    StopStepping();
                }
                TickWalkIn(bot);
                return;
            }

            if (!_clockedIn)
            {
                ClockIn(bot);
            }

            TrySpeak(bot);

            // Work theater: face the "work face", swing, thunk.
            if (Core.Now >= _nextSwing)
            {
                _nextSwing = Core.Now + SwingInterval +
                    TimeSpan.FromMilliseconds(Utility.Random(1500));

                if (Utility.RandomDouble() < 0.15)
                {
                    StepAlongTheFace(bot);
                }

                // Swing animation (one-hand chop) + the trade sound.
                // Never from the saddle — mounted bodies can't play the
                // work animations (ClockIn dismounts; this is the backstop
                // for any path that re-mounted mid-shift).
                if (!bot.Mounted)
                {
                    bot.Animate(11, 5, 1, true, false, 0);
                    bot.PlaySound(bot.Class == BotClass.Miner ? 0x125 : 0x13E);
                }
            }

            // The yield: real stackables into the pack.
            if (Core.Now >= _nextHarvest)
            {
                _nextHarvest = Core.Now + HarvestInterval +
                    TimeSpan.FromSeconds(Utility.Random(20));
                AddYield(bot);
            }
        }

        // The bot just crossed into the site — the shift starts HERE, so the
        // walk-in doesn't eat the working window. Only the FIRST clock-in
        // stamps the shift; walking back in after being shoved out doesn't
        // buy another one.
        private void ClockIn(PlayerBot bot)
        {
            StopStepping();
            _clockedIn = true;
            _anchor = bot.Location;
            _nextSwing = Core.Now;
            _nextHarvest = Core.Now + HarvestInterval;

            // Gatherers spawn unmounted (their only animal is the pack
            // beast) — this dismount is just a backstop against any odd
            // handoff arriving in the saddle. Nobody mines from one.
            if (bot.Mounted)
            {
                BotMountHelper.DismountAndDelete(bot);
            }

            if (!_shiftStamped)
            {
                _shiftStamped = true;
                VisitExpiresAt = Core.Now + TimeSpan.FromMinutes(Utility.RandomMinMax(4, 8));

                // The shift's pack beast — the yield rides in ITS pack
                // (miners favor llamas, lumberjacks horses). It follows
                // the bot through the shift and the haul to town, where
                // the delivery stables it.
                BotPackAnimals.SpawnFor(bot);
            }
        }

        // Walking in from wherever the traveler's last-leg drift ran out.
        private void TickWalkIn(PlayerBot bot)
        {
            if (Core.Now - _walkInStartedAt > WalkInTimeout)
            {
                // Can't reach the face — wedged behind the mountain, or the
                // polygon is painted over unwalkable ground. Don't stand in
                // the wilderness pretending to mine; go somewhere else.
                StopStepping();
                Console.WriteLine($"[gather] {bot.Name} couldn't get inside " +
                                  $"'{_site.Name}' — leaving instead of working outside it");
                bot.Behavior = BehaviorRegistry.Create("Traveler");
                return;
            }

            EnsureStepping(bot);
        }

        // Work the face: a step in a random direction, but only ever onto a
        // tile that's still inside the site. Blocked ones just don't happen;
        // if we've somehow ended up on the rim, step back toward the middle.
        private void StepAlongTheFace(PlayerBot bot)
        {
            if (_site == null)
            {
                // Unpainted site — the old anchor leash.
                var d = (Direction)Utility.Random(8);
                if (bot.InRange(_anchor, 4))
                {
                    bot.Direction = d;
                    bot.Move(d);
                }
                else
                {
                    var home = bot.GetDirectionTo(_anchor);
                    bot.Direction = home;
                    bot.Move(home);
                }
                return;
            }

            var dir = (Direction)Utility.Random(8);
            int nx = bot.X, ny = bot.Y;
            Offset(dir, ref nx, ref ny);
            if (_site.Contains(nx, ny))
            {
                bot.Direction = dir;
                bot.Move(dir);
                return;
            }

            // That step would leave the site — drift back in instead.
            var inward = bot.GetDirectionTo(_site.InteriorGoal(bot.Map, bot.Z));
            int ix = bot.X, iy = bot.Y;
            Offset(inward, ref ix, ref iy);
            bot.Direction = inward;
            if (_site.Contains(ix, iy))
            {
                bot.Move(inward);
            }
        }

        private static void Offset(Direction d, ref int x, ref int y)
        {
            switch (d & Direction.Mask)
            {
                case Direction.North: --y; break;
                case Direction.Right: ++x; --y; break;
                case Direction.East:  ++x; break;
                case Direction.Down:  ++x; ++y; break;
                case Direction.South: ++y; break;
                case Direction.Left:  --x; ++y; break;
                case Direction.West:  --x; break;
                case Direction.Up:    --x; --y; break;
            }
        }

        private void EnsureStepping(PlayerBot bot)
        {
            if (_stepTimer?.Running == true)
            {
                return;
            }
            _stepTimer = Timer.DelayCall(TimeSpan.Zero, StepInterval, 0, () =>
            {
                if (bot.Deleted || bot.Behavior != this || !bot.Alive)
                {
                    StopStepping();
                    return;
                }
                if (_site != null && _site.Contains(bot.X, bot.Y))
                {
                    // Crossed the boundary — the Tick clocks us in.
                    StopStepping();
                    return;
                }
                _follower ??= new PathFollower(bot, _walkInGoal);
                _follower.Follow(run: false, range: 1);
            });
        }

        private void StopStepping()
        {
            _stepTimer?.Stop();
            _stepTimer = null;
            _follower = null;
        }

        private static int CountYield(Server.Items.Container pack)
        {
            if (pack == null)
            {
                return 0;
            }
            int n = 0;
            foreach (var item in pack.Items)
            {
                if (item is Server.Items.Log or Server.Items.IronOre)
                {
                    n += item.Amount;
                }
            }
            return n;
        }

        private static void AddYield(PlayerBot bot)
        {
            if (bot.Backpack == null)
            {
                return;
            }

            // The pack beast carries the load when there is one — that's
            // what it's FOR, and it doubles the haul a shift brings home.
            var beast = bot.PackAnimal is { Deleted: false } pa && pa.Backpack != null
                ? pa
                : null;

            int carried = CountYield(bot.Backpack) +
                          (beast != null ? CountYield(beast.Backpack) : 0);
            int cap = beast != null ? MaxCarried * 2 : MaxCarried;
            if (carried >= cap)
            {
                return;
            }

            Item yield = bot.Class == BotClass.Miner
                ? new Server.Items.IronOre(Utility.RandomMinMax(2, 6))
                : new Server.Items.Log(Utility.RandomMinMax(3, 8));

            if (beast != null)
            {
                beast.Backpack.DropItem(yield);
                if (Utility.RandomDouble() < 0.20)
                {
                    BotScene.Deliver(bot, bot.Class == BotClass.Miner
                        ? "*loads ore onto the pack beast*"
                        : "*straps logs onto the pack beast*");
                }
                return;
            }

            if (!bot.Backpack.TryDropItem(bot, yield, sendFullMessage: false))
            {
                yield.Delete();
            }
        }
    }
}
