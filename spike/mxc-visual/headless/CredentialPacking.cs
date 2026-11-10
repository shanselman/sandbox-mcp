using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class CredentialPacking
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PasswordIdentity
    {
        public IntPtr User;
        public uint UserLength;
        public IntPtr Domain;
        public uint DomainLength;
        public IntPtr Password;
        public uint PasswordLength;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PackedPassword
    {
        public ushort HeaderLength;
        public ushort StructureLength;
        public Guid Type;
        public uint DataOffset;
        public ushort DataLength;
    }

    [DllImport("credui.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredPackAuthenticationBufferW(
        uint flags, string user, IntPtr password, IntPtr buffer, ref uint bytes);

    [DllImport("credui.dll", EntryPoint = "CredPackAuthenticationBufferW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PackEncoded(uint flags, IntPtr user, IntPtr password, IntPtr buffer, ref uint bytes);

    [DllImport("credui.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredUnPackAuthenticationBufferW(uint flags, IntPtr buffer, uint bytes,
        IntPtr user, ref uint userChars, IntPtr domain, ref uint domainChars, IntPtr password, ref uint passwordChars);

    [DllImport("secur32.dll", ExactSpelling = true)]
    private static extern int SspiValidateAuthIdentity(IntPtr identity);

    [DllImport("secur32.dll", ExactSpelling = true)]
    private static extern int SspiCompareAuthIdentities(IntPtr first, IntPtr second, out byte sameUser, out byte sameIdentity);

    [DllImport("secur32.dll", ExactSpelling = true)]
    private static extern int SspiPrepareForCredWrite(IntPtr identity, IntPtr target, out uint type,
        out IntPtr targetName, out IntPtr userName, out IntPtr blob, out uint bytes);
    [DllImport("secur32.dll", ExactSpelling = true)]
    private static extern void SspiLocalFree(IntPtr buffer);
    [DllImport("secur32.dll", ExactSpelling = true)]
    private static extern int SspiMarshalAuthIdentity(IntPtr identity, out uint bytes, out IntPtr buffer);
    [DllImport("secur32.dll", ExactSpelling = true)]
    private static extern int SspiUnmarshalAuthIdentity(uint bytes, IntPtr buffer, out IntPtr identity);
    [DllImport("secur32.dll", ExactSpelling = true)]
    private static extern void SspiFreeAuthIdentity(IntPtr identity);

    public static int WithEx2(string user, IntPtr password, Func<IntPtr, uint, int> action, Dictionary<string, object?>? diagnostics = null)
    {
        string[] parts = user.Split('\\', 2);
        if (parts.Length != 2) throw new ArgumentException("Expected a domain-qualified test identity");
        byte[] userBytes = Encoding.Unicode.GetBytes(parts[1]);
        byte[] domainBytes = Encoding.Unicode.GetBytes(parts[0]);
        int passwordBytes = 0;
        while (passwordBytes < 65500 && Marshal.ReadInt16(password, passwordBytes) != 0) passwordBytes += 2;
        if (passwordBytes == 65500) throw new InvalidDataException("Test password is not bounded");
        int header = Marshal.SizeOf<Program.AuthIdentityEx2>();
        int packedHeader = Marshal.SizeOf<PackedPassword>();
        int credentialsOffset = (header + userBytes.Length + domainBytes.Length + 3) & ~3;
        int credentialsLength = checked(packedHeader + passwordBytes);
        int allocated = checked(credentialsOffset + credentialsLength);
        IntPtr buffer = Marshal.AllocHGlobal(allocated);
        try
        {
            Marshal.Copy(new byte[allocated], 0, buffer, allocated);
            Marshal.StructureToPtr(new Program.AuthIdentityEx2
            {
                Version = 0x201, HeaderLength = checked((ushort)header), StructureLength = checked((uint)allocated),
                UserOffset = checked((uint)header), UserLength = checked((ushort)userBytes.Length),
                DomainOffset = checked((uint)(header + userBytes.Length)), DomainLength = checked((ushort)domainBytes.Length),
                CredentialsOffset = checked((uint)credentialsOffset), CredentialsLength = checked((ushort)credentialsLength),
                Flags = 6 // Unicode + marshalled; this is a local password, not an online provider identity.
            }, buffer, false);
            Marshal.Copy(userBytes, 0, buffer + header, userBytes.Length);
            Marshal.Copy(domainBytes, 0, buffer + header + userBytes.Length, domainBytes.Length);
            Marshal.StructureToPtr(new PackedPassword
            {
                HeaderLength = checked((ushort)packedHeader), StructureLength = checked((ushort)credentialsLength),
                Type = new Guid("28BFC32F-10F6-4738-98D1-1AC061DF716A"),
                DataOffset = checked((uint)packedHeader), DataLength = checked((ushort)passwordBytes)
            }, buffer + credentialsOffset, false);
            for (int offset = 0; offset < passwordBytes; offset += 2)
                Marshal.WriteInt16(buffer, credentialsOffset + packedHeader + offset, Marshal.ReadInt16(password, offset));
            int status = SspiValidateAuthIdentity(buffer);
            if (status != 0) throw new COMException("Credential-bearing EX2 validation", status);
            var sizeValidation = new Dictionary<string, object?>();
            LocalAgentCredential.ValidateSerializedEx2(buffer, checked((uint)allocated), sizeValidation);
            if (diagnostics is not null) diagnostics["sizeAwareValidation"] = sizeValidation;
            if (!VerifyLocalPassword(buffer, user, password, diagnostics))
                throw new InvalidDataException("EX2 did not recover the local username/password or carries the online-ID flag");
            return action(buffer, checked((uint)allocated));
        }
        finally { ZeroFree(buffer, allocated); }
    }

    public static int SelfTest()
    {
        string user = $@"{Environment.MachineName}\sbx-{Guid.NewGuid():N}"[..(Environment.MachineName.Length + 19)];
        byte[] random = RandomNumberGenerator.GetBytes(32);
        char[] password = new char[37];
        "Aa1!".CopyTo(0, password, 0, 4);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        for (int i = 0; i < random.Length; i++) password[i + 4] = alphabet[random[i] & 63];
        IntPtr secret = Marshal.AllocHGlobal(password.Length * sizeof(char));
        try
        {
            Marshal.Copy(password, 0, secret, password.Length);
            Array.Clear(password);
            CryptographicOperations.ZeroMemory(random);
            var results = new List<Dictionary<string, object?>>();
            foreach (uint flags in new uint[] { 0, 8 })
                results.Add(Test(flags, user, secret, 36));
            var local = new Dictionary<string, object?> { ["operation"] = "local-password EX2 with SSPI password normalization" };
            try
            {
                WithEx2(user, secret, (buffer, bytes) =>
                {
                    local["bytes"] = bytes;
                    local["version"] = $"0x{Marshal.ReadInt32(buffer):X8}";
                    local["localUsernameAndPasswordVerified"] = true;
                    local["serializedIdentity"] = CheckSerialization(buffer, user, secret);
                    local["passed"] = true;
                    return 0;
                }, local);
            }
            catch (Exception error) { local["passed"] = false; local["error"] = error.Message; }
            results.Add(local);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.UtcNow,
                operation = "synthetic credential packing only; no account creation or logon",
                results
            }, new JsonSerializerOptions { WriteIndented = true }));
            return results.All(result => result.TryGetValue("passed", out var passed) && passed is true) ? 0 : 1;
        }
        finally
        {
            Array.Clear(password);
            CryptographicOperations.ZeroMemory(random);
            ZeroFree(secret, password.Length * sizeof(char));
        }
    }

    private static Dictionary<string, object?> CheckSerialization(IntPtr identity, string user, IntPtr password)
    {
        var report = new Dictionary<string, object?>();
        IntPtr buffer = IntPtr.Zero, decoded = IntPtr.Zero;
        uint bytes = 0;
        try
        {
            int status = SspiMarshalAuthIdentity(identity, out bytes, out buffer);
            if (status != 0) throw new COMException("Synthetic EX2 serialization", status);
            LocalAgentCredential.ValidateSerializedEx2(buffer, bytes, report);
            status = SspiUnmarshalAuthIdentity(bytes, buffer, out decoded);
            if (status != 0) throw new COMException("Synthetic EX2 deserialization", status);
            if (!VerifyLocalPassword(decoded, user, password))
                throw new InvalidDataException("Serialized identity did not preserve the synthetic username/password");
            report["passwordPreserved"] = true;
            var truncated = new Dictionary<string, object?>();
            try
            {
                LocalAgentCredential.ValidateSerializedEx2(buffer, bytes / 2, truncated);
                throw new InvalidDataException("Truncated serialized identity was unexpectedly accepted");
            }
            catch (COMException error)
            {
                report["truncatedRejected"] = true;
                report["truncatedStatus"] = $"0x{error.HResult:X8}";
            }
            return report;
        }
        finally
        {
            if (decoded != IntPtr.Zero) SspiFreeAuthIdentity(decoded);
            if (buffer != IntPtr.Zero)
            {
                for (int i = 0; i < bytes; i++) Marshal.WriteByte(buffer, i, 0);
                SspiLocalFree(buffer);
            }
        }
    }

    private static Dictionary<string, object?> Test(uint flags, string user, IntPtr password, int passwordChars)
    {
        var result = new Dictionary<string, object?> { ["flags"] = flags, ["expectedFormat"] = flags == 8 ? "online-ID EX2 (not local password identity)" : "KERB_INTERACTIVE_LOGON" };
        IntPtr packed = IntPtr.Zero;
        int allocated = 0;
        try
        {
            uint size = 0;
            bool sized = CredPackAuthenticationBufferW(flags, user, password, IntPtr.Zero, ref size);
            int error = Marshal.GetLastWin32Error();
            if (sized || error != 122 || size == 0 || size > 1024 * 1024)
                throw new Win32Exception(error, "CredPackAuthenticationBufferW sizing did not return a bounded required size");
            allocated = checked((int)size);
            packed = Marshal.AllocHGlobal(allocated);
            if (!CredPackAuthenticationBufferW(flags, user, password, packed, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CredPackAuthenticationBufferW");
            if (size > allocated || size < 12) throw new InvalidDataException("Unexpected packed buffer size");
            uint version = unchecked((uint)Marshal.ReadInt32(packed));
            result["bytes"] = size;
            result["firstDword"] = $"0x{version:X8}";
            if (flags == 8)
            {
                if (version != 0x201 || size < 48 || unchecked((uint)Marshal.ReadInt32(packed, 8)) != size)
                    throw new InvalidDataException("Packed credential is not the expected complete EX2");
                int status = SspiValidateAuthIdentity(packed);
                result["sspiValidation"] = $"0x{status:X8}";
                if (status != 0) throw new COMException("SSPI validation failed", status);
                bool matches = VerifyLocalPassword(packed, user, password, result);
                result["isLocalPasswordRepresentation"] = matches;
                if (matches) throw new InvalidDataException("Online-ID negative control unexpectedly matched a local password identity");
            }
            else if (version == 0x201)
                throw new InvalidDataException("Negative format control unexpectedly produced EX2");
            bool equal = RoundTrip(flags, packed, size, user, password, passwordChars);
            result[flags == 8 ? "encodedIdentityRoundTrip" : "usernameAndPasswordRoundTrip"] = equal;
            result["passed"] = equal;
        }
        catch (Exception error)
        {
            result["passed"] = false;
            result["error"] = error.Message;
            result["hresult"] = $"0x{error.HResult:X8}";
        }
        finally { ZeroFree(packed, allocated); }
        return result;
    }

    private static bool VerifyLocalPassword(IntPtr packed, string qualifiedUser, IntPtr password, Dictionary<string, object?>? report = null)
    {
        string[] parts = qualifiedUser.Split('\\', 2);
        if (parts.Length != 2) throw new ArgumentException("Expected a domain-qualified test identity");
        IntPtr user = Marshal.StringToHGlobalUni(parts[1]);
        IntPtr domain = Marshal.StringToHGlobalUni(parts[0]);
        IntPtr identity = IntPtr.Zero;
        try
        {
            uint chars = 0;
            while (chars < 32768 && Marshal.ReadInt16(password, checked((int)chars * 2)) != 0) chars++;
            if (chars == 32768) throw new InvalidDataException("Test password is not bounded");
            identity = Marshal.AllocHGlobal(Marshal.SizeOf<PasswordIdentity>());
            Marshal.StructureToPtr(new PasswordIdentity
            {
                User = user, UserLength = (uint)parts[1].Length,
                Domain = domain, DomainLength = (uint)parts[0].Length,
                Password = password, PasswordLength = chars, Flags = 2
            }, identity, false);
            int status = SspiCompareAuthIdentities(identity, packed, out byte sameUser, out byte sameIdentity);
            if (status != 0) throw new COMException("Comparing the original password identity with EX2", status);
            if (report is not null)
            {
                report["sameSuppliedUser"] = sameUser != 0;
                report["sameSuppliedIdentity"] = sameIdentity != 0;
                string packedUser = Marshal.PtrToStringUni(packed + Marshal.ReadInt32(packed, 12),
                    unchecked((ushort)Marshal.ReadInt16(packed, 16)) / 2) ?? "";
                string packedDomain = Marshal.PtrToStringUni(packed + Marshal.ReadInt32(packed, 20),
                    unchecked((ushort)Marshal.ReadInt16(packed, 24)) / 2) ?? "";
                report["ex2UserMatchesQualifiedInput"] = packedUser == qualifiedUser;
                report["ex2UserMatchesUnqualifiedInput"] = packedUser == parts[1];
                report["ex2DomainMatchesInput"] = packedDomain == parts[0];
                report["ex2DomainEmpty"] = packedDomain.Length == 0;
                report["ex2Flags"] = $"0x{Marshal.ReadInt32(packed, 36):X8}";
                IntPtr packedCredentials = packed + Marshal.ReadInt32(packed, 28);
                report["packedCredentialHeaderBytes"] = unchecked((ushort)Marshal.ReadInt16(packedCredentials));
                report["packedCredentialDataOffset"] = Marshal.ReadInt32(packedCredentials, 20);
                report["packedCredentialDataBytes"] = unchecked((ushort)Marshal.ReadInt16(packedCredentials, 24));
            }
            bool passwordMatches = MatchesPreparedPassword(packed, qualifiedUser, password, checked(chars * 2), report);
            return sameUser != 0 && passwordMatches && (Marshal.ReadInt32(packed, 36) & 0x80000) == 0;
        }
        finally
        {
            if (identity != IntPtr.Zero) Marshal.FreeHGlobal(identity);
            Marshal.FreeHGlobal(domain);
            Marshal.FreeHGlobal(user);
        }
    }

    private static bool MatchesPreparedPassword(IntPtr identity, string user, IntPtr password, uint passwordBytes,
        Dictionary<string, object?>? report)
    {
        IntPtr target = IntPtr.Zero, name = IntPtr.Zero, blob = IntPtr.Zero;
        uint bytes = 0;
        try
        {
            // This only prepares in-memory values; never call CredWrite.
            int status = SspiPrepareForCredWrite(identity, IntPtr.Zero, out uint type, out target, out name, out blob, out bytes);
            if (status != 0) throw new COMException("SSPI credential normalization", status);
            bool sameUser = Marshal.PtrToStringUni(name) == user;
            bool samePassword = bytes == passwordBytes;
            int difference = 0;
            if (samePassword)
                for (int i = 0; i < bytes; i++) difference |= Marshal.ReadByte(blob, i) ^ Marshal.ReadByte(password, i);
            samePassword &= difference == 0;
            if (report is not null)
            {
                report["preparedCredentialType"] = type;
                report["preparedBlobBytes"] = bytes;
                report["preparedUsernameMatches"] = sameUser;
                report["preparedPasswordMatches"] = samePassword;
            }
            // EX2 uses CRED_TYPE_DOMAIN_EXTENDED (6), not the legacy password type (2).
            return type is 2 or 6 && sameUser && samePassword;
        }
        finally
        {
            if (blob != IntPtr.Zero)
            {
                for (int i = 0; i < bytes; i++) Marshal.WriteByte(blob, i, 0);
                SspiLocalFree(blob);
            }
            if (name != IntPtr.Zero) SspiLocalFree(name);
            if (target != IntPtr.Zero) SspiLocalFree(target);
        }
    }

    private static bool RoundTrip(uint flags, IntPtr packed, uint bytes, string expectedUser, IntPtr expectedPassword, int expectedChars)
    {
        uint userChars = 0, domainChars = 0, passwordChars = 0;
        bool sized = CredUnPackAuthenticationBufferW(0, packed, bytes, IntPtr.Zero, ref userChars,
            IntPtr.Zero, ref domainChars, IntPtr.Zero, ref passwordChars);
        int error = Marshal.GetLastWin32Error();
        if (sized || error != 122 || userChars == 0 || passwordChars == 0
            || userChars > 32768 || domainChars > 32768 || passwordChars > 32768)
            throw new Win32Exception(error, "CredUnPackAuthenticationBufferW sizing");
        int userBytes = checked((int)userChars * 2);
        int domainBytes = checked((int)Math.Max(domainChars, 1) * 2);
        int passwordBytes = checked((int)passwordChars * 2);
        IntPtr storage = Marshal.AllocHGlobal(userBytes + domainBytes + passwordBytes);
        IntPtr domain = storage + userBytes;
        IntPtr password = domain + domainBytes;
        try
        {
            if (!CredUnPackAuthenticationBufferW(0, packed, bytes, storage, ref userChars,
                domain, ref domainChars, password, ref passwordChars))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CredUnPackAuthenticationBufferW");
            // EX2 unpacking returns encoded credentials, not plaintext. Repack
            // them in native memory and compare identities through SSPI.
            if (flags == 8) return RepackAndCompare(packed, storage, password);
            string actualUser = Marshal.PtrToStringUni(storage) ?? "";
            string actualDomain = domainChars == 0 ? "" : Marshal.PtrToStringUni(domain) ?? "";
            bool userMatches = actualUser == expectedUser || $"{actualDomain}\\{actualUser}" == expectedUser;
            if (passwordChars != expectedChars + 1) return false;
            int difference = 0;
            for (int i = 0; i <= expectedChars; i++)
                difference |= Marshal.ReadInt16(password, i * 2) ^ Marshal.ReadInt16(expectedPassword, i * 2);
            return userMatches && difference == 0;
        }
        finally { ZeroFree(storage, userBytes + domainBytes + passwordBytes); }
    }

    private static bool RepackAndCompare(IntPtr original, IntPtr encodedUser, IntPtr encodedPassword)
    {
        uint bytes = 0;
        bool sized = PackEncoded(8, encodedUser, encodedPassword, IntPtr.Zero, ref bytes);
        int error = Marshal.GetLastWin32Error();
        if (sized || error != 122 || bytes == 0 || bytes > 1024 * 1024)
            throw new Win32Exception(error, "EX2 repacking size");
        int allocated = checked((int)bytes);
        IntPtr copy = Marshal.AllocHGlobal(allocated);
        try
        {
            if (!PackEncoded(8, encodedUser, encodedPassword, copy, ref bytes))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EX2 repacking");
            int status = SspiValidateAuthIdentity(copy);
            if (status != 0) throw new COMException("Repacked EX2 validation", status);
            status = SspiCompareAuthIdentities(original, copy, out byte sameUser, out byte sameIdentity);
            if (status != 0) throw new COMException("SspiCompareAuthIdentities", status);
            return sameUser != 0 && sameIdentity != 0;
        }
        finally { ZeroFree(copy, allocated); }
    }

    private static void ZeroFree(IntPtr buffer, int bytes)
    {
        if (buffer == IntPtr.Zero) return;
        byte[] zeroes = new byte[4096];
        try
        {
            for (int offset = 0; offset < bytes; offset += zeroes.Length)
                Marshal.Copy(zeroes, 0, buffer + offset, Math.Min(zeroes.Length, bytes - offset));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
