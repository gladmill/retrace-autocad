using System.Text;

namespace DialogCapture;

internal sealed record TemplateControl(string? ClassName, ushort ClassAtom, string? Text, int Id, uint Style);

/// <summary>Just enough of DLGTEMPLATE / DLGTEMPLATEEX to read control classes and patch the dialog style.</summary>
internal sealed class Template
{
    public bool Extended;
    public int StyleOffset;          // where the dialog's 32-bit style lives in the buffer
    public uint Style, ExStyle;
    public string? Title;
    public string? FontFace;
    public int FontPoints;
    public List<TemplateControl> Controls = new();

    public const uint WS_POPUP = 0x80000000, WS_CHILD = 0x40000000, WS_CAPTION = 0x00C00000, DS_CONTROL = 0x0400, DS_SETFONT = 0x40;

    /// <summary>Predefined classes referenced by atom (0x0080..0x0085).</summary>
    public static string? AtomName(ushort a) => a switch
    {
        0x0080 => "Button", 0x0081 => "Edit", 0x0082 => "Static", 0x0083 => "ListBox", 0x0084 => "ScrollBar", 0x0085 => "ComboBox",
        _ => null,
    };

    public static Template Parse(byte[] b)
    {
        var t = new Template();
        int p = 0;
        ushort U16(int o) => BitConverter.ToUInt16(b, o);
        uint U32(int o) => BitConverter.ToUInt32(b, o);
        string Sz(ref int o)
        {
            int s = o;
            while (U16(o) != 0) o += 2;
            var str = Encoding.Unicode.GetString(b, s, o - s);
            o += 2;
            return str;
        }
        // menu / class / title ordinal-or-string
        string? OrdOrSz(ref int o)
        {
            if (U16(o) == 0) { o += 2; return null; }
            if (U16(o) == 0xFFFF) { o += 4; return null; }
            return Sz(ref o);
        }

        int count;
        if (U16(0) == 1 && U16(2) == 0xFFFF)
        {
            t.Extended = true;
            t.ExStyle = U32(8); t.StyleOffset = 12; t.Style = U32(12);
            count = U16(16); p = 18 + 8;
        }
        else
        {
            t.Style = U32(0); t.ExStyle = U32(4); t.StyleOffset = 0;
            count = U16(8); p = 10 + 8;
        }
        OrdOrSz(ref p);                       // menu
        OrdOrSz(ref p);                       // window class
        t.Title = Sz(ref p);
        if ((t.Style & DS_SETFONT) != 0)
        {
            t.FontPoints = U16(p); p += 2;
            if (t.Extended) p += 4;           // weight, italic, charset
            t.FontFace = Sz(ref p);
        }
        for (int i = 0; i < count; i++)
        {
            p = (p + 3) & ~3;
            uint style; int id;
            if (t.Extended) { style = U32(p + 8); p += 12 + 8; id = (int)U32(p); p += 4; }
            else { style = U32(p); p += 8 + 8; id = U16(p); p += 2; }
            string? cls = null; ushort atom = 0;
            if (U16(p) == 0xFFFF) { atom = U16(p + 2); cls = AtomName(atom); p += 4; }
            else cls = Sz(ref p);
            string? text = null;
            if (U16(p) == 0xFFFF) p += 4; else text = Sz(ref p);
            int extra = U16(p); p += 2 + extra;
            t.Controls.Add(new TemplateControl(cls, atom, text, id, style));
        }
        return t;
    }
}
