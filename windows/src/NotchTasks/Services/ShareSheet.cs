using System.IO;
using System.Runtime.InteropServices;
using NotchTasks.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using WinRT;

namespace NotchTasks.Services;

/// The Windows share sheet for files (the shelf's Share, P3d-R4). The sheet is shown for a window: the pill's, through the
/// DataTransferManager of that window, which offers the files as storage items. Share returns false when the sheet cannot be shown.
public static partial class ShareSheet
{
    private const string ManagerClass = "Windows.ApplicationModel.DataTransfer.DataTransferManager";

    /// IDataTransferManagerInterop (the Windows desktop share interop: not in the Windows SDK projection, so it is declared here).
    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        void GetForWindow(nint appWindow, ref Guid riid, out nint dataTransferManager);

        void ShowShareUIForWindow(nint appWindow);
    }

    /// The IID of IDataTransferManager, the interface GetForWindow returns.
    private static readonly Guid DataTransferManagerIid = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");

    /// The manager of the window that was last shared from. It is kept, so the DataRequested handler is subscribed once, and the
    /// handler reads the files of the current share from _files.
    private static DataTransferManager? _manager;
    private static nint _managerWindow;
    private static IReadOnlyList<string> _files = Array.Empty<string>();

    /// Shows the share sheet for the window, offering the files (existing files and folders). Returns false, with nothing shown,
    /// when there is nothing to share or Windows does not show the sheet, for any reason; the reason is logged once. Call on the UI thread.
    public static bool Share(nint hwnd, IReadOnlyList<string> paths)
    {
        if (hwnd == nint.Zero || paths.Count == 0)
        {
            return false;
        }
        try
        {
            _files = paths.ToList();
            var interop = ActivationFactory();
            if (_manager is null || _managerWindow != hwnd)
            {
                var iid = DataTransferManagerIid;
                interop.GetForWindow(hwnd, ref iid, out var pointer);
                var manager = MarshalInterface<DataTransferManager>.FromAbi(pointer);
                manager.DataRequested += OnDataRequested;
                _manager = manager;
                _managerWindow = hwnd;
            }
            interop.ShowShareUIForWindow(hwnd);
            return true;
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
            return false;
        }
    }

    private static void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args) => Offer(args.Request, _files);

    /// The DataTransferManager's activation factory, as the share interop (RoGetActivationFactory with the interop's IID). The factory
    /// pointer is released once the wrapper holds it.
    private static IDataTransferManagerInterop ActivationFactory()
    {
        Check(WindowsCreateString(ManagerClass, ManagerClass.Length, out var name));
        try
        {
            var iid = typeof(IDataTransferManagerInterop).GUID;
            Check(RoGetActivationFactory(name, ref iid, out var factory));
            try
            {
                return (IDataTransferManagerInterop)Marshal.GetObjectForIUnknown(factory);
            }
            finally
            {
                Marshal.Release(factory);  // the wrapper holds its own reference: ours is released here
            }
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    private static void Check(int hresult)
    {
        if (hresult < 0)
        {
            throw Marshal.GetExceptionForHR(hresult) ?? new COMException("A Windows Runtime call failed.", hresult);
        }
    }

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, int length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

    /// Offers the files as storage items when the sheet asks: the title is the shelf's name, and a path that is gone is left out.
    private static async void Offer(DataRequest request, IReadOnlyList<string> paths)
    {
        var deferral = request.GetDeferral();
        try
        {
            request.Data.Properties.Title = L10n.L("File shelf");
            var items = new List<IStorageItem>();
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    items.Add(await StorageFolder.GetFolderFromPathAsync(path));
                }
                else if (File.Exists(path))
                {
                    items.Add(await StorageFile.GetFileFromPathAsync(path));
                }
            }
            request.Data.SetStorageItems(items);
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorLog.Write(e);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
