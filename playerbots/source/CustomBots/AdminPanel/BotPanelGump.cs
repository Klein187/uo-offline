// =========================================================================
// BotPanelGump.cs — The [GmPanel admin gump.
//
// Layout philosophy: four clearly separated sections, no horizontal
// overflow, destructive actions confirm before firing.
//
//   ┌────────────────────────────────────────────────┐
//   │  GM Panel                                  [X] │
//   │  Location: ... (X,Y,Z)                         │
//   │  Nearby (20 tiles): N bot(s), M spawner(s)     │
//   ├────────────────────────────────────────────────┤
//   │  WORLD                                          │
//   │    [Decorate]  [SignGen]   [TelGen]            │
//   │    [MoonGen]   [TownCriers][Spawners]          │
//   │    [Bots]      [Save]      [★ Run All]         │
//   ├────────────────────────────────────────────────┤
//   │  SPAWN HERE                                     │
//   │    (draft table — see below)                   │
//   │    [+ Add Behavior]                            │
//   │    [Commit Spawner Here]  [Clear Draft]        │
//   ├────────────────────────────────────────────────┤
//   │  TRAVEL                                         │
//   │    Cities:   [Britain] [Vesper] [Trinsic] ...  │
//   │    Dungeons: [Despise] [Destard] ...           │
//   │    Inside:   [Despise] [Destard] ...           │
//   ├────────────────────────────────────────────────┤
//   │  CLEANUP                                        │
//   │    [Clear bots near]  [Clear ALL bots*]        │
//   │    [Remove ALL spawners*]                       │
//   │    [Remove (target one)]                        │
//   │    *destructive — confirms before acting       │
//   └────────────────────────────────────────────────┘
//
// Buttons encode an Action and (optionally) a row index. We pack as:
//   actionId * 1000 + rowIndex
// Keep action IDs <100 so they don't overflow.
// =========================================================================

using System;
using Server;
using Server.Gumps;
using Server.Mobiles;
using Server.Network;
using Server.Targeting;

namespace Server.CustomBots
{
    public sealed class BotPanelGump : Gump
    {
        // ---- Layout constants ----

        private const int PanelW        = 620;
        private const int PanelH        = 820;
        private const int PadX          = 14;
        private const int LineH         = 22;
        private const int SectionGap    = 10;
        private const int ButtonH       = 22;
        private const int ButtonW       = 110;

        // ModernUO gump art IDs.
        private const int BgArt         = 9270;     // beige scroll background
        private const int BtnNormal     = 4005;     // generic button up
        private const int BtnPressed    = 4007;     // generic button down
        private const int PlusUp        = 0x983;
        private const int PlusDown      = 0x984;
        private const int MinusUp       = 0x985;
        private const int MinusDown     = 0x986;
        private const int ExitUp        = 0xFB1;
        private const int ExitDown      = 0xFB3;

        private const int LabelHue      = 1153;

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

        // Behaviors available in the picker. Order here is the order they
        // appear in the grid. Keep these short so two-column layout fits.
        private static readonly string[] BehaviorChoices =
        {
            "BankSitter", "Wander", "Idle", "Adventurer", "Traveler"
        };

        // ---- Action IDs ----

        private enum Act
        {
            Close = 1,
            Refresh,

            // World setup
            FreshDecorate    = 10,
            FreshSignGen,
            FreshTelGen,
            FreshMoonGen,
            FreshTownCriers,
            FreshSpawners,
            FreshGenerateBots,
            FreshSaveWorld,
            FreshRunAll,

            // Draft
            DraftAddBehavior  = 30,
            DraftCancelAdd,
            DraftPickBehavior,
            DraftIncrement,
            DraftDecrement,
            DraftIncrement5,
            DraftDecrement5,
            DraftRemoveRow,
            DraftCommit,
            DraftClear,

            // Travel
            Travel        = 50,
            Dungeon       = 55,
            DungeonInside = 56,

            // Cleanup
            ClearBotsHere      = 60,
            ClearBotsAll,
            RemoveAllSpawners,
            RemoveSingleTarget,
        }

        private static readonly string[] CityKeys =
        {
            "Britain", "Vesper", "Trinsic", "Yew", "Minoc",
            "Magincia", "Jhelom", "Skara Brae", "Moonglow"
        };

        private static readonly string[] DungeonKeys =
        {
            "Despise", "Destard", "Covetous", "Deceit", "Hythloth",
            "Shame", "Wrong", "Ice", "Fire"
        };

        // ---- Per-instance state ----
        private readonly bool _pickerOpen;

        // ---- Constructor ----
        public BotPanelGump(Mobile from, bool pickerOpen = false) : base(50, 10)
        {
            _pickerOpen = pickerOpen;
            BuildLayout(from);
        }

