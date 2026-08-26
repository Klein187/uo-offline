// =========================================================================
// BotShopDeal.cs — one bot walks over and buys from another, for real.
//
// The old WTS "deal" was four lines of dialogue with nothing behind it:
// two bots stood where they were, said "ill take it" and "sold!", and no
// item or coin moved. This runs the actual transaction.
//
//   1. A stocked hawker and a nearby bot with money in its pack.
//   2. The buyer WALKS over — that's the part you see from across the
//      bank, and it's what made the old version read as a puppet show.
//   3. They haggle out loud, over the real asking price and the seller's
//      real floor (BotShop.Consider does the arithmetic). The buyer's
//      nerve depends on what it can actually afford.
//   4. Agreement moves the item into the buyer's pack and the gold into
//      the seller's. Either side short and the deal falls through, which
//      is a perfectly good outcome and reads as one.
//
// A deal is a small state machine on a timer rather than a Behavior: the
// buyer keeps whatever it was doing and just steps aside for a minute,
// so this can't strand a bot in a shopping trance if a beat is missed.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Items;

namespace Server.CustomBots
{
    public sealed class BotShopDeal
    {
        // How far a buyer will cross a bank floor for a deal.
        public const int MaxNoticeRange = 14;

        // Close enough to talk money.
        private const int DealRange = 2;

        private const double StepInterval = 0.45;
        private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(25);

        private static readonly List<BotShopDeal> _active = new();
        public static int ActiveCount => _active.Count;

        private readonly PlayerBot _buyer;
        private readonly PlayerBot _seller;
        private readonly ShopStock _stock;
        private readonly int _purse;
        private PathFollower _walk;
        private DateTime _walkUntil;
        private int _offer;
        private bool _done;

        private BotShopDeal(PlayerBot buyer, PlayerBot seller, ShopStock stock, int purse)
        {
            _buyer = buyer;
            _seller = seller;
            _stock = stock;
            _purse = purse;
        }

        // -----------------------------------------------------------------
        // Matchmaking. Returns true when a deal actually started.
        // -----------------------------------------------------------------
        public static bool TryStart(PlayerBot seller, Func<PlayerBot, bool> ready)
        {
            var stock = BotShop.StockOf(seller);
            if (stock == null || seller.Map == null || IsBusy(seller))
            {
                return false;
            }

            foreach (var m in seller.Map.GetMobilesInRange(seller.Location, MaxNoticeRange))
            {
                if (m is not PlayerBot buyer || buyer == seller || IsBusy(buyer) ||
                    !ready(buyer))
                {
                    continue;
                }

                // Only bots with a reason to be standing around shopping.
                if (buyer.Behavior is not (BankSitterBehavior or IdleBehavior
                                        or WanderBehavior or ShopperBehavior))
                {
                    continue;
                }

                // Money talks. A buyer that cannot reach the seller's floor
                // has no business opening its mouth — and this is why the
                // keeps and towers sit unsold at the bank all day, which is
                // exactly how it was.
                int purse = CrafterStock.GoldOnHand(buyer);
                if (purse < stock.Floor)
                {
                    continue;
                }

                var deal = new BotShopDeal(buyer, seller, stock, purse);
                _active.Add(deal);
                deal.Begin();
                return true;
            }

            return false;
        }

        // Is this bot mid-deal? Other systems ask so they don't fight the
        // walk or start a second negotiation with the same pair.
        public static bool IsDealing(PlayerBot bot) => bot != null && IsBusy(bot);

        private static bool IsBusy(PlayerBot bot)
        {
            foreach (var d in _active)
            {
                if (d._buyer == bot || d._seller == bot)
                {
                    return true;
                }
            }
            return false;
        }

        // -----------------------------------------------------------------
        private void Begin()
        {
            var opener = ChatLibrary.PickRandom("haggle_ask");
            BotScene.Deliver(_buyer, string.IsNullOrEmpty(opener)
                ? $"how much for the {_stock.Noun}"
                : opener.Replace("{item}", _stock.Noun, StringComparison.Ordinal));

            // No Mover: PathFollower falls back to Mobile.Move, which is
            // what every other walking behaviour in the tree relies on.
            _walk = new PathFollower(_buyer, _seller);
            _walkUntil = Core.Now + WalkTimeout;
            Timer.DelayCall(TimeSpan.FromSeconds(StepInterval), Step);
        }

