using System;
using System.Runtime.InteropServices;

namespace Python.Runtime.Native;

#pragma warning disable CS0169

[StructLayout(LayoutKind.Sequential)]
struct PyConfig
{
    int _config_init;
    public int isolated;
    int use_environment;
    int dev_mode;
    public int install_signal_handlers;

    // Create an int array of size 256 as padding
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1024)]
    int[] padding;
}

public enum PyStatusType : int
{
    PyStatus_Ok,
    PyStatus_Error,
    PyStatus_Exception,
    PyStatus_Exit
}

struct PyStatus
{
    public PyStatusType type;
    IntPtr func;
    IntPtr err_msg;
    int exitcode;
}
