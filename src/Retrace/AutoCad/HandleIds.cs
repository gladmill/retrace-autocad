using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;

namespace Retrace.AutoCad;

/// <summary>Recorded handles (hex text) to object ids in a drawing: the one place the replay turns a handle into an object.</summary>
static class HandleIds
{
    /// <summary>The live (not erased) object with this handle, or false.</summary>
    public static bool TryGet(Database db, string handle, out ObjectId id)
    {
        id = ObjectId.Null;
        if (!long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        return db.TryGetObjectId(new Handle(v), out id) && !id.IsNull && !id.IsErased;
    }

    /// <summary>The object exists and is the recorded kind of object (<paramref name="dxf"/> null or "?" = any).</summary>
    public static bool Exists(Database db, string handle, string? dxf) =>
        TryGet(db, handle, out var id) && (dxf == null || dxf == "?" || string.Equals(id.ObjectClass.DxfName, dxf, StringComparison.OrdinalIgnoreCase));
}
