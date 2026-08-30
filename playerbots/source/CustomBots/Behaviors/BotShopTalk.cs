// =========================================================================
// BotShopTalk.cs — haggling with a real player, out loud.
//
// A hawker shouts "WTS GM halberd 5k". The player who walks over gets the
// conversation that shout implies, typed the way it was typed in 1999:
//
//   you: how much for the halberd
//   Ulric: 5k
//   you: 3500?
//   Ulric: cant do that. 4400
//   you: 4k?
//   Ulric: 4k and its yours
//   you: k
//   Ulric: drop the gold on me
//   [drag 4000 gold onto Ulric -> the trade window opens]
//
// The numbers are not decoration. BotShop.Consider runs the same
// arithmetic for a player as it does for a bot buyer, against the same
// asking price and the same hidden floor, and the price that comes out is
// the price the trade window will hold the player to.
//
// This hangs off BotSpeechResponder, which owns the "one bot answers"
// claim and the per-bot cooldowns.
// =========================================================================

using System;
using Server;
using Server.Items;

namespace Server.CustomBots
{
    public static class BotShopTalk
    {
        // Shop talk is a face-to-face thing. Shouting a number across the
        // bank at nobody in particular shouldn't buy you a halberd.
        public const int TalkRange = 6;

        private static readonly string[] PriceAsks =
        {
            "how much", "howmuch", "hm?", "hm", "price", "how much for",
            "whats the price", "what's the price", "wat price", "how much is",
            "cost", "how much u want", "how much you want", "hw much",
        };

        // Unambiguous even from a stranger: nobody says "ill take it" to
        // someone they aren't buying from.
        private static readonly string[] StrongAccepts =
        {
            "ill take it", "i'll take it", "ill take", "take it", "deal",
            "sold", "ill buy", "i'll buy", "ill buy it", "gimme", "give me",
        };

        // Ordinary words that only mean "yes, sold" INSIDE a negotiation.
        // Un-gated, these hijacked everything else a player says near a
        // bank: "sure" is how you accept a party invite, "ok" is how you
        // answer anyone, and a hawker would have swallowed both.
        private static readonly string[] WeakAccepts =
        {
            "ok", "k", "kk", "fine", "done", "yes", "ya", "yea", "yeah",
            "sure", "aight", "ill take that", "buy",
        };

        private static readonly string[] StrongRejects =
        {
            "nvm", "nevermind", "never mind", "no thanks", "no thx", "nty",
            "too much", "to much", "too expensive", "forget it", "not worth",
        };

        private static readonly string[] WeakRejects =
        {
            "no", "nah", "pass", "lol no", "meh",
        };

        private static readonly string[] StockAsks =
        {
            "what are you selling", "what r u selling", "whatcha selling",
            "what do you have", "what do u have", "what u got", "what you got",
            "wat u got", "selling", "wts?", "what are u selling",
        };

