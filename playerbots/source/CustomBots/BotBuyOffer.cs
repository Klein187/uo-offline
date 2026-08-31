// =========================================================================
// BotBuyOffer.cs — you shout WTS at the bank and somebody actually comes.
//
// BotShopDeal runs a bot buying from a bot. BotShopTalk runs a player
// buying from a bot. This is the corner that was missing: a REAL PLAYER
// selling, with a bot on the buying end.
//
//   you: WTS GM halberd 5k
//   Ulric: whats the halberd, ill have a look          [starts walking]
//   Ulric: 2800?                                       [arrives, offers]
//   you: 4k
//   Ulric: 3400 is my max
//   you: ok
//   Ulric: drop it on me
//   [drag the halberd onto Ulric -> the trade window opens with his gold
//    already on his side]
//
// The shape is deliberately the same as the buy side, because it is the
// same market seen from the other chair:
//
//   the shout      BotAppraisal.BandForNoun reads the words and says what
//                  that kind of thing trades for. No band, no interest.
//   the walk       PathFollower across the bank floor, on a timeout. The
//                  crossing IS the feature; a bot that teleports to you or
//                  answers from thirty tiles away reads as furniture.
//   the haggle     the bot's ceiling is real and hidden, it counters, and
//                  it will walk. It never pays more than its purse holds.
//   the payoff     BotTradeWindow, the same real trade window, with the
//                  sides swapped.
//
// Not every shout finds a buyer, and that is the point. Two gates have to
// open before anyone crosses the floor — what the room wants (rolled once
// per seller and per kind of goods, then REMEMBERED, so shouting the same
// thing again for the next quarter hour does not change anyone's mind) and
// what the individual bot wants (a mage buys reagents, a fencer buys
// weapons, a merchant buys anything). Some things simply do not sell.
//
// The one thing this has that the buy side does not: the bot agrees a
// price for goods it has never seen. So the number is re-checked against
// the actual item when it lands on the table (BotTradeWindow calls
// Balks), and a bot that agreed 3k for a GM halberd will not hand it over
// for a rusty dagger. That check is the whole anti-bait-and-switch story.
//
// There are two ways in. This file handles a player's WTS shout; BotWantAd
// handles the mirror, where the BOT shouted WTB and the player answers "i
// have one". Both land in the same state machine from the walk onward.
//
// Test hook: [BotBuy (GameMaster) fakes the shout from where you stand.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;
using Server.Items;

namespace Server.CustomBots
{
    public sealed class BotBuyOffer
    {
        // A WTS shout carries across a bank floor. Deliberately wider than
        // the responder's talking ranges: shouting is the point.
        public const int ShoutRange = 16;

        // Close enough to talk money.
        private const int DealRange = 2;

        // How many bots will be crossing the floor for one player at once.
        // The bank has a crowd in it; the crowd should not all come at you.
        private const int MaxConcurrent = 2;

        private const double StepInterval = 0.45;
        private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(25);

        // A negotiation nobody comes back to goes cold, and the bot goes
        // back to whatever it was doing.
        private static readonly TimeSpan Idle = TimeSpan.FromMinutes(2);

        // A settled price is good for this long — long enough to find the
        // item in your pack, short enough that it isn't a standing offer.
        public static readonly TimeSpan AgreementWindow = TimeSpan.FromMinutes(5);

        // A bot that just haggled with you doesn't bite on your next shout.
        private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(3);

        // How long the room stays uninterested in a given seller's given
        // goods. Long enough that re-shouting is not a way to farm a yes,
        // short enough that coming back later is worth doing.
        private static readonly TimeSpan NoInterestWindow = TimeSpan.FromMinutes(12);

        private static readonly List<BotBuyOffer> _active = new();
        private static readonly Dictionary<Serial, DateTime> _cooldowns = new();

        // (seller, goods) -> when the room will reconsider.
        private static readonly Dictionary<(Serial Seller, string Noun), DateTime> _noInterest = new();

        public static int ActiveCount => _active.Count;

