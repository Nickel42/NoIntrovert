using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Views;

/// <summary>
/// Shows the queue's overall progress on the window's taskbar button
/// (Windows 7 and later, through ITaskbarList3). Other platforms get nothing.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TaskbarProgress : IProgressIndicator
{
    private readonly IntPtr _hwnd;
    private readonly ITaskbarList3? _taskbar;
    private TbpFlag _state = TbpFlag.NoProgress;
    private ulong _value = ulong.MaxValue;

    private TaskbarProgress(IntPtr hwnd)
    {
        _hwnd = hwnd;
        try
        {
            _taskbar = (ITaskbarList3)new TaskbarListComObject();
            _taskbar.HrInit();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _taskbar = null; // e.g. no shell (Windows Server Core)
        }
    }

    public static IProgressIndicator? For(IntPtr hwnd) =>
        OperatingSystem.IsWindowsVersionAtLeast(6, 1) && hwnd != IntPtr.Zero ? new TaskbarProgress(hwnd) : null;

    public void Show(double? fraction, bool hasErrors)
    {
        if (_taskbar is null) return;
        var state = fraction is null ? TbpFlag.NoProgress : hasErrors ? TbpFlag.Error : TbpFlag.Normal;
        try
        {
            if (state != _state)
            {
                _taskbar.SetProgressState(_hwnd, state);
                _state = state;
                _value = ulong.MaxValue;
            }
            if (fraction is { } f)
            {
                var value = (ulong)Math.Round(Math.Clamp(f, 0, 1) * 1000);
                if (value != _value)
                {
                    _taskbar.SetProgressValue(_hwnd, value, 1000);
                    _value = value;
                }
            }
        }
        catch (COMException)
        {
            // The taskbar button can go away (Explorer restarting); try again next time.
            _state = TbpFlag.NoProgress;
        }
    }

    private enum TbpFlag
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8,
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListComObject;

    // Methods in vtable order: ITaskbarList, ITaskbarList2, then the ITaskbarList3 ones used here.
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, TbpFlag flags);
    }
}