        // Cross the floor. A bot that can't get there (wedged, shoved,
        // blocked by the crowd) gives up and the deal quietly dies.
        private void Step()
        {
            if (Finished())
            {
                return;
            }

            if (_buyer.InRange(_seller.Location, DealRange))
            {
                _buyer.Direction = _buyer.GetDirectionTo(_seller);
                Timer.DelayCall(TimeSpan.FromSeconds(1.2), Open);
                return;
            }

            if (Core.Now > _walkUntil)
            {
                var off = ChatLibrary.PickRandom("haggle_walkaway");
                if (!string.IsNullOrEmpty(off))
                {
                    BotScene.Deliver(_buyer, off);
                }
                End();
                return;
            }

            _walk.Follow(false, DealRange);
            Timer.DelayCall(TimeSpan.FromSeconds(StepInterval), Step);
        }

        // The seller names its price.
        private void Open()
        {
            if (Finished())
            {
                return;
            }

            Say(_seller, "haggle_price", _stock.Asking);

            // What the buyer opens with: a shade under what it can afford,
            // never over the asking price. A bot with deep pockets doesn't
            // bother lowballing much.
            double nerve = _purse >= _stock.Asking
                ? Utility.RandomMinMax(80, 100) / 100.0
                : Utility.RandomMinMax(62, 88) / 100.0;
            _offer = Math.Min(_stock.Asking, Math.Max(1, (int)(Math.Min(_purse, _stock.Asking) * nerve)));

            Timer.DelayCall(TimeSpan.FromSeconds(2.6), () => Bargain(1));
        }

        // One round of back-and-forth, up to three.
        private void Bargain(int round)
        {
            if (Finished())
            {
                return;
            }

            // A buyer that opens AT the asking price isn't haggling, it's
            // buying — say so rather than pantomiming a negotiation.
            if (_offer >= _stock.Asking)
            {
                BotShop.Agree(_stock, _buyer.Serial, _stock.Asking);
                Say(_buyer, "haggle_take", _stock.Asking);
                Timer.DelayCall(TimeSpan.FromSeconds(2.0), () => Settle(_stock.Asking));
                return;
            }

            Say(_buyer, "haggle_offer", _offer);

            var result = BotShop.Consider(_stock, _buyer.Serial, _offer, out int counter);

            Timer.DelayCall(TimeSpan.FromSeconds(2.4), () =>
            {
                if (Finished())
                {
                    return;
                }

                switch (result)
                {
                    case BotShop.HaggleResult.Accepted:
                    {
                        Say(_seller, "haggle_deal", counter);
                        Timer.DelayCall(TimeSpan.FromSeconds(1.8), () => Settle(counter));
                        return;
                    }

                    case BotShop.HaggleResult.Insulted:
                    {
                        Say(_seller, "haggle_insult", _stock.Asking);
                        End();
                        return;
                    }

                    case BotShop.HaggleResult.Refused:
                    {
                        Say(_seller, "haggle_final", counter);

                        // Last chance: meet the number or walk.
                        if (_purse >= counter && Utility.RandomDouble() < 0.55)
                        {
                            BotShop.Agree(_stock, _buyer.Serial, counter);
                            Timer.DelayCall(TimeSpan.FromSeconds(1.8), () =>
                            {
                                if (!Finished())
                                {
                                    Say(_buyer, "haggle_take", counter);
                                    Timer.DelayCall(TimeSpan.FromSeconds(1.6), () => Settle(counter));
                                }
                            });
                            return;
                        }

                        Timer.DelayCall(TimeSpan.FromSeconds(1.6), () =>
                        {
                            if (!Finished())
                            {
                                Say(_buyer, "haggle_walkaway", 0);
                                End();
                            }
                        });
                        return;
                    }

                    default: // Countered
                    {
                        var temper = _stock.Temper == HaggleTemper.Firm && Utility.RandomBool()
                            ? "haggle_firm"
                            : "haggle_counter";
                        Say(_seller, temper, counter);

                        if (round >= 3)
                        {
                            // Out of patience. Take it or leave it.
                            if (_purse >= counter && Utility.RandomDouble() < 0.6)
                            {
                                BotShop.Agree(_stock, _buyer.Serial, counter);
                                Timer.DelayCall(TimeSpan.FromSeconds(1.8), () =>
                                {
                                    if (!Finished())
                                    {
                                        Say(_buyer, "haggle_take", counter);
                                        Timer.DelayCall(TimeSpan.FromSeconds(1.6), () => Settle(counter));
                                    }
                                });
                            }
                            else
                            {
                                Timer.DelayCall(TimeSpan.FromSeconds(1.6), () =>
                                {
                                    if (!Finished())
                                    {
                                        Say(_buyer, "haggle_walkaway", 0);
                                        End();
                                    }
                                });
                            }
                            return;
                        }

                        // Split the difference and go again, if it can.
                        int next = Math.Min(_purse, (_offer + counter) / 2);
                        if (next <= _offer)
                        {
                            Timer.DelayCall(TimeSpan.FromSeconds(1.6), () =>
                            {
                                if (!Finished())
                                {
                                    Say(_buyer, "haggle_walkaway", 0);
                                    End();
                                }
                            });
                            return;
                        }

                        _offer = next;
                        Timer.DelayCall(TimeSpan.FromSeconds(2.2), () => Bargain(round + 1));
                        return;
                    }
                }
            });
        }

