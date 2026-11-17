#if NETFRAMEWORK
// Microsoft.Win32.OpenFolderDialog is WPF on .NET 8 only. On .NET Framework (AutoCAD 2024) this stand-in wraps the WinForms folder browser,
// with the members the code uses. It picks one folder at a time: Multiselect is accepted and ignored (FolderNames holds the one folder).
namespace Microsoft.Win32
{
    sealed class OpenFolderDialog
    {
        public string Title { get; set; } = "";
        public string InitialDirectory { get; set; } = "";
        public bool Multiselect { get; set; }
        public string FolderName { get; private set; } = "";
        public string[] FolderNames => FolderName.Length == 0 ? System.Array.Empty<string>() : new[] { FolderName };

        public bool? ShowDialog() => ShowDialog(null);

        public bool? ShowDialog(System.Windows.Window? owner)
        {
            using var d = new System.Windows.Forms.FolderBrowserDialog { Description = Title, ShowNewFolderButton = true };
            if (InitialDirectory.Length > 0 && System.IO.Directory.Exists(InitialDirectory)) d.SelectedPath = InitialDirectory;
            var hwnd = owner == null ? System.IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            var result = hwnd == System.IntPtr.Zero ? d.ShowDialog() : d.ShowDialog(new Win32Owner(hwnd));
            if (result != System.Windows.Forms.DialogResult.OK) return false;
            FolderName = d.SelectedPath;
            return true;
        }

        sealed class Win32Owner : System.Windows.Forms.IWin32Window
        {
            public Win32Owner(System.IntPtr h) { Handle = h; }
            public System.IntPtr Handle { get; }
        }
    }
}
#endif
