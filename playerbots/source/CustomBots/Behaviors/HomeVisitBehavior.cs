// =========================================================================
// HomeVisitBehavior — an owner at its own house.
//
// The bot walks up to the front door, lets itself in with its key (the
// door's own Use: the key in the pack is what opens it), puts spare loot
// in its chest, and stays a while, moving about now and then. Near the end
// of its session it stays until it logs out, so it logs out inside and
// comes back there next time. Otherwise it lets itself out and travels on.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Items;
using Server.Multis;

namespace Server.CustomBots
{
    public class HomeVisitBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Traveler"; // a reload just travels on

        private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(400);

        private enum State { ToDoor, Entering, Inside, Leaving, Exiting }

        private readonly BaseHouse _house;
        private State _state = State.ToDoor;
        private Point3D _outside, _inside;
        private Direction _dirIn;
        private PathFollower _follower;
        private Point3D _walkGoal;
        private Timer _timer;
        private int _tries;
        private DateTime _stateSince, _stayUntil, _nextMove;
        private List<Point3D> _rooms;

        public HomeVisitBehavior(BaseHouse house)
        {
            _house = house;
            ChatCategories = new[] { "small_talk" };
            ChatChance = 0.03;
        }

        public override string GetStatusLine(PlayerBot bot) => _state switch
        {
            State.ToDoor   => "walking up to the house",
            State.Entering => "unlocking the front door",
            State.Inside   => "at home",
            _              => "heading out of the house",
        };

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            if (_house == null || _house.Deleted || !FindDoor(_house, out _outside, out _inside, out _dirIn))
            {
                Leave(bot);
                return;
            }
            _rooms = InteriorTiles(_house);

