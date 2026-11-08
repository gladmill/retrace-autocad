using Autodesk.AutoCAD.Runtime;

namespace Retrace.Samples
{
    /// <summary>
    /// DRAWCHAINDLG: the DRAWITEMS dialog (a list, an Add button, an Options... sub dialog), started the way a vendor add-in does it: the ribbon button "Chain..."
    /// runs the LISP (C:CHAINADD) (<c>chain/chain.lsp</c>), which runs UCS and then <c>(command "_.SCRIPT" "C:/temp/retrace-chain/chain.scr")</c>; the script types
    /// DRAWCHAINDLG. A recording of that click holds a ribbon click, a LISP, UCS, SCRIPT and DRAWCHAINDLG, all started without a person after the click.
    /// </summary>
    public class ChainCommands
    {
        [CommandMethod("DRAWCHAINDLG")]
        public void DrawChainDialog() => new ItemsCommands().DrawItems();
    }
}
