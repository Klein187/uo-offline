// =========================================================================
// BotTradeWindow.cs — the real 1999 trade window, with a bot on one side.
//
// A player buys from a hawker the way a player always bought from another
// player: drag gold onto them, the trade window opens, the seller puts the
// goods in, both tick the box. No gump, no vendor menu, no "buy" command.
//
// Making a bot a legal participant took one stock-file patch (NetState is
// dereferenced unconditionally in SecureTrade; a bot has none — see
// INTEGRATION-NOTES.txt) and this file, which supplies the half of the
// exchange a human would otherwise do by hand:
//
//   - the window opens on the drag, not on a command
//   - the bot drops the goods on its side immediately
//   - a watcher counts the gold on the player's side each tick and ticks
//     the bot's box the moment the agreed price is covered
//   - anything that should kill a trade (walking away, dying, logging
//     out, the goods leaving the pack) cancels it
//
// The price is whatever was agreed by talking (BotSpeechResponder), or
// the asking price if the player never haggled and just paid up.
//
// The same window runs the other direction. A player who shouts WTS at the
// bank and haggles a bot up (BotBuyOffer) hands the goods over the same
// way: drag the item onto the bot, and the window opens with the bot's
// gold already counted out on its side. The only extra step selling adds
// is the appraisal — the bot shook on a description, so it looks at what
// actually landed on the table before it pays for it.
// =========================================================================

