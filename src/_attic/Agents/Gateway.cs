using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeGUI.Agents
{
    /// <summary>
    /// The aggregating LLM gateway: one key behind which every CLI × model combination
    /// lives. Profiles reference it as "{gateway.baseUrl}" / "{gateway.key}" so the endpoint
    /// is written once instead of once per profile.
    /// </summary>
    public sealed class Gateway
    {
        public string? BaseUrl { get; set; }

        /// <summary>
        /// Where the key lives — "credential:llm-gateway" reads Coda:llm-gateway out of
        /// Windows Credential Manager. The key itself is never in this file.
        /// </summary>
        public string? KeyRef { get; set; }

        /// <summary>Shown in the error message when the key is missing or rejected.</summary>
        public string? ApplyKeyUrl { get; set; }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(KeyRef);

        /// <summary>The credential name behind <see cref="KeyRef"/>, or null if not a ref we own.</summary>
        public string? CredentialName()
        {
            string r = (KeyRef ?? "").Trim();
            return r.StartsWith("credential:", StringComparison.OrdinalIgnoreCase)
                ? r.Substring("credential:".Length).Trim()
                : null;
        }

        /// <summary>The decrypted key, or null with the credential target that is missing.</summary>
        public bool TryResolveKey(out string key, out string? missing)
        {
            key = "";
            missing = null;

            string? name = CredentialName();
            if (name == null)
            {
                missing = "gateway.json 的 keyRef 必须写成 credential:<名字>";
                return false;
            }

            string? v = SecretStore.Read(name);
            if (v == null) { missing = SecretStore.TargetPrefix + name; return false; }

            key = v;
            return true;
        }
    }

    /// <summary>Loads gateway.json and the cached model list it produced.</summary>
    public static class GatewayStore
    {
        public const string PathEnvVar = "CODA_GATEWAY";

        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string FilePath
        {
            get
            {
                string? configured = Environment.GetEnvironmentVariable(PathEnvVar);
                if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
                return Path.Combine(AgentProfiles.ConfigDir, "gateway.json");
            }
        }

        public static string ModelsCachePath => Path.Combine(AgentProfiles.ConfigDir, "models-cache.json");

        public static Gateway Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    Directory.CreateDirectory(AgentProfiles.ConfigDir);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(Seed(), WriteOptions));
                }
                return JsonSerializer.Deserialize<Gateway>(File.ReadAllText(FilePath), ReadOptions)
                       ?? new Gateway();
            }
            catch (Exception) { return new Gateway(); }
        }

        private static Gateway Seed() => new()
        {
            BaseUrl = "https://llm-proxy-v2-global.nicebuild.click",
            KeyRef = "credential:llm-gateway",
            ApplyKeyUrl = "https://vela-global.nicebuild.click/llm-proxy",
        };

        /// <summary>The model list from the last successful fetch. Empty when never fetched.</summary>
        public static List<string> LoadCachedModels()
        {
            try
            {
                if (!File.Exists(ModelsCachePath)) return new List<string>();
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ModelsCachePath))
                       ?? new List<string>();
            }
            catch (Exception) { return new List<string>(); }
        }

        public static void SaveCachedModels(IEnumerable<string> models)
        {
            Directory.CreateDirectory(AgentProfiles.ConfigDir);
            File.WriteAllText(ModelsCachePath,
                JsonSerializer.Serialize(models.ToList(), WriteOptions));
        }
    }

    /// <summary>Talks to the gateway's OpenAI-compatible /v1/models.</summary>
    public static class GatewayClient
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        public sealed record Result(List<string> Models, string? Error);

        /// <summary>
        /// Fetches the model list. Failures are split because the fix is different: a rejected
        /// key needs a new key, an unreachable host needs the network looked at, and telling
        /// the user "failed" for both sends them down the wrong path.
        /// </summary>
        public static async Task<Result> FetchModelsAsync(Gateway gw, CancellationToken ct = default)
        {
            if (!gw.IsConfigured)
                return new Result(new(), $"gateway.json 还没配置 baseUrl / keyRef：{GatewayStore.FilePath}");

            if (!gw.TryResolveKey(out string key, out string? missing))
                return new Result(new(),
                    $"网关 key 不存在：{missing} — 先执行  cmdkey /generic:{missing} /user:api /pass:<你的 key>" +
                    (string.IsNullOrWhiteSpace(gw.ApplyKeyUrl) ? "" : $"（申请地址 {gw.ApplyKeyUrl}）"));

            string url = gw.BaseUrl!.TrimEnd('/') + "/v1/models";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("x-api-key", key);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);

                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

                if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                    or System.Net.HttpStatusCode.Forbidden)
                    return new Result(new(),
                        "网关拒绝了这个 key（无效或未激活）" +
                        (string.IsNullOrWhiteSpace(gw.ApplyKeyUrl) ? "" : $" — 申请地址 {gw.ApplyKeyUrl}"));

                if (!resp.IsSuccessStatusCode)
                    return new Result(new(), $"网关返回 {(int)resp.StatusCode} {resp.ReasonPhrase}");

                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return new Result(ParseModels(body), null);
            }
            catch (TaskCanceledException)
            {
                return new Result(new(), $"网关超时：{url} — 检查网络或代理");
            }
            catch (HttpRequestException ex)
            {
                return new Result(new(), $"网关不可达：{ex.Message}");
            }
        }

        /// <summary>Accepts both the OpenAI {"data":[{"id":…}]} shape and a bare string array.</summary>
        private static List<string> ParseModels(string json)
        {
            var list = new List<string>();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array) array = root;
            else if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array) array = d;
            else if (root.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array) array = m;
            else return list;

            foreach (var item in array.EnumerateArray())
            {
                string? id = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.TryGetProperty("id", out var idv) ? idv.GetString()
                    : item.TryGetProperty("name", out var nv) ? nv.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(id)) list.Add(id!.Trim());
            }

            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }
}
