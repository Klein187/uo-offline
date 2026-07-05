// =========================================================================
// BotPadAudit.cs — functional audit of every dungeon teleporter record.
//
// The nav audit validates COORDINATES against teleporters.json; this one
// validates REALITY: for each DungeonEntrance / DungeonDescend /
// DungeonAscend destination it
//   1. looks for a real, ACTIVE Teleporter item at the arrival tile
//      (records generated from teleporters.json entries that never became
//      in-world items are the "ascend to nowhere" phantoms);
//   2. cross-checks the item's PointDest against the record's Target;
//   3. walks a hidden Player-flagged probe onto the pad and confirms the
//      probe actually teleports, and lands where the record claims.
//
// Output: console lines + Data/Live/padaudit_report.json.
// Run via [AuditPads (GameMaster) or the padaudit_request.txt token.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;
using Server.Items;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotPadAudit
    {
        public static void Configure()
        {
            CommandSystem.Register("AuditPads", AccessLevel.GameMaster, OnCommand);
        }

        private static void OnCommand(CommandEventArgs e)
        {
            var findings = Run();
            e.Mobile.SendMessage($"Pad audit: {findings.Count} finding(s) — see console/report.");
        }

        public static List<string> Run()
        {
            var findings = new List<string>();
            var map = Map.Felucca;
            int checked_ = 0, fired = 0;

            // One probe for the whole run. A bare hidden PlayerMobile with
            // the Player flag passes Teleporter.CanTeleport exactly like a
            // PlayerBot does, without waking any bot systems.
            var probe = new PlayerMobile
            {
                Name = "pad probe",
                Body = 0x190,
                Hidden = true,
                Blessed = true,
                Player = true,
            };

            try
            {
                foreach (var d in DestinationCatalog.All)
                {
                    if (d.Type != DestinationType.DungeonEntrance &&
                        d.Type != DestinationType.DungeonDescend &&
                        d.Type != DestinationType.DungeonAscend)
                    {
                        continue;
                    }

                    checked_++;
                    var pad = d.ArrivalPoint ?? d.Location;
                    var spot = d.PickArrival();
                    if (spot != null)
                    {
                        pad = spot.Point;
                    }

                    // ---- 1. the physical item ----
                    Teleporter tele = null;
                    foreach (var item in map.GetItemsInRange<Teleporter>(
                                 new Point3D(pad.X, pad.Y, pad.Z), 1))
                    {
                        tele = item;
                        if (item.X == pad.X && item.Y == pad.Y)
                        {
                            break; // exact tile wins
                        }
                    }

                    if (tele == null)
                    {
                        findings.Add($"NO ITEM: '{d.Name}' — no Teleporter within 1 " +
                                     $"of ({pad.X},{pad.Y},{pad.Z})");
                        continue;
                    }
                    if (!tele.Active)
                    {
                        findings.Add($"INACTIVE: '{d.Name}' — Teleporter at " +
                                     $"({tele.X},{tele.Y},{tele.Z}) is switched off");
                        continue;
                    }

                    // ---- 2. destination cross-check ----
                    if (d.Target.HasValue)
                    {
                        var t = d.Target.Value;
                        int miss = Math.Max(Math.Abs(tele.PointDest.X - t.X),
                                            Math.Abs(tele.PointDest.Y - t.Y));
                        if (miss > 5)
                        {
                            findings.Add($"DEST MISMATCH: '{d.Name}' — item sends to " +
                                         $"({tele.PointDest.X},{tele.PointDest.Y}) but record " +
                                         $"says ({t.X},{t.Y})");
                        }
                    }

                    // ---- 3. the probe walk ----
                    // A pad on stairs fires from below but not from above:
                    // try stepping on from EVERY standable neighbor before
                    // declaring the pad unusable — one firing side is all a
                    // walking bot needs.
                    bool jumped = false;
                    bool anyStart = false;
                    var lastProbe = Point3D.Zero;
                    foreach (var start in StepOffTiles(map, tele))
                    {
                        anyStart = true;
                        probe.MoveToWorld(start, map);
                        var dir = probe.GetDirectionTo(tele.Location);
                        probe.Direction = dir;
                        probe.Move(dir);
                        lastProbe = probe.Location;
                        // Fired = probe materialized at the item's dest.
                        // Distance alone misses SHORT-HOP pads (Hythloth's
                        // lava-gap shortcuts move you 4 tiles).
                        if (Math.Max(Math.Abs(probe.X - tele.PointDest.X),
                                     Math.Abs(probe.Y - tele.PointDest.Y)) <= 2 ||
                            Math.Max(Math.Abs(probe.X - tele.X),
                                     Math.Abs(probe.Y - tele.Y)) > 20)
                        {
                            jumped = true;
                            break;
                        }
                    }

                    if (!anyStart)
                    {
                        findings.Add($"NO APPROACH: '{d.Name}' — no standable tile " +
                                     $"beside the pad at ({tele.X},{tele.Y},{tele.Z})");
                        continue;
                    }
                    if (!jumped)
                    {
                        findings.Add($"PAD UNUSABLE: '{d.Name}' — no approach side " +
                                     $"fires ({tele.X},{tele.Y},{tele.Z}); last probe " +
                                     $"({lastProbe.X},{lastProbe.Y},{lastProbe.Z})");
                        continue;
                    }

                    fired++;
                    int off = Math.Max(Math.Abs(probe.X - tele.PointDest.X),
                                       Math.Abs(probe.Y - tele.PointDest.Y));
                    if (off > 5)
                    {
                        findings.Add($"ODD LANDING: '{d.Name}' — probe landed at " +
                                     $"({probe.X},{probe.Y}) vs item dest " +
                                     $"({tele.PointDest.X},{tele.PointDest.Y})");
                    }
                }
            }
            finally
            {
                probe.Delete();
            }

            Console.WriteLine($"[AuditPads] {checked_} records checked, {fired} pads fired, " +
                              $"{findings.Count} finding(s)");
            foreach (var f in findings)
            {
                Console.WriteLine($"[AuditPads] {f}");
            }
            return findings;
        }

        // Every standable non-pad tile within 2 of the teleporter, one per
        // (x,y), preferring the pad's own Z (dungeon floors are statics over
        // bogus land Z � spawning at land Z puts the probe on the layer
        // underneath, where it walks BENEATH the pad without firing it).
        private static IEnumerable<Point3D> StepOffTiles(Map map, Teleporter tele)
        {
            for (int r = 1; r <= 2; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        int x = tele.X + dx;
                        int y = tele.Y + dy;
                        foreach (int z in new[]
                                 {
                                     tele.Z, tele.Z + 5, tele.Z - 5,
                                     tele.Z + 10, tele.Z - 10,
                                     map.GetAverageZ(x, y),
                                 })
                        {
                            if (!map.CanSpawnMobile(x, y, z))
                            {
                                continue;
                            }
                            bool onPad = false;
                            foreach (var other in map.GetItemsInRange<Teleporter>(
                                         new Point3D(x, y, z), 0))
                            {
                                onPad = true;
                                break;
                            }
                            if (!onPad)
                            {
                                yield return new Point3D(x, y, z);
                            }
                            break; // one Z per (x,y) is enough
                        }
                    }
                }
            }
        }
    }
}
