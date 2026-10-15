// =========================================================================
// VisitorBehavior.cs — a purposeful short stop at a non-vendor spot.
//
// Healers, inns, stables, and shrines had no arrival behavior: bots
// reached them, fell through the handoff switch, and stood around
// looking broken until something else moved them. Now they VISIT — a
// couple of themed lines and actions that fit the place, then back on
// the road:
//
//   Healer  — gets a wound looked at ("how much for a cure?")
//   Inn     — checks the board, rents a bed ("a real bed at last")
//   Stables — fusses over the horses ("*feeds his horse an apple*")
//   Shrine  — kneels and CHANTS THE VIRTUE'S MANTRA (era: Ahm, Mu, Summ,
//             Lum, Beh, Cah, Om, Ra) between quiet meditation
//   Tavern  — the old small-talk/lfg loiter, now with an exit time
//   Landmark — strolls around the drawn shape, stops, looks about
//
// The visit honors VisitExpiresAt (stamped by the arrival handoff) and
// hands back to a Traveler when it ends. A Visitor loaded from a stale
// save self-heals: default chat + a short window, then moves on.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    public class VisitorBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Visitor";

        private string _mantra;
        private string _statusLine = "visiting";
        private Point3D _anchor;
        private DateTime _nextBeat;

        // Landmarks: the drawn shape to stroll around in (null = around the
        // arrival spot), and the walk to the next place to look from.
        public PaintedZone Area { get; set; }
        private bool _sightseeing;
        private Point3D? _strollTo;
        private ILegFollower _strollPath;
        private int _strollSteps;
        private Timer _strollTimer;
        private const int StrollMaxSteps = 60;
        private const int StrollRadius = 8;

        public override string GetStatusLine(PlayerBot bot) => _statusLine;

        public VisitorBehavior()
        {
            ChatCategories  = new[] { "small_talk" };
            ChatChance      = 0.25;
            MinChatCooldown = TimeSpan.FromSeconds(12);
            MaxChatCooldown = TimeSpan.FromSeconds(30);
        }

        // Called by the arrival handoff so the visit fits the place.
        public void ConfigureFor(DestinationType type, string destName)
        {
            switch (type)
            {
                case DestinationType.Healer:
                    ChatCategories = new[] { "healer_visit", "small_talk" };
                    _statusLine = "seeing the healer";
                    break;
                case DestinationType.Inn:
                    ChatCategories = new[] { "inn_visit", "small_talk" };
                    _statusLine = "resting at the inn";
                    break;
                case DestinationType.Stables:
                    ChatCategories = new[] { "stable_visit", "small_talk" };
                    _statusLine = "at the stables";
                    break;
                case DestinationType.Tavern:
                    // "wts" dropped — see BotShop: selling is for bots
                    // that actually have the goods on them.
                    ChatCategories = new[] { "small_talk", "lfg", "wtb" };
                    _statusLine = "drinking at the tavern";
                    break;
                case DestinationType.Shrine:
                    ChatCategories = new[] { "shrine_visit" };
                    _statusLine = "praying at the shrine";
                    _mantra = MantraFor(destName);
                    break;
                case DestinationType.Landmark:
                    ChatCategories = new[] { "landmark_visit", "small_talk" };
                    _statusLine = $"looking around {destName}";
                    _sightseeing = true;
                    break;
            }
        }

        public override void OnDetached(PlayerBot bot)
        {
            StopStroll();
            base.OnDetached(bot);
        }

        private void StopStroll()
        {
            _strollTimer?.Stop();
            _strollTimer = null;
            _strollTo = null;
            _strollPath = null;
        }

        // Somewhere else to stand and look from: inside the drawn shape, or
        // near where the bot arrived when there is none.
        private Point3D? PickLookout(PlayerBot bot)
        {
            for (int i = 0; i < 12; i++)
            {
                int x, y;
                if (Area != null)
                {
                    x = Utility.RandomMinMax(Area.MinX, Area.MaxX);
                    y = Utility.RandomMinMax(Area.MinY, Area.MaxY);
                    if (!Area.Contains(x, y))
                    {
                        continue;
                    }
                }
                else
                {
                    x = _anchor.X + Utility.RandomMinMax(-StrollRadius, StrollRadius);
                    y = _anchor.Y + Utility.RandomMinMax(-StrollRadius, StrollRadius);
                }
                if (Walkable.TryFindSeedZ(bot.Map, x, y, bot.Z, out var z))
                {
                    return new Point3D(x, y, z);
                }
            }
            return null;
        }

        // A walking pace along a real path (around walls and buildings),
        // giving up on a spot it can't reach in a short while.
        private void StartStroll(PlayerBot bot, Point3D to)
        {
            StopStroll();
            _strollTo = to;
            _strollSteps = 0;
            _strollPath = LegFollowers.Create(bot, to, null);
            _strollTimer = Timer.DelayCall(TimeSpan.FromMilliseconds(450), TimeSpan.FromMilliseconds(450), () =>
            {
                if (bot.Deleted || !bot.Alive || bot.Behavior != this || _strollPath == null ||
                    bot.Combatant != null || ++_strollSteps > StrollMaxSteps)
                {
                    StopStroll();
                    return;
                }
                bool arrived;
                try
                {
                    arrived = _strollPath.Follow(false, 1);
                }
                catch
                {
                    arrived = true;
                }
                if (arrived)
                {
                    StopStroll();
                    bot.Direction = (Direction)Utility.Random(8);
                }
            });
        }

        // The era mantras, one per virtue shrine.
        private static string MantraFor(string shrineName)
        {
            var n = shrineName ?? "";
            if (n.Contains("Compassion"))   return "Mu";
            if (n.Contains("Honesty"))      return "Ahm";
            if (n.Contains("Honor"))        return "Summ";
            if (n.Contains("Humility"))     return "Lum";
            if (n.Contains("Justice"))      return "Beh";
            if (n.Contains("Sacrifice"))    return "Cah";
            if (n.Contains("Spirituality")) return "Om";
            if (n.Contains("Valor"))        return "Ra";
            return null; // Chaos shrine keeps its silence
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            _anchor = bot.Location;
            _nextBeat = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(2, 6));
            // Stale-save or direct attach: a short stop, not a residency.
            VisitExpiresAt ??= Core.Now +
                TimeSpan.FromMinutes(Utility.RandomMinMax(1, 3));
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal || bot.Deleted || !bot.Alive)
            {
                return;
            }

            // Jumped mid-visit: defend, ordinary life resumes after.
            if (bot.Combatant is Mobile threat && threat.Alive && !threat.Deleted)
            {
                bot.Behavior = new AdventurerBehavior
                {
                    DefenderMode = true,
                    DefenderRetreatHpFraction = 0.45,
                };
                return;
            }

            // Visit over — back on the road.
            if (VisitExpiresAt != null && Core.Now >= VisitExpiresAt.Value)
            {
                bot.Behavior = BehaviorRegistry.Create("Traveler");
                return;
            }

            if (Core.Now < _nextBeat)
            {
                return;
            }
            _nextBeat = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(8, 18));

            // At a landmark: walk somewhere else in it, or stand and take
            // it in, now and then saying so.
            if (_sightseeing)
            {
                if (_strollTo == null && Utility.RandomDouble() < 0.6 && PickLookout(bot) is Point3D next)
                {
                    StartStroll(bot, next);
                }
                else if (_strollTo == null)
                {
                    bot.Direction = (Direction)Utility.Random(8);
                    TrySpeak(bot);
                }
                return;
            }

            // Shrine visitors kneel and chant the mantra between lines.
            if (_mantra != null && Utility.RandomDouble() < 0.55)
            {
                bot.Animate(32, 5, 1, true, false, 0); // kneel/bow
                bot.Say(_mantra);
                return;
            }

            TrySpeak(bot);

            // Small shuffle so the visit reads as alive, held to the spot.
            if (Utility.RandomDouble() < 0.35)
            {
                if (bot.InRange(_anchor, 3))
                {
                    var dir = (Direction)Utility.Random(8);
                    bot.Direction = dir;
                    bot.Move(dir);
                }
                else
                {
                    var back = bot.GetDirectionTo(_anchor);
                    bot.Direction = back;
                    bot.Move(back);
                }
            }
            else
            {
                bot.Direction = (Direction)Utility.Random(8);
            }
        }
    }
}