        private readonly PlayerBot _bot;
        private readonly Mobile _player;
        private readonly string _noun;

        // The most it will pay. Hidden from the player, exactly like the
        // hawker's floor is hidden when the trade runs the other way.
        private readonly int _ceiling;

        // Which chat category opens the conversation. A bot flagged down by
        // a stranger's WTS asks what the goods are; a bot answering its own
        // WTB already knows, and says so.
        private readonly string _opener;

        private int _standing;      // the number the bot last said out loud
        private int _agreed;        // > 0 once both sides have shaken on it
        private DateTime _agreedUntil;
        private DateTime _lastAt;
        private int _rounds;
        private bool _arrived;
        private bool _done;
        private PathFollower _walk;
        private DateTime _walkUntil;

        private BotBuyOffer(PlayerBot bot, Mobile player, string noun, int ceiling,
            string opener)
        {
            _bot = bot;
            _player = player;
            _noun = noun;
            _ceiling = ceiling;
            _opener = opener;
            _lastAt = Core.Now;
        }

        public static void Configure()
        {
            CommandSystem.Register("BotBuy", AccessLevel.GameMaster, OnCommand);
        }

        // -----------------------------------------------------------------
        // Is this a player putting something up for sale? Sell words alone
        // are not enough — "selling" on its own is how you ask a hawker
        // what it has, and that belongs to BotShopTalk. It only counts as a
        // WTS when the shout also names goods the stock table knows.
        // -----------------------------------------------------------------
        private static readonly string[] SellShouts =
        {
            "wts", "w t s", "wtsell", "selling", "sellin", "s>", "for sale",
            "anyone buying", "any1 buying", "anybody buying", "who's buying",
            "whos buying", "anyone want", "any1 want",
        };

        public static bool IsSellShout(string lower, out string noun, out int low,
            out int high, out int asking)
        {
            noun = null;
            low = 0;
            high = 0;
            asking = 0;

            if (string.IsNullOrEmpty(lower) || !MatchesAny(lower, SellShouts))
            {
                return false;
            }

            if (!BotAppraisal.BandForNoun(lower, out low, out high, out noun))
            {
                return false;
            }

            asking = LastGold(lower);
            return true;
        }

        // -----------------------------------------------------------------
        // Should THIS bot cross the floor for that shout? Called for every
        // listener; the responder's claim keeps one shout to one buyer.
        // -----------------------------------------------------------------
        public static bool Notice(PlayerBot bot, Mobile player, string lower, int dist)
        {
            if (!BotShop.Enabled || bot == null || player == null || dist > ShoutRange)
            {
                return false;
            }

            if (_active.Count >= MaxConcurrent || IsBusy(bot) || BotShopDeal.IsDealing(bot))
            {
                return false;
            }

            // A hawker is working the other side of the market. Bots that
            // are fighting, travelling, or away from the keyboard are not
            // shopping either.
            if (BotShop.HasStock(bot) || !IsShopping(bot))
            {
                return false;
            }

            if (_cooldowns.TryGetValue(bot.Serial, out var until) && Core.Now < until)
            {
                return false;
            }

            if (!IsSellShout(lower, out var noun, out int low, out int high, out int asking))
            {
                return false;
            }

            var kind = BotAppraisal.BandForNoun(lower, out _, out _, out _, out var k)
                ? k
                : GoodsKind.Bulk;

            // Would someone like this even want it? A bank full of fencers
            // has no use for your mandrake.
            if (!Wants(bot.Class, kind))
            {
                return false;
            }

            // Money talks, in both directions. A bot that cannot reach the
            // bottom of the band has no business crossing the floor.
            int purse = CrafterStock.GoldOnHand(bot);
            if (purse < low)
            {
                return false;
            }

            // Nerve: where in the band this particular bot tops out, capped
            // by what is actually in its pack.
            double nerve = Utility.RandomMinMax(MinNerve, MaxNerve) / 100.0;
            int ceiling = Math.Min(purse, CeilingFor(low, high, nerve));
            if (ceiling < low / 2)
            {
                return false;
            }

            // A silly number gets ignored rather than answered. Somebody
            // asking 300k for a halberd is talking to themselves.
            if (asking > 0 && asking > high * 4)
            {
                return false;
            }

            // Nearest bot that could actually take the deal answers.
            if (!IsNearestBuyer(bot, player, kind, low, dist))
            {
                return false;
            }

            // Last gate, and the sticky one: does the room want it at all?
            // Rolled here rather than earlier so a verdict is only spent
            // when somebody suitable was actually standing there to give it.
            if (!RoomWants(player, noun, kind, BotAppraisal.ClaimsPremium(lower)))
            {
                return false;
            }

            var offer = new BotBuyOffer(bot, player, noun, ceiling, "buy_notice");
            _active.Add(offer);
            offer.Begin();
            return true;
        }

