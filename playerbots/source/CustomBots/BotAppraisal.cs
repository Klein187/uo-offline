// =========================================================================
// BotAppraisal.cs — what a bot thinks your stuff is worth.
//
// BotShop values goods in one direction: a hawker rolls a price off the
// stock table and shouts it. Selling TO a bot needs the same arithmetic
// run backwards, on an item the bot did not roll and has never seen.
//
// Two questions, asked at two different moments:
//
//   BandForNoun("wts GM halberd 5k")  — before anything is on the table.
//     All a bot has is the shout. It matches the words against the stock
//     table's nouns (and the era's shorthand: hally, bm, sa, xbow, vanq)
//     and gets back the price band that kind of thing trades in. That is
//     enough to decide whether the shout is worth crossing the floor for.
//
//   Value(item)                       — once the goods are in the window.
//     Now it can see the thing. The same table row prices it for real,
//     placed in its band by amount for a stack and by quality and magic
//     tier for gear. Deterministic on purpose: the number must not move
//     between the handshake and the trade, or a bot would talk itself
//     into a price it then refuses to pay.
//
// Nothing here rolls dice. The haggling upstream does that.
// =========================================================================

using System;
using Server;
using Server.Items;

namespace Server.CustomBots
{
    public static class BotAppraisal
    {
        // A buyer at the bank is not a vendor paying retail. This is the
        // share of open-market value a bot will go to at its most generous;
        // BotBuyOffer rolls its own nerve inside this.
        public const double TopOfferShare = 0.85;

        // A gear row is priced for the exceptional piece, because that is
        // what a hawker shouts about. Plain work is worth this much of it.
        private const double PlainGearShare = 0.40;

        // What the era typed instead of the item's real name. The stock
        // table's nouns are already the common words ("heavy xbow", "gheal
        // scrolls"), so this only covers what it doesn't say.
        private static readonly (string Slang, string Noun)[] Slang =
        {
            // "WTB regs" is the single most-shouted line at any 1999 bank
            // and it resolved to nothing, so the most common want on the
            // shard was the one nobody could answer. Reagents have no one
            // row, so it lands on the cheapest one and the band covers the
            // rest; a buyer shouting "regs" is not being precise either.
            ("regs", "ginseng"),
            ("reagents", "ginseng"),
            ("reg", "ginseng"),

            ("hally", "halberd"),
            ("halb", "halberd"),
            ("bard", "bardiche"),
            ("xbow", "crossbow"),
            ("hxbow", "heavy xbow"),
            ("bm", "blood moss"),
            ("bp", "black pearl"),
            ("sa", "sulfurous ash"),
            ("ns", "nightshade"),
            ("ss", "spider silk"),
            ("mr", "mandrake"),
            ("gins", "ginseng"),
            ("gheal", "gheal scrolls"),
            ("ebolt", "ebolt scrolls"),
            ("recalls", "recall scrolls"),
            ("runes", "mark scrolls"),
            ("bandies", "bandages"),
            ("ingots", "iron ingots"),
            ("lumber", "boards"),
            ("plate", "plate tunic"),
            ("chain", "chain tunic"),
            ("studded", "studded tunic"),
            ("ringmail", "ringmail tunic"),
        };

        // Words that tell a bot which end of the table the shout is about
        // before it can see anything. "wts vanq hally" and "wts hally" are
        // not remotely the same offer.
        private static readonly string[] MagicWords =
        {
            "vanq", "vanquishing", "power", "force", "might", "ruin",
            "invul", "invuln", "invulnerability", "fortif", "fortification",
            "hardening", "magic", "silver",
        };

        private static readonly string[] QualityWords =
        {
            "gm", "exceptional", "exc", "excep",
        };

