// =========================================================================
// DoorHelper.cs — shared "open the door like a player would" helper.
// Used by Traveler stuck-recovery so post-visit bots can LEAVE buildings
// (Shoppers already open doors inbound; outbound was the missing half).
// =========================================================================
using System;
using Server;

namespace Server.CustomBots
{
    public static class DoorHelper
    {
        // Open any closed, unlocked door adjacent to the mobile.
        public static bool TryOpenAdjacent(Mobile m)
        {
            if (m?.Map == null || m.Map == Map.Internal) return false;
            foreach (var item in m.Map.GetItemsInRange(m.Location, 1))
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
