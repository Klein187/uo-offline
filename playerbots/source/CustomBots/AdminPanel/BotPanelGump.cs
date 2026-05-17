// =========================================================================
// BotPanelGump.cs — Main admin panel gump.
//
// Sections (top to bottom):
//   1. Header             - location, region, nearby counts
//   2. Fresh World Setup  - 8 numbered buttons + "Run All"
//   3. Place Spawner      - draft rows with +/-, behavior picker, commit
//   4. Travel             - 9 city buttons
//   5. Cleanup            - clear bots near / all
//   6. World              - save / export / regenerate
//   7. Test               - one test bot per behavior
//   8. Log                - last few action messages
//
// Button IDs are namespaced via Action enum + index encoding so multiple
// rows of the same kind don't collide. See ButtonID() / DecodeButtonID().
//
// After most clicks the gump re-sends itself to refresh state. This is
// the standard UO gump pattern — gumps are stateless on the client side.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Gumps;
using Server.Network;

namespace Server.CustomBots
{
    public class BotPanelGump : Gump
    {
        // ---- Layout constants ----

        private const int PanelX        = 50;
        private const int PanelY        = 10;
        private const int PanelW        = 620;
        private const int PanelH        = 820;
        private const int PadX          = 14;
        private const int ColStart      = PadX;
        private const int LineH         = 22;
        private const int SectionGap    = 8;
        private const int ButtonH       = 22;
        private const int ButtonW       = 110;
        private const int SmallButtonW  = 30;

        // ModernUO gump art IDs.
        private const int BgArt         = 9270;     // beige scroll background
        private const int BtnNormal     = 4005;     // generic button up
        private const int BtnPressed    = 4007;     // generic button down
        private const int PlusUp        = 0x983;    // green plus, up
        private const int PlusDown      = 0x984;
        private const int MinusUp       = 0x985;    // red minus, up
        private const int MinusDown     = 0x986;
        private const int ExitUp        = 0xFB1;    // red X
        private const int ExitDown      = 0xFB3;

        // Hues for text. 1153 is a light off-white that reads cleanly on
        // the dark gump background (same approach as ModernUO's spawner
        // gump, which uses light-on-dark via BASEFONT HTML).
        private const int LabelHue      = 1153;

        // Sensible starting counts per behavior so the user doesn't have to
        // click + many times. BankSitters are usually deployed in larger
        // populations than wanderers/idle bots.
        private static int DefaultCountFor(string behaviorName) =>
            behaviorName switch
            {
                "BankSitter" => 8,
                "Wander"     => 3,
                "Idle"       => 3,
                "Adventurer" => 4,
                "Traveler"   => 2,
                _            => 1,
            };

        // ---- Behaviors available in the picker ----
        // Hardcoded for now; could pull from BehaviorRegistry.KnownNames.
        private static readonly string[] BehaviorChoices =
        {
            "BankSitter", "Wander", "Idle", "Adventurer", "Traveler"
        };

        // ---- Action IDs ----
        //
        // Buttons encode an Action and (optionally) a row index.
        // We pack as: actionId * 1000 + rowIndex.
        //
        // Keep IDs <100 so they don't overflow when multiplied. Plenty of
        // room for future actions.

        private enum Act
        {
            Close = 1,
            Refresh,

            // Fresh World Setup (1-8 + Run All)
            FreshDecorate = 10,
            FreshSignGen,
            FreshTelGen,
            FreshMoonGen,
            FreshTownCriers,
            FreshSpawners,
            FreshGenerateBots,
            FreshSaveWorld,
            FreshRunAll,

            // Draft management
            DraftAddBehavior = 30,   // expands the inline picker
            DraftCancelAdd,          // hides the inline picker
            DraftPickBehavior,       // row index = which behavior in BehaviorChoices
            DraftIncrement,          // row index = draft row to +1
            DraftDecrement,          // row index = draft row to -1
            DraftIncrement5,         // row index = draft row to +5
            DraftDecrement5,         // row index = draft row to -5
            DraftRemoveRow,          // row index = draft row to delete
            DraftCommit,
            DraftClear,

            // Travel — row index = position in CityKeys
            Travel = 50,

            // Dungeon — row index = position in DungeonKeys
            Dungeon = 55,

            // DungeonInside — same indices as Dungeon; goes inside the dungeon
            DungeonInside = 56,