        // -----------------------------------------------------------------
        // The shout. Returns the band the named goods trade in, or false
        // when nothing in the table matches - a bot that cannot place what
        // you are selling has no opinion about it and stays put.
        //
        // The band is shaped by the words, not just the noun, so it lines up
        // with what Value() will say once the goods are on the table. A gear
        // row is priced for the craftsman's mark, because that is what gets
        // hawked; a shout that never says GM is offering plain work and gets
        // the plain-work band. Otherwise a bot talks itself up to 3k for a
        // halberd it would then refuse to pay for at the window.
        // -----------------------------------------------------------------
        public static bool BandForNoun(string lower, out int low, out int high, out string noun) =>
            BandForNoun(lower, out low, out high, out noun, out _);

        // The same scan, also handing back what KIND of goods the shout is
        // about. BotBuyOffer needs that for the demand roll, and it has to
        // come out of this scan rather than a second one, or the price and
        // the appetite could end up talking about different rows.
        public static bool BandForNoun(string lower, out int low, out int high, out string noun,
            out GoodsKind kind)
        {
            low = 0;
            high = 0;
            noun = null;
            kind = GoodsKind.Bulk;

            if (string.IsNullOrEmpty(lower))
            {
                return false;
            }

            // Slang first, so "wts GM hally 5k" finds the halberd rows.
            lower = ExpandSlang(lower);

            bool magic = MatchesAny(lower, MagicWords);
            bool gm = !magic && MatchesAny(lower, QualityWords);

            foreach (var row in BotShop.Table)
            {
                // A magic shout is about the magic rows and nothing else,
                // and a plain one must never be priced off them.
                if (row.Magic != magic)
                {
                    continue;
                }

                if (!Mentions(lower, row.Noun))
                {
                    continue;
                }

                // Widest reading of an ambiguous word wins. "wts plate"
                // could be the exceptional tunic or an invulnerability one,
                // and the bot does not know which until it sees it.
                if (noun == null || row.Noun.Length > noun.Length)
                {
                    noun = row.Noun;
                    kind = row.Kind;
                }

                int rowLow = row.MinPrice;
                int rowHigh = row.MaxPrice;

                // A stack is priced by how big it is, and people say so:
                // "wts 200 mandrake 900". Without a count the whole band
                // stands, because a bot cannot tell 40 pearl from 200.
                if (row.MaxAmount > 1)
                {
                    int amount = AmountNamed(lower);
                    if (amount > 0)
                    {
                        int v = StackValue(row, amount);
                        rowLow = (int)(v * 0.75);
                        rowHigh = (int)(v * 1.25);
                    }
                }
                else if (row.Exceptional && !gm)
                {
                    // Plain work off a shelf, not a smith's best piece.
                    rowLow = (int)(rowLow * PlainGearShare);
                    rowHigh = (int)(rowHigh * PlainGearShare);
                }

                if (low == 0 || rowLow < low)
                {
                    low = rowLow;
                }
                if (rowHigh > high)
                {
                    high = rowHigh;
                }
            }

            return noun != null && high > 0;
        }

        // Append the real name of anything said in shorthand, so the rest of
        // the matching only has to know the table's own nouns. Appending
        // rather than replacing keeps the original words intact for the
        // quality and count checks that also read this string.
        public static string ExpandSlang(string lower)
        {
            if (string.IsNullOrEmpty(lower))
            {
                return lower;
            }

            foreach (var (slang, real) in Slang)
            {
                if (ContainsWord(lower, slang))
                {
                    lower += " " + real;
                }
            }

            return lower;
        }

        // Punctuation between words hides a phrase from the matchers: the
        // matchers look for " phrase " with spaces around it, so "i have
        // one, 5k" does not read as "i have one" and a real offer gets
        // ignored. People type commas.
        //
        // Only ever used for PHRASE matching. Never run it before reading a
        // price: it would turn "1,200" into two numbers.
        public static string Spaced(string lower)
        {
            if (string.IsNullOrEmpty(lower))
            {
                return lower;
            }

            Span<char> buffer = stackalloc char[lower.Length];
            for (int i = 0; i < lower.Length; i++)
            {
                var c = lower[i];
                buffer[i] = c is ',' or '.' or '!' or '?' or ';' or ':' or '"' ? ' ' : c;
            }

            return new string(buffer);
        }

