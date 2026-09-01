// =========================================================================
// PlayerBotBehavior.cs — Abstract base class for bot behaviors.
//
// v2 changes:
//   - Random first-letter capitalization for organic feel
//   - (Per-bot color is handled automatically: Mobile.SpeechHue is set on
//     the bot at construction and Say() picks it up by default.)
// =========================================================================

using System;
using Server;
using Server.Mobiles;

namespace Server.CustomBots
{
    public abstract class PlayerBotBehavior
    {
        public abstract string SerializableName { get; }

        // One human-readable line describing what the bot is doing RIGHT NOW
        // ("→ Vesper Bank · leg 12/31 via Brit-East-4", "fighting a lich").
        // Exported by LiveMapSnapshot so the map editor's bot inspector can
        // narrate the world. Null = nothing more specific than the behavior
        // name. Must be cheap — it runs for every bot on every snapshot.
        public virtual string GetStatusLine(PlayerBot bot) => null;

        // Chat config — override in subclasses.
        public virtual string[] ChatCategories { get; protected set; } = Array.Empty<string>();
        public virtual double ChatChance        { get; protected set; } = 0.15;
        public virtual TimeSpan MinChatCooldown { get; protected set; } = TimeSpan.FromSeconds(30);
        public virtual TimeSpan MaxChatCooldown { get; protected set; } = TimeSpan.FromSeconds(90);
        public virtual int ChatHearRange        { get; protected set; } = 22;

        // Which pool the late-night texture layer draws from. Behaviors
        // whose bots aren't adventurers override this so a smith's 2am
        // mutter is about closing the shop, never "one more dungeon run".
        protected virtual string NightTalkCategory => "night_talk";

        // Probability that a spoken line gets its first letter capitalized.
        // Mimics how some real players naturally capitalize sentences while
        // others don't. ~35% feels right — most lowercase (1998 UO style),
        // but enough capitals to vary the feed.
        protected virtual double CapitalizeChance => 0.35;

        private DateTime _nextChatAllowed = DateTime.MinValue;

        // Event lines (battle cries, victory shouts, flee screams) run on
        // their OWN cooldown, separate from ambient chat — a dramatic
        // moment should be able to speak even if the bot small-talked ten
        // seconds ago, but a bot in constant combat still shouldn't shout
        // on every engagement.
        private DateTime _nextEventLineAllowed = DateTime.MinValue;

        protected virtual TimeSpan EventLineCooldown => TimeSpan.FromSeconds(10);

        // ---- Destination visit expiry ----
        //
        // When a Traveler arrives somewhere and hands off to a destination
        // behavior (e.g. becomes a BankSitter at a bank, an Adventurer at a
        // graveyard), the new behavior is a TIMED VISIT — it should run for
        // a while, then return the bot to traveling.
        //
        // VisitExpiresAt holds the time the visit ends. Null means "not a
        // visit" — the behavior runs open-ended until the lifecycle manager
        // moves the bot (the normal case for lifecycle-assigned behaviors).
        //
        // A destination-visit behavior should call CheckVisitExpired() at
        // the top of its Tick. If it returns true, the behavior has already
        // swapped the bot back to a Traveler and the caller must return
        // immediately without touching any more state.
        public DateTime? VisitExpiresAt { get; set; }

        protected bool CheckVisitExpired(PlayerBot bot)
        {
            if (VisitExpiresAt == null) return false;
            if (Core.Now < VisitExpiresAt.Value) return false;

            // Visit's over. Return the bot to traveling — it'll roll a
            // fresh destination and continue its journey. Swapping Behavior
            // detaches THIS behavior, so the caller must return right away.
            bot.Behavior = RedTerritory.TravelBrain(bot);
            return true;
        }

        // ---- Lifecycle ----

        public virtual void Tick(PlayerBot bot) { }

        public virtual void OnAttached(PlayerBot bot)
        {
            _nextChatAllowed = Core.Now + RandomCooldown();
        }

        public virtual void OnDetached(PlayerBot bot) { }

        // How often an ambient chat slot is spent retelling a REAL recent
        // event from the journal instead of a canned line.
        private const double GossipShare = 0.20;

        // -------------------------------------------------------------------
        // TrySpeak — gate-check, then potentially Say a random line.
        //
        // Priority within an allowed slot:
        //   1. Greet a nearby friend/guildmate by name ("yo Corwin").
        //   2. Gossip about a real recent journal event (~20%).
        //   3. Ambient line from this behavior's categories.
        // -------------------------------------------------------------------
        // A line the caller already composed, through the same gates a
        // random ambient line goes through: cooldown, audience, chance.
        // The hawker uses it to shout its REAL stock instead of drawing a
        // random claim out of a file.
        protected bool TrySpeakLine(PlayerBot bot, string line, double chance = 1.0)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal ||
                string.IsNullOrEmpty(line))
            {
                return false;
            }

            if (Core.Now < _nextChatAllowed || Utility.RandomDouble() > chance ||
                !IsPlayerNearby(bot))
            {
                return false;
            }

