using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Principal;

// Build-specific experiment, not a published SDK contract. See the handoff's
// matched public-symbol/static-call-site evidence before changing this pin.
internal sealed class LocalAgentCredential : IDisposable
{
    private const string InspectedCoreSha256 = "91150F898562280F91EDDDDF54054AE0507191073C55B152B64B3C28F6E89D71";
    // Keep the inspected module mapped for this process. Verify under a read
    // handle before the first load, rather than racing a path replacement.
    private static readonly Lazy<IntPtr> PinnedModule = new(() =>
    {
        string path = Path.Combine(Environment.SystemDirectory, "IsoSessionCore.dll");
        using var file = File.OpenRead(path);
        if (Convert.ToHexString(SHA256.HashData(file)) != InspectedCoreSha256)
            throw new PlatformNotSupportedException("IsoSessionCore changed before loading");
        return NativeLibrary.Load(path);
    });
    private IntPtr identity;
    private bool identityValidated;
    public IntPtr Buffer { get; private set; }
    public uint Length { get; private set; }

    [DllImport("IsoSessionCore.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int IsoSessionGetLocalAgentLogonCredential(
        byte[] owningUserSid, string owningAppId, string agentName, out IntPtr credential);

    [DllImport("IsoSessionCore.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void IsoSessionFreeLogonCredential(IntPtr credential);

    [DllImport("secur32.dll", ExactSpelling = true)]
    private static extern int SspiValidateAuthIdentity(IntPtr identity);

    [DllImport("secur32.dll", ExactSpelling = true)]
    private static extern int SspiMarshalAuthIdentity(IntPtr identity, out uint length, out IntPtr buffer);

    [DllImport("secur32.dll", ExactSpelling = true)]
    private static extern void SspiZeroAuthIdentity(IntPtr identity);

    [DllImport("secur32.dll", ExactSpelling = true)]
    private static extern void SspiLocalFree(IntPtr buffer);

    [DllImport("secur32.dll", ExactSpelling = true)]
    private static extern int SspiUnmarshalAuthIdentity(uint length, IntPtr buffer, out IntPtr identity);

    [DllImport("secur32.dll", ExactSpelling = true)]
    private static extern void SspiFreeAuthIdentity(IntPtr identity);

    public static LocalAgentCredential Acquire(string agentName, Dictionary<string, object?> report, SecurityIdentifier? provisioningOwner = null)
    {
        string hash = CheckSupportedBuild();
        report["credentialModuleSha256"] = hash;
        _ = PinnedModule.Value;

        using var caller = WindowsIdentity.GetCurrent();
        if (provisioningOwner is not null && caller.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
            throw new UnauthorizedAccessException("Only the explicitly approved System probe may supply the provisioning owner");
        var sid = provisioningOwner ?? caller.User ?? throw new InvalidOperationException("Caller token has no user SID");
        byte[] sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);
        var credential = new LocalAgentCredential();
        try
        {
            // Windows' broker passes this exact literal at its local-agent call site.
            report["credentialAppScope"] = "unknown";
            report["credentialOwner"] = provisioningOwner is null ? "current caller token user" : "original MXC provisioning caller";
            int status = IsoSessionGetLocalAgentLogonCredential(sidBytes, "unknown", agentName, out credential.identity);
            report["credentialHresult"] = $"0x{status:X8}";
            report["credentialReturned"] = credential.identity != IntPtr.Zero;
            if (status < 0)
            {
                if ((unchecked((uint)status) & 0xFFFF0000) == 0x80070000)
                    report["credentialError"] = new { code = status & 0xFFFF, message = new Win32Exception(status & 0xFFFF).Message };
                throw new COMException("IsoSessionGetLocalAgentLogonCredential failed", status);
            }
            if (credential.identity == IntPtr.Zero)
                throw new InvalidOperationException("Credential API succeeded without returning an identity");

            status = SspiValidateAuthIdentity(credential.identity);
            report["credentialValidation"] = $"0x{status:X8}";
            if (status != 0) throw new COMException("Returned identity failed SSPI validation", status);
            credential.identityValidated = true;

            status = SspiMarshalAuthIdentity(credential.identity, out uint length, out IntPtr buffer);
            credential.Buffer = buffer;
            credential.Length = length;
            report["credentialMarshalStatus"] = $"0x{status:X8}";
            if (status != 0) throw new COMException("SspiMarshalAuthIdentity failed", status);
            ValidateSerializedEx2(buffer, length, report);
            return credential;
        }
        catch
        {
            credential.Dispose();
            throw;
        }
    }

    internal static string CheckSupportedBuild()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            throw new PlatformNotSupportedException("The inspected credential ABI is pinned to this ARM64 build");
        using var module = File.OpenRead(Path.Combine(Environment.SystemDirectory, "IsoSessionCore.dll"));
        string hash = Convert.ToHexString(SHA256.HashData(module));
        if (hash != InspectedCoreSha256)
            throw new PlatformNotSupportedException("IsoSessionCore changed; re-establish the ABI before invoking it");
        return hash;
    }

    internal static void ValidateSerializedEx2(IntPtr buffer, uint length, Dictionary<string, object?> report)
    {
        if (buffer == IntPtr.Zero || length < 12)
            throw new InvalidDataException("SSPI returned an incomplete serialized identity");
        uint version = unchecked((uint)Marshal.ReadInt32(buffer));
        report["authIdentityVersion"] = version;
        report["authIdentityBytes"] = length;
        report["serializedWordAt4"] = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
        report["serializedDwordAt8"] = unchecked((uint)Marshal.ReadInt32(buffer, 8));
        if (version != 0x201) throw new InvalidDataException("SSPI did not serialize EX2");
        IntPtr decoded = IntPtr.Zero;
        try
        {
            // Serialized storage may include padding beyond cbStructureLength.
            // Validate the actual byte count with SSPI, not strict equality.
            int status = SspiUnmarshalAuthIdentity(length, buffer, out decoded);
            report["authIdentityUnmarshalStatus"] = $"0x{status:X8}";
            if (status != 0) throw new COMException("Serialized EX2 failed size-aware SSPI unmarshaling", status);
            if (decoded == IntPtr.Zero) throw new InvalidDataException("SSPI returned no decoded identity");
            status = SspiValidateAuthIdentity(decoded);
            report["authIdentityValidation"] = $"0x{status:X8}";
            if (status != 0) throw new COMException("Decoded EX2 failed SSPI validation", status);
        }
        finally
        {
            if (decoded != IntPtr.Zero) SspiFreeAuthIdentity(decoded);
        }
    }

    internal static void VerifyOwnerBinding(string agentName, SecurityIdentifier provisioningOwner, SecurityIdentifier agentSid)
    {
        using var caller = WindowsIdentity.GetCurrent();
        if (caller.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true
            || provisioningOwner.IsWellKnown(WellKnownSidType.LocalSystemSid) || provisioningOwner == agentSid)
            throw new UnauthorizedAccessException("Owner-binding check requires a System broker for a non-System owner");
        // The freshly provisioned account is a real, distinct SID, but is not
        // the owner that provisioned itself. No unrelated user's data is read.
        var wrongOwner = agentSid;
        byte[] sid = new byte[wrongOwner.BinaryLength];
        wrongOwner.GetBinaryForm(sid, 0);
        IntPtr unexpected = IntPtr.Zero;
        try
        {
            int status = IsoSessionGetLocalAgentLogonCredential(sid, "unknown", agentName, out unexpected);
            if (status >= 0 || unexpected != IntPtr.Zero)
                throw new UnauthorizedAccessException("The experimental credential API did not enforce the tested owner binding");
        }
        finally
        {
            if (unexpected != IntPtr.Zero)
            {
                if (SspiValidateAuthIdentity(unexpected) == 0) SspiZeroAuthIdentity(unexpected);
                IsoSessionFreeLogonCredential(unexpected);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (Buffer != IntPtr.Zero)
            {
                try
                {
                    // Copy only zeroes into native storage; never copy a secret into a managed string/array.
                    byte[] zeroes = new byte[4096];
                    for (long offset = 0; offset < Length; offset += zeroes.Length)
                        Marshal.Copy(zeroes, 0, new IntPtr(Buffer.ToInt64() + offset),
                            (int)Math.Min(zeroes.Length, Length - offset));
                }
                finally
                {
                    SspiLocalFree(Buffer);
                    Buffer = IntPtr.Zero;
                    Length = 0;
                }
            }
        }
        finally
        {
            if (identity != IntPtr.Zero)
            {
                try
                {
                    if (identityValidated) SspiZeroAuthIdentity(identity);
                }
                finally
                {
                    IsoSessionFreeLogonCredential(identity);
                    identity = IntPtr.Zero;
                }
            }
        }
    }
}
