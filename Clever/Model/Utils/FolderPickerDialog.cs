using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Clever.Model.Utils
{
    internal class FolderPickerDialog
    {
        private const int ErrorCancelled = unchecked((int)0x800704C7);

        public string Title { get; set; }
        public string InitialDirectory { get; set; }

        public bool Show(out string selectedPath)
        {
            selectedPath = "";

            try
            {
                return ShowModern(out selectedPath);
            }
            catch
            {
                return ShowFallback(out selectedPath);
            }
        }

        private bool ShowModern(out string selectedPath)
        {
            selectedPath = "";
            IFileDialog dialog = null;
            IShellItem result = null;
            IShellItem initialFolder = null;

            try
            {
                dialog = (IFileDialog)new FileOpenDialog();

                FileOpenOptions options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options
                    | FileOpenOptions.PickFolders
                    | FileOpenOptions.ForceFileSystem
                    | FileOpenOptions.PathMustExist);

                if (!string.IsNullOrWhiteSpace(Title))
                    dialog.SetTitle(Title);

                if (!string.IsNullOrWhiteSpace(InitialDirectory) && Directory.Exists(InitialDirectory))
                {
                    Guid shellItemGuid = typeof(IShellItem).GUID;
                    NativeMethods.SHCreateItemFromParsingName(InitialDirectory, IntPtr.Zero, ref shellItemGuid, out initialFolder);
                    if (initialFolder != null)
                        dialog.SetFolder(initialFolder);
                }

                var hr = dialog.Show(IntPtr.Zero);
                if (hr == ErrorCancelled)
                    return false;

                Marshal.ThrowExceptionForHR(hr);

                dialog.GetResult(out result);
                IntPtr pathPointer;
                result.GetDisplayName(ShellItemDisplayName.FileSystemPath, out pathPointer);
                selectedPath = Marshal.PtrToStringUni(pathPointer);
                Marshal.FreeCoTaskMem(pathPointer);

                return !string.IsNullOrWhiteSpace(selectedPath);
            }
            finally
            {
                if (result != null)
                    Marshal.ReleaseComObject(result);
                if (initialFolder != null)
                    Marshal.ReleaseComObject(initialFolder);
                if (dialog != null)
                    Marshal.ReleaseComObject(dialog);
            }
        }

        private bool ShowFallback(out string selectedPath)
        {
            selectedPath = "";
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = Title ?? "";
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrWhiteSpace(InitialDirectory) && Directory.Exists(InitialDirectory))
                    dialog.SelectedPath = InitialDirectory;

                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return false;

                selectedPath = dialog.SelectedPath;
                return !string.IsNullOrWhiteSpace(selectedPath);
            }
        }

        [Flags]
        private enum FileOpenOptions : uint
        {
            PickFolders = 0x00000020,
            ForceFileSystem = 0x00000040,
            PathMustExist = 0x00000800
        }

        private enum ShellItemDisplayName : uint
        {
            FileSystemPath = 0x80058000
        }

        [ComImport]
        [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialog
        {
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(ShellItemDisplayName sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport]
        [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig]
            int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(FileOpenOptions fos);
            void GetOptions(out FileOpenOptions pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, uint fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        private static class NativeMethods
        {
            [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
            internal static extern void SHCreateItemFromParsingName(
                [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
                IntPtr pbc,
                ref Guid riid,
                [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
        }
    }
}
