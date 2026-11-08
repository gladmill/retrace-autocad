using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Retrace.Samples
{
    /// <summary>
    /// DRAWMFCFRAME and DRAWMFCDIALOG: real MFC windows, from the native DLL of <c>samples/Retrace.MfcSample</c> (built on the Windows machine by its <c>build.ps1</c>; no binary is in git). DRAWMFCFRAME shows a modal
    /// CFrameWnd with a CFormView (a CToolBar with the view buttons, a tab control, a list view in icon mode, a tree, rich edit, date pickers, a month calendar, an IP address, a hot key, ComboBoxEx, a CCheckListBox and
    /// the MFC Feature Pack controls); DRAWMFCDIALOG shows the same controls in a CDialog. Each draws one TEXT per value the window returns, so a checkpoint tells a missed value. The DLL is looked for in
    /// the environment variable <c>RETRACE_MFCSAMPLE_DLL</c>, then <c>C:\temp\retrace-mfcsample\Retrace.MfcSample.dll</c>, then next to Retrace.Samples.dll; if it is not there the command says so and does nothing.
    /// </summary>
    public class MfcCommands
    {
        public const string DllName = "Retrace.MfcSample.dll", EnvVar = "RETRACE_MFCSAMPLE_DLL", DefaultFolder = @"C:\temp\retrace-mfcsample";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        delegate int ShowWindow(IntPtr owner, StringBuilder result, int capacity);

        /// <summary>The path of the DLL, or null when none of the places has it.</summary>
        public static string? FindDll()
        {
            var env = Environment.GetEnvironmentVariable(EnvVar);
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);
            candidates.Add(Path.Combine(DefaultFolder, DllName));
            var here = Path.GetDirectoryName(typeof(MfcCommands).Assembly.Location);
            if (!string.IsNullOrEmpty(here)) candidates.Add(Path.Combine(here, DllName));
            return candidates.FirstOrDefault(File.Exists);
        }

        [CommandMethod("DRAWMFCFRAME")]
        public void DrawFrame() => Run("RetraceMfcFrame");

        [CommandMethod("DRAWMFCDIALOG")]
        public void DrawDialog() => Run("RetraceMfcDialog");

        static void Run(string export)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            var path = FindDll();
            if (path == null)
            {
                ed.WriteMessage($"\n{DllName} was not found. Build it with samples\\Retrace.MfcSample\\build.ps1 and put it in {DefaultFolder}, or set {EnvVar} to its path.");
                return;
            }
            try
            {
                var lib = NativeLibrary.Load(path);   // loading an already loaded dll gives the same module
                var fn = Marshal.GetDelegateForFunctionPointer<ShowWindow>(NativeLibrary.GetExport(lib, export));
                var result = new StringBuilder(8192);
                int r = fn(Application.MainWindow.Handle, result, result.Capacity);
                if (r < 0) { ed.WriteMessage($"\n{export} could not create its window ({r})."); return; }
                if (r == 0) { ed.WriteMessage("\nCancelled; nothing drawn."); return; }
                var lines = result.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                using (doc.LockDocument())
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var text = new DBText { TextString = lines[i].Trim('\r'), Position = new Point3d(0, -i * 4, 0), Height = 2.5 };
                        modelSpace.AppendEntity(text);
                        tr.AddNewlyCreatedDBObject(text, true);
                    }
                    tr.Commit();
                }
                ed.WriteMessage($"\n{lines.Length} values drawn.");
            }
            catch (System.Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or ArgumentException)
            {
                ed.WriteMessage($"\n{path} could not be used: {ex.Message} (it must be the x64 build of samples\\Retrace.MfcSample).");
            }
            // the dll stays loaded (an MFC regular DLL that is freed and loaded again fails with "Encountered an improper argument"): restart AutoCAD to use a rebuilt one
        }
    }
}
