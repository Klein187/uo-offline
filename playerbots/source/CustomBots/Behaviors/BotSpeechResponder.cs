// =========================================================================
// BotSpeechResponder.cs — bots answer when a real player talks to them.
//
// The single loudest "that's an NPC" tell is a "player" who ignores you.
// This hooks PlayerBot.OnSpeech (the same per-listener pipeline vendors
// use) and gives bots the minimal, human response surface:
//
//   - say a bot's NAME nearby      → it turns and answers ("yeah?")
//   - greet within earshot         → the CLOSEST bot greets back ("sup")
//   - ask a question close by      → a shrug ("dunno", "no idea m8")
//   - say anything in its face     → sometimes "what" / "hm?" — and
//     sometimes it just ignores you, which is also exactly what a real
//     player did
//
// Hard rules: only REAL players trigger it (a PlayerBot speaker never
// does — no bot-to-bot echo loops), per-bot cooldowns stop farming, the
// bank's AFK/macro roles stay silent (they're "away"), and replies come
// after a short typing delay, not instantly.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotSpeechResponder
    {
        private const int NameRange = 10;  // your name carries across a room
        private const int GreetRange = 5;  // "hi" only lands close by
        private const int CloseRange = 2;  // talking right in someone's face

        private static readonly Dictionary<Serial, DateTime> _cooldowns = new();

        // Name-mentions cut through the general cooldown — you answer to
        // your NAME even if you just spoke — but keep their own short
        // guard so "thorgil thorgil thorgil" can't farm chatter.
        private static readonly Dictionary<Serial, DateTime> _lastReplyAt = new();
        private static readonly TimeSpan NameReplyGuard = TimeSpan.FromSeconds(15);

        // One reply per utterance. Every listener's OnSpeech runs in the
        // same pass, so distance checks alone can't stop a chorus (ties +
        // same-pass cooldowns poison the "am I closest" logic). The first
        // bot that decides to answer CLAIMS the utterance; the rest let it
        // stand. Name-mentions override a generic claim — "hey Tobias"
        // belongs to Tobias no matter who spoke up first.
        private static Serial _claimSpeaker;
        private static string _claimText;
        private static DateTime _claimAt;

        private static bool Claimed(Mobile speaker, string text) =>
            _claimSpeaker == speaker.Serial && _claimText == text &&
            Core.Now - _claimAt < TimeSpan.FromSeconds(2);

        private static void Claim(Mobile speaker, string text)
        {
            _claimSpeaker = speaker.Serial;
            _claimText = text;
            _claimAt = Core.Now;
        }

        private static readonly string[] Greetings =
        {
            "hi", "hello", "hey", "heya", "hiya", "yo", "sup", "hail", "oi",
            "greetings", "wassup", "o/", "ello", "hey there",
        };

        private static readonly string[] QuestionStarts =
        {
            "who", "what", "where", "when", "why", "how", "anyone", "any1",
            "can", "does", "do", "is", "are", "u know", "you know",
        };

        // A player recruiting out loud ("lfg despise", "anyone want to
        // hunt?") — nearby bots that are free answer AND join.
        private static readonly string[] LfgPhrases =
        {
            "lfg", "lf group", "looking for group", "anyone want to hunt",
            "who wants to hunt", "anyone wanna hunt", "forming a group",
            "forming group", "need a group",
        };

        // A player asking the bot in front of them directly.
        private static readonly string[] GroupAskPhrases =
        {
            "wanna group", "want to group", "wanna form a group",
            "want to form a group", "form a group", "group up", "party up",
            "wanna hunt", "want to hunt", "join me", "join my party",
            "come hunt", "come with me", "wanna party",
        };

        // A player answering a bot's OWN recruiting shout.
        private static readonly string[] JoinPhrases =
        {
            "me", "me too", "im in", "i'm in", "inv", "inv me", "invite me",
            "count me in", "ill come", "i'll come", "sure",
        };

        // One LFG shout recruits at most this many bots.
        private const int LfgJoinBudget = 3;
        private static Serial _lfgSpeaker;
        private static DateTime _lfgAt;
        private static int _lfgJoins;

        public static void Handle(PlayerBot bot, SpeechEventArgs e)
        {
            var speaker = e?.Mobile;
            if (bot == null || bot.Deleted || speaker == null || speaker.Deleted)
            {
                return;
            }

            // Real players only. A PlayerBot speaker must never trigger a
            // reply — that's an echo chamber waiting to happen.
            if (!speaker.Player || speaker is PlayerBot)
            {
                return;
            }

            if (!bot.Alive || bot.Hidden || bot.LoggingOut ||
                bot.Combatant != null || bot.Map != speaker.Map ||
                string.IsNullOrWhiteSpace(e.Speech))
            {
                return;
            }

            // The bank's AFK and macro crowd is away from the keyboard —
            // silence IS their answer.
            if (bot.Behavior is BankSitterBehavior bs &&
                bs.Role is BankSitterBehavior.BankRole.Afk
                        or BankSitterBehavior.BankRole.ResistMacro
                        or BankSitterBehavior.BankRole.HidingMacro
                        or BankSitterBehavior.BankRole.StealthMacro)
            {
                return;
            }

            int dist = Cheby(bot.Location, speaker.Location);
            if (dist > NameRange)
            {
                return;
            }

            var lower = e.Speech.Trim().ToLowerInvariant();

            // 1. My name? That always gets a turn and an answer — even if
            // someone else already piped up, and even mid-cooldown (its
            // own short guard applies instead). A name + group ask
            // ("garrick wanna hunt?") is a direct recruitment.
            if (ContainsWord(lower, FirstName(bot.Name)))
            {
                if (!_lastReplyAt.TryGetValue(bot.Serial, out var last) ||
                    Core.Now - last >= NameReplyGuard)
                {
                    Claim(speaker, lower);
                    if (MatchesAny(lower, GroupAskPhrases))
                    {
                        AnswerGroupAsk(bot, speaker);
                    }
                    else
                    {
                        Reply(bot, speaker, "respond_name");
                    }
                }
                return;
            }

            // 2. Answering a recruiting hunt leader: the player says "me"
            // near a bot mustering its own dungeon run — the leader sends
            // a REAL party invite and the player taps accept.
            if (dist <= GreetRange && IsJoinPhrase(lower) &&
                BotPlayerParty.IsRecruitingHuntLeader(bot))
            {
                if (!Claimed(speaker, lower))
                {
                    Claim(speaker, lower);
                    SetCooldown(bot, TimeSpan.FromSeconds(10));
                    BotPlayerParty.InvitePlayerToHunt(bot, speaker);
                }
                return;
            }

            // 3. A player's LFG shout: free bots nearby answer and join —
            // a few of them, staggered, never a whole plaza.
            if (dist <= NameRange && MatchesAny(lower, LfgPhrases))
            {
                if (_lfgSpeaker != speaker.Serial || Core.Now - _lfgAt > TimeSpan.FromSeconds(8))
                {
                    _lfgSpeaker = speaker.Serial;
                    _lfgAt = Core.Now;
                    _lfgJoins = 0;
                }
                if (_lfgJoins >= LfgJoinBudget || OnCooldown(bot) ||
                    !BotPlayerParty.CanJoin(bot, out _) ||
                    Utility.RandomDouble() > 0.55)
                {
                    return;
                }
                if (BotPlayerParty.TryRecruitToPlayer(
                        speaker, bot, 1.0 + _lfgJoins * 1.6 + Utility.RandomDouble()))
                {
                    _lfgJoins++;
                    SetCooldown(bot, TimeSpan.FromSeconds(30));
                }
                return;
            }

            // 4. A direct "wanna group?" to whoever's closest.
            if (dist <= GreetRange && MatchesAny(lower, GroupAskPhrases))
            {
                if (!Claimed(speaker, lower) && ClosestEligible(bot, speaker, dist))
                {
                    Claim(speaker, lower);
                    AnswerGroupAsk(bot, speaker);
                }
                return;
            }

            if (OnCooldown(bot))
            {
                return;
            }

            if (Claimed(speaker, lower))
            {
                return; // someone already answered (or the room chose not to)
            }

            if (dist <= GreetRange && IsGreeting(lower))
            {
                if (ClosestEligible(bot, speaker, dist))
                {
                    Claim(speaker, lower);
                    // A bank "hi" going unanswered is period-accurate too.
                    if (Utility.RandomDouble() < 0.65)
                    {
                        Reply(bot, speaker, "respond_greet");
                    }
                }
                return;
            }

            if (dist <= GreetRange && LooksLikeQuestion(lower))
            {
                if (ClosestEligible(bot, speaker, dist))
                {
                    Claim(speaker, lower);
                    if (Utility.RandomDouble() < 0.75)
                    {
                        Reply(bot, speaker, "respond_question");
                    }
                }
                return;
            }

            // 4. Said something else right in my face. Sometimes "what",
            // sometimes pointedly nothing — both are human.
            if (dist <= CloseRange && ClosestEligible(bot, speaker, dist))
            {
                Claim(speaker, lower);
                if (Utility.RandomDouble() < 0.45)
                {
                    Reply(bot, speaker, "respond_what");
                }
                else
                {
                    SetCooldown(bot, TimeSpan.FromSeconds(20));
                }
            }
        }

        // -------------------------------------------------------------------
        // Face the speaker, then answer after a human typing delay.
        // -------------------------------------------------------------------
        private static void Reply(PlayerBot bot, Mobile speaker, string category)
        {
            var line = ChatLibrary.PickRandom(category);
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            SetCooldown(bot, TimeSpan.FromSeconds(Utility.RandomMinMax(45, 120)));
            if (_lastReplyAt.Count > 2000)
            {
                _lastReplyAt.Clear();
            }
            _lastReplyAt[bot.Serial] = Core.Now;

            var d = bot.GetDirectionTo(speaker);
            if (bot.Direction != d)
            {
                bot.Direction = d;
            }

            double delay = 0.9 + Utility.RandomDouble() * 1.2 +
                           Math.Min(line.Length * 0.04, 1.2);
            Timer.DelayCall(TimeSpan.FromSeconds(delay), () =>
            {
                if (bot.Deleted || !bot.Alive || bot.Hidden || speaker.Deleted)
                {
                    return;
                }
                bot.Say(line);
                Console.WriteLine(
                    $"[speech] {bot.Name} answers {speaker.Name}: {line}");
            });
        }

        // Am I the closest eligible bot to the speaker? Keeps a "hi" from
        // turning six heads at once — one person answers, like a real room.
        private static bool ClosestEligible(PlayerBot bot, Mobile speaker, int myDist)
        {
            foreach (var m in speaker.Map.GetMobilesInRange(speaker.Location, GreetRange))
            {
                if (m == bot || m is not PlayerBot other || other.Deleted ||
                    !other.Alive || other.Hidden || other.LoggingOut ||
                    other.Combatant != null || OnCooldown(other))
                {
                    continue;
                }
                if (other.Behavior is BankSitterBehavior obs &&
                    obs.Role is BankSitterBehavior.BankRole.Afk
                             or BankSitterBehavior.BankRole.ResistMacro
                             or BankSitterBehavior.BankRole.HidingMacro
                             or BankSitterBehavior.BankRole.StealthMacro)
                {
                    continue;
                }
                int d = Cheby(other.Location, speaker.Location);
                if (d < myDist)
                {
                    return false;
                }
            }
            return true;
        }

        // A free bot says yes and joins (the "im in" comes from the
        // invite-accept path); a busy one explains itself.
        private static void AnswerGroupAsk(PlayerBot bot, Mobile speaker)
        {
            if (BotPlayerParty.CanJoin(bot, out var declineLine))
            {
                SetCooldown(bot, TimeSpan.FromSeconds(20));
                BotPlayerParty.TryRecruitToPlayer(speaker, bot, 0.8);
            }
            else
            {
                SetCooldown(bot, TimeSpan.FromSeconds(20));
                var d = bot.GetDirectionTo(speaker);
                if (bot.Direction != d)
                {
                    bot.Direction = d;
                }
                Timer.DelayCall(TimeSpan.FromSeconds(1.0 + Utility.RandomDouble()), () =>
                {
                    if (!bot.Deleted && bot.Alive && !string.IsNullOrEmpty(declineLine))
                    {
                        bot.Say(declineLine);
                    }
                });
            }
        }

        private static bool MatchesAny(string lower, string[] phrases)
        {
            foreach (var p in phrases)
            {
                if (lower.Contains(p))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsJoinPhrase(string lower)
        {
            foreach (var p in JoinPhrases)
            {
                if (lower == p || lower.StartsWith(p + " ") || lower.EndsWith(" " + p))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsGreeting(string lower)
        {
            foreach (var g in Greetings)
            {
                if (lower == g || lower.StartsWith(g + " ") || lower.StartsWith(g + ","))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool LooksLikeQuestion(string lower)
        {
            if (lower.EndsWith("?"))
            {
                return true;
            }
            foreach (var q in QuestionStarts)
            {
                if (lower.StartsWith(q + " "))
                {
                    return true;
                }
            }
            return false;
        }

        private static string FirstName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "";
            }
            int sp = name.IndexOf(' ');
            return (sp > 0 ? name[..sp] : name).ToLowerInvariant();
        }

        // Whole-word match without regex allocation.
        private static bool ContainsWord(string text, string word)
        {
            if (string.IsNullOrEmpty(word) || word.Length < 2)
            {
                return false;
            }
            int i = 0;
            while ((i = text.IndexOf(word, i, StringComparison.Ordinal)) >= 0)
            {
                bool startOk = i == 0 || !char.IsLetter(text[i - 1]);
                int end = i + word.Length;
                bool endOk = end >= text.Length || !char.IsLetter(text[end]);
                if (startOk && endOk)
                {
                    return true;
                }
                i = end;
            }
            return false;
        }

        private static bool OnCooldown(PlayerBot bot) =>
            _cooldowns.TryGetValue(bot.Serial, out var until) && Core.Now < until;

        private static void SetCooldown(PlayerBot bot, TimeSpan span)
        {
            if (_cooldowns.Count > 2000)
            {
                _cooldowns.Clear();
            }
            _cooldowns[bot.Serial] = Core.Now + span;
        }

        private static int Cheby(Point3D a, Point3D b)
        {
            int dx = Math.Abs(a.X - b.X);
            int dy = Math.Abs(a.Y - b.Y);
            return dx > dy ? dx : dy;
        }
    }
}