            // Already inside (logged in at home, or just placed it from in here).
            SetState(_house.IsInside(bot) ? State.Inside : State.ToDoor);
            if (_state == State.Inside)
            {
                StartStay(bot);
            }
            _timer = Timer.DelayCall(TimeSpan.Zero, StepInterval, 0, () => Step(bot));
        }

        public override void OnDetached(PlayerBot bot)
        {
            _timer?.Stop();
            _timer = null;
            base.OnDetached(bot);
        }

        private void SetState(State s)
        {
            _state = s;
            _stateSince = Core.Now;
            _tries = 0;
            _follower = null;
        }

        private void Step(PlayerBot bot)
        {
            if (bot.Deleted || bot.Behavior != this || !bot.Alive || _house.Deleted)
            {
                _timer?.Stop();
                if (!bot.Deleted && bot.Behavior == this)
                {
                    Leave(bot);
                }
                return;
            }

            // A fight comes first; the traveler knows how to defend itself.
            if (bot.Combatant != null && _state != State.Inside)
            {
                Leave(bot);
                return;
            }

            switch (_state)
            {
                case State.ToDoor:
                    if (bot.X == _outside.X && bot.Y == _outside.Y)
                    {
                        bot.Direction = _dirIn;
                        SetState(State.Entering);
                    }
                    else if (!WalkTo(bot, _outside, 0, TimeSpan.FromMinutes(2)))
                    {
                        Leave(bot);
                    }
                    break;

                case State.Entering:
                    if (_house.IsInside(bot) && !OnDoorTile(bot))
                    {
                        SetState(State.Inside);
                        StartStay(bot);
                    }
                    else if (++_tries > 10)
                    {
                        // Locked out (no key) or something in the doorway.
                        Leave(bot);
                    }
                    else
                    {
                        bot.Move(_dirIn);
                    }
                    break;

                case State.Inside:
                    TickInside(bot);
                    break;

                case State.Leaving:
                    if (bot.X == _inside.X && bot.Y == _inside.Y)
                    {
                        bot.Direction = Reverse(_dirIn);
                        SetState(State.Exiting);
                    }
                    else if (!WalkTo(bot, _inside, 0, TimeSpan.FromMinutes(1)))
                    {
                        bot.MoveToWorld(_inside, _house.Map);
                    }
                    break;

                case State.Exiting:
                    if (!_house.IsInside(bot) && !OnDoorTile(bot))
                    {
                        Console.WriteLine($"[homes] {bot.Name} locked up and left home");
                        Leave(bot);
                    }
                    else if (++_tries > 8)
                    {
                        // Can't get out the door. Players logged out and in
                        // to fix this; a bot is simply put on the step.
                        bot.MoveToWorld(_outside, _house.Map);
                        Leave(bot);
                    }
                    else
                    {
                        bot.Move(Reverse(_dirIn));
                    }
                    break;
            }
        }

        private void StartStay(PlayerBot bot)
        {
            int stashed = BotHomes.StashLoot(bot, _house);
            Console.WriteLine($"[homes] {bot.Name} is home" +
                (stashed > 0 ? $", stashed {stashed} item(s)" : ""));

            // A shop in the house: takings, wages, restock.
            BotVendorHouses.OnOwnerHome(bot, _house);

            // Near the end of the session: stay until the session manager
            // logs it out in here.
            bool late = bot.SessionEndsAt != DateTime.MinValue &&
                bot.SessionEndsAt - Core.Now < TimeSpan.FromMinutes(20);
            _stayUntil = Core.Now + (late
                ? TimeSpan.FromMinutes(30)
                : TimeSpan.FromSeconds(Utility.RandomMinMax(180, 600)));
            _nextMove = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(20, 60));
            _walkGoal = Point3D.Zero;
        }

        private void TickInside(PlayerBot bot)
        {
            if (bot.LoggingOut)
            {
                return;
            }
            if (Core.Now >= _stayUntil)
            {
                SetState(State.Leaving);
                return;
            }

            // Move about the house now and then.
            if (_walkGoal != Point3D.Zero)
            {
                if (bot.InRange(_walkGoal, 0) || !WalkTo(bot, _walkGoal, 0, TimeSpan.FromSeconds(20)))
                {
                    _walkGoal = Point3D.Zero;
                }
            }
            else if (Core.Now >= _nextMove && _rooms.Count > 0)
            {
                _walkGoal = _rooms[Utility.Random(_rooms.Count)];
                _follower = null;
                _stateSince = Core.Now;
                _nextMove = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(30, 90));
            }
            else
            {
                TrySpeak(bot);
            }
        }

        // One PathFollower step toward goal. False = gave up (too long).
        private bool WalkTo(PlayerBot bot, Point3D goal, int range, TimeSpan limit)
        {
            if (Core.Now - _stateSince > limit)
            {
                return false;
            }
            _follower ??= new PathFollower(bot, goal);
            _follower.Follow(run: false, range: range);
            return true;
        }

        private bool OnDoorTile(PlayerBot bot)
        {
            foreach (var door in _house.Doors)
            {
                if (door != null && door.X == bot.X && door.Y == bot.Y)
                {
                    return true;
                }
            }
            return false;
        }

        // Back to the road first, with the engine's pathfinder: the stretch
        // between a house and its road node is open country the road
        // follower does not know. Then off on a new trip.
        private void Leave(PlayerBot bot)
        {
            _timer?.Stop();
            if (bot.Deleted || bot.Behavior != this)
            {
                return;
            }
            var road = BotHomes.RoadOf(_house);
            if (road is Point3D r && bot.InRange(r, 45) && !bot.InRange(r, 3))
            {
                bot.Behavior = new ErrandWalkBehavior(r, 2, "walking back to the road",
                    b => b.Behavior = new TravelerBehavior(), b => b.Behavior = new TravelerBehavior());
            }
            else
            {
                bot.Behavior = new TravelerBehavior();
            }
        }

        private static Direction Reverse(Direction d) => (Direction)(((int)d + 4) & 0x7);

        // -------------------------------------------------------------------
        // Geometry
        // -------------------------------------------------------------------

        // The front door: a tile outside it and a tile inside it, and the
        // direction from one to the other.
        public static bool FindDoor(BaseHouse house, out Point3D outside, out Point3D inside, out Direction dirIn)
        {
            outside = inside = Point3D.Zero;
            dirIn = Direction.North;
            var map = house.Map;
            if (map == null || map == Map.Internal)
            {
                return false;
            }

            (int dx, int dy, Direction d)[] sides =
            {
                (0, 1, Direction.North), (0, -1, Direction.South),
                (1, 0, Direction.West), (-1, 0, Direction.East),
            };

            foreach (var door in house.Doors)
            {
                if (door == null || door.Deleted)
                {
                    continue;
                }
                foreach (var (dx, dy, d) in sides)
                {
                    // Inside is one step back from the door. Outside is the
                    // first tile past the house going the other way: the
                    // front steps are part of the house, so it can be up to
                    // three tiles out.
                    int ix = door.X - dx, iy = door.Y - dy;
                    if (!Walkable.TryFindSeedZ(map, ix, iy, door.Z, out var iz) ||
                        !house.IsInside(new Point3D(ix, iy, iz), 16))
                    {
                        continue;
                    }
                    for (int k = 1; k <= 3; k++)
                    {
                        int ox = door.X + dx * k, oy = door.Y + dy * k;
                        if (!Walkable.TryFindSeedZ(map, ox, oy, door.Z, out var oz))
                        {
                            break;
                        }
                        var o = new Point3D(ox, oy, oz);
                        if (!house.IsInside(o, 16))
                        {
                            outside = o;
                            inside = new Point3D(ix, iy, iz);
                            dirIn = d;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // Floor tiles inside the house a mobile can stand on, door tiles left
        // out (furniture goes here, and the owner wanders between them).
        public static List<Point3D> InteriorTiles(BaseHouse house)
        {
            var list = new List<Point3D>();
            var map = house.Map;
            if (map == null || map == Map.Internal)
            {
                return list;
            }
            var mcl = house.Components;
            var doors = new HashSet<(int, int)>();
            foreach (var d in house.Doors)
            {
                if (d != null)
                {
                    doors.Add((d.X, d.Y));
                }
            }
            for (int x = house.X + mcl.Min.X; x <= house.X + mcl.Max.X; x++)
            {
                for (int y = house.Y + mcl.Min.Y; y <= house.Y + mcl.Max.Y; y++)
                {
                    if (doors.Contains((x, y)) ||
                        !Walkable.TryFindSeedZ(map, x, y, house.Z + 7, out var z))
                    {
                        continue;
                    }
                    var p = new Point3D(x, y, z);
                    if (house.IsInside(p, 16) && !NextToDoor(doors, x, y))
                    {
                        list.Add(p);
                    }
                }
            }
            return list;
        }

        private static bool NextToDoor(HashSet<(int, int)> doors, int x, int y) =>
            doors.Contains((x, y + 1)) || doors.Contains((x, y - 1)) ||
            doors.Contains((x + 1, y)) || doors.Contains((x - 1, y));
    }
}
