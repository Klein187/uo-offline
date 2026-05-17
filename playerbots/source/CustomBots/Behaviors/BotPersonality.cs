// =========================================================================
// BotPersonality.cs — Per-bot inclination toward different behaviors.
//
// Stored as a struct on each PlayerBot, persisted across saves. Drives
// the BotLifecycleManager: when a bot transitions phases, the next phase
// is rolled weighted by personality. Different personalities produce
// visibly different bots over time.
//
// "Weights" don't need to sum to 1.0; the manager normalizes at roll time.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    [Flags]
    public enum PersonalityTrait
    {
        None      = 0,
        Restless  = 1 << 0,  // shorter phases, more transitions
        Homebody  = 1 << 1,  // longer phases, fewer transitions
        Brave     = 1 << 2,  // adventurer tendency boost
        Cautious  = 1 << 3,  // banker tendency boost
        Wealthy   = 1 << 4,  // prefers banks/shops
        Rough     = 1 << 5,  // prefers wilderness
    }

    public struct BotPersonality
    {
        public double BankerTendency;
        public double AdventurerTendency;
        public double TravelerTendency;
        public double WanderTendency;
        public double IdleTendency;

        public PersonalityTrait Traits;

        // Average duration of a phase for this bot. Restless cuts it,
        // Homebody extends it.
        public TimeSpan AveragePhaseDuration;

        public bool IsAssigned => AveragePhaseDuration > TimeSpan.Zero;

        // -- Construction helpers ----------------------------------------

        // Random personality — uniform-ish weights with a few traits rolled.
        public static BotPersonality RollRandom()
        {
            var p = new BotPersonality
            {
                BankerTendency     = RollWeight(),
                AdventurerTendency = RollWeight(),
                TravelerTendency   = RollWeight(),
                WanderTendency     = RollWeight() * 0.5, // less common
                IdleTendency       = RollWeight() * 0.3, // rarest
                AveragePhaseDuration = TimeSpan.FromMinutes(Utility.RandomMinMax(30, 180)),
                Traits = PersonalityTrait.None,
            };

            // Roll traits with low independent probability.
            if (Utility.RandomDouble() < 0.20) p.Traits |= PersonalityTrait.Restless;
            if (Utility.RandomDouble() < 0.20) p.Traits |= PersonalityTrait.Homebody;
            if (Utility.RandomDouble() < 0.25) p.Traits |= PersonalityTrait.Brave;
            if (Utility.RandomDouble() < 0.25) p.Traits |= PersonalityTrait.Cautious;
            if (Utility.RandomDouble() < 0.20) p.Traits |= PersonalityTrait.Wealthy;
            if (Utility.RandomDouble() < 0.20) p.Traits |= PersonalityTrait.Rough;

            // Apply trait modifiers.
            if (p.HasTrait(PersonalityTrait.Brave))    p.AdventurerTendency *= 1.5;
            if (p.HasTrait(PersonalityTrait.Cautious)) p.BankerTendency     *= 1.5;
            if (p.HasTrait(PersonalityTrait.Wealthy))  p.BankerTendency     *= 1.3;
            if (p.HasTrait(PersonalityTrait.Rough))    p.AdventurerTendency *= 1.3;
            if (p.HasTrait(PersonalityTrait.Restless))
                p.AveragePhaseDuration = TimeSpan.FromTicks(p.AveragePhaseDuration.Ticks / 2);
            if (p.HasTrait(PersonalityTrait.Homebody))
                p.AveragePhaseDuration = TimeSpan.FromTicks(p.AveragePhaseDuration.Ticks * 2);

            return p;
        }

        private static double RollWeight() => Utility.RandomDouble();

        public bool HasTrait(PersonalityTrait t) => (Traits & t) != 0;

        public override string ToString()
        {
            return $"B={BankerTendency:F2} A={AdventurerTendency:F2} T={TravelerTendency:F2} " +
                   $"W={WanderTendency:F2} I={IdleTendency:F2} " +
                   $"dur={AveragePhaseDuration.TotalMinutes:F0}m traits={Traits}";
        }

        // -- Serialization ------------------------------------------------

        public void Write(IGenericWriter writer)
        {
            writer.Write((byte)1);                       // personality version
            writer.Write(BankerTendency);
            writer.Write(AdventurerTendency);
            writer.Write(TravelerTendency);
            writer.Write(WanderTendency);
            writer.Write(IdleTendency);
            writer.Write((int)Traits);
            writer.Write(AveragePhaseDuration);
        }

        public static BotPersonality Read(IGenericReader reader)
        {
            byte v = reader.ReadByte();
            if (v < 1)
                return default;

            return new BotPersonality
            {
                BankerTendency       = reader.ReadDouble(),
                AdventurerTendency   = reader.ReadDouble(),
                TravelerTendency     = reader.ReadDouble(),
                WanderTendency       = reader.ReadDouble(),
                IdleTendency         = reader.ReadDouble(),
                Traits               = (PersonalityTrait)reader.ReadInt(),
                AveragePhaseDuration = reader.ReadTimeSpan(),
            };
        }
    }
}
