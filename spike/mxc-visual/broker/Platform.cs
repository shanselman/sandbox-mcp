using System.Security.Cryptography;

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
        LocalAgentCredential.CheckSupportedBuild();
        foreach (var (name, hash) in Pins)
        {
            using var file = File.OpenRead(Path.Combine(Environment.SystemDirectory, name));
            if (Convert.ToHexString(SHA256.HashData(file)) != hash)
                throw new PlatformNotSupportedException($"{name} differs from the validated experimental build; revalidate before upgrading the broker");
        }
    }
}
