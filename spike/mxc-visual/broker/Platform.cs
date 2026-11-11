using System.Runtime.InteropServices;

namespace SandboxMcp.FullBroker;

internal static class Platform
{
    public static void Check()
    {
        var compatibility = Inspect();
        if (!compatibility.Ready) throw new PlatformNotSupportedException(compatibility.Message);
    }

    internal sealed record Compatibility(bool Ready, string Code, string Message, string Architecture, string Windows,
        string[] MissingApis, string[] UnvalidatedModules);
    internal static Compatibility Inspect()
    {
        var missing = new List<string>();
        var changed = new List<string>();
        var exports = new Dictionary<string, string[]>
        {
            ["wtsapi32.dll"] = ["EXPERIMENTAL_WTSCreateHeadlessSession", "EXPERIMENTAL_WTSStopHeadlessSession", "WTSQueryUserToken"],
            ["IsoSessionCore.dll"] = ["IsoSessionGetLocalAgentLogonCredential", "IsoSessionFreeLogonCredential"]
        };
        foreach (var (module, names) in exports)
        {
            if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, module), out var library))
            {
                missing.Add(module);
                continue;
            }
            try { missing.AddRange(names.Where(name => !NativeLibrary.TryGetExport(library, name, out _)).Select(name => module + "!" + name)); }
            finally { NativeLibrary.Free(library); }
        }
        // WTS entry points have a supplied V1 declaration; internal service
        // implementations may change with Windows servicing. Only the inferred
        // private credential ABI still requires an inspected binary identity.
        if (File.Exists(Path.Combine(Environment.SystemDirectory, "IsoSessionCore.dll")))
        {
            try { LocalAgentCredential.CheckSupportedBuild(); }
            catch (PlatformNotSupportedException) { changed.Add("IsoSessionCore.dll/architecture"); }
        }
        return Classify(RuntimeInformation.ProcessArchitecture.ToString(), Environment.OSVersion.VersionString, missing.Distinct().ToArray(), changed.ToArray());
    }

    internal static Compatibility Classify(string architecture, string windows, string[] missing, string[] changed)
    {
        if (missing.Length != 0)
            return new(false, "experimental_apis_missing",
                "Required experimental Windows APIs are missing. Install the latest available Windows updates, restart if requested, then run doctor again. Availability depends on the Windows build/channel; latest stable Windows is not a guarantee. Do not copy system DLLs or bypass feature gates.",
                architecture, windows, missing, changed);
        if (architecture != "Arm64")
            return new(false, "architecture_not_validated", "This experimental broker has only been validated on ARM64. A Windows update alone does not validate another architecture.", architecture, windows, missing, changed);
        if (changed.Length != 0)
            return new(false, "experimental_build_not_validated",
                "The WTS APIs exist, but the undocumented IsoSessionCore credential ABI differs from the inspected build. Obtain a broker release qualified for that credential ABI; do not downgrade system DLLs. WTS and internal Windows service DLLs are not hash-pinned.",
                architecture, windows, missing, changed);
        return new(true, "experimental_available", "Required experimental exports and the inspected private credential ABI are present. Eligible for staged testing, not proof of native lifecycle safety. Internal Windows service DLLs are not hash-pinned.", architecture, windows, missing, changed);
    }
}