        // -----------------------------------------------------------------
        // Returns true when this utterance was shop talk and got handled —
        // the caller then stops, so the generic "dunno" shrug never lands
        // on top of a price quote.
        // -----------------------------------------------------------------
        public static bool Handle(PlayerBot bot, Mobile speaker, string lower, int dist)
        {
            if (dist > TalkRange || string.IsNullOrEmpty(lower))
            {
                return false;
            }

            var stock = BotShop.StockOf(bot);
            if (stock == null)
            {
                return false;
            }

            // Two hawkers side by side both hear "how much". The nearer one
            // answers. Comparing only against other SELLERS on purpose: an
            // ordinary bot standing closer shouldn't be able to intercept a
            // price question and shrug at it.
            if (!IsClosestSeller(bot, speaker, dist))
            {
                return false;
            }

            // Strip the bot's own name so "ulric how much" reads the same
            // as "how much" — people address the seller by name constantly.
            lower = StripName(lower, bot.Name);

            // Already shaken on a price? Then everything is about payment.
            int agreed = BotShop.AgreedPriceFor(stock, speaker.Serial);

            if (MatchesAny(lower, StockAsks))
            {
                Reply(bot, speaker, "shop_stock", stock, stock.Asking);
                return true;
            }

            if (MatchesAny(lower, PriceAsks) || MentionsGoods(lower, stock))
            {
                // Naming the goods with a number in the same breath is an
                // offer, not a question: "3k for the halberd".
                int inline = ParseGold(lower);
                if (inline > 0)
                {
                    return Offer(bot, speaker, stock, inline);
                }

                Reply(bot, speaker, agreed > 0 ? "shop_agreed" : "shop_price",
                    stock, agreed > 0 ? agreed : stock.Asking);
                return true;
            }

            // Vague words only count once this player has actually been
            // talking to this seller.
            bool engaged = agreed > 0 || BotShop.HasSession(stock, speaker.Serial);

            if (ReadsAsAccept(lower, engaged))
            {
                int price = agreed > 0 ? agreed : stock.Asking;
                BotShop.Agree(stock, speaker.Serial, price);
                Reply(bot, speaker, "shop_paynow", stock, price);
                return true;
            }

            if (ReadsAsReject(lower, engaged))
            {
                Reply(bot, speaker, "shop_nodeal", stock, stock.Asking);
                return true;
            }

            // A bare number is an offer — but only from someone already in
            // the conversation. Shouting "5k" across a bank at nobody is
            // how people talked, and it shouldn't bind you to a halberd.
            int bare = ParseBareGold(lower);
            if (bare > 0 && engaged)
            {
                return Offer(bot, speaker, stock, bare);
            }

            return false;
        }

        // Does this read as "yes, sold" / "no thanks"? `engaged` is whether
        // this player is already mid-conversation with this seller: the
        // vague words ("ok", "sure", "no") only count once they are, or a
        // hawker swallows every "sure" said anywhere near a bank.
        //
        // Public and pure so the phrase rules can be tested without
        // standing a hawker up at a bank.
        public static bool ReadsAsAccept(string lower, bool engaged) =>
            MatchesAny(lower, StrongAccepts) || (engaged && MatchesAny(lower, WeakAccepts));

        public static bool ReadsAsReject(string lower, bool engaged) =>
            MatchesAny(lower, StrongRejects) || (engaged && MatchesAny(lower, WeakRejects));

        // -----------------------------------------------------------------
        private static bool Offer(PlayerBot bot, Mobile speaker, ShopStock stock, int offer)
        {
            var result = BotShop.Consider(stock, speaker.Serial, offer, out int counter);

            switch (result)
            {
                case BotShop.HaggleResult.Accepted:
                    Reply(bot, speaker, "shop_deal", stock, counter);
                    // Follow the handshake with where to put the money.
                    Nudge(bot, speaker, stock, counter);
                    return true;

                case BotShop.HaggleResult.Insulted:
                    Reply(bot, speaker, "shop_insult", stock, stock.Asking);
                    return true;

                case BotShop.HaggleResult.Refused:
                    Reply(bot, speaker, "shop_final", stock, counter);
                    return true;

                default:
                    Reply(bot, speaker,
                        stock.Temper == HaggleTemper.Firm && Utility.RandomBool()
                            ? "shop_firm"
                            : "shop_counter",
                        stock, counter);
                    return true;
            }
        }

        // "drop the gold on me" — the only instruction a player needs,
        // because dragging coin onto the seller IS the trade.
        private static void Nudge(PlayerBot bot, Mobile speaker, ShopStock stock, int price)
        {
            var line = ChatLibrary.PickRandom("shop_paynow");
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            line = Fill(line, stock, price);
            Timer.DelayCall(TimeSpan.FromSeconds(2.2 + Utility.RandomDouble()), () =>
            {
                if (bot is { Deleted: false, Alive: true } && !speaker.Deleted &&
                    speaker.InRange(bot.Location, TalkRange))
                {
                    bot.Say(line);
                }
            });
        }