            // Cleanup
            ClearBotsHere = 60,
            ClearBotsAll,

            // World
            SaveWorld = 70,
            ExportJson,
            Regenerate,

            // Test — row index = position in BehaviorChoices
            SpawnTest = 80,
        }

        // City order for the Travel section. Matches BotPanelActions.CityCoords.
        private static readonly string[] CityKeys =
        {
            "Britain", "Vesper", "Trinsic", "Yew", "Minoc",
            "Magincia", "Jhelom", "Skara Brae", "Moonglow"
        };

        // Dungeon order for the Dungeons section.
        private static readonly string[] DungeonKeys =
        {
            "Despise", "Destard", "Covetous", "Deceit", "Hythloth",
            "Shame", "Wrong", "Ice", "Fire"
        };

        // ---- Per-instance state ----
        // Whether the inline behavior picker is open (between two refreshes).
        private readonly bool _pickerOpen;

        // ---- Constructor ----

        public BotPanelGump(Mobile from, bool pickerOpen = false) : base(PanelX, PanelY)
        {
            _pickerOpen = pickerOpen;
            BuildLayout(from);
        }

        // ---- Button ID encoding ----
        private static int ButtonID(Act action, int rowIndex = 0) =>
            (int)action * 1000 + rowIndex;

        private static (Act action, int rowIndex) DecodeButtonID(int id)
        {
            int rowIndex = id % 1000;
            Act action   = (Act)(id / 1000);
            return (action, rowIndex);
        }

        // ---- Layout ----