        // -----------------------------------------------------------------
        // The other way in: the bot shouted WTB, the player said "i have
        // one", and BotWantAd sends it here.
        //
        // Nothing is rolled. The demand gate asks whether anyone wants this
        // kind of thing and the appetite gate asks whether someone like this
        // bot would — both already answered, out loud, by the bot itself.
        // It is also a little bolder than one flagged down in the street,
        // because it went looking.
        // -----------------------------------------------------------------
        public static bool StartFromWant(PlayerBot bot, Mobile player, string noun,
            int low, int high, int asking)
        {
            if (!BotShop.Enabled || bot == null || player == null)
            {
                return false;
            }

            if (_active.Count >= MaxConcurrent || IsBusy(bot) || BotShopDeal.IsDealing(bot) ||
                !IsShopping(bot))
            {
                return false;
            }

            int purse = CrafterStock.GoldOnHand(bot);
            if (purse < low)
            {
                return false;
            }

            // A silly counter-ask still gets ignored rather than answered.
            if (asking > 0 && asking > high * 4)
            {
                return false;
            }

            double nerve = Utility.RandomMinMax(MinNerve + WantNerveBonus,
                                                MaxNerve + WantNerveBonus) / 100.0;
            int ceiling = Math.Min(purse, CeilingFor(low, high, nerve));
            if (ceiling <= 0)
            {
                return false;
            }

            var offer = new BotBuyOffer(bot, player, noun, ceiling, "buy_answered");
            _active.Add(offer);
            offer.Begin();
            return true;
        }

        // -----------------------------------------------------------------
        // Demand.
        //
        // A flat coin flip per shout is not "sometimes nobody wants it" — it
        // is "shout again". The verdict has to STICK, so it is rolled once
        // per seller and per kind of goods and then remembered: a plain
        // ringmail tunic that the bank passed on is still passed on the
        // fourth time you shout about it.
        //
        // The odds come from what the thing is. Somebody at a bank always
        // needs reagents. The market drowned in exceptional weapons, and a
        // plain one off a vendor shelf is nearly unsellable. Nobody standing
        // at a bank is carrying the coin for a keep deed.
        // -----------------------------------------------------------------
        public static double DemandFor(GoodsKind kind, bool premium) => kind switch
        {
            GoodsKind.Bulk      => 0.70,
            GoodsKind.Scroll    => 0.55,
            GoodsKind.Rare      => 0.45,
            GoodsKind.Gear      => premium ? 0.40 : 0.18,
            GoodsKind.BigTicket => 0.10,
            _                   => 0.30,
        };

        // What someone like that would want. Deliberately coarse: this is
        // "would a fencer look up at this", not an inventory system.
        public static bool Wants(BotClass cls, GoodsKind kind) => cls switch
        {
            // The town mule buys anything it thinks it can move on.
            BotClass.Merchant => true,

            // Casters: reagents, scrolls, and a magic piece worth having.
            BotClass.Mage or BotClass.Healer or BotClass.Bard or BotClass.Tamer =>
                kind is GoodsKind.Bulk or GoodsKind.Scroll or GoodsKind.Rare,

            // Fighters: what they swing and what they wear.
            BotClass.Warrior or BotClass.Fencer or BotClass.Archer or BotClass.Ranger
                or BotClass.Thief =>
                kind is GoodsKind.Gear or GoodsKind.Rare or GoodsKind.Bulk,

            BotClass.TreasureHunter =>
                kind is GoodsKind.Rare or GoodsKind.Scroll or GoodsKind.Bulk,

            // Artisans and gatherers buy materials and nothing else.
            _ => kind is GoodsKind.Bulk,
        };

