// =========================================================================
// BotBanking.cs — "withdraw 5000" moves 5000 gold.
//
// The bank crowd has always said the real commands out loud, because that
// is what a player standing at a bank does. They just never meant
// anything: bank_actions is a text file, the line was spoken, and not a
// coin moved. A bot with forty gold to its name would announce a five
// thousand gold withdrawal to a room full of people.
//
// The stock Banker cannot help here. Its OnSpeech works off e.Keywords,
// and keywords are parsed out of the CLIENT's speech packet — a
// Mobile.Say from server code carries none, so a banker standing three
// tiles away hears nothing it can act on. (BotPKWatch hit the same wall
// yelling for guards.) So the bot does its own banking, under the
// banker's rules: a real banker within range, no business with criminals,
// and the era's withdrawal ceiling.
//
// Where the money comes from matters. Nothing here invents any. Bots
// already spawn with a purse scaled to their skill tier and earn more
// hauling ore, selling crafts and looting corpses; a bot at a bank now
// does what a player did with all that — dumps the surplus over walking
// money into the box, and draws it back out when it runs low. A Novice
// with forty gold banks nothing and says "balance", which is the honest
// line for someone with nothing in the account.
// =========================================================================

using System;
using Server;
using Server.Items;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotBanking
    {
        public static bool Enabled = true;

        // ---- Knobs ----

        // The banker's own hearing range (Banker.HandlesOnSpeech).
        private const int BankerRange = 12;

        // Walking money. Above this the surplus goes in the box; below it
        // the bot has an actual reason to be at the counter.
        private const int PocketMoney = 500;

        // Below this it isn't worth crossing the room for.
        private const int MinTransaction = 100;

        // "Thou canst not withdraw so much at one time!" — the ceiling is
        // era-dependent, so read it the way the Banker does.
        private static int MaxWithdrawal => Core.ML ? 60000 : 5000;

        // -------------------------------------------------------------------
        // Prepare — called from the speech funnel BEFORE the words leave the
        // bot's mouth, so nobody announces an amount they haven't got.
        // Returns the line to actually say.
        // -------------------------------------------------------------------
        public static string Prepare(PlayerBot bot, string line)
        {
            if (!Enabled || bot == null || string.IsNullOrEmpty(line))
            {
                return line;
            }

            if (!IsBankLine(line, out var asked))
            {
                return line;
            }

            // No counter, no transaction. Away from a bank these are just
            // words, which is all they ever were.
            if (FindBanker(bot) == null)
            {
                return line;
            }

            // At the counter, first thing a player does is dump the loot.
            Settle(bot);

            if (asked <= 0)
            {
                return line; // "bank" or "balance" — nothing more to decide
            }

            // Already carrying walking money: no reason to draw more. Ask
            // what's in the account instead, which is the other thing
            // everyone said at a bank.
            if (PackGold(bot) >= PocketMoney)
            {
                return "balance";
            }

            var available = Math.Min(Banker.GetBalance(bot), MaxWithdrawal);
            if (available < MinTransaction)
            {
                return "balance";
            }

            // Round numbers, because that is how people typed it.
            var amount = Math.Min(asked, available) / 100 * 100;

            return amount < MinTransaction ? "balance" : $"withdraw {amount}";
        }

        // -------------------------------------------------------------------
        // Spoke — called straight after the line is said. Does it.
        // -------------------------------------------------------------------
        public static void Spoke(PlayerBot bot, string line)
        {
            if (!Enabled || bot == null || string.IsNullOrEmpty(line))
            {
                return;
            }

            if (!IsBankLine(line, out var amount) || amount < MinTransaction ||
                amount > MaxWithdrawal)
            {
                return;
            }

            if (FindBanker(bot) == null)
            {
                return;
            }

            // "I will not do business with a criminal!"
            if (bot.Criminal)
            {
                return;
            }

            var pack = bot.Backpack;
            if (pack?.Deleted != false)
            {
                return;
            }

            if (!Banker.Withdraw(bot, amount))
            {
                return; // "Thou hast not so much gold!"
            }

            pack.DropItem(new Gold(amount));

            Console.WriteLine(
                $"[bank] {bot.Name} withdrew {amount} gold " +
                $"(pack {PackGold(bot)}, account {Banker.GetBalance(bot)})");
        }

        // -------------------------------------------------------------------
        // What this bot can actually spend: pocket money plus the account.
        //
        // Every "can it afford this" test on the shard used to read pack
        // gold alone, and Settle banks everything above PocketMoney — so a
        // bot with 40,000 in the bank read as broke. That is why WTB shouts
        // were never backed and why bot-to-bot deals almost never fired:
        // the floor on a plain longsword is more walking money than any bot
        // carries. A player standing at a bank counts their bank balance
        // when deciding what they can buy, and so should these.
        // -------------------------------------------------------------------
        public static int Wealth(PlayerBot bot)
        {
            if (bot == null || bot.Deleted)
            {
                return 0;
            }

            try
            {
                return PackGold(bot) + Banker.GetBalance(bot);
            }
            catch
            {
                return PackGold(bot);
            }
        }

        // Get `amount` into the pack, pulling the shortfall out of the
        // account. False means it could not be covered and NOTHING moved,
        // so a caller can still back out of the deal cleanly.
        //
        // Era note: this is a bank withdrawal, and the bots doing it are
        // standing at a bank — the WTB crowd is BankSitterBehavior by
        // construction. It is not a licence to conjure coin in a dungeon.
        public static bool CoverInPack(PlayerBot bot, int amount)
        {
            var pack = bot?.Backpack;
            if (pack?.Deleted != false || amount <= 0)
            {
                return false;
            }

            int have = PackGold(bot);
            if (have >= amount)
            {
                return true;
            }

            int need = amount - have;

            if (!Banker.Withdraw(bot, need))
            {
                return false;
            }

            pack.DropItem(new Gold(need));

            Console.WriteLine(
                $"[bank] {bot.Name} drew {need} to cover {amount} " +
                $"(pack {PackGold(bot)}, account {Banker.GetBalance(bot)})");
            return true;
        }

        // -------------------------------------------------------------------
        // Settle — bank everything over walking money. This is the only
        // place a bot's account grows, so every coin in it was carried in.
        // -------------------------------------------------------------------
        public static bool Settle(PlayerBot bot)
        {
            var pack = bot?.Backpack;
            if (pack?.Deleted != false)
            {
                return false;
            }

            var surplus = PackGold(bot) - PocketMoney;
            if (surplus < MinTransaction)
            {
                return false;
            }

            if (!pack.ConsumeTotal(typeof(Gold), surplus))
            {
                return false;
            }

            if (!Banker.Deposit(bot, surplus, false))
            {
                pack.DropItem(new Gold(surplus)); // box wouldn't take it — keep it
                return false;
            }

            Console.WriteLine(
                $"[bank] {bot.Name} banked {surplus} gold " +
                $"(pack {PackGold(bot)}, account {Banker.GetBalance(bot)})");

            return true;
        }

        // -------------------------------------------------------------------
        // "withdraw 5000" -> 5000. "bank" / "balance" -> 0. Anything else
        // isn't a bank line at all. Case-insensitive because the speech
        // funnel capitalizes some lines on the way out.
        // -------------------------------------------------------------------
        private static bool IsBankLine(string line, out int amount)
        {
            amount = 0;

            var text = line.Trim();

            if (text.InsensitiveEquals("bank") || text.InsensitiveEquals("balance"))
            {
                return true;
            }

            if (!text.InsensitiveStartsWith("withdraw"))
            {
                return false;
            }

            var rest = text[8..].Trim();

            return int.TryParse(rest, out amount) && amount > 0;
        }

        private static int PackGold(Mobile m) =>
            m.Backpack?.GetAmount(typeof(Gold)) ?? 0;

        private static Banker FindBanker(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal)
            {
                return null;
            }

            foreach (var m in bot.Map.GetMobilesInRange<Banker>(bot.Location, BankerRange))
            {
                if (m.Alive && !m.Deleted)
                {
                    return m;
                }
            }

            return null;
        }
    }
}