        private void BuildLayout(Mobile from)
        {
            AddPage(0);
            AddBackground(0, 0, PanelW, PanelH, BgArt);

            int y = 14;

            // ── Title and close button ──────────────────────────────────
            AddHtml(PadX, y, PanelW - 80, 22,
                "<BASEFONT COLOR=#F4F4F4 SIZE=4><B>PlayerBot Admin Panel</B></BASEFONT>");
            AddButton(PanelW - 36, y, ExitUp, ExitDown, ButtonID(Act.Close));
            y += LineH + 4;

            // ── Header: location + counts ───────────────────────────────
            var (botCount, spawnerCount, regionName) = BotPanelActions.CountNearby(from, 20);

            string headerLine1 = $"Location: {regionName} ({from.X},{from.Y},{from.Z})";
            string headerLine2 = $"Nearby (20 tiles): {botCount} bot(s), {spawnerCount} spawner(s)";

            AddLabel(PadX, y, LabelHue, headerLine1); y += LineH;
            AddLabel(PadX, y, LabelHue, headerLine2); y += LineH + SectionGap;

            // ── Section: Fresh World Setup ──────────────────────────────
            y = AddSectionHeader(y, "FRESH WORLD SETUP");

            // 3x3 grid: 8 commands + Run All
            string[] setupLabels = {
                "1. Decorate", "2. SignGen", "3. TelGen",
                "4. MoonGen", "5. TownCriers", "6. Spawners",
                "7. GenerateBots", "8. Save World", "★ Run All"
            };
            Act[] setupActs = {
                Act.FreshDecorate, Act.FreshSignGen, Act.FreshTelGen,
                Act.FreshMoonGen, Act.FreshTownCriers, Act.FreshSpawners,
                Act.FreshGenerateBots, Act.FreshSaveWorld, Act.FreshRunAll
            };

            for (int i = 0; i < setupLabels.Length; i++)
            {
                int col = i % 3;
                int row = i / 3;
                int bx  = PadX + col * (ButtonW + 50);
                int by  = y + row * (ButtonH + 4);

                AddButton(bx, by, BtnNormal, BtnPressed, ButtonID(setupActs[i]));
                AddLabel(bx + 30, by + 2, LabelHue, setupLabels[i]);
            }
            y += 3 * (ButtonH + 4) + SectionGap;

            // ── Section: Place Spawner (draft) ──────────────────────────
            y = AddSectionHeader(y, "PLACE SPAWNER HERE");

            var draft = BotPanelState.GetDraft(from);
            if (draft.Count == 0)
            {
                AddLabel(PadX + 10, y, LabelHue,
                    "(Draft is empty. Click + Add Behavior below to begin.)");
                y += LineH;
            }
            else
            {
                AddLabel(PadX,       y, LabelHue, "Behavior");
                AddLabel(PadX + 200, y, LabelHue, "Count");
                AddLabel(PadX + 360, y, LabelHue, "Action");
                y += LineH;

                for (int i = 0; i < draft.Count; i++)
                {
                    var e = draft[i];
                    AddLabel(PadX,       y + 2, LabelHue, e.BehaviorName);

                    // Compact count row: [-5] [-1]  count  [+1] [+5]
                    // No labels on the buttons; pattern is "two together =
                    // bigger step." Users learn it in a couple of clicks.
                    AddButton(PadX + 170, y, MinusUp, MinusDown,
                        ButtonID(Act.DraftDecrement5, i));
                    AddButton(PadX + 192, y, MinusUp, MinusDown,
                        ButtonID(Act.DraftDecrement, i));

                    AddLabel(PadX + 220, y + 2, LabelHue, e.Count.ToString().PadLeft(3));

                    AddButton(PadX + 256, y, PlusUp, PlusDown,
                        ButtonID(Act.DraftIncrement, i));
                    AddButton(PadX + 278, y, PlusUp, PlusDown,
                        ButtonID(Act.DraftIncrement5, i));

                    // [Remove]
                    AddButton(PadX + 360, y, BtnNormal, BtnPressed,
                        ButtonID(Act.DraftRemoveRow, i));
                    AddLabel(PadX + 390, y + 2, LabelHue, "Remove");

                    y += LineH;
                }
            }

            // Add behavior picker — collapsed by default
            if (!_pickerOpen)
            {
                AddButton(PadX, y, BtnNormal, BtnPressed, ButtonID(Act.DraftAddBehavior));
                AddLabel(PadX + 30, y + 2, LabelHue, "+ Add Behavior");
                y += LineH;
            }
            else
            {
                AddLabel(PadX, y + 2, LabelHue, "Pick a behavior:");
                int px = PadX + 130;
                for (int i = 0; i < BehaviorChoices.Length; i++)
                {
                    AddButton(px, y, BtnNormal, BtnPressed,
                        ButtonID(Act.DraftPickBehavior, i));
                    AddLabel(px + 30, y + 2, LabelHue, BehaviorChoices[i]);
                    px += ButtonW + 30;
                }
                // Cancel goes on the next line so it never collides with the
                // behavior list as we add more choices over time.
                y += LineH;
                AddButton(PadX, y, BtnNormal, BtnPressed, ButtonID(Act.DraftCancelAdd));
                AddLabel(PadX + 30, y + 2, LabelHue, "Cancel");
                y += LineH;
            }

            // Commit + Clear Draft
            AddButton(PadX, y, BtnNormal, BtnPressed, ButtonID(Act.DraftCommit));
            AddLabel(PadX + 30, y + 2, LabelHue, "Commit Spawner Here");

            AddButton(PadX + 280, y, BtnNormal, BtnPressed, ButtonID(Act.DraftClear));
            AddLabel(PadX + 310, y + 2, LabelHue, "Clear Draft");
            y += LineH + SectionGap;

            // ── Section: Travel ─────────────────────────────────────────
            y = AddSectionHeader(y, "TRAVEL");

            for (int i = 0; i < CityKeys.Length; i++)
            {
                int col = i % 5;
                int row = i / 5;
                int bx  = PadX + col * (ButtonW + 8);
                int by  = y + row * (ButtonH + 4);

                AddButton(bx, by, BtnNormal, BtnPressed, ButtonID(Act.Travel, i));
                AddLabel(bx + 30, by + 2, LabelHue, CityKeys[i]);
            }
            y += 2 * (ButtonH + 4) + SectionGap;

            // ── Section: Dungeons ───────────────────────────────────────
            y = AddSectionHeader(y, "DUNGEONS");

            for (int i = 0; i < DungeonKeys.Length; i++)
            {
                int col = i % 5;
                int row = i / 5;
                int bx  = PadX + col * (ButtonW + 8);
                int by  = y + row * (ButtonH + 4);

                AddButton(bx, by, BtnNormal, BtnPressed, ButtonID(Act.Dungeon, i));
                AddLabel(bx + 30, by + 2, LabelHue, DungeonKeys[i]);
            }
            y += 2 * (ButtonH + 4) + SectionGap;

            // ── Section: Dungeons (Inside) ─────────────────────────────
            // Compact: single row, shorter labels, smaller buttons.
            // Drops you directly inside the dungeon (past the entrance
            // teleporter) so you can place spawners in there.
            y = AddSectionHeader(y, "DUNGEONS (INSIDE)");

            int dlx = PadX;
            for (int i = 0; i < DungeonKeys.Length; i++)
            {
                AddButton(dlx, y, BtnNormal, BtnPressed, ButtonID(Act.DungeonInside, i));
                AddLabel(dlx + 22, y + 2, LabelHue, DungeonKeys[i].Substring(0, Math.Min(4, DungeonKeys[i].Length)));
                dlx += 60;
            }
            y += LineH + SectionGap;

            // ── Section: Cleanup ────────────────────────────────────────
            y = AddSectionHeader(y, "CLEANUP");

            AddButton(PadX, y, BtnNormal, BtnPressed, ButtonID(Act.ClearBotsHere));
            AddLabel(PadX + 30, y + 2, LabelHue, "Clear bots near");

            AddButton(PadX + 220, y, BtnNormal, BtnPressed, ButtonID(Act.ClearBotsAll));
            AddLabel(PadX + 250, y + 2, LabelHue, "Clear ALL bots");
            y += LineH + SectionGap;

            // ── Section: World ──────────────────────────────────────────
            y = AddSectionHeader(y, "WORLD");

            AddButton(PadX, y, BtnNormal, BtnPressed, ButtonID(Act.SaveWorld));
            AddLabel(PadX + 30, y + 2, LabelHue, "Save World");

            AddButton(PadX + 180, y, BtnNormal, BtnPressed, ButtonID(Act.ExportJson));
            AddLabel(PadX + 210, y + 2, LabelHue, "Export JSON");

            AddButton(PadX + 380, y, BtnNormal, BtnPressed, ButtonID(Act.Regenerate));
            AddLabel(PadX + 410, y + 2, LabelHue, "Regenerate from JSON");
            y += LineH + SectionGap;

            // ── Section: Test (compact) ─────────────────────────────────
            y = AddSectionHeader(y, "TEST");

            for (int i = 0; i < BehaviorChoices.Length; i++)
            {
                int bx = PadX + i * (ButtonW + 40);
                AddButton(bx, y, BtnNormal, BtnPressed, ButtonID(Act.SpawnTest, i));
                AddLabel(bx + 30, y + 2, LabelHue, $"Spawn {BehaviorChoices[i]}");
            }
            y += LineH + SectionGap;

            // ── Section: Log (newest at bottom) ─────────────────────────
            y = AddSectionHeader(y, "RECENT ACTIONS");
            var log = BotPanelState.GetLog(from);
            if (log.Count == 0)
            {
                AddLabel(PadX + 10, y, LabelHue, "(No actions yet.)");
            }
            else
            {
                foreach (var line in log)
                {
                    AddLabel(PadX + 10, y, LabelHue, Truncate(line, 90));
                    y += 16;
                }
            }
        }