        private static void Reply(PlayerBot bot, Mobile speaker, string category,
            ShopStock stock, int price)
        {
            var line = ChatLibrary.PickRandom(category);
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            line = Fill(line, stock, price);

            var d = bot.GetDirectionTo(speaker);
            if (bot.Direction != d)
            {
                bot.Direction = d;
            }

            // Same typing pause the rest of the responder uses — an
            // instant answer is the loudest tell there is.
            double delay = 0.9 + Utility.RandomDouble() * 1.1 +
                           Math.Min(line.Length * 0.04, 1.2);
            Timer.DelayCall(TimeSpan.FromSeconds(delay), () =>
            {
                if (bot is { Deleted: false, Alive: true, Hidden: false } && !speaker.Deleted)
                {
                    bot.Say(line);
                }
            });

            Console.WriteLine($"[shop] {bot.Name} -> {speaker.Name}: {category} ({price}gp)");
        }

        private static string Fill(string line, ShopStock stock, int price) =>
            line.Replace("{item}", stock.Noun, StringComparison.Ordinal)
                .Replace("{price}", BotShop.Coin(price), StringComparison.Ordinal);

        // -----------------------------------------------------------------
        // Parsing what people actually typed.
        // -----------------------------------------------------------------

        // A number anywhere in the sentence: "3k for the halberd", "ill go
        // 2500". Ignores digits that are part of a word.
        public static int ParseGold(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsDigit(text[i]))
                {
                    continue;
                }
                if (i > 0 && char.IsLetter(text[i - 1]))
                {
                    continue; // mid-word, e.g. "b19"
                }

                int j = i;
                while (j < text.Length && (char.IsDigit(text[j]) || text[j] == '.' ||
                                           text[j] == ','))
                {
                    j++;
                }

                var span = text[i..j].Replace(",", "", StringComparison.Ordinal);
                bool k = j < text.Length && (text[j] == 'k' || text[j] == 'K');

                if (double.TryParse(span, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    if (k)
                    {
                        value *= 1000;
                    }
                    int gold = (int)Math.Round(value);
                    if (gold > 0 && gold < 100_000_000)
                    {
                        return gold;
                    }
                }

                i = j;
            }
            return 0;
        }

        // A message that is ONLY a number ("4k", "3500", "2.5k?"). Used
        // for the bare-offer case so ordinary chat with a digit in it
        // ("meet me at 2 pm") doesn't read as a bid.
        private static int ParseBareGold(string text)
        {
            var t = text.Trim().TrimEnd('?', '!', '.', ' ');
            if (t.Length == 0 || t.Length > 12)
            {
                return 0;
            }

            foreach (var c in t)
            {
                if (!char.IsDigit(c) && c != 'k' && c != 'K' && c != '.' && c != ',')
                {
                    return 0;
                }
            }
            return ParseGold(t);
        }

        // Did they name the thing on offer? The stock noun carries its
        // adjectives ("exceptional halberd"), so match on the last word —
        // nobody types the whole thing.
        private static bool MentionsGoods(string lower, ShopStock stock)
        {
            var noun = stock.Noun;
            int sp = noun.LastIndexOf(' ');
            var head = sp >= 0 ? noun[(sp + 1)..] : noun;
            return head.Length >= 3 && lower.Contains(head, StringComparison.Ordinal);
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

        // Nearest bot holding stock wins the question. Ties go to whoever
        // the speech pass reaches first, which is fine — two sellers on the
        // same tile is not a case worth arbitrating.
        private static bool IsClosestSeller(PlayerBot bot, Mobile speaker, int myDist)
        {
            if (speaker.Map == null)
            {
                return true;
            }

            foreach (var m in speaker.Map.GetMobilesInRange(speaker.Location, TalkRange))
            {
                if (m == bot || m is not PlayerBot other || other.Deleted ||
                    !other.Alive || other.Hidden || other.LoggingOut ||
                    !BotShop.HasStock(other))
                {
                    continue;
                }

                int dx = Math.Abs(other.X - speaker.X);
                int dy = Math.Abs(other.Y - speaker.Y);
                if ((dx > dy ? dx : dy) < myDist)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool MatchesAny(string lower, string[] phrases)
        {
            // Punctuation directly after a phrase used to hide it from every
            // test below, because each one wants a space where the message
            // carries on. "deal!", "sold!", "ok." and "deal, drop it on me"
            // were all silently ignored, and those are the normal ways to
            // type it. Normalised HERE rather than at the call sites so no
            // future one can forget.
            //
            // Price parsing deliberately does not get this: it would read
            // "1,200" as two numbers.
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
