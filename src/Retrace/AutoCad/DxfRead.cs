using System.Runtime.InteropServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>
/// The DXF groups of one object (what `entget` returns), read without a DXF filer. Needed for two persisted values the managed API only gives
/// through session state: a layout's PSLTSCALE flag and a viewport's off bit (<see cref="Retrace.Core.DxfGroups"/>).
/// AutoCAD.NET has no managed entget, and <c>Application.Invoke("entget")</c> is refused (eInvalidInput), so this calls two exports of the
/// AutoCAD process by their C++ names (acdbGetAdsName in acdb&lt;major&gt;.dll, acdbEntGet in accore.dll).
/// Those are not a published .NET API and a changed binary interface could crash AutoCAD past any try/catch, so they are used only on the
/// AutoCAD major version they were verified on (<see cref="VerifiedMajor"/>, AutoCAD 2026 = 25.x). On any other version, or when an export is
/// missing or a call fails, <see cref="ReadFlag"/> returns null, the caller leaves the field out of the snapshot, and the reason is logged once.
/// Read-only; call it where entities may be read (a command or timer), never from an event handler.
/// </summary>
static class DxfRead
{
    /// <summary>The AutoCAD major version (<c>Application.Version.Major</c>) the exports were checked on. A new AutoCAD release: re-check, then change this.</summary>
    internal const int VerifiedMajor = 25;

    delegate int GetAdsNameFn([In, Out] long[] name, ObjectId id);
    delegate IntPtr EntGetFn([In] long[] name);

    static GetAdsNameFn? _getAdsName;
    static EntGetFn? _entGet;
    static bool _tried;
    static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    static void LogOnce(string key, string message, Exception? ex = null)
    {
        if (!Logged.Add(key)) return;
        if (ex != null) Log.Error(message, ex); else Log.Info(message);
    }

    static bool Init()
    {
        if (_tried) return _getAdsName != null;
        _tried = true;
        try
        {
            var major = AcApp.Version.Major;
            if (major != VerifiedMajor)
            {
                LogOnce("version", $"DxfRead: AutoCAD {AcApp.Version} is not the version entget was verified on ({VerifiedMajor}.x): layout psltscale and viewport off are left out of snapshots");
                return false;
            }
            var db = NativeLibrary.Load($"acdb{major}.dll");
            var core = NativeLibrary.Load("accore.dll");
            _getAdsName = Marshal.GetDelegateForFunctionPointer<GetAdsNameFn>(
                NativeLibrary.GetExport(db, "?acdbGetAdsName@@YA?AW4ErrorStatus@Acad@@AEAY01_JVAcDbObjectId@@@Z"));
            _entGet = Marshal.GetDelegateForFunctionPointer<EntGetFn>(NativeLibrary.GetExport(core, "?acdbEntGet@@YAPEAUresbuf@@QEB_J@Z"));
            return true;
        }
        catch (Exception ex)
        {
            _getAdsName = null; _entGet = null;
            LogOnce("init", "DxfRead: entget is not available in this AutoCAD (layout psltscale and viewport off are left out of snapshots)", ex);
            return false;
        }
    }

    /// <summary>Runs <paramref name="pick"/> over the object's (group code, value) pairs, lazily: it can stop at the group it needs and the rest is
    /// not converted. Returns its result, or null when entget cannot be used, failed or returned nothing (logged once per kind of object).</summary>
    public static int? ReadFlag(ObjectId id, Func<IEnumerable<(int Code, object? Value)>, int?> pick)
    {
        if (!Init()) return null;
        string cls = "?";
        try
        {
            try { cls = id.ObjectClass?.DxfName ?? id.ObjectClass?.Name ?? "?"; } catch { /* the class name is only for the log */ }
            var name = new long[2];
            var rc = _getAdsName!(name, id);
            if (rc != 0) { LogOnce("rc:" + cls, $"DxfRead: acdbGetAdsName returned status {rc} for a {cls}; the field is left out"); return null; }
            var ptr = _entGet!(name);
            if (ptr == IntPtr.Zero) { LogOnce("null:" + cls, $"DxfRead: acdbEntGet returned nothing for a {cls}; the field is left out"); return null; }
            using var rb = ResultBuffer.Create(ptr, true);   // takes ownership: the chain is released with it
            return pick(Groups(rb));
        }
        catch (Exception ex)
        {
            LogOnce("read:" + cls, $"DxfRead.ReadFlag for a {cls} (logged once)", ex);
            return null;
        }
    }

    static IEnumerable<(int Code, object? Value)> Groups(ResultBuffer rb)
    {
        foreach (var tv in rb) yield return (tv.TypeCode, tv.Value);
    }
}