        // Pack/unpack button IDs so multiple rows of the same Act can coexist.
        private static int ButtonID(Act action, int rowIndex = 0) =>
            (int)action * 1000 + rowIndex;

        private static (Act action, int rowIndex) DecodeButtonID(int id)
        {
            int a = id / 1000;
            int r = id % 1000;
            return ((Act)a, r);
        }

        // ---- Layout ----

        private void BuildLayout(Mobile from)
        {
            AddPage(0);
            AddBackground(0, 0, PanelW, PanelH, BgArt);

            int y = 14;

            // ── Title and close button ──────────────────────────────────
            AddHtml(PadX, y, PanelW - 80, 22,
                "<BASEFONT COLOR=#F4F4F4 SIZE=4><B>GM Panel</B></BASEFONT>");
            AddButton(PanelW - 36, y, ExitUp, ExitDown, ButtonID(Act.Close));
            y += LineH + 4;

            // ── Header: location + counts ───────────────────────────────
            var (botCount, spawnerCount, regionName) = BotPanelActions.CountNearby(from, 20);

            AddLabel(PadX, y, LabelHue,
                $"Location: {regionName} ({from.X},{from.Y},{from.Z})"); y += LineH;
            AddLabel(PadX, y, LabelHue,
                $"Nearby (20 tiles): {botCount} bot(s), {spawnerCount} spawner(s)");
            y += LineH + SectionGap;

            // ── Section 1: World ───────────────────────────────────────
            y = AddSectionHeader(y, "WORLD");
            y = BuildWorldSection(y);

            // ── Section 2: Spawn Here (draft) ──────────────────────────
            y = AddSectionHeader(y, "SPAWN HERE");
            y = BuildSpawnSection(from, y);

            // ── Section 3: Travel ──────────────────────────────────────
            y = AddSectionHeader(y, "TRAVEL");
            y = BuildTravelSection(y);

            // ── Section 4: Cleanup ─────────────────────────────────────
            y = AddSectionHeader(y, "CLEANUP");
            y = BuildCleanupSection(y);

            // ── Log line ───────────────────────────────────────────────
            var logQueue = BotPanelState.GetLog(from);
            if (logQueue != null && logQueue.Count > 0)
            {
                // Show the most recent line. Iterating a Queue gives oldest-first,
                // so we take the last entry via ToArray.
                var arr = logQueue.ToArray();
                string logMsg = arr[arr.Length - 1];
                y += SectionGap;
                AddHtml(PadX, y, PanelW - 28, 22,
                    $"<BASEFONT COLOR=#B4E1A8><I>{logMsg}</I></BASEFONT>");
            }
        }

        private int AddSectionHeader(int y, string title)
        {
            AddHtml(PadX, y, PanelW - 28, 22,
                $"<BASEFONT COLOR=#FFD080 SIZE=3><B>{title}</B></BASEFONT>");
            return y + LineH;
        }

        // -------------------------------------------------------------------
        // Section: World — 3x3 grid (8 setup commands + Run All)
        // -------------------------------------------------------------------
        private int BuildWorldSection(int y)
        {
            string[] labels = {
                "Decorate", "SignGen", "TelGen",
                "MoonGen", "TownCriers", "Spawners",
                "Bots", "Save World", "★ Run All",
            };
            Act[] acts = {
                Act.FreshDecorate, Act.FreshSignGen, Act.FreshTelGen,
                Act.FreshMoonGen, Act.FreshTownCriers, Act.FreshSpawners,
                Act.FreshGenerateBots, Act.FreshSaveWorld, Act.FreshRunAll,
            };

            // Three columns evenly across panel width.
            int colW = (PanelW - 2 * PadX) / 3;
            for (int i = 0; i < labels.Length; i++)
            {
                int col = i % 3;
                int row = i / 3;
                int bx  = PadX + col * colW;
                int by  = y   + row * (ButtonH + 4);

                AddButton(bx, by, BtnNormal, BtnPressed, ButtonID(acts[i]));
                AddLabel(bx + 30, by + 2, LabelHue, labels[i]);
            }
            return y + 3 * (ButtonH + 4) + SectionGap;
        }

