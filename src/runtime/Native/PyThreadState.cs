using System;
using System.Runtime.InteropServices;

namespace Python.Runtime.Native;

[StructLayout(LayoutKind.Sequential)]
struct PyThreadState
{
  public IntPtr prev;
  public IntPtr next;
  public IntPtr interp;
}