        // Did the shout claim the good stuff? A bot prices, and wants, a
        // "GM halberd" quite differently from a "halberd".
        public static bool ClaimsPremium(string lower) =>
            !string.IsNullOrEmpty(lower) &&
            (MatchesAny(lower, MagicWords) || MatchesAny(lower, QualityWords));

        // A count standing on its own in the shout ("wts 200 mandrake 900").
        // The last number is the price, so anything before it is the lot.
        private static int AmountNamed(string lower)
        {
            int first = 0;
            int seen = 0;

            for (int i = 0; i < lower.Length; i++)
            {
                if (!char.IsDigit(lower[i]) || (i > 0 && char.IsLetterOrDigit(lower[i - 1])))
                {
                    continue;
                }

                int j = i;
                while (j < lower.Length && char.IsDigit(lower[j]))
                {
                    j++;
                }

                // "5k" is money, never a count of anything.
                bool k = j < lower.Length && (lower[j] == 'k' || lower[j] == 'K');
                if (!k && int.TryParse(lower[i..j], out var n) && n > 1 && n <= 60000)
                {
                    seen++;
                    if (seen == 1)
                    {
                        first = n;
                    }
                }

                i = j;
            }

            // One bare number is the price, not a count. Two means the
            // first one was counting something.
            return seen >= 2 ? first : 0;
        }

        // -----------------------------------------------------------------
        // The goods, in hand. Open-market value, 0 for anything a bot at a
        // bank would not give you a coin for.
        // -----------------------------------------------------------------
        public static int Value(Item item)
        {
            if (item == null || item.Deleted)
            {
                return 0;
            }

            int amount = Math.Max(1, item.Amount);
            var row = PickRow(item);

            return row != null ? BandValue(row, item, amount) : GenericValue(item, amount);
        }

        // What a bot will pay at the very top of its nerve.
        public static int TopOffer(Item item) => (int)(Value(item) * TopOfferShare);

        // -----------------------------------------------------------------
        // What the bot calls the thing, once it can see it. Mirrors what a
        // hawker would have shouted about the same item.
        // -----------------------------------------------------------------
        public static string NameFor(Item item)
        {
            if (item == null)
            {
                return "it";
            }

            var row = PickRow(item);
            var noun = row?.Noun ?? DefaultNoun(item);
            int amount = Math.Max(1, item.Amount);

            string prefix = item switch
            {
                BaseWeapon w when w.DamageLevel != WeaponDamageLevel.Regular =>
                    DamageWord(w.DamageLevel) + " ",
                BaseWeapon { Quality: WeaponQuality.Exceptional } => "GM ",
                BaseArmor a when a.ProtectionLevel != ArmorProtectionLevel.Regular =>
                    ProtectionWord(a.ProtectionLevel) + " ",
                BaseArmor { Quality: ArmorQuality.Exceptional } => "GM ",
                _ when IsRareHue(item.Hue) => item.Hue == 0x0001 ? "black " : "",
                _ => "",
            };

            return amount > 1 ? $"{amount} {prefix}{noun}" : $"{prefix}{noun}";
        }

        // -----------------------------------------------------------------
        // Table lookup. A type can carry more than one row — a halberd is
        // both the exceptional one at 2.5k and the vanq one at 40k — so the
        // item's own properties choose which row prices it.
        // -----------------------------------------------------------------
        private static Goods PickRow(Item item)
        {
            var typeName = item.GetType().FullName;
            Goods plain = null, magic = null, hued = null;

            foreach (var row in BotShop.Table)
            {
                if (row.Type != typeName)
                {
                    continue;
                }

                if (row.Magic)
                {
                    magic = row;
                }
                else if (row.RareHue)
                {
                    hued = row;
                }
                else
                {
                    plain ??= row;
                }
            }

            if (IsMagic(item) && magic != null)
            {
                return magic;
            }

            if (hued != null && IsRareHue(item.Hue))
            {
                return hued;
            }

            return plain ?? magic ?? hued;
        }

