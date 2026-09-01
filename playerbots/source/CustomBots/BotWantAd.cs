// =========================================================================
// BotWantAd.cs — "WTB GM hally" was the last line at the bank that meant
// nothing. Now you can answer it.
//
// The bank crowd has always shouted WTB. BankSitterBehavior says why the
// WTS half was taken away from it and given to BotShop: a bot hawking an
// empty pack is the lie this whole system exists to stop telling. WTB was
// left alone with the note "wanting to buy promises nothing" — true, and
// also the reason nobody could ever do anything about it.
//
//   Ulric: WTB GM hally
//   you:   i have one
//   Ulric: yeah im buying, 1 sec              [starts walking]
//   Ulric: 2900?                              [arrives, offers]
//   you:   4k
//   Ulric: 3400 is my max
//   you:   ok
//   Ulric: drop it on me
//
// Every line a bot speaks passes through PlayerBotBehavior.SpeakLine, so
// that is where a WTB gets noticed and turned into a standing want: the
// goods, the band they trade in, and a clock. Only a bot that can actually
// pay records one — the shout is still just flavour otherwise, but a want
// you can answer is always backed by coin in a real pack.
//
// From "i have one" onward this is the same machinery a WTS shout uses.
// The only difference is that nothing is rolled to decide whether anyone
// is interested: the bot already said it was, out loud, and it is
// answering the person who spoke up.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.CustomBots
{
    public static class BotWantAd
    {
        // As far as the shout carries.
        public const int AnswerRange = 16;

        // How long a want stands before the bot has moved on. Short: this
        // is somebody at a bank, not a classified ad.
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(4);

        private sealed class Want
        {
            public string Noun;
            public int Low;
            public int High;
            public DateTime Until;
        }

        private static readonly Dictionary<Serial, Want> _wants = new();

        // -----------------------------------------------------------------
        // Every spoken line comes through here. A WTB that names something
        // the stock table knows, from a bot that can cover the cheap end of
        // the band, becomes a want somebody can answer.
        //
        // The announcement no longer wins on its own: it has to be a
        // thing this class would actually buy, and the bot has to be able
        // to pay for it. A shout that fails either test stays flavour, so
        // nothing a bot says is ever contradicted by what it does.
        // -----------------------------------------------------------------
        public static void Posted(PlayerBot bot, string line)
        {
            if (bot == null || bot.Deleted || string.IsNullOrEmpty(line) || !BotShop.Enabled)
            {
                return;
            }

            // Checked before lowercasing on purpose: every line every bot
            // speaks comes through here, and all but a handful are not WTB.
            if (!line.StartsWith("wtb", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!BotAppraisal.BandForNoun(line.ToLowerInvariant(), out int low, out int high,
                    out var noun, out var kind))
            {
                return;
            }

            // Would this bot really buy it? A mage reading the halberd line
            // off the list is still just talking. The shout stays flavour,
            // the same as every WTB used to be — but the ones that fit the
            // speaker are now offers a person can answer.
            if (!BotWants.Wants(bot, kind, noun))
            {
                return;
            }

            // Back the claim or it stays flavour. Wealth, not pocket money:
            // Settle banks everything above walking money, so testing the
            // pack alone made every bot on the shard look broke and killed
            // every want worth more than a bag of reagents.
            if (BotBanking.Wealth(bot) < low)
            {
                return;
            }

            Prune();

            _wants[bot.Serial] = new Want
            {
                Noun = noun,
                Low = low,
                High = high,
                Until = Core.Now + Lifetime,
            };
        }

        // What this bot is in the market for, or null.
        public static string NounWanted(PlayerBot bot) => Live(bot)?.Noun;

        // -----------------------------------------------------------------
        // "i have one". Returns true when it started a negotiation, so the
        // responder stops and the generic shrug never lands on top of it.
        // -----------------------------------------------------------------
        public static bool Answered(PlayerBot bot, Mobile player, string lower, int dist)
        {
            if (bot == null || player == null || dist > AnswerRange || string.IsNullOrEmpty(lower))
            {
                return false;
            }

            var want = Live(bot);
            if (want == null || BotBuyOffer.IsBuying(bot) || BotShopDeal.IsDealing(bot))
            {
                return false;
            }

            lower = StripName(lower, bot.Name);

            if (!ReadsAsHavingIt(lower, want.Noun))
            {
                return false;
            }

            // People name their price in the same breath as often as not.
            int asking = BotBuyOffer.LastGold(lower);

            if (!BotBuyOffer.StartFromWant(bot, player, want.Noun, want.Low, want.High, asking))
            {
                return false;
            }

            _wants.Remove(bot.Serial);
            Console.WriteLine(
                $"[buy] {player.Name} answered {bot.Name}'s WTB for {want.Noun}");
            return true;
        }

        // Does this read as "yes, I have that"?
        //
        // "i have one" is unambiguous from anybody. "i have" on its own is
        // how "i have to go" starts, so the vague forms only count when the
        // goods are named in the same breath — in the table's words or in
        // the era's shorthand, since somebody answering "WTB GM hally" is
        // going to type hally back.
        //
        // Public and pure so it can be tested without standing a bot up at
        // a bank.
        public static bool ReadsAsHavingIt(string lower, string noun)
        {
            if (string.IsNullOrEmpty(lower))
            {
                return false;
            }

            if (MatchesAny(lower, StrongHave))
            {
                return true;
            }

            // Mentions matches on letter boundaries, so it never had the
            // punctuation hole the phrase matcher did.
            return BotAppraisal.Mentions(BotAppraisal.ExpandSlang(lower), noun) &&
                   MatchesAny(lower, WeakHave);
        }

        // Unambiguous: nobody says these to someone they are not selling to.
        private static readonly string[] StrongHave =
        {
            "i have one", "i have 1", "ive got one", "i've got one", "i got one",
            "got one", "i have it", "i got it", "ive got it", "i've got it",
            "i have some", "i got some", "got some", "i have two", "i have a few",
            "i can sell you one", "i can sell u one", "i can sell one",
            "i have that", "i got that", "i sell one", "want mine",
            "i have one for sale", "ill sell you one", "ill sell u one",
        };

        // Only count once the goods have been named.
        private static readonly string[] WeakHave =
        {
            "i have", "ive got", "i've got", "i got", "yeah", "ya", "yep",
            "yes", "sure", "here", "selling", "wts", "for sale", "want",
        };

        // -----------------------------------------------------------------
        private static Want Live(PlayerBot bot)
        {
            if (bot == null || !_wants.TryGetValue(bot.Serial, out var want))
            {
                return null;
            }

            if (Core.Now >= want.Until)
            {
                _wants.Remove(bot.Serial);
                return null;
            }

            return want;
        }

        // Wants expire on their own; this just stops a long-running shard
        // accumulating the dead ones.
        private static void Prune()
        {
            if (_wants.Count < 64)
            {
                return;
            }

            var now = Core.Now;
            var stale = new List<Serial>();

            foreach (var kv in _wants)
            {
                if (now >= kv.Value.Until)
                {
                    stale.Add(kv.Key);
                }
            }

            foreach (var key in stale)
            {
                _wants.Remove(key);
            }
        }

        private static string StripName(string lower, string botName)
        {
            if (string.IsNullOrEmpty(botName))
            {
                return lower;
            }

            int sp = botName.IndexOf(' ');
            var first = (sp > 0 ? botName[..sp] : botName).ToLowerInvariant();
            return first.Length < 3
                ? lower
                : lower.Replace(first, "", StringComparison.Ordinal).Trim(' ', ',');
        }

        // See the note in BotShopTalk.MatchesAny.
        private static bool MatchesAny(string lower, string[] phrases)
        {
            lower = BotAppraisal.Spaced(lower);

            foreach (var p in phrases)
            {
                if (lower == p || lower.StartsWith(p + " ", StringComparison.Ordinal) ||
                    lower.EndsWith(" " + p, StringComparison.Ordinal) ||
                    lower.Contains(" " + p + " ", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