        private static bool RoomWants(Mobile player, string noun, GoodsKind kind, bool premium)
        {
            var key = (player.Serial, noun);

            if (_noInterest.TryGetValue(key, out var until))
            {
                if (Core.Now < until)
                {
                    return false;
                }

                _noInterest.Remove(key);
            }

            if (Utility.RandomDouble() < DemandFor(kind, premium))
            {
                return true;
            }

            PruneNoInterest();
            _noInterest[key] = Core.Now + NoInterestWindow;
            Console.WriteLine($"[buy] nobody at the bank wants {player.Name}'s {noun} right now");
            return false;
        }

        // The verdicts are small and self-expiring, but a busy shard should
        // not accumulate them forever.
        private static void PruneNoInterest()
        {
            if (_noInterest.Count < 64)
            {
                return;
            }

            var now = Core.Now;
            var stale = new List<(Serial, string)>();

            foreach (var kv in _noInterest)
            {
                if (now >= kv.Value)
                {
                    stale.Add(kv.Key);
                }
            }

            foreach (var key in stale)
            {
                _noInterest.Remove(key);
            }
        }

        // How much nerve a buyer turns up with, as a percentage. Even the
        // boldest sits well inside the band: this is a stranger at a bank
        // buying sight-unseen, not a vendor paying book value.
        private const int MinNerve = 45;
        private const int MaxNerve = 85;

        // A bot answering its own WTB went looking for the thing, so it
        // pays a little better than one that got flagged down.
        private const int WantNerveBonus = 10;

        // The most a bot with that nerve will pay for goods in that band.
        //
        // Public because it is one half of an invariant the other half has
        // to hold up: a ceiling above twice BotAppraisal.Value would let a
        // bot haggle its way to a number it then balks at when the goods
        // land on the table, and every honest sale would end in an argument.
        // BotAppraisalTests pins the two together at MaxNerve.
        public static int CeilingFor(int low, int high, double nerve)
        {
            if (nerve < 0)
            {
                nerve = 0;
            }
            else if (nerve > 1)
            {
                nerve = 1;
            }

            return (int)(low + (high - low) * 0.30 * nerve);
        }

        // The boldest a buyer ever gets, across BOTH ways in. Tests take the
        // worst case from here rather than restating the number.
        public static double TopNerve => (MaxNerve + WantNerveBonus) / 100.0;

        // -----------------------------------------------------------------
        // Talking, once a bot is on its way over or standing in front of
        // you. Returns true when the utterance was handled, so the generic
        // shrug never lands on top of a price.
        // -----------------------------------------------------------------
        public static bool HandleSpeech(PlayerBot bot, Mobile player, string lower, int dist)
        {
            var offer = Find(bot, player);
            if (offer == null || offer._done || string.IsNullOrEmpty(lower))
            {
                return false;
            }

            offer._lastAt = Core.Now;

            // Strip the bot's name so "ulric 4k" reads as "4k".
            lower = StripName(lower, bot.Name);

            // Nothing is agreed until the bot has arrived and named a
            // number; before that, anything said just keeps it walking.
            if (!offer._arrived)
            {
                return false;
            }

            if (MatchesAny(lower, Accepts))
            {
                offer.Shake(offer._standing);
                return true;
            }

            if (MatchesAny(lower, Rejects))
            {
                offer.Say("haggle_walkaway", 0);
                offer.End();
                return true;
            }

            int ask = LastGold(lower);
            if (ask > 0)
            {
                offer.ConsiderAsk(ask);
                return true;
            }

            return false;
        }