        private static int BandValue(Goods row, Item item, int amount)
        {
            // Stackables price by the pile. Per-unit rises with the size of
            // the lot in the table (40 pearl at 3gp, 200 at 4.5gp), so walk
            // the same slope and let a lot bigger than the table's biggest
            // keep the top unit price rather than falling off a cliff.
            if (row.MaxAmount > 1)
            {
                return StackValue(row, amount);
            }

            double place = row.Magic
                ? MagicPlace(item)
                : row.RareHue
                    ? (item.Hue == 0x0001 ? 1.0 : 0.4)
                    : QualityPlace(row, item);

            int value = (int)(row.MinPrice + (row.MaxPrice - row.MinPrice) * Clamp01(place));

            // A gear row assumes the craftsman's mark, because that is what
            // gets hawked. Plain work off a vendor shelf is worth a lot less
            // than the exceptional piece the row is priced for.
            if (row.Exceptional && !IsExceptional(item) && !IsMagic(item))
            {
                value = (int)(value * PlainGearShare);
            }

            return Math.Max(1, value);
        }

        // Where a magic piece sits in its band: the damage or protection
        // ladder everyone knew by name, nudged up for the extra lines.
        private static double MagicPlace(Item item)
        {
            switch (item)
            {
                case BaseWeapon w:
                {
                    double t = Span((int)w.DamageLevel, 1, 5);
                    if (w.AccuracyLevel != WeaponAccuracyLevel.Regular)
                    {
                        t += 0.08;
                    }
                    if (w.DurabilityLevel != WeaponDurabilityLevel.Regular)
                    {
                        t += 0.05;
                    }
                    return t;
                }
                case BaseArmor a:
                {
                    double t = Span((int)a.ProtectionLevel, 1, 5);
                    if (a.Durability != ArmorDurabilityLevel.Regular)
                    {
                        t += 0.06;
                    }
                    return t;
                }
                default:
                    return 0.5;
            }
        }

        private static double QualityPlace(Goods row, Item item) =>
            row.Kind == GoodsKind.BigTicket ? 0.5 : IsExceptional(item) ? 0.45 : 0.15;

        // -----------------------------------------------------------------
        // Off the table entirely. A bot still knows roughly what a sword is
        // worth, so a piece the stock table never lists is not a blank look.
        // Anything that is not gear, cloth, or a jewel comes back 0 and the
        // bot says it isn't interested, which is the honest answer.
        // -----------------------------------------------------------------
        private static int GenericValue(Item item, int amount)
        {
            int per = item switch
            {
                BaseWeapon    => 90,
                BaseArmor     => 70,
                BaseClothing  => 25,
                BaseJewel     => 40,
                BasePotion    => 12,
                _             => 0,
            };

            if (per == 0)
            {
                return 0;
            }

            if (IsExceptional(item))
            {
                per *= 2;
            }

            if (IsMagic(item))
            {
                per = (int)(per * (2.0 + 6.0 * MagicPlace(item)));
            }

            return Math.Max(1, per * amount);
        }

        // -----------------------------------------------------------------
        // What a pile is worth. Per-unit rises with the size of the lot in
        // the table (40 pearl at 3gp each, 200 at 4.5), so walk the same
        // slope, and let a lot bigger than the table's biggest keep the top
        // unit price rather than falling off a cliff.
        private static int StackValue(Goods row, int amount)
        {
            double unitLow = row.MinPrice / (double)Math.Max(1, row.MinAmount);
            double unitHigh = row.MaxPrice / (double)Math.Max(1, row.MaxAmount);
            double t = Span(amount, row.MinAmount, row.MaxAmount);
            return Math.Max(1, (int)((unitLow + (unitHigh - unitLow) * t) * amount));
        }

