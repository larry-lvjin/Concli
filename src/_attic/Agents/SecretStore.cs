using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeCodeGUI.Agents
{
    /// <summary>
    /// Reads API keys out of Windows Credential Manager so profiles.json only ever holds
    /// a reference. A plaintext key sitting in a config file is a real incident the first
    /// time this app gets handed to a colleague, and that always happens.
    ///
    /// Read-only on purpose: adding a secret is one documented command,
    ///   cmdkey /generic:Coda:kimi /user:api /pass:sk-...
    /// and keeping the write path out of the app means it can never overwrite a credential
    /// someone else owns.
    /// </summary>
    public static class SecretStore
    {
        /// <summary>Prefix for every target name this app reads, so it cannot collide.</summary>
        public const string TargetPrefix = "Coda:";

        private const int CRED_TYPE_GENERIC = 1;

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredFree")]
        private static extern void CredFree(IntPtr buffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        /// <summary>The secret stored under Coda:<paramref name="name"/>, or null if absent.</summary>
        public static string? Read(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (!CredRead(TargetPrefix + name.Trim(), CRED_TYPE_GENERIC, 0, out IntPtr handle))
                return null;

            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
                int size = (int)cred.CredentialBlobSize;
                if (size <= 0 || cred.CredentialBlob == IntPtr.Zero) return "";

                var bytes = new byte[size];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, size);

                // cmdkey writes UTF-16; other tools write UTF-8. An odd length cannot be
                // UTF-16, and a UTF-16 ASCII key is full of interleaved zero bytes.
                bool utf16 = size % 2 == 0 && size >= 2 && bytes[1] == 0;
                return (utf16 ? Encoding.Unicode : Encoding.UTF8).GetString(bytes).TrimEnd('\0');
            }
            finally { CredFree(handle); }
        }

        /// <summary>
        /// Expands "${credential:name}" and "${env:NAME}" in a profile's env value.
        /// Returns false and names the first thing it could not resolve, so the caller can
        /// refuse to launch instead of starting a CLI that will fail on its first request
        /// with an authentication error nobody connects back to a missing credential.
        /// </summary>
        public static bool TryExpand(string raw, out string value, out string? missing)
        {
            value = raw ?? "";
            missing = null;
            if (value.Length == 0 || !value.Contains("${")) return true;

            var sb = new StringBuilder();
            int i = 0;
            while (i < value.Length)
            {
                int open = value.IndexOf("${", i, StringComparison.Ordinal);
                if (open < 0) { sb.Append(value, i, value.Length - i); break; }

                int close = value.IndexOf('}', open + 2);
                if (close < 0) { sb.Append(value, i, value.Length - i); break; }

                sb.Append(value, i, open - i);
                string token = value.Substring(open + 2, close - open - 2).Trim();
                i = close + 1;

                string? resolved;
                if (token.StartsWith("credential:", StringComparison.OrdinalIgnoreCase))
                {
                    string key = token.Substring("credential:".Length).Trim();
                    resolved = Read(key);
                    if (resolved is null) { missing = TargetPrefix + key; value = ""; return false; }
                }
                else if (token.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
                {
                    string key = token.Substring("env:".Length).Trim();
                    resolved = Environment.GetEnvironmentVariable(key);
                    if (resolved is null) { missing = "%" + key + "%"; value = ""; return false; }
                }
                else
                {
                    // Not a token we own — leave it alone rather than eat it.
                    sb.Append("${").Append(token).Append('}');
                    continue;
                }

                sb.Append(resolved);
            }

            value = sb.ToString();
            return true;
        }
    }
}
