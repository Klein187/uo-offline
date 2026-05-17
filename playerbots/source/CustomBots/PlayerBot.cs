// =========================================================================
// PlayerBot.cs — Fake player with swappable behavior, speech color, outfit.
//
// What's new in v3:
//   - Random outfit archetypes via EquipmentTable (peasant, mage, warrior,
//     adventurer, merchant, wanderer)
//
// v2 additions still here:
//   - Names from NamePool (hundreds curated + algorithmic fallback)
//   - Per-bot speech color via Mobile.SpeechHue
//
// To use:
//   [SpawnTestBot          - drop a bot at your feet
//   [SetBehavior wander    - target a bot, switch it to wander
// =========================================================================

using System;
using Server;
using Server.Commands;
using Server.Items;
using Server.Mobiles;
using Server.Network;
using Server.Targeting;

namespace Server.CustomBots
{
    public class PlayerBot : PlayerMobile
    {
        public bool IsBot { get; set; } = true;

        // -------------------------------------------------------------------
        // Note on speech color:
        // Mobile.SpeechHue (inherited) is what overhead chat actually uses.
        // We set it in the constructor and let Mobile's own serialization
        // persist it. No SpeechHue field of our own; redeclaring would
        // shadow the inherited one and break Say()'s color lookup.
        // -------------------------------------------------------------------

        // -------------------------------------------------------------------
        // Behavior — the current "brain". Always non-null; falls back to
        // IdleBehavior. Swap via the setter; OnAttached/OnDetached fire.
        // -------------------------------------------------------------------
        private PlayerBotBehavior _behavior;

        public PlayerBotBehavior Behavior
        {
            get => _behavior;
            set
            {
                var newBehavior = value ?? new IdleBehavior();
                if (ReferenceEquals(_behavior, newBehavior))
                {
                    return;
                }

                _behavior?.OnDetached(this);
                _behavior = newBehavior;
                _behavior.OnAttached(this);

                // Reset phase timer whenever the brain changes. The
                // BotLifecycleManager uses this to decide when to transition
                // again. Skip on bots that haven't yet been seen by the
                // lifecycle (no personality assigned) — that case is
                // handled when personality is first assigned.
                if (Personality.IsAssigned)
                {
                    PhaseStartedAt = Core.Now;
                }
            }
        }

        // ---- Lifecycle state ----

        // Personality drives behavior selection in the lifecycle manager.
        // Assigned lazily on first lifecycle tick if not present.
        public BotPersonality Personality;

        // When the current phase began. Lifecycle compares (now - this) to
        // Personality.AveragePhaseDuration to decide on transitions.
        public DateTime PhaseStartedAt;

        // ---- Constructors ----

        // [Constructible] makes this constructor visible to:
        //   - The [add command (`[add playerbot`)
        //   - ModernUO's spawner system (BaseSpawner only calls constructors
        //     that pass IsConstructible — without this attribute, spawners
        //     log "There is no constructor for ... that matches the given
        //     predicate" and silently produce nothing).
        [Constructible(AccessLevel.GameMaster)]
        public PlayerBot() : base()
        {
            Female = Utility.RandomBool();
            Body   = Female ? 0x191 : 0x190;

            Hue = Race.RandomSkinHue();
            Utility.AssignRandomHair(this);

            if (!Female)
            {
                Utility.AssignRandomFacialHair(this, randomHue: false);
                FacialHairHue = HairHue;
            }

            RawStr = 50;
            RawDex = 50;
            RawInt = 50;
            Hits = HitsMax;
            Stam = StamMax;
            Mana = ManaMax;

            Name = NamePool.PickRandom(Female);
            SpeechHue = SpeechHues.PickRandom();

            // Roll a random outfit from the six archetypes (peasant, mage,
            // warrior, adventurer, merchant, wanderer). Replaces the v1
            // always-robe-and-boots look.
            EquipmentTable.RollOutfit(this);

            Behavior = new IdleBehavior();
        }

        public PlayerBot(Serial serial) : base(serial)
        {
            // State restored in Deserialize.
        }

        public override bool ShouldCheckStatTimers => false;

