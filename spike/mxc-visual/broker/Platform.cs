using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace SandboxMcp.FullBroker;

internal static class Platform
{
    private static readonly IReadOnlyDictionary<string, string> Pins = new Dictionary<string, string>
    {
        ["wtsapi32.dll"] = "999C21B2F73212EFCD89E017DC65BA5F21D40CAF86BB921FE9692EB66E567888",
        ["winsta.dll"] = "0480DC132339AC3F06277824A925774819994EA550BEDEABA0ECA9A0EF47AB3A",
        ["termsrv.dll"] = "B85A40F624CC8A10A301AA55CCE2F53243E60E4D19D2A6F799E5E2E2B2601B66",
        ["lsm.dll"] = "D132944B00F3FEEAF7AC64BBE29A2867A1B2CD36C3330478FCE09896FB441262",
    };
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
        foreach (var (name, expected) in Pins)
        {
            string path = Path.Combine(Environment.SystemDirectory, name);
            if (!File.Exists(path)) { missing.Add(name); continue; }
            using var file = File.OpenRead(path);
            if (Convert.ToHexString(SHA256.HashData(file)) != expected) changed.Add(name);
        }
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
                "The APIs exist, but these Windows binaries differ from the validated experimental build. Update Windows normally, then obtain a broker release validated for that build; do not remove the ABI pins or downgrade system DLLs.",
                architecture, windows, missing, changed);
        return new(true, "validated", "Experimental APIs and validated build pins are present. This is not a stable Windows API contract.", architecture, windows, missing, changed);
    }
}
