// =========================================================================
// GmPanelConfirmGump.cs — Small confirmation prompt for destructive
// GM panel actions like "remove all spawners" or "clear all bots".
//
// Caller passes a message and a callback. We render two buttons (Confirm
// and Cancel). On Confirm we invoke the callback then re-open the main
// panel. On Cancel we just re-open the panel.
// =========================================================================

using System;
using Server;
using Server.Gumps;
using Server.Network;

namespace Server.CustomBots
{
    public sealed class GmPanelConfirmGump : Gump
    {
        private readonly string _message;
        private readonly Action _onConfirm;

        // Button IDs
        private const int B_Cancel  = 1;
        private const int B_Confirm = 2;

        public GmPanelConfirmGump(string message, Action onConfirm)
            : base(120, 80)
        {
            _message   = message;
            _onConfirm = onConfirm;

            Closable   = true;
            Disposable = true;
            Draggable  = true;
            Resizable  = false;

            AddPage(0);
            AddBackground(0, 0, 360, 160, 9270);

            // Title bar
            AddHtml(14, 14, 332, 22,
                "<BASEFONT COLOR=#F4F4F4 SIZE=4><B>Are you sure?</B></BASEFONT>");

            // Message body — wrap to width
            AddHtml(14, 44, 332, 60,
                $"<BASEFONT COLOR=#E0E0E0>{_message}</BASEFONT>");

            // Buttons
            AddButton(14, 116, 4005, 4007, B_Cancel);
            AddLabel(46, 118, 1153, "Cancel");

            AddButton(220, 116, 4005, 4007, B_Confirm);
            AddLabel(252, 118, 1153, "Yes, do it");
        }

        public override void OnResponse(NetState sender, in RelayInfo info)
        {
            var from = sender?.Mobile;
            if (from == null) return;

            switch (info.ButtonID)
            {
                case B_Confirm:
                    try { _onConfirm?.Invoke(); }
                    catch (Exception ex)
                    {
                        from.SendMessage($"Confirm action failed: {ex.Message}");
                    }
                    from.SendGump(new BotPanelGump(from));
                    break;

                case B_Cancel:
                default:
                    from.SendGump(new BotPanelGump(from));
                    break;
            }
        }
    }
}