        // -----------------------------------------------------------------
        // Money and goods change hands. This is the whole point.
        // -----------------------------------------------------------------
        private void Settle(int price)
        {
            if (Finished())
            {
                return;
            }

            var item = BotShop.TakeStockItem(_seller);
            if (item == null || item.Deleted)
            {
                End();
                return;
            }

            // Re-check the purse at the till, not at the handshake — the
            // buyer may have spent it on the walk over.
            if (!CrafterStock.SpendGold(_buyer, price))
            {
                Say(_buyer, "haggle_broke", price);
                End();
                return;
            }

            if (!_buyer.AddToBackpack(item))
            {
                // Pack full: give the money back and let it go.
                _buyer.AddToBackpack(new Gold(price));
                End();
                return;
            }

            _seller.AddToBackpack(new Gold(price));
            _seller.PlaySound(0x2E6); // coin clink
            BotScene.Deliver(_seller, ChatLibrary.PickRandom("trade_close") ?? "sold!");

            BotEventJournal.Record("sale", _seller.Name, _buyer.Name,
                _seller.Location, _seller.Map);

            Console.WriteLine(
                $"[shop] {_buyer.Name} bought {_stock.Noun} from {_seller.Name} " +
                $"for {price}gp (asked {_stock.Asking}, floor {_stock.Floor}) " +
                $"at ({_seller.X},{_seller.Y})");

            // The seller picks up something new to hawk after a breather.
            var seller = _seller;
            Timer.DelayCall(TimeSpan.FromSeconds(Utility.RandomMinMax(60, 180)), () =>
            {
                if (seller is { Deleted: false, Alive: true } &&
                    seller.Behavior is BankSitterBehavior { Role: BankSitterBehavior.BankRole.Hawker })
                {
                    BotShop.Stock(seller);
                }
            });

            End();
        }

        // -----------------------------------------------------------------
        private void Say(PlayerBot who, string category, int price)
        {
            var line = ChatLibrary.PickRandom(category);
            if (string.IsNullOrEmpty(line))
            {
                return;
            }
            BotScene.Deliver(who, line
                .Replace("{item}", _stock.Noun, StringComparison.Ordinal)
                .Replace("{price}", BotShop.Coin(price), StringComparison.Ordinal));
        }

        // Any reason the deal can no longer happen. Checked at the top of
        // every beat, because each one runs on its own timer and the world
        // moves in between.
        private bool Finished()
        {
            if (_done)
            {
                return true;
            }

            if (_buyer.Deleted || !_buyer.Alive || _seller.Deleted || !_seller.Alive ||
                _buyer.Map != _seller.Map || _buyer.Combatant != null ||
                _seller.Combatant != null || _buyer.LoggingOut || _seller.LoggingOut ||
                !_buyer.InRange(_seller.Location, MaxNoticeRange))
            {
                End();
                return true;
            }

            return false;
        }

        private void End()
        {
            _done = true;
            _active.Remove(this);
        }
    }
}
