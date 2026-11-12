using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class CalculatorApp
{
    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
    }

    public static Process Start()
    {
        var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), throwOnError: true)!;
        var manager = (IApplicationActivationManager)Activator.CreateInstance(type)!;
        try
        {
            int result = manager.ActivateApplication("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", null, 0, out uint pid);
            Marshal.ThrowExceptionForHR(result);
            var process = Process.GetProcessById(checked((int)pid));
            if (process.SessionId == Process.GetCurrentProcess().SessionId) return process;
            process.Dispose();
            throw new UnauthorizedAccessException("Calculator activated outside the owned agent session");
        }
        finally { Marshal.ReleaseComObject(manager); }
    }
}