using System;
using Server;
using Server.Items;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotTradeWindow
    {
        // Beyond this the trade closes itself — the same leash the client
        // puts on a player-to-player trade.
        private const int MaxTradeRange = 3;

        // How often the bot looks at what's on the table.
        private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(0.75);

        // A window nobody finishes shouldn't stand open forever.
        private static readonly TimeSpan WindowTimeout = TimeSpan.FromMinutes(3);

        // -----------------------------------------------------------------
        // Entry point: PlayerBot.OnDragDrop -> PlayerBot.OpenTrade -> here.
        // Returning false bounces the dropped item back to the player,
        // which is exactly what should happen when there's no deal.
        // -----------------------------------------------------------------
        public static bool TryOpen(PlayerBot bot, Mobile from, Item offer)
        {
            if (bot == null || from == null || from is PlayerBot || !from.Player)
            {
                return false;
            }

            if (bot.Deleted || !bot.Alive || !from.Alive || bot.Map != from.Map ||
                !from.InRange(bot.Location, MaxTradeRange) || from.NetState == null)
            {
                return false;
            }

            // Goods, not coin: the player is selling. A bot only takes
            // that from someone it has actually shaken hands with — this
            // is the payoff of a WTS shout it walked over for.
            if (offer is not Gold)
            {
                if (BotBuyOffer.AgreedPriceFor(bot, from) > 0)
                {
                    return TryOpenSale(bot, from, offer);
                }

                // Otherwise it goes straight back with a word, which is a
                // great deal more courtesy than most 1999 players managed.
                Decline(bot, ChatLibrary.PickRandom("trade_not_buying") ?? "not buying, sry");
                return false;
            }

            var stock = BotShop.StockOf(bot);
            if (stock == null)
            {
                Decline(bot, "nothing to sell sry");
                return false;
            }

            // The number: what this player talked the seller down to, or
            // the asking price if they never bothered to haggle.
            int price = BotShop.AgreedPriceFor(stock, from.Serial);
            if (price <= 0)
            {
                price = stock.Asking;
                BotShop.Agree(stock, from.Serial, price);
            }

            var item = World.FindItem(stock.ItemSerial);
            if (item == null || item.Deleted || bot.Backpack == null ||
                !item.IsChildOf(bot.Backpack))
            {
                BotShop.Clear(bot);
                Decline(bot, "sold already sry");
                return false;
            }

            // An existing window with this bot just takes the extra coin.
            var existing = from.NetState.FindTradeContainer(bot);
            if (existing != null)
            {
                existing.DropItem(offer);
                return true;
            }

            // From = the player (the one with a client), To = the bot.
            var trade = new SecureTrade(from, bot);
            from.NetState.Trades.Add(trade);

            trade.From.Container.DropItem(offer);

            // The goods go on the table right away — the seller isn't
            // going to make you ask twice once the coin is out.
            if (!trade.To.Container.TryDropItem(bot, item, sendFullMessage: false))
            {
                trade.Cancel();
                return false;
            }

            var opener = ChatLibrary.PickRandom("trade_open");
            BotScene.Deliver(bot, string.IsNullOrEmpty(opener)
                ? $"{BotShop.Coin(price)} for the {stock.Noun}"
                : opener
                    .Replace("{item}", stock.Noun, StringComparison.Ordinal)
                    .Replace("{price}", BotShop.Coin(price), StringComparison.Ordinal));

            Console.WriteLine(
                $"[shop] {from.Name} opened a trade with {bot.Name} for {stock.Noun} at {price}gp");

            Watch(trade, bot, from, stock.Noun, price, item.Serial, Core.Now + WindowTimeout);
            return true;
        }

        // -----------------------------------------------------------------
        // The player is the seller. The bot shook on a price for something
        // it had only heard described, so this is where it finally looks at
        // the goods — and where a bot that agreed 3k for a GM halberd
        // refuses to hand it over for a rusty dagger.
        // -----------------------------------------------------------------
        private static bool TryOpenSale(PlayerBot bot, Mobile from, Item goods)
        {
            int price = BotBuyOffer.AgreedPriceFor(bot, from);
            var noun = BotBuyOffer.NounFor(bot, from) ?? BotAppraisal.NameFor(goods);

            // More goods onto a table that is already set: no second
            // payment, the agreed number stands. Same as the buy side
            // taking extra coin without renegotiating.
            var existing = from.NetState.FindTradeContainer(bot);
            if (existing != null)
            {
                existing.DropItem(goods);
                return true;
            }

            if (BotBuyOffer.Balks(bot, from, goods, price))
            {
                var balk = ChatLibrary.PickRandom("buy_balk");
                Decline(bot, string.IsNullOrEmpty(balk)
                    ? $"thats not worth {BotShop.Coin(price)} m8"
                    : balk
                        .Replace("{item}", BotAppraisal.NameFor(goods), StringComparison.Ordinal)
                        .Replace("{price}", BotShop.Coin(price), StringComparison.Ordinal));

                Console.WriteLine(
                    $"[buy] {bot.Name} BALKED at {from.Name}'s {BotAppraisal.NameFor(goods)} " +
                    $"(agreed {price}gp, worth {BotAppraisal.Value(goods)}gp)");

                BotBuyOffer.Close(bot, from);
                return false;
            }

            // The purse is checked at the till, not at the handshake — the
            // bot may have spent it since. Draw on the account first: the
            // bot qualified to buy on what it OWNS, so this is where owning
            // has to become carrying. A cover that fails moves nothing and
            // the sale falls over honestly on the next line.
            BotBanking.CoverInPack(bot, price);

            if (!CrafterStock.SpendGold(bot, price))
            {
                Decline(bot, ChatLibrary.PickRandom("haggle_broke") ?? "im short sry");
                BotBuyOffer.Close(bot, from);
                return false;
            }

            var coin = new Gold(price);

            // From = the player (the one with a client), To = the bot.
            var trade = new SecureTrade(from, bot);
            from.NetState.Trades.Add(trade);

            trade.From.Container.DropItem(goods);

            if (!trade.To.Container.TryDropItem(bot, coin, sendFullMessage: false))
            {
                trade.Cancel();
                bot.AddToBackpack(coin);
                return false;
            }

            var opener = ChatLibrary.PickRandom("buy_open", "trade_open");
            BotScene.Deliver(bot, string.IsNullOrEmpty(opener)
                ? $"{BotShop.Coin(price)} for the {noun}"
                : opener
                    .Replace("{item}", noun, StringComparison.Ordinal)
                    .Replace("{price}", BotShop.Coin(price), StringComparison.Ordinal));

            Console.WriteLine(
                $"[buy] {from.Name} opened a sale to {bot.Name}: {noun} at {price}gp");

            WatchSale(trade, bot, from, noun, price, goods.Serial, Core.Now + WindowTimeout);
            return true;
        }

        // The bot's half, once per tick: the goods have to be on the
        // player's side before it ticks its box. Polling for the same
        // reason the buy side polls — every add or remove runs ClearChecks,
        // and there is no event in that path we own.
        private static void WatchSale(SecureTrade trade, PlayerBot bot, Mobile seller,
            string noun, int price, Serial goodsSerial, DateTime expiresAt)
        {
            Timer.DelayCall(WatchInterval, () =>
            {
                if (trade == null)
                {
                    return;
                }

                if (!trade.Valid)
                {
                    SettleSale(bot, seller, noun, price, goodsSerial);
                    return;
                }

                bool botIsFrom = trade.From.Mobile == bot;
                var botSide = botIsFrom ? trade.From : trade.To;
                var sellerSide = botIsFrom ? trade.To : trade.From;

                if (bot.Deleted || !bot.Alive || seller.Deleted || !seller.Alive ||
                    bot.Map != seller.Map || !seller.InRange(bot.Location, MaxTradeRange) ||
                    Core.Now > expiresAt)
                {
                    var off = ChatLibrary.PickRandom("trade_cancel");
                    if (!string.IsNullOrEmpty(off) && bot.Alive && !bot.Deleted)
                    {
                        BotScene.Deliver(bot, off);
                    }

                    // Cancel hands every side's items back to its owner, so
                    // the bot's coin returns to its pack on its own.
                    trade.Cancel();
                    return;
                }

                bool goodsOnTable = false;
                foreach (var i in sellerSide.Container.Items)
                {
                    if (i.Serial == goodsSerial)
                    {
                        goodsOnTable = true;
                        break;
                    }
                }

                bool happy = goodsOnTable && CountGold(botSide) >= price;

                if (botSide.Accepted != happy)
                {
                    botSide.Accepted = happy;
                    trade.Update();

                    if (!trade.Valid)
                    {
                        SettleSale(bot, seller, noun, price, goodsSerial);
                        return;
                    }
                }

                WatchSale(trade, bot, seller, noun, price, goodsSerial, expiresAt);
            });
        }

        // Did the goods actually change hands? The engine has already moved
        // everything, so the honest test is where the item ended up.
        private static void SettleSale(PlayerBot bot, Mobile seller, string noun, int price,
            Serial goodsSerial)
        {
            var item = World.FindItem(goodsSerial);
            bool bought = item != null && !item.Deleted && item.RootParent == bot;

            BotBuyOffer.Close(bot, seller);

            if (!bought)
            {
                Console.WriteLine(
                    $"[buy] {bot.Name}'s purchase of {noun} from {seller.Name} fell through");
                return;
            }

            var line = ChatLibrary.PickRandom("trade_close");
            if (!string.IsNullOrEmpty(line) && bot.Alive && !bot.Deleted)
            {
                BotScene.Deliver(bot, line);
            }

            BotEventJournal.Record("sale", seller.Name, bot.Name, bot.Location, bot.Map);
            Console.WriteLine($"[buy] {bot.Name} BOUGHT {noun} from {seller.Name} for {price}gp");
        }

        private static void Decline(PlayerBot bot, string line)
        {
            if (!string.IsNullOrEmpty(line))
            {
                BotScene.Deliver(bot, line);
            }
        }

        // -----------------------------------------------------------------
        // The bot's half of the negotiation, once per tick: count what is
        // on the buyer's side and tick the box when it covers the price.
        //
        // Polling rather than hooking: every add or remove runs
        // SecureTradeContainer.ClearChecks, which resets BOTH boxes and
        // calls Update. There is no event we own in that path, and a
        // three-quarter-second look at a container is nothing.
        // -----------------------------------------------------------------
        private static void Watch(SecureTrade trade, PlayerBot bot, Mobile buyer,
            string noun, int price, Serial goodsSerial, DateTime expiresAt,
            int lastCoin = -1)
        {
            Timer.DelayCall(WatchInterval, () =>
            {
                if (trade == null)
                {
                    return;
                }

                // Closed already — either it went through or somebody
                // cancelled. Settle up and stop looking.
                if (!trade.Valid)
                {
                    Settle(bot, buyer, noun, price, goodsSerial);
                    return;
                }

                bool botIsFrom = trade.From.Mobile == bot;
                var botSide = botIsFrom ? trade.From : trade.To;
                var buyerSide = botIsFrom ? trade.To : trade.From;

                // Reasons to walk away from the table.
                if (bot.Deleted || !bot.Alive || buyer.Deleted || !buyer.Alive ||
                    bot.Map != buyer.Map || !buyer.InRange(bot.Location, MaxTradeRange) ||
                    Core.Now > expiresAt)
                {
                    var off = ChatLibrary.PickRandom("trade_cancel");
                    if (!string.IsNullOrEmpty(off) && bot.Alive && !bot.Deleted)
                    {
                        BotScene.Deliver(bot, off);
                    }
                    trade.Cancel();
                    return;
                }

                bool goodsOnTable = false;
                foreach (var i in botSide.Container.Items)
                {
                    if (i.Serial == goodsSerial)
                    {
                        goodsOnTable = true;
                        break;
                    }
                }

                int coin = CountGold(buyerSide);
                bool happy = goodsOnTable && coin >= price;

                if (botSide.Accepted != happy)
                {
                    botSide.Accepted = happy;
                    trade.Update();

                    // Update can complete the whole trade, at which point
                    // the containers are gone; don't touch them again.
                    if (!trade.Valid)
                    {
                        Settle(bot, buyer, noun, price, goodsSerial);
                        return;
                    }
                }

                // Coin is down but it doesn't cover the price. Say so, and
                // say the number. A seller that just leaves its box unticked
                // is indistinguishable from a broken one: the buyer ticks
                // theirs, nothing happens, and there is nothing on screen to
                // explain why. Only on a CHANGE, so it answers the buyer
                // rather than nagging four times a second.
                if (!happy && goodsOnTable && coin > 0 && coin != lastCoin)
                {
                    var short_ = ChatLibrary.PickRandom("trade_short");
                    if (!string.IsNullOrEmpty(short_))
                    {
                        BotScene.Deliver(bot, short_
                            .Replace("{short}", BotShop.Coin(price - coin),
                                StringComparison.Ordinal)
                            .Replace("{price}", BotShop.Coin(price),
                                StringComparison.Ordinal));
                    }
                }

                Watch(trade, bot, buyer, noun, price, goodsSerial, expiresAt, coin);
            });
        }

        // Coin on one side of the table. Real Gold items for T2A; the
        // virtual check covers the account-gold ruleset if it's ever on.
        private static int CountGold(SecureTradeInfo side)
        {
            int total = side.Gold;
            foreach (var i in side.Container.Items)
            {
                if (i is Gold g)
                {
                    total += g.Amount;
                }
            }
            return total;
        }

        // -----------------------------------------------------------------
        // After the window closes: did the goods actually change hands?
        // The engine has already moved everything, so the honest test is
        // where the item ended up.
        // -----------------------------------------------------------------
        private static void Settle(PlayerBot bot, Mobile buyer, string noun, int price,
            Serial goodsSerial)
        {
            var item = World.FindItem(goodsSerial);
            bool sold = item != null && !item.Deleted && item.RootParent == buyer;

            if (!sold)
            {
                // Back in the pack and still for sale. The stock entry
                // survives untouched, so it keeps advertising.
                Console.WriteLine(
                    $"[shop] {bot.Name}'s sale of {noun} to {buyer.Name} " +
                    $"fell through (wanted {price}gp)");
                return;
            }

            BotShop.Clear(bot);

            var line = ChatLibrary.PickRandom("trade_close");
            if (!string.IsNullOrEmpty(line) && bot.Alive && !bot.Deleted)
            {
                BotScene.Deliver(bot, line);
            }

            BotEventJournal.Record("sale", bot.Name, buyer.Name, bot.Location, bot.Map);
            Console.WriteLine($"[shop] {bot.Name} SOLD {noun} to {buyer.Name} for {price}gp");

            // The seller restocks after a breather rather than instantly
            // shouting about a new halberd it conjured mid-sentence.
            Timer.DelayCall(TimeSpan.FromSeconds(Utility.RandomMinMax(45, 120)), () =>
            {
                if (bot is { Deleted: false, Alive: true } &&
                    bot.Behavior is BankSitterBehavior { Role: BankSitterBehavior.BankRole.Hawker })
                {
                    BotShop.Stock(bot);
                }
            });
        }
    }
}