            SpeakLine(bot, line);
            _nextChatAllowed = Core.Now + RandomCooldown();
            return true;
        }

        protected bool TrySpeak(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
            {
                return false;
            }

            if (Core.Now < _nextChatAllowed)
            {
                return false;
            }

            // Friend greetings skip the ChatChance roll — spotting a friend
            // IS the trigger — but still respect the cooldown and audience
            // gates below.
            bool wantAmbient = Utility.RandomDouble() <= ChatChance;

            if (!IsPlayerNearby(bot))
            {
                return false;
            }

            if (TryGreetFriend(bot))
            {
                _nextChatAllowed = Core.Now + RandomCooldown();
                return true;
            }

            if (!wantAmbient)
            {
                return false;
            }

            if (Utility.RandomDouble() < GossipShare)
            {
                var gossip = BotEventJournal.ComposeGossip(bot.Name, bot.Location);
                if (!string.IsNullOrEmpty(gossip))
                {
                    SpeakLine(bot, gossip);
                    _nextChatAllowed = Core.Now + RandomCooldown();
                    return true;
                }
            }

            var cats = ChatCategories;
            if (cats == null || cats.Length == 0)
            {
                return false;
            }

            // Texture layers stealing an occasional ambient slot:
            //   - late-night lines when it's actually late at night
            //   - wordless emotes (*stretches*, *dances at the bank*)
            string line;
            int hour = DateTime.Now.Hour;
            bool night = hour >= 21 || hour < 5;
            double texture = Utility.RandomDouble();
            if (night && texture < 0.25)
            {
                line = ChatLibrary.PickRandom(NightTalkCategory);
            }
            else if (texture < 0.10)
            {
                line = ChatLibrary.PickRandom("emotes");
            }
            else
            {
                line = null;
            }
            line ??= ChatLibrary.PickRandom(cats);
            if (string.IsNullOrEmpty(line))
            {
                return false;
            }

            SpeakLine(bot, line);

            _nextChatAllowed = Core.Now + RandomCooldown();
            return true;
        }

        // -------------------------------------------------------------------
        // TryGreetFriend — friends (hunted together) and guildmates get
        // greeted BY FIRST NAME on sight. Per-pair cooldown keeps two
        // regulars at the same bank from looping.
        // -------------------------------------------------------------------
        private bool TryGreetFriend(PlayerBot bot)
        {
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, 8))
            {
                if (m is not PlayerBot other || other == bot || other.Deleted || !other.Alive)
                {
                    continue;
                }

                bool guildmate = bot.BotGuildIndex >= 0 &&
                                 bot.BotGuildIndex == other.BotGuildIndex;
                if (!guildmate && !BotSocialGraph.AreFriends(bot, other))
                {
                    continue;
                }
                if (!BotSocialGraph.CanGreet(bot, other))
                {
                    continue;
                }

                var template = ChatLibrary.PickRandom("friend_greet");
                if (string.IsNullOrEmpty(template))
                {
                    return false;
                }

                BotSocialGraph.MarkGreeted(bot, other);
                SpeakLine(bot, template.Replace("{name}", FirstName(other.Name), StringComparison.Ordinal));
                return true;
            }
            return false;
        }

        // "Tessa Ravenwood" is greeted as "Tessa" — nobody yo's a surname.
        private static string FirstName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }
            int space = name.IndexOf(' ');
            return space > 0 ? name[..space] : name;
        }

        // -------------------------------------------------------------------
        // TryEventLine — speak a line for a MOMENT (engaging a foe, fleeing,
        // a kill) rather than ambient filler. Own chance + cooldown; still
        // requires a real player nearby — nobody around to hear means don't
        // bother. Categories are ChatLibrary category names (txt files).
        // -------------------------------------------------------------------
        protected bool TryEventLine(PlayerBot bot, double chance, params string[] categories)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
            {
                return false;
            }

            if (Core.Now < _nextEventLineAllowed)
            {
                return false;
            }

            if (Utility.RandomDouble() > chance)
            {
                return false;
            }

            if (!IsPlayerNearby(bot))
            {
                return false;
            }

            var line = ChatLibrary.PickRandom(categories);
            if (string.IsNullOrEmpty(line))
            {
                return false;
            }

            SpeakLine(bot, line);

            _nextEventLineAllowed = Core.Now + EventLineCooldown;
            return true;
        }

        // Say a chat line with the shared voice treatment.
        private void SpeakLine(PlayerBot bot, string line)
        {
            // Lines written as "*bows*" / "*dances*" render as real Emotes
            // — the wordless social layer that reads as very "players".
            if (line.Length > 1 && line[0] == '*')
            {
                bot.Emote(line.Trim('*', ' '));
                return;
            }

            // "withdraw 5000" is a real UO command, and a bot that says it
            // at a bank now means it — including trimming the number down
            // to what is actually in the account before saying it out loud.
            line = BotBanking.Prepare(bot, line);

            // Probabilistic capitalization — sometimes "hail", sometimes "Hail".
            // Don't touch lines that start with non-letters (e.g.
            // "WTS GM hally"). Those should keep their case as written.
            if (Utility.RandomDouble() < CapitalizeChance
                && line.Length > 0
                && char.IsLower(line[0]))
            {
                line = char.ToUpper(line[0]) + line.Substring(1);
            }

            // Mobile.Say uses bot.SpeechHue automatically. The hue was set
            // on the bot when it was constructed and persists across saves.
            bot.Say(line);

            // "WTB GM hally" used to promise nothing. Every spoken line
            // funnels through here, so this is where one becomes a standing
            // want a player can answer with "i have one".
            BotWantAd.Posted(bot, line);

            // Same idea, with coins: the words are said, now do the thing.
            BotBanking.Spoke(bot, line);
        }

        protected bool IsPlayerNearby(PlayerBot bot)
        {
            var range = ChatHearRange;
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, range))
            {
                if (m is PlayerMobile && m is not PlayerBot)
                {
                    return true;
                }
            }
            return false;
        }

        private TimeSpan RandomCooldown()
        {
            var minMs = MinChatCooldown.TotalMilliseconds;
            var maxMs = MaxChatCooldown.TotalMilliseconds;
            var pick  = minMs + Utility.RandomDouble() * (maxMs - minMs);
            return TimeSpan.FromMilliseconds(pick);
        }
    }
}