        private static bool MatchesAny(string lower, string[] words)
        {
            foreach (var w in words)
            {
                if (ContainsWord(lower, w))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsMagic(Item item) => item switch
        {
            BaseWeapon w => w.DamageLevel != WeaponDamageLevel.Regular ||
                            w.AccuracyLevel != WeaponAccuracyLevel.Regular,
            BaseArmor a  => a.ProtectionLevel != ArmorProtectionLevel.Regular,
            _            => false,
        };

        private static bool IsExceptional(Item item) => item switch
        {
            BaseWeapon w => w.Quality == WeaponQuality.Exceptional,
            BaseArmor a  => a.Quality == ArmorQuality.Exceptional,
            _            => false,
        };

        // The 1998 dye tub range the rest of the kit rolls in.
        private static bool IsRareHue(int hue) => hue == 0x0001 || (hue >= 2 && hue <= 1001);

        private static string DamageWord(WeaponDamageLevel level) => level switch
        {
            WeaponDamageLevel.Ruin  => "ruin",
            WeaponDamageLevel.Might => "might",
            WeaponDamageLevel.Force => "force",
            WeaponDamageLevel.Power => "power",
            WeaponDamageLevel.Vanq  => "vanq",
            _                       => "",
        };

        // See BotShop.ProtectionWord: "{level:L}" is a FormatException on an
        // enum, and this copy would have thrown the moment a bot was asked
        // to name a piece of armour with a protection level on it.
        private static string ProtectionWord(ArmorProtectionLevel level) => level switch
        {
            ArmorProtectionLevel.Defense        => "defense",
            ArmorProtectionLevel.Guarding       => "guarding",
            ArmorProtectionLevel.Hardening      => "hardening",
            ArmorProtectionLevel.Fortification  => "fortification",
            ArmorProtectionLevel.Invulnerability => "invulnerability",
            _                                   => "",
        };

        // Last resort name: the item's own, lowercased the way a player
        // would type it, with the article the client puts on the front cut.
        private static string DefaultNoun(Item item)
        {
            var name = item.Name;
            if (string.IsNullOrEmpty(name))
            {
                name = item.GetType().Name;
            }

            name = name.ToLowerInvariant();
            foreach (var article in new[] { "an ", "a " })
            {
                if (name.StartsWith(article, StringComparison.Ordinal))
                {
                    name = name[article.Length..];
                    break;
                }
            }
            return name;
        }

        // Did the shout name these goods? The table's nouns carry their
        // adjectives ("recall scrolls", "plate tunic"), and nobody types the
        // whole thing, so the head word counts as a mention on its own.
        public static bool Mentions(string lower, string noun)
        {
            if (ContainsWord(lower, noun))
            {
                return true;
            }

            int sp = noun.LastIndexOf(' ');
            if (sp < 0)
            {
                return false;
            }

            var head = noun[(sp + 1)..];
            return head.Length >= 4 && ContainsWord(lower, head);
        }

        // Whole words only. "sa" must not match "sash", and "bp" must not
        // match "bpx" — the shorthand is short enough to hit anything.
        private static bool ContainsWord(string haystack, string word)
        {
            int at = 0;
            while ((at = haystack.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
            {
                bool leftOk = at == 0 || !char.IsLetterOrDigit(haystack[at - 1]);
                int end = at + word.Length;
                bool rightOk = end >= haystack.Length || !char.IsLetterOrDigit(haystack[end]);

                if (leftOk && rightOk)
                {
                    return true;
                }
                at = end;
            }
            return false;
        }

        private static double Span(int value, int min, int max) =>
            max <= min ? 1.0 : Clamp01((value - min) / (double)(max - min));

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