        // -------------------------------------------------------------------
        // OnAfterSpawn — called by Mobile after a spawner places this bot in
        // the world. By now this.Spawner is set.
        //
        // We do three things here:
        //   1. Copy our behavior from the spawner (if it's a PlayerBotSpawner)
        //   2. For BankSitters, 80% try to relocate next to a wall (their
        //      back to the wall, face the crowd) — the classic AFK macroer
        //      look. The other 20% stand in the open.
        //   3. Pick a camera-facing direction so we don't all stare at the
        //      back wall.
        //
        // Manually-spawned bots (via [SpawnTestBot or [add) have no Spawner;
        // they get the default Idle behavior and a random camera-facing
        // direction. No wall-hug attempt.
        // -------------------------------------------------------------------
        public override void OnAfterSpawn()
        {
            base.OnAfterSpawn();

            string behaviorName = null;
            if (Spawner is PlayerBotSpawner pbs)
            {
                behaviorName = pbs.BehaviorName;
                Behavior = BehaviorRegistry.Create(behaviorName);
            }

            // BankSitters: most of them lean against the nearest wall.
            // Anyone else (Wanderers, Idle): just stand and face the camera.
            bool hugged = false;
            if (behaviorName == "BankSitter" && Utility.RandomDouble() < 0.80)
            {
                hugged = TryHugNearbyWall();
            }

            if (!hugged)
            {
                Direction = RandomCameraFacingDirection();
            }
        }