        private static readonly string[] Accepts =
        {
            "ok", "k", "kk", "deal", "sold", "done", "fine", "yes", "ya",
            "yea", "yeah", "sure", "aight", "its urs", "its yours", "take it",
            "ill take that", "u got it", "you got it", "np",
        };

        private static readonly string[] Rejects =
        {
            "no", "nah", "nvm", "nevermind", "never mind", "no thanks",
            "no thx", "nty", "forget it", "not enough", "too low", "to low",
            "lowball", "pass", "keeping it",
        };

        // -----------------------------------------------------------------
        // The beats.
        // -----------------------------------------------------------------
        private void Begin()
        {
            Say(_opener, 0);

            _walk = new PathFollower(_bot, _player);
            _walkUntil = Core.Now + WalkTimeout;
            Timer.DelayCall(TimeSpan.FromSeconds(StepInterval), Step);
        }

        // Cross the floor. A bot that cannot get there gives up, and the
        // offer quietly dies rather than shouting numbers from a doorway.
        private void Step()
        {
            if (Finished())
            {
                return;
            }

            if (_bot.InRange(_player.Location, DealRange))
            {
                _bot.Direction = _bot.GetDirectionTo(_player);
                Timer.DelayCall(TimeSpan.FromSeconds(1.2), Arrive);
                return;
            }

            if (Core.Now > _walkUntil)
            {
                Say("haggle_walkaway", 0);
                End();
                return;
            }

            _walk.Follow(DealRange);
            Timer.DelayCall(TimeSpan.FromSeconds(StepInterval), Step);
        }

        // In front of you now, with a number.
        private void Arrive()
        {
            if (Finished())
            {
                return;
            }

            _arrived = true;

            // Opens below its ceiling, the way anyone opens. Never at it —
            // a buyer that leads with its best price isn't haggling.
            _standing = Math.Max(1, (int)(_ceiling * (Utility.RandomMinMax(55, 78) / 100.0)));
            Say("haggle_offer", _standing);
        }

        // The player named a number.
        private void ConsiderAsk(int ask)
        {
            if (Finished())
            {
                return;
            }

            if (ask <= _standing)
            {
                // They came in at or under what is already on the table.
                Shake(ask);
                return;
            }

            if (ask > _ceiling * 3)
            {
                Say("buy_toorich", _standing);
                End();
                return;
            }

            _rounds++;

            // Affordable, and it has haggled enough to feel like haggling.
            if (ask <= _ceiling && (_rounds >= 2 || Utility.RandomDouble() < 0.35))
            {
                Shake(ask);
                return;
            }

            if (_rounds >= 3)
            {
                // Out of patience: the real number, take it or leave it.
                _standing = _ceiling;
                Say("buy_final", _standing);
                return;
            }

            double give = _rounds == 1 ? 0.45 : 0.75;
            int target = Math.Min(ask, _ceiling);
            int next = _standing + (int)((target - _standing) * give);

            if (next <= _standing)
            {
                Say("buy_final", _standing);
                return;
            }

            _standing = next;
            Say("buy_counter", _standing);
        }

        // A number both sides have said out loud. From here the player just
        // has to hand the goods over.
        private void Shake(int price)
        {
            if (price <= 0 || price > _ceiling)
            {
                price = _ceiling;
            }

            // The purse is checked again at the window, but there is no
            // point shaking on money that already isn't there.
            if (CrafterStock.GoldOnHand(_bot) < price)
            {
                Say("buy_toorich", price);
                End();
                return;
            }

            _agreed = price;
            _standing = price;
            _agreedUntil = Core.Now + AgreementWindow;

            Say("haggle_take", price);
            Timer.DelayCall(TimeSpan.FromSeconds(2.2), () =>
            {
                if (!Finished() && _agreed > 0)
                {
                    Say("buy_paynow", _agreed);
                }
            });
        }

        // -----------------------------------------------------------------
        // What BotTradeWindow needs to know.
        // -----------------------------------------------------------------
        public static BotBuyOffer Find(PlayerBot bot, Mobile player)
        {
            foreach (var o in _active)
            {
                if (o._bot == bot && o._player == player && !o._done)
                {
                    return o;
                }
            }
            return null;
        }

