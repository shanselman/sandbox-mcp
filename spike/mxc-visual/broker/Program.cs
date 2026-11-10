using System.ComponentModel;
using System.Text.Json;
using System.Security.Principal;

namespace SandboxMcp.FullBroker;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["service"]) { Server.Dispatch(); return 0; }
            if (args is ["call"]) return Client.Call().GetAwaiter().GetResult();
            if (args is ["selftest"]) return SelfTest.Run();
            if (args is ["doctor"])
            {
                var compatibility = Platform.Inspect();
                Console.WriteLine(JsonSerializer.Serialize(compatibility, Protocol.Json));
                return compatibility.Ready ? 0 : 1;
            }
            if (args is ["verify-package", var verifySource])
            {
                Console.WriteLine(JsonSerializer.Serialize(Installer.VerifyPackage(Path.GetFullPath(verifySource)), Protocol.Json));
                return 0;
            }
            if (args is ["client-lock"]) return Client.HoldLock().GetAwaiter().GetResult();
            if (args is ["client-path"])
            {
                Console.WriteLine(JsonSerializer.Serialize(new { root = Client.StateRoot }));
                return 0;
            }
            if (args is ["account-state", var name, var sid])
            {
                bool exists;
                try { exists = ((SecurityIdentifier)new NTAccount(Environment.MachineName, name).Translate(typeof(SecurityIdentifier))).Value == new SecurityIdentifier(sid).Value; }
                catch (IdentityNotMappedException) { exists = false; }
                Console.WriteLine(JsonSerializer.Serialize(new { exists }));
                return 0;
            }
            if (args is ["install", var package]) return Installer.Elevate("install-elevated", package);
            if (args is ["uninstall"]) return Installer.Elevate("uninstall-elevated", null);
            if (args is ["install-elevated", var source, var owner, var hash, var resultPipe, var parentPid])
                return Installer.ReportResult(resultPipe, uint.Parse(parentPid), () => Installer.Install(source, owner, hash));
            if (args is ["uninstall-elevated", var uninstallOwner, var uninstallPipe, var uninstallParent])
                return Installer.ReportResult(uninstallPipe, uint.Parse(uninstallParent), () => Installer.Uninstall(uninstallOwner));
            throw new ArgumentException("Usage: SandboxMcp.FullBroker install <package> | uninstall | call (JSON on stdin) | selftest");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = error.Message, type = error.GetType().Name }));
            return error is Win32Exception { NativeErrorCode: 1223 } ? 2 : 1;
        }
    }
}
