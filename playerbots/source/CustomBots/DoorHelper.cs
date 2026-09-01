// =========================================================================
// DoorHelper.cs — shared "open the door like a player would" helper.
//
// TryOpenAhead is the auto-open-doors behaviour every UO client has had
// a checkbox for: walk into a closed door and it opens. PlayerBot.Move
// calls it whenever a step is refused, which is what keeps bots from
// piling up inside dungeon rooms and the Britain bank.
//
// TryOpenAdjacent is the older, looser sweep used by Traveler
// stuck-recovery so post-visit bots can LEAVE buildings (Shoppers
// already open doors inbound; outbound was the missing half).
// =========================================================================
using System;
using Server;
using Server.Items;
using CalcMoves = Server.Movement.Movement;

namespace Server.CustomBots
{
    public static class DoorHelper
    {
        // Open the closed door in the tile directly ahead, using it exactly
        // as a player double-clicking would. BaseDoor.Use carries the rules
        // with it: locked doors stay shut unless the bot has the key or is
        // inside, and house doors run their own CheckAccess, so this cannot
        // let a bot walk into someone's locked home.
        //
        // ONLY closed doors are touched. Use() toggles, so calling it on an
        // open door would slam it shut on whoever is walking through it —
        // a step can fail for reasons that have nothing to do with the door
        // (another bot standing in the doorway being the common one).
        public static bool TryOpenAhead(Mobile m, Direction d)
        {
            var map = m?.Map;
            if (map == null || map == Map.Internal || !m.CheckAlive())
            {
                return false;
            }

            int x = m.X, y = m.Y;
            CalcMoves.Offset(d, ref x, ref y);

            foreach (var item in map.GetItemsAt(x, y))
            {
                if (item is not BaseDoor door || door.Open)
                {
                    continue;
                }

                // Same vertical window the client's open-door macro uses, so
                // a door on the floor above is not reachable from down here.
                if (door.Z + door.ItemData.Height <= m.Z || m.Z + 16 <= door.Z)
                {
                    continue;
                }

                if (!m.CanSee(door) || !m.InLOS(door))
                {
                    continue;
                }

                door.Use(m);

                // Use() is a no-op on a locked door the bot cannot open, so
                // report what actually happened rather than that we tried.
                return door.Open;
            }

            return false;
        }

        // Open any closed, unlocked door adjacent to the mobile.
        public static bool TryOpenAdjacent(Mobile m) => TryOpenNear(m, 1);

        // Same, within a radius. A bot walking to a door stops ArrivalRange
        // tiles short of it, so "adjacent" is not close enough for anything
        // that ROUTED to a door on purpose.
        public static bool TryOpenNear(Mobile m, int range)
        {
            if (m?.Map == null || m.Map == Map.Internal) return false;
            foreach (var item in m.Map.GetItemsInRange(m.Location, range))
            {
                if (item is Server.Items.BaseDoor d && !d.Open && !d.Locked &&
                    Math.Abs(d.Z - m.Z) <= 15)
                {
                    d.Open = true;
                    return true;
                }
            }
            return false;
        }
    }
}