        // The price this player shook on with this bot, or 0.
        public static int AgreedPriceFor(PlayerBot bot, Mobile player)
        {
            var o = Find(bot, player);
            return o != null && o._agreed > 0 && Core.Now < o._agreedUntil ? o._agreed : 0;
        }

        public static string NounFor(PlayerBot bot, Mobile player) => Find(bot, player)?._noun;

        // Is the thing on the table anything like the thing that was
        // agreed? A bot cannot see your pack, so it shakes on a description
        // and checks the goods when they arrive. Worth less than half the
        // number and the deal is off — which is what a player would say too.
        public static bool Balks(PlayerBot bot, Mobile player, Item item, int price)
        {
            int worth = BotAppraisal.Value(item);
            return worth <= 0 || worth * 2 < price;
        }

        // The window closed. Either way this negotiation is over.
        public static void Close(PlayerBot bot, Mobile player)
        {
            Find(bot, player)?.End();
        }

        public static bool IsBuying(PlayerBot bot) => bot != null && IsBusy(bot);

        private static bool IsBusy(PlayerBot bot)
        {
            foreach (var o in _active)
            {
                if (o._bot == bot)
                {
                    return true;
                }
            }
            return false;
        }

        // -----------------------------------------------------------------
        private void Say(string category, int price)
        {
            var line = ChatLibrary.PickRandom(category);
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            line = line
                .Replace("{item}", _noun, StringComparison.Ordinal)
                .Replace("{price}", BotShop.Coin(price), StringComparison.Ordinal);

            var d = _bot.GetDirectionTo(_player);
            if (_bot.Direction != d)
            {
                _bot.Direction = d;
            }

            // The same typing pause the rest of the responder uses.
            double delay = 0.9 + Utility.RandomDouble() * 1.1 +
                           Math.Min(line.Length * 0.04, 1.2);
            var bot = _bot;
            Timer.DelayCall(TimeSpan.FromSeconds(delay), () =>
            {
                if (bot is { Deleted: false, Alive: true, Hidden: false })
                {
                    bot.Say(line);
                }
            });

            Console.WriteLine($"[buy] {_bot.Name} -> {_player.Name}: {category} ({price}gp)");
        }

        // Any reason this can no longer happen. Checked at the top of every
        // beat, because each one runs on its own timer and the world moves
        // in between.
        private bool Finished()
        {
            if (_done)
            {
                return true;
            }

            if (_bot.Deleted || !_bot.Alive || _player.Deleted || !_player.Alive ||
                _bot.Map != _player.Map || _bot.Combatant != null || _bot.LoggingOut ||
                !_bot.InRange(_player.Location, ShoutRange) ||
                Core.Now - _lastAt > Idle)
            {
                End();
                return true;
            }

            return false;
        }

        private void End()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            _active.Remove(this);
            _cooldowns[_bot.Serial] = Core.Now + Cooldown;
        }

        // -----------------------------------------------------------------
        // Who is eligible, and who is closest.
        // -----------------------------------------------------------------
        private static bool IsShopping(PlayerBot bot)
        {
            if (bot.Deleted || !bot.Alive || bot.Hidden || bot.LoggingOut ||
                bot.Combatant != null)
            {
                return false;
            }

            // The bank's away crowd is away. Same rule the responder uses.
            if (bot.Behavior is BankSitterBehavior bs)
            {
                return bs.Role is not (BankSitterBehavior.BankRole.Afk
                                    or BankSitterBehavior.BankRole.ResistMacro
                                    or BankSitterBehavior.BankRole.HidingMacro
                                    or BankSitterBehavior.BankRole.StealthMacro);
            }

            return bot.Behavior is IdleBehavior or WanderBehavior or ShopperBehavior;
        }