        // -------------------------------------------------------------------
        // Section: Spawn Here — draft table + behavior picker
        // -------------------------------------------------------------------
        private int BuildSpawnSection(Mobile from, int y)
        {
            var draft = BotPanelState.GetDraft(from);
            if (draft.Count == 0)
            {
                AddLabel(PadX + 10, y, LabelHue,
                    "(no behaviors yet — click + Add Behavior below)");
                y += LineH;
            }
            else
            {
                // Header row
                AddLabel(PadX,       y, LabelHue, "Behavior");
                AddLabel(PadX + 200, y, LabelHue, "Count");
                AddLabel(PadX + 360, y, LabelHue, "Action");
                y += LineH;

                for (int i = 0; i < draft.Count; i++)
                {
                    var e = draft[i];

                    AddLabel(PadX,       y + 2, LabelHue, e.BehaviorName);

                    // -5 / -1 / count / +1 / +5
                    AddButton(PadX + 170, y, MinusUp, MinusDown,
                        ButtonID(Act.DraftDecrement5, i));
                    AddButton(PadX + 192, y, MinusUp, MinusDown,
                        ButtonID(Act.DraftDecrement, i));

                    AddLabel(PadX + 220, y + 2, LabelHue, e.Count.ToString().PadLeft(3));

                    AddButton(PadX + 256, y, PlusUp, PlusDown,
                        ButtonID(Act.DraftIncrement, i));
                    AddButton(PadX + 278, y, PlusUp, PlusDown,
                        ButtonID(Act.DraftIncrement5, i));

                    AddButton(PadX + 360, y, BtnNormal, BtnPressed,
                        ButtonID(Act.DraftRemoveRow, i));
                    AddLabel(PadX + 390, y + 2, LabelHue, "Remove");
                    y += LineH;
                }
            }

            // Picker — either "+ Add Behavior" button OR the picker grid
            if (!_pickerOpen)
            {
                AddButton(PadX, y, BtnNormal, BtnPressed, ButtonID(Act.DraftAddBehavior));
                AddLabel(PadX + 30, y + 2, LabelHue, "+ Add Behavior");
                y += LineH;
            }
            else
            {
                AddLabel(PadX, y + 2, LabelHue, "Pick a behavior:");
                y += LineH;

                // 2-column grid for behaviors — fits any reasonable count.
                int colW = (PanelW - 2 * PadX) / 2;
                for (int i = 0; i < BehaviorChoices.Length; i++)
                {
                    int col = i % 2;
                    int row = i / 2;
                    int bx  = PadX + col * colW;
                    int by  = y   + row * (ButtonH + 2);

                    AddButton(bx, by, BtnNormal, BtnPressed,
                        ButtonID(Act.DraftPickBehavior, i));
                    AddLabel(bx + 30, by + 2, LabelHue, BehaviorChoices[i]);
                }
                int rows = (BehaviorChoices.Length + 1) / 2;
                y += rows * (ButtonH + 2);

                AddButton(PadX, y, BtnNormal, BtnPressed, ButtonID(Act.DraftCancelAdd));
                AddLabel(PadX + 30, y + 2, LabelHue, "Cancel");
                y += LineH;
            }

            // Commit + Clear (placed side-by-side under the picker)
            y += 4;
            AddButton(PadX, y, BtnNormal, BtnPressed, ButtonID(Act.DraftCommit));
            AddLabel(PadX + 30, y + 2, LabelHue, "Commit Spawner Here");

            AddButton(PadX + 280, y, BtnNormal, BtnPressed, ButtonID(Act.DraftClear));
            AddLabel(PadX + 310, y + 2, LabelHue, "Clear Draft");
            return y + LineH + SectionGap;
        }

        // -------------------------------------------------------------------
        // Section: Travel — cities, dungeon entrances, dungeon interiors
        // -------------------------------------------------------------------
        private int BuildTravelSection(int y)
        {
            // Cities — 3 cols × 3 rows
            AddLabel(PadX, y + 2, LabelHue, "Cities:");
            y = LayOutKeyGrid(y, CityKeys, Act.Travel, 3, indent: 60);

            // Dungeon entrances
            AddLabel(PadX, y + 2, LabelHue, "Dungeons:");
            y = LayOutKeyGrid(y, DungeonKeys, Act.Dungeon, 3, indent: 60);

            // Dungeon interiors
            AddLabel(PadX, y + 2, LabelHue, "Inside:");
            y = LayOutKeyGrid(y, DungeonKeys, Act.DungeonInside, 3, indent: 60);

            return y + SectionGap;
        }

        // Lay out a row of keys (cities or dungeons) in a grid with the
        // given number of columns, starting at PadX+indent so the section
        // label can fit alongside the first row.
        private int LayOutKeyGrid(int y, string[] keys, Act act, int cols, int indent)
        {
            int startX = PadX + indent;
            int colW = (PanelW - PadX - startX) / cols;

            for (int i = 0; i < keys.Length; i++)
            {
                int col = i % cols;
                int row = i / cols;
                int bx  = startX + col * colW;
                int by  = y      + row * (ButtonH + 2);

                AddButton(bx, by, BtnNormal, BtnPressed, ButtonID(act, i));
                AddLabel(bx + 22, by + 2, LabelHue, keys[i]);
            }
            int rows = (keys.Length + cols - 1) / cols;
            return y + rows * (ButtonH + 2) + 4;
        }

