// =========================================================================
// Walkable.cs — "can a mobile stand on / step to a tile here?"
//
// Z HANDLING (the crux): destinations are often on building floors above the
// land surface but STORED with Z=0, so naive land-Z CanFit fails on every
// interior tile and the flood collapses to 1 tile ("TINY"). We split into:
//
//   TryFindSeedZ  — wide upward scan from land surface. Used ONCE, for the
//                   destination tile, to find the real floor height even if
//                   the stored Z is 0. Generous because it has no "from"
//                   tile to stay continuous with.
//
//   TryFindStandZ — tight climb/drop window around a reference Z. Used for
//                   every flood STEP, so coverage stays connected to the
//                   floor it started on (a ground-floor flood can't teleport
//                   up onto a second story).
// =========================================================================

using Server;

namespace Server.CustomBots
{
    public static class Walkable
    {
        public const int StandHeight = 16;

        // Per-step vertical limits (UO lets a mobile climb a little, drop a
        // lot). Keeps flood coverage on one connected surface.
        private const int MaxClimb = 4;
        private const int MaxDrop  = 20;

        // How far to scan from the land surface when SEEDING a field.
        private const int SeedScanRange = 60;

        // Wide scan: nearest standable Z around the land surface, searching
        // BOTH up (interior floors above land) and down (sunken/below-land
        // tiles, water-adjacent docks, terrain dips). For seeding only.
        public static bool TryFindSeedZ(Map map, int x, int y, int storedZ, out int z)
        {
            z = storedZ;
            if (map == null || map == Map.Internal) return false;

            // Honor the stored Z if it actually fits.
            if (map.CanFit(x, y, storedZ, StandHeight, checkBlocksFit: true, checkMobiles: false))
            { z = storedZ; return true; }

            int landZ = map.GetAverageZ(x, y);

            // Land surface itself.
            if (map.CanFit(x, y, landZ, StandHeight, checkBlocksFit: true, checkMobiles: false))
            { z = landZ; return true; }

            // Expand outward from land Z in both directions, nearest first.
            for (int d = 1; d <= SeedScanRange; d++)
            {
                if (map.CanFit(x, y, landZ + d, StandHeight, checkBlocksFit: true, checkMobiles: false))
                { z = landZ + d; return true; }
                if (map.CanFit(x, y, landZ - d, StandHeight, checkBlocksFit: true, checkMobiles: false))
                { z = landZ - d; return true; }
            }
            return false;
        }

        // Spiral outward from (x,y) to find the nearest tile that has a
        // standable Z. Returns the tile and its Z. Used to REPAIR destination
        // coords that point at a blocked tile (a wall corner, a counter) but
        // are otherwise in the right spot. maxRadius caps the search.
        public static bool NearestStandable(Map map, int x, int y, int maxRadius,
                                            out int fx, out int fy, out int fz)
        {
            fx = x; fy = y; fz = 0;
            if (map == null || map == Map.Internal) return false;

            for (int r = 0; r <= maxRadius; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    // Only the outer ring at this radius (interior already done).
                    if (r > 0 && System.Math.Abs(dx) != r && System.Math.Abs(dy) != r)
                        continue;

                    int tx = x + dx, ty = y + dy;
                    if (TryFindSeedZ(map, tx, ty, 0, out int tz))
                    {
                        fx = tx; fy = ty; fz = tz;
                        return true;
                    }
                }
            }
            return false;
        }

        // Convenience: is there a standable Z at roughly ground level here?
        // CHEAP version for hot paths like HPA* entrance detection, which
        // calls this across the whole map. Checks the land surface and a
        // small window only — NOT the full seed scan (which would make the
        // HPA build do a 120-iteration probe per border tile). Interior
        // floors aren't relevant for cross-cluster borders anyway; those
        // borders are outdoors/terrain.
        public static bool CanStand(Map map, int x, int y)
        {
            if (map == null || map == Map.Internal) return false;
            int landZ = map.GetAverageZ(x, y);
            if (map.CanFit(x, y, landZ, StandHeight, checkBlocksFit: true, checkMobiles: false))
                return true;
            // small window for minor terrain offsets
            for (int d = 1; d <= 6; d++)
            {
                if (map.CanFit(x, y, landZ + d, StandHeight, checkBlocksFit: true, checkMobiles: false))
                    return true;
                if (map.CanFit(x, y, landZ - d, StandHeight, checkBlocksFit: true, checkMobiles: false))
                    return true;
            }
            return false;
        }

        // Tight scan: a standable Z within the climb/drop window of refZ.
        // For flood steps — keeps the field on one connected level.
        public static bool TryFindStandZ(Map map, int x, int y, int refZ, out int z)
        {
            z = refZ;
            if (map == null || map == Map.Internal) return false;

            // Prefer the reference height itself.
            if (map.CanFit(x, y, refZ, StandHeight, checkBlocksFit: true, checkMobiles: false))
            { z = refZ; return true; }

            // Search within the window, nearest-to-reference first.
            for (int d = 1; d <= MaxDrop; d++)
            {
                if (d <= MaxClimb &&
                    map.CanFit(x, y, refZ + d, StandHeight, checkBlocksFit: true, checkMobiles: false))
                { z = refZ + d; return true; }

                if (map.CanFit(x, y, refZ - d, StandHeight, checkBlocksFit: true, checkMobiles: false))
                { z = refZ - d; return true; }
            }
            return false;
        }

        // Can a mobile at (x0,y0,z0) step to adjacent (x1,y1)? Resolves the
        // destination Z within the step window and outputs it for the flood.
        public static bool CanStep(Map map, int x0, int y0, int z0,
                                   int x1, int y1, out int z1)
        {
            z1 = z0;
            if (!TryFindStandZ(map, x1, y1, z0, out z1))
                return false;

            // Diagonal corner-cut guard.
            if (x0 != x1 && y0 != y1)
            {
                bool elbowA = TryFindStandZ(map, x1, y0, z0, out _);
                bool elbowB = TryFindStandZ(map, x0, y1, z0, out _);
                if (!elbowA && !elbowB)
                    return false;
            }
            return true;
        }
    }
}