        // Nearest bot that could actually take the deal wins the shout.
        // Comparing only against bots that pass every gate this one passed:
        // a statue standing closer shouldn't be able to intercept an offer
        // and ignore it, and neither should a broke fencer who has no
        // interest in the reagents you are shouting about.
        private static bool IsNearestBuyer(PlayerBot bot, Mobile player, GoodsKind kind,
            int low, int myDist)
        {
            if (player.Map == null)
            {
                return true;
            }

            foreach (var m in player.Map.GetMobilesInRange<PlayerBot>(player.Location, ShoutRange))
            {
                if (m == bot || BotShop.HasStock(m) || !IsShopping(m) || IsBusy(m) ||
                    BotShopDeal.IsDealing(m) || !Wants(m.Class, kind) ||
                    CrafterStock.GoldOnHand(m) < low)
                {
                    continue;
                }

                int dx = Math.Abs(m.X - player.X);
                int dy = Math.Abs(m.Y - player.Y);
                if ((dx > dy ? dx : dy) < myDist)
                {
                    return false;
                }
            }
            return true;
        }

        // -----------------------------------------------------------------
        // Parsing.
        // -----------------------------------------------------------------

        // The LAST number in the shout is the price: the era wrote the lot
        // first and the money last ("WTS 200 mandrake 900"). BotShopTalk's
        // parser takes the first, which is right when a player is bidding
        // on one item and wrong here.
        public static int LastGold(string text)
        {
            int last = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsDigit(text[i]) || (i > 0 && char.IsLetter(text[i - 1])))
                {
                    continue;
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
                        last = gold;
                    }
                }

                i = j;
            }

            return last;
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

        // See the note in BotShopTalk.MatchesAny: normalising inside the
        // helper is what stops this hole reopening at a new call site.
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

        // -----------------------------------------------------------------
        // [BotBuy — fake the shout from where you stand, so the walk and
        // the haggle can be watched without waiting for a bot to be in the
        // mood. [BotBuy with no argument reports what is running.
        // -----------------------------------------------------------------
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null)
            {
                return;
            }

            if (e.Arguments.Length == 0)
            {
                from.SendMessage($"{_active.Count} buy offer(s) running.");
                foreach (var o in _active)
                {
                    from.SendMessage(
                        $"  {o._bot.Name} <- {o._player.Name}: {o._noun}, standing {o._standing}, ceiling {o._ceiling}");
                    if (o._agreed > 0)
                    {
                        from.SendMessage($"    agreed at {o._agreed}gp");
                    }
                }
                return;
            }

            var shout = string.Join(' ', e.Arguments).ToLowerInvariant();
            if (!IsSellShout(shout, out var noun, out int low, out int high, out int asking))
            {
                from.SendMessage("Nothing in the stock table matches that. " +
                                 "Try: [BotBuy wts GM halberd 5k");
                return;
            }

            BotAppraisal.BandForNoun(shout, out _, out _, out _, out var kind);
            bool premium = BotAppraisal.ClaimsPremium(shout);

            from.SendMessage($"\"{shout}\" -> {noun}, band {low}-{high}gp, asking {asking}gp");
            from.SendMessage($"  kind {kind}, demand {DemandFor(kind, premium) * 100:F0}%");

            if (_noInterest.TryGetValue((from.Serial, noun), out var cold) && Core.Now < cold)
            {
                from.SendMessage("  the room already passed on these goods; it will reconsider " +
                                 $"in {(cold - Core.Now).TotalMinutes:F0} min.");
                return;
            }

            if (from.Map == null)
            {
                return;
            }

            foreach (var m in from.Map.GetMobilesInRange<PlayerBot>(from.Location, ShoutRange))
            {
                if (Notice(m, from, shout, Cheby(m, from)))
                {
                    from.SendMessage($"{m.Name} is coming over.");
                    return;
                }
            }

            from.SendMessage("Nobody nearby is buying right now.");
        }

        private static int Cheby(Mobile a, Mobile b)
        {
            int dx = Math.Abs(a.X - b.X);
            int dy = Math.Abs(a.Y - b.Y);
            return dx > dy ? dx : dy;
        }
    }
}