        // -------------------------------------------------------------------
        // Wall-hugger: scan the 5-tile-radius around us for tiles that are
        // (a) walkable and (b) have at least one impassable neighbor (i.e.
        // a wall). If found, teleport there and face AWAY from the wall —
        // back to the wall, head toward the room. Returns true on success.
        //
        // Uses Map.CanFit as the universal "walkable" probe rather than
        // poking TileFlags directly — captures static walls, land
        // impassables, and furniture all in one check.
        // -------------------------------------------------------------------
        private bool TryHugNearbyWall()
        {
            if (Map == null || Map == Map.Internal)
            {
                return false;
            }

            // Range we'll scan for a suitable wall-adjacent tile.
            const int scanRange = 5;
            // Height needed for a person to stand here. 16 matches what
            // ModernUO uses for the standard "can a mobile stand here" check.
            const int height = 16;

            // Build a list of (candidate, wallDirection) pairs.
            var candidates = new System.Collections.Generic.List<(Point3D loc, Direction wallDir)>();

            for (int dx = -scanRange; dx <= scanRange; dx++)
            {
                for (int dy = -scanRange; dy <= scanRange; dy++)
                {
                    if (dx == 0 && dy == 0) continue;

                    int x = Location.X + dx;
                    int y = Location.Y + dy;
                    int z = Location.Z;

                    // The tile itself must be standable.
                    if (!Map.CanFit(x, y, z, height, checkBlocksFit: false, checkMobiles: true))
                    {
                        continue;
                    }

                    // Check the 4 cardinal neighbors. If any is impassable,
                    // this tile is wall-adjacent. We track which side the
                    // wall is on so we can face away from it.
                    Direction? wallSide = null;
                    if (!Map.CanFit(x,     y - 1, z, height, false, false)) wallSide = Direction.North;
                    else if (!Map.CanFit(x + 1, y,     z, height, false, false)) wallSide = Direction.East;
                    else if (!Map.CanFit(x,     y + 1, z, height, false, false)) wallSide = Direction.South;
                    else if (!Map.CanFit(x - 1, y,     z, height, false, false)) wallSide = Direction.West;

                    if (wallSide.HasValue)
                    {
                        candidates.Add((new Point3D(x, y, z), wallSide.Value));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            var pick = candidates[Utility.Random(candidates.Count)];
            MoveToWorld(pick.loc, Map);

            // Face AWAY from the wall — back to the wall, face the room.
            // Direction enum: 0=North, 1=Right, 2=East... add 4 mod 8 for opposite.
            Direction = (Direction)(((int)pick.wallDir + 4) & 7);
            return true;
        }

        // South-facing for the offline single-player viewing angle.
        // ClassicUO's default camera looks down at South/SE/SW. Bots that
        // face one of these directions appear to look "at the player".
        private static readonly Direction[] CameraFacing =
        {
            Direction.South, Direction.South,    // weighted toward straight south
            Direction.Right,                     // SE
            Direction.Down                       // SW
        };

        private static Direction RandomCameraFacingDirection()
        {
            return CameraFacing[Utility.Random(CameraFacing.Length)];
        }

        // ---- Serialization ----
        //
        // Version history:
        //   0 — IsBot only
        //   1 — IsBot, behavior name
        //   2 — IsBot, behavior name, personality, phase started at
        //
        // SpeechHue is handled by Mobile.Serialize / Mobile.Deserialize
        // automatically; we don't touch it here.
        //
        // For bots saved at v0 that load under this code, behavior defaults
        // to "Idle" (the safe fallback). Personality is default (unassigned);
        // the lifecycle manager will roll a fresh one when it first sees them.

        public override void Serialize(IGenericWriter writer)
        {
            base.Serialize(writer);

            writer.Write(2);                                       // version
            writer.Write(IsBot);
            writer.Write(_behavior?.SerializableName ?? "Idle");
            Personality.Write(writer);
            writer.Write(PhaseStartedAt);
        }

        public override void Deserialize(IGenericReader reader)
        {
            base.Deserialize(reader);

            int version = reader.ReadInt();

            string behaviorName = "Idle";

            if (version >= 0)
            {
                IsBot = reader.ReadBool();
            }
            if (version >= 1)
            {
                behaviorName = reader.ReadString();
            }
            if (version >= 2)
            {
                Personality = BotPersonality.Read(reader);
                PhaseStartedAt = reader.ReadDateTime();
            }

            Behavior = BehaviorRegistry.Create(behaviorName);
        }
    }

    // -----------------------------------------------------------------------
    // [SpawnTestBot — admin command that drops a PlayerBot at your feet.
    // -----------------------------------------------------------------------
    public static class SpawnTestBotCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("SpawnTestBot", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("SpawnTestBot")]
        [Description("Spawns a single PlayerBot at your location with Idle behavior.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null || from.Map == null)
            {
                return;
            }

            var bot = new PlayerBot();
            bot.MoveToWorld(from.Location, from.Map);

            from.SendMessage($"Spawned {bot.Name} ({(bot.Female ? "F" : "M")}, hue {bot.SpeechHue}). Use [SetBehavior to give it a job.");
        }
    }

    // -----------------------------------------------------------------------
    // [SetBehavior — admin command that swaps a PlayerBot's behavior.
    //   Usage:  [SetBehavior <name>      → target a PlayerBot
    //   Names:  idle, wander
    // -----------------------------------------------------------------------
    public static class SetBehaviorCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("SetBehavior", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("SetBehavior <name>")]
        [Description("Targets a PlayerBot and swaps its behavior. Known names: idle, wander.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null)
            {
                return;
            }

            if (e.Arguments.Length == 0)
            {
                from.SendMessage("Usage: [SetBehavior <name>");
                from.SendMessage("Known behaviors: " + string.Join(", ", BehaviorRegistry.KnownNames));
                return;
            }

            var name = e.Arguments[0];
            from.SendMessage($"Target a PlayerBot to assign behavior '{name}'.");
            from.Target = new SetBehaviorTarget(name);
        }

        private class SetBehaviorTarget : Target
        {
            private readonly string _behaviorName;

            public SetBehaviorTarget(string behaviorName) : base(12, false, TargetFlags.None)
            {
                _behaviorName = behaviorName;
            }

            protected override void OnTarget(Mobile from, object targeted)
            {
                if (targeted is not PlayerBot bot)
                {
                    from.SendMessage("That's not a PlayerBot.");
                    return;
                }

                var behavior = BehaviorRegistry.Create(_behaviorName);
                bot.Behavior = behavior;
                from.SendMessage($"{bot.Name} now has behavior: {behavior.SerializableName}");
            }
        }
    }

    // -----------------------------------------------------------------------
    // [ClearBots — admin command that deletes PlayerBots.
    //   [ClearBots          - deletes all PlayerBots within 30 tiles
    //   [ClearBots <range>  - within <range> tiles
    //   [ClearBots all      - deletes ALL PlayerBots in the world
    //
    // Useful for dev: spawn 20 test bots, iterate behavior, wipe with one
    // command. Doesn't touch monsters, NPCs, or your own character — only
    // bots that are instances of PlayerBot.
    // -----------------------------------------------------------------------
    public static class ClearBotsCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("ClearBots", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("ClearBots [range | 'all']")]
        [Description("Deletes PlayerBots. Default range 30 tiles; 'all' wipes them globally.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null || from.Map == null)
            {
                return;
            }

            // Parse the argument
            bool worldwide = false;
            int  range     = 30;

            if (e.Arguments.Length > 0)
            {
                var arg = e.Arguments[0];
                if (string.Equals(arg, "all", StringComparison.OrdinalIgnoreCase))
                {
                    worldwide = true;
                }
                else if (int.TryParse(arg, out var n) && n > 0)
                {
                    range = n;
                }
                else
                {
                    from.SendMessage("Usage: [ClearBots [range | 'all']");
                    return;
                }
            }

            // Snapshot mobiles into a list — deletion during iteration is
            // unsafe in some collection types.
            var victims = new System.Collections.Generic.List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot bot || bot.Deleted)
                {
                    continue;
                }
                if (!worldwide)
                {
                    if (bot.Map != from.Map) continue;
                    if (!bot.InRange(from.Location, range)) continue;
                }
                victims.Add(bot);
            }

            foreach (var bot in victims)
            {
                bot.Delete();
            }

            if (worldwide)
            {
                from.SendMessage($"Cleared {victims.Count} PlayerBots from the world.");
            }
            else
            {
                from.SendMessage($"Cleared {victims.Count} PlayerBots within {range} tiles.");
            }
        }
    }
}
