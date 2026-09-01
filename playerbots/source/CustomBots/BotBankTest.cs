// =========================================================================
// BotBankTest.cs — does "withdraw 5000" move 5000 gold?
//
// Drives a real bank sitter standing at a real banker through the whole
// counter sequence and reads the coins after every step: bank the
// surplus, go short, ask for more than the account holds, get trimmed to
// what is there, and have it land in the pack. Then the two ways it is
// supposed to say no — an empty account, and no banker in earshot.
//
// Everything the rig touches is put back: the bot leaves with the purse
// it had and an empty account, so running this doesn't quietly make
// somebody at the Britain bank rich.
//
// The last phase proves the WIRING rather than the arithmetic. Bots only
// speak when a real player is within earshot (PlayerBotBehavior.
// IsPlayerNearby), so headless nothing is ever said and the hook in the
// speech funnel never runs. So it stands a throwaway player at the
// counter, primes one bank sitter to be short of walking money with a
// balance behind it, and leaves them to it. Watch the console for
// "[bank] ... withdrew" from a bot the rig never called into directly.
//
//   [TestBank [linger]   — run it, results to the caller + console.
//   bank_request.txt     — headless: "token [linger]" -> bank_ack.json.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Commands;
using Server.Items;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotBankTest
    {
        private const int BankerRange = 12;

        public static void Configure()
        {
            CommandSystem.Register("TestBank", AccessLevel.GameMaster, OnCommand);
        }

        // Long enough for the bank crowd's 15-45s chat cooldown to come
        // round a few times.
        public const int DefaultLinger = 120;

        private static void OnCommand(CommandEventArgs e)
        {
            var linger = e.Length > 0 ? Math.Clamp(e.GetInt32(0), 0, 600) : DefaultLinger;
            foreach (var line in Run(linger))
            {
                e.Mobile.SendMessage(line.StartsWith("FAIL") ? 0x22 : 0x3F, line);
            }
        }

        public static List<string> Run(int lingerSeconds = DefaultLinger)
        {
            var findings = new List<string>();

            var bot = FindBotAtACounter(out var banker);
            if (bot == null)
            {
                findings.Add("FAIL no bot standing within earshot of a banker");
                return Report(findings);
            }

            var pack = bot.Backpack;
            var startPack = PackGold(bot);
            var startBank = Banker.GetBalance(bot);

            findings.Add(
                $"OK   {bot.Name} at {banker.Name} in {bot.Region?.Name ?? "?"}: " +
                $"pack {startPack}, account {startBank}");

            try
            {
                // ---- 1. The surplus goes in the box -------------------------
                SetPackGold(bot, 3000);
                BotBanking.Settle(bot);

                var afterPack = PackGold(bot);
                var afterBank = Banker.GetBalance(bot);
                var banked = afterBank - startBank;

                findings.Add(
                    $"{(afterPack == 500 && banked == 2500 ? "OK  " : "FAIL")} banked the surplus: " +
                    $"pack 3000 -> {afterPack}, account +{banked} (want pack 500, +2500)");

                // ---- 2. Broke enough to want it back ------------------------
                SetPackGold(bot, 100);

                var said = BotBanking.Prepare(bot, "withdraw 5000");
                findings.Add(
                    $"{(said == "withdraw 2500" ? "OK  " : "FAIL")} asked for 5000 with {afterBank} " +
                    $"in the account, said \"{said}\" (want \"withdraw 2500\")");

                // ---- 3. Saying it moves the coins --------------------------
                BotBanking.Spoke(bot, said);

                var paidPack = PackGold(bot);
                var paidBank = Banker.GetBalance(bot);

                findings.Add(
                    $"{(paidPack == 2600 && paidBank == startBank ? "OK  " : "FAIL")} withdrew it: " +
                    $"pack 100 -> {paidPack}, account {afterBank} -> {paidBank} " +
                    $"(want pack 2600, account {startBank})");

                // ---- 4. An empty account admits it -------------------------
                EmptyAccount(bot);
                SetPackGold(bot, 50);

                var broke = BotBanking.Prepare(bot, "withdraw 1000");
                findings.Add(
                    $"{(broke == "balance" ? "OK  " : "FAIL")} nothing in the account, said " +
                    $"\"{broke}\" (want \"balance\")");

                // ---- 5. Carrying enough already ----------------------------
                // 900 in the pack: 400 of it is surplus and goes in the box,
                // and the 500 left is walking money — no reason to draw.
                SetPackGold(bot, 900);
                var flush = BotBanking.Prepare(bot, "withdraw 1000");
                findings.Add(
                    $"{(flush == "balance" ? "OK  " : "FAIL")} carrying 900 already, said " +
                    $"\"{flush}\" (want \"balance\" — nothing to come for)");

                // ---- 6. No counter, no transaction -------------------------
                var loner = FindBotAwayFromCounters();
                if (loner == null)
                {
                    findings.Add("SKIP no bot standing away from every banker");
                }
                else
                {
                    var lonerPack = PackGold(loner);
                    var untouched = BotBanking.Prepare(loner, "withdraw 1000");
                    BotBanking.Spoke(loner, "withdraw 1000");

                    findings.Add(
                        $"{(untouched == "withdraw 1000" && PackGold(loner) == lonerPack ? "OK  " : "FAIL")} " +
                        $"{loner.Name} away from any banker: said \"{untouched}\", " +
                        $"pack {lonerPack} -> {PackGold(loner)} (want the line untouched, no coins)");
                }
            }
            finally
            {
                // Put the bot back exactly as it was found.
                EmptyAccount(bot);
                SetPackGold(bot, startPack);

                if (startBank > 0)
                {
                    Banker.Deposit(bot, startBank, false);
                }

                findings.Add(
                    $"OK   restored {bot.Name}: pack {PackGold(bot)}, account {Banker.GetBalance(bot)} " +
                    $"(was pack {startPack}, account {startBank})");
            }

            if (lingerSeconds > 0)
            {
                findings.Add(Listen(banker, bot, lingerSeconds));
            }

            return Report(findings);
        }

        // Stand somebody at the counter so the crowd talks, and give one
        // of them a reason to draw. Everything is handed back when the
        // timer runs out.
        private static string Listen(Banker banker, PlayerBot skip, int lingerSeconds)
        {
            var subject = FindSitterAt(banker, skip);
            if (subject == null)
            {
                return "SKIP no bank sitter at this counter to prime";
            }

            var listener = new PlayerMobile
            {
                Name = "Bank Test",
                Body = 0x190,
                Hue = 0x83EA,
                Player = true
            };

            listener.MoveToWorld(banker.Location, banker.Map);

            // Bank 2500 of its own accord, then leave it short of walking
            // money — which is the whole reason anyone withdrew anything.
            var wasCarrying = PackGold(subject);
            SetPackGold(subject, 3000);
            BotBanking.Settle(subject);
            SetPackGold(subject, 100);

            Timer.DelayCall(
                TimeSpan.FromSeconds(lingerSeconds),
                () =>
                {
                    listener.Delete();
                    EmptyAccount(subject);
                    SetPackGold(subject, wasCarrying);
                    Console.WriteLine(
                        $"[TestBank] listener gone, {subject.Name} put back to {wasCarrying} gold");
                });

            return
                $"WATCH {subject.Name} primed (100 in pack, {Banker.GetBalance(subject)} banked) with a " +
                $"player listening for {lingerSeconds}s — expect \"[bank] {subject.Name} withdrew\"";
        }

        private static PlayerBot FindSitterAt(Banker banker, PlayerBot skip)
        {
            foreach (var n in banker.Map.GetMobilesInRange<PlayerBot>(banker.Location, BankerRange))
            {
                if (n != skip && !n.Deleted && n.Alive && n.Backpack != null &&
                    n.Behavior is BankSitterBehavior)
                {
                    return n;
                }
            }

            return null;
        }

        private static List<string> Report(List<string> findings)
        {
            foreach (var line in findings)
            {
                Console.WriteLine($"[TestBank] {line}");
            }

            return findings;
        }

        private static PlayerBot FindBotAtACounter(out Banker banker)
        {
            banker = null;

            foreach (var m in World.Mobiles.Values)
            {
                if (m is not Banker b || b.Deleted || !b.Alive ||
                    b.Map == null || b.Map == Map.Internal)
                {
                    continue;
                }

                foreach (var n in b.Map.GetMobilesInRange<PlayerBot>(b.Location, BankerRange))
                {
                    if (!n.Deleted && n.Alive && n.Backpack != null)
                    {
                        banker = b;
                        return n;
                    }
                }
            }

            return null;
        }

        private static PlayerBot FindBotAwayFromCounters()
        {
            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot bot || bot.Deleted || !bot.Alive ||
                    bot.Backpack == null || bot.Map == null || bot.Map == Map.Internal)
                {
                    continue;
                }

                var near = false;

                foreach (var b in bot.Map.GetMobilesInRange<Banker>(bot.Location, BankerRange))
                {
                    if (!b.Deleted)
                    {
                        near = true;
                        break;
                    }
                }

                if (!near)
                {
                    return bot;
                }
            }

            return null;
        }

        private static int PackGold(Mobile m) => m.Backpack?.GetAmount(typeof(Gold)) ?? 0;

        private static void SetPackGold(Mobile m, int amount)
        {
            var pack = m.Backpack;
            if (pack == null)
            {
                return;
            }

            var have = PackGold(m);
            if (have > 0)
            {
                pack.ConsumeTotal(typeof(Gold), have);
            }

            if (amount > 0)
            {
                pack.DropItem(new Gold(amount));
            }
        }

        private static void EmptyAccount(Mobile m)
        {
            var balance = Banker.GetBalance(m);
            if (balance > 0)
            {
                Banker.Withdraw(m, balance);
            }
        }
    }
}