        private int AddSectionHeader(int y, string title)
        {
            // FFD700 (gold) on the dark gump = the warm-yellow look UO has
            // always used for "category header" type text. Pops without
            // being garish.
            AddHtml(PadX, y, PanelW - PadX * 2, 20,
                $"<BASEFONT COLOR=#FFD700 SIZE=2><B>── {title} ──</B></BASEFONT>");
            return y + 22;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max - 1) + "…";
        }

        // ---- Response handler ----

        public override void OnResponse(NetState sender, in RelayInfo info)
        {
            var from = sender.Mobile;
            if (from == null) return;

            var (action, row) = DecodeButtonID(info.ButtonID);

            bool reopen      = true;
            bool keepPicker  = false;

            switch (action)
            {
                case Act.Close:
                    reopen = false;
                    break;

                case Act.Refresh:
                    break;

                // ----- Fresh world setup -----
                case Act.FreshDecorate:
                    BotPanelActions.RunCommand(from, "Decorate"); break;
                case Act.FreshSignGen:
                    BotPanelActions.RunCommand(from, "SignGen"); break;
                case Act.FreshTelGen:
                    BotPanelActions.RunCommand(from, "TelGen"); break;
                case Act.FreshMoonGen:
                    BotPanelActions.RunCommand(from, "MoonGen"); break;
                case Act.FreshTownCriers:
                    BotPanelActions.RunCommand(from, "TownCriers"); break;
                case Act.FreshSpawners:
                    BotPanelActions.RunCommand(from, "GenerateSpawners Spawners/uoclassic/UOClassic.map");
                    break;
                case Act.FreshGenerateBots:
                    BotPanelActions.RunCommand(from, "GenerateBots"); break;
                case Act.FreshSaveWorld:
                    BotPanelActions.SaveWorld(from); break;
                case Act.FreshRunAll:
                    BotPanelActions.RunCommand(from, "Decorate");
                    BotPanelActions.RunCommand(from, "SignGen");
                    BotPanelActions.RunCommand(from, "TelGen");
                    BotPanelActions.RunCommand(from, "MoonGen");
                    BotPanelActions.RunCommand(from, "TownCriers");
                    BotPanelActions.RunCommand(from, "GenerateSpawners Spawners/uoclassic/UOClassic.map");
                    BotPanelActions.RunCommand(from, "GenerateBots");
                    BotPanelActions.SaveWorld(from);
                    BotPanelState.Log(from, "Run All complete.");
                    break;

                // ----- Draft -----
                case Act.DraftAddBehavior:
                    keepPicker = true; break;

                case Act.DraftCancelAdd:
                    keepPicker = false; break;

                case Act.DraftPickBehavior:
                    if (row >= 0 && row < BehaviorChoices.Length)
                    {
                        var name = BehaviorChoices[row];
                        BotPanelState.AddDraftEntry(from, name, DefaultCountFor(name));
                    }
                    keepPicker = false;
                    break;

                case Act.DraftIncrement:
                {
                    var d = BotPanelState.GetDraft(from);
                    if (row >= 0 && row < d.Count) d[row].Count++;
                    break;
                }
                case Act.DraftDecrement:
                {
                    var d = BotPanelState.GetDraft(from);
                    if (row >= 0 && row < d.Count && d[row].Count > 0) d[row].Count--;
                    break;
                }
                case Act.DraftIncrement5:
                {
                    var d = BotPanelState.GetDraft(from);
                    if (row >= 0 && row < d.Count) d[row].Count += 5;
                    break;
                }
                case Act.DraftDecrement5:
                {
                    var d = BotPanelState.GetDraft(from);
                    if (row >= 0 && row < d.Count)
                    {
                        d[row].Count = Math.Max(0, d[row].Count - 5);
                    }
                    break;
                }
                case Act.DraftRemoveRow:
                    BotPanelState.RemoveDraftEntry(from, row);
                    break;

                case Act.DraftCommit:
                    BotPanelActions.CommitDraft(from);
                    break;

                case Act.DraftClear:
                    BotPanelState.ClearDraft(from);
                    BotPanelState.Log(from, "Draft cleared.");
                    break;

                // ----- Travel -----
                case Act.Travel:
                    if (row >= 0 && row < CityKeys.Length)
                    {
                        BotPanelActions.GoToCity(from, CityKeys[row]);
                    }
                    break;

                case Act.Dungeon:
                    if (row >= 0 && row < DungeonKeys.Length)
                    {
                        BotPanelActions.GoToDungeon(from, DungeonKeys[row]);
                    }
                    break;

                case Act.DungeonInside:
                    if (row >= 0 && row < DungeonKeys.Length)
                    {
                        BotPanelActions.GoToDungeonInside(from, DungeonKeys[row]);
                    }
                    break;

                // ----- Cleanup -----
                case Act.ClearBotsHere:
                    BotPanelActions.ClearBotsHere(from); break;
                case Act.ClearBotsAll:
                    BotPanelActions.ClearBotsAll(from); break;

                // ----- World -----
                case Act.SaveWorld:
                    BotPanelActions.SaveWorld(from); break;
                case Act.ExportJson:
                    BotPanelActions.RunCommand(from, "ExportBotSpawners"); break;
                case Act.Regenerate:
                    BotPanelActions.RunCommand(from, "GenerateBots"); break;

                // ----- Test -----
                case Act.SpawnTest:
                    if (row >= 0 && row < BehaviorChoices.Length)
                    {
                        BotPanelActions.SpawnTestBot(from, BehaviorChoices[row]);
                    }
                    break;
            }

            if (reopen)
            {
                from.SendGump(new BotPanelGump(from, keepPicker));
            }
        }
    }
}
