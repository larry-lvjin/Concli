using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeCodeGUI.Agents
{
    /// <summary>
    /// Turns a profile into the environment its child process gets. Order is load-bearing:
    /// clear first, then the private config dir, then the profile's own variables — a
    /// gateway token injected before ANTHROPIC_API_KEY is removed loses to the leftover key.
    ///
    /// The key only ever travels through this dictionary into CreateProcess. It must never
    /// be typed into the terminal as <c>$env:KEY='…'</c>: that lands in the scrollback, then
    /// in terminal.txt, then in the projection under the project directory, then in git.
    /// </summary>
    public static class ProfileEnvironment
    {
        /// <summary>
        /// The variable overrides for one launch. A null value means "remove this variable",
        /// which is not the same as an empty one — an empty ANTHROPIC_API_KEY is still a key
        /// as far as the CLI is concerned.
        /// </summary>
        public static bool TryBuild(
            AgentProfile profile,
            Gateway gateway,
            string? model,
            out Dictionary<string, string?>? env,
            out string? problem)
        {
            env = null;
            problem = null;

            var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            // 1. Clear. Inherited leftovers fight an endpoint override.
            foreach (string k in profile.ClearEnv)
                if (!string.IsNullOrWhiteSpace(k)) map[k.Trim()] = null;

            // 2. Private config dir. This is what keeps the official CLI and a gateway build
            //    from sharing credentials — and what moves where transcripts land.
            string? cfgVar = profile.ConfigDirVariable();
            string? cfgDir = profile.ExpandedConfigDir();
            if (cfgVar != null && cfgDir != null)
            {
                try { Directory.CreateDirectory(cfgDir); }
                catch (Exception ex)
                {
                    problem = $"建不出 {profile.Name} 的配置目录 {cfgDir}：{ex.Message}";
                    return false;
                }
                map[cfgVar] = cfgDir;
            }

            // 3. The profile's own variables, with every token resolved.
            foreach (var (k, raw) in profile.Env)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                if (!TryResolve(raw ?? "", gateway, model, out string v, out problem)) return false;
                map[k.Trim()] = v;
            }

            env = map.Count == 0 ? null : map;
            return true;
        }

        /// <summary>
        /// Expands {gateway.baseUrl} / {gateway.key} / {model} and then the
        /// ${credential:…} / ${env:…} forms <see cref="SecretStore"/> owns.
        /// </summary>
        private static bool TryResolve(
            string raw, Gateway gateway, string? model, out string value, out string? problem)
        {
            value = raw;
            problem = null;

            if (value.Contains("{model}"))
                value = value.Replace("{model}", model ?? "");

            if (value.Contains("{gateway.baseUrl}"))
            {
                if (string.IsNullOrWhiteSpace(gateway.BaseUrl))
                {
                    problem = $"gateway.json 里没有 baseUrl：{GatewayStore.FilePath}";
                    return false;
                }
                value = value.Replace("{gateway.baseUrl}", gateway.BaseUrl!.TrimEnd('/'));
            }

            if (value.Contains("{gateway.key}"))
            {
                if (!gateway.TryResolveKey(out string key, out string? missing))
                {
                    problem = $"网关 key 不存在：{missing} — " +
                              $"先执行  cmdkey /generic:{missing} /user:api /pass:<你的 key>";
                    return false;
                }
                value = value.Replace("{gateway.key}", key);
            }

            if (!SecretStore.TryExpand(value, out value, out string? missingRef))
            {
                problem = $"凭据不存在：{missingRef} — " +
                          $"先执行  cmdkey /generic:{missingRef} /user:api /pass:<你的 key>";
                return false;
            }

            return true;
        }
    }
}