        // -------------------------------------------------------------------
        // Section: Cleanup — buttons for clearing bots and spawners
        // -------------------------------------------------------------------
        private int BuildCleanupSection(int y)
        {
            // Row 1: Clear bots near | Clear ALL bots
            AddButton(PadX, y, BtnNormal, BtnPressed, ButtonID(Act.ClearBotsHere));
            AddLabel(PadX + 30, y + 2, LabelHue, "Clear bots near");

            AddButton(PadX + 220, y, BtnNormal, BtnPressed, ButtonID(Act.ClearBotsAll));
            AddLabel(PadX + 250, y + 2, LabelHue, "Clear ALL bots *");
            y += LineH;

            // Row 2: Remove ALL spawners
            AddButton(PadX, y, BtnNormal, BtnPressed, ButtonID(Act.RemoveAllSpawners));
            AddLabel(PadX + 30, y + 2, LabelHue, "Remove ALL spawners *");
            y += LineH;

            // Row 3: Remove single target
            AddButton(PadX, y, BtnNormal, BtnPressed, ButtonID(Act.RemoveSingleTarget));
            AddLabel(PadX + 30, y + 2, LabelHue, "Remove (target one)");
            y += LineH;

            // Footer note
            AddHtml(PadX, y, PanelW - 28, 22,
                "<BASEFONT COLOR=#B0B0B0 SIZE=2>* = will ask for confirmation</BASEFONT>");
            y += LineH;

            return y + SectionGap;
        }

        // ---- Response handling ----

        public override void OnResponse(NetState sender, in RelayInfo info)
        {
            var from = sender?.Mobile;
            if (from == null) return;

            var (action, row) = DecodeButtonID(info.ButtonID);

            bool reopen     = true;
            bool keepPicker = false;

            switch (action)
            {
                case Act.Close:
                    reopen = false;
                    break;

                case Act.Refresh:
                    break;

                // ----- World setup -----
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
                    BotPanelActions.RunCommand(from,
                        "GenerateSpawners Spawners/uoclassic/UOClassic.map");
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
                    BotPanelActions.RunCommand(from,
                        "GenerateSpawners Spawners/uoclassic/UOClassic.map");
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
                        d[row].Count = Math.Max(0, d[row].Count - 5);
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
                        BotPanelActions.GoToCity(from, CityKeys[row]);
                    break;
                case Act.Dungeon:
                    if (row >= 0 && row < DungeonKeys.Length)
                        BotPanelActions.GoToDungeon(from, DungeonKeys[row]);
                    break;
                case Act.DungeonInside:
                    if (row >= 0 && row < DungeonKeys.Length)
                        BotPanelActions.GoToDungeonInside(from, DungeonKeys[row]);
                    break;

                // ----- Cleanup -----
                case Act.ClearBotsHere:
                    BotPanelActions.ClearBotsHere(from);
                    break;

                case Act.ClearBotsAll:
                    // Destructive — confirm first.
                    from.SendGump(new GmPanelConfirmGump(
                        "This will delete every PlayerBot in the entire world. The spawners will eventually replace them. Continue?",
                        () => BotPanelActions.ClearBotsAll(from)));
                    return; // don't auto-reopen; confirm gump handles it

                case Act.RemoveAllSpawners:
                    // Destructive — confirm first.
                    from.SendGump(new GmPanelConfirmGump(
                        "This will delete every PlayerBotSpawner in the entire world. Bots already spawned will continue to exist (use 'Clear ALL bots' for those). Continue?",
                        () => BotPanelActions.RemoveAllSpawners(from)));
                    return; // don't auto-reopen

                case Act.RemoveSingleTarget:
                    from.SendMessage("Target the bot, spawner, item, or NPC to remove.");
                    from.Target = new RemoveTarget();
                    return; // don't auto-reopen; let the target prompt run
            }

            if (reopen)
                from.SendGump(new BotPanelGump(from, keepPicker));
        }

        // ---- Single-target remove target ----

        private class RemoveTarget : Target
        {
            public RemoveTarget() : base(12, false, TargetFlags.None) { }

            protected override void OnTarget(Mobile from, object targeted)
            {
                BotPanelActions.RemoveSingleObject(from, targeted);
                from.SendGump(new BotPanelGump(from));
            }

            protected override void OnTargetCancel(Mobile from, TargetCancelType cancelType)
            {
                from.SendGump(new BotPanelGump(from));
            }
        }
    }
}
