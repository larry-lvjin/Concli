using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCodeGUI.Terminal;

namespace ClaudeCodeGUI.Agents
{
    /// <summary>The on-disk shape of profiles.json.</summary>
    public sealed class ProfileFile
    {
        /// <summary>Free text, written into the seed file so the syntax is discoverable.</summary>
        public string? Note { get; set; }

        public List<AgentProfile> Profiles { get; set; } = new();
    }

    /// <summary>
    /// Loads the profile list. Model names churn faster than releases do, so the list is a
    /// config file, never a switch statement — adding a provider must not need a rebuild.
    /// </summary>
    public static class AgentProfiles
    {
        public const string PathEnvVar = "CODA_PROFILES";

        /// <summary>
        /// Where this app's own config lives. Not the history index — that one is deliberately
        /// elsewhere (D:\MyFile\CODE\history) and configurable; this is small, machine-local,
        /// and has no business being synced.
        /// </summary>
        public static string ConfigDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeGUI");

        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            // Matches the camelCase used in the note and the docs; reading is
            // case-insensitive either way, so hand-edited files keep working.
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Otherwise the CJK display names come back as \uXXXX and the file is unreadable
            // in the editor the user is meant to edit it in.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string FilePath
        {
            get
            {
                string? configured = Environment.GetEnvironmentVariable(PathEnvVar);
                if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
                return Path.Combine(ConfigDir, "profiles.json");
            }
        }

        /// <summary>
        /// Reads the file, writing the seed first if it is missing. A malformed file is
        /// reported rather than swallowed — silently falling back to defaults would look
        /// exactly like "my edit did nothing".
        /// </summary>
        public static List<AgentProfile> Load(out string? error)
        {
            error = null;
            string path = FilePath;
            List<AgentProfile> list;

            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonSerializer.Serialize(Seed(), WriteOptions));
                }

                var file = JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(path), ReadOptions);
                list = file?.Profiles?.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList()
                       ?? new List<AgentProfile>();
                if (list.Count == 0)
                {
                    error = "profiles.json 里没有任何 profile，已回退到内置默认值";
                    list = Seed().Profiles;
                }
            }
            catch (Exception ex)
            {
                error = $"读不出 {path}：{ex.Message}（已回退到内置默认值）";
                list = Seed().Profiles;
            }

            Resolve(list);
            return list;
        }

        /// <summary>
        /// Fills in what the file cannot state: whether the CLI is actually installed, and
        /// which models to offer. Done once at load rather than per-dropdown-open, because
        /// probing PATH on every open would run on the UI thread.
        /// </summary>
        public static void Resolve(List<AgentProfile> list)
        {
            List<string>? gatewayModels = null;

            foreach (var p in list)
            {
                if (string.IsNullOrWhiteSpace(p.Id)) p.Id = p.Name;
                p.ResolvedExePath = p.IsPlainShell ? null : Shell.Which(p.Exe);

                if (p.ModelsFromGateway)
                {
                    gatewayModels ??= GatewayStore.LoadCachedModels();
                    // Falls back to the hand-written list when the gateway has never been
                    // queried, so a fresh install still has something in the dropdown.
                    p.EffectiveModels = gatewayModels.Count > 0 ? gatewayModels : p.Models;
                }
                else
                {
                    p.EffectiveModels = p.Models;
                }
            }
        }

        /// <summary>
        /// What ships on first run. Deliberately built from what is actually reachable on this
        /// box — a seed full of providers you do not have is worse than no seed. Every profile
        /// that talks to the gateway clears the inherited Anthropic variables first: this
        /// machine has ANTHROPIC_API_KEY and the ANTHROPIC_VERTEX_* set globally, and leaving
        /// them in place sends the request to the wrong endpoint with no visible symptom.
        /// </summary>
        public static ProfileFile Seed() => new()
        {
            Note =
                "exe 留空 = 只开 PowerShell。env 的值可以写 ${credential:名字}（读 Windows 凭据管理器里的 " +
                "Coda:名字，用 cmdkey /generic:Coda:名字 /user:api /pass:<key> 添加）、${env:变量名}、" +
                "{gateway.baseUrl}、{gateway.key}（都来自 gateway.json）；不要在这里写明文 key。" +
                "{model} 会被下拉框选中的模型替换。clearEnv 里的变量在注入前先从继承环境里删掉 —— " +
                "把它设成空字符串是不够的，空的 ANTHROPIC_API_KEY 对 CLI 来说仍然是一个 key。" +
                "configDir 给该 profile 一个私有配置目录（claude→CLAUDE_CONFIG_DIR，codex→CODEX_HOME），" +
                "这同时改变了它的历史落盘位置，所以会话记录里存的是 profile id 而不只是 CLI 名。" +
                "switchModelCommand 为空表示该 CLI 不支持会话内换模型，届时模型下拉会置灰。",
            Profiles = new List<AgentProfile>
            {
                new()
                {
                    Id = "powershell",
                    Name = "PowerShell",
                    Exe = null,
                },
                new()
                {
                    Id = "claude-official",
                    Name = "Claude Code（官方）",
                    Exe = "claude",
                    LaunchArgs = "--model {model}",
                    Models = new List<string> { "opus", "sonnet", "haiku" },
                    SwitchModelCommand = "/model {model}",
                    ResumeCommand = "claude --resume {id}",
                },
                new()
                {
                    Id = "claude-gw",
                    Name = "Claude Code（网关）",
                    Exe = "claude",
                    LaunchArgs = "--model {model}",
                    ConfigDir = @"%LOCALAPPDATA%\ClaudeCodeGUI\cli\claude-gw",
                    ClearEnv = new List<string>
                    {
                        "ANTHROPIC_API_KEY",
                        "CLAUDE_CODE_USE_VERTEX",
                        "CLAUDE_CODE_SKIP_VERTEX_AUTH",
                        "ANTHROPIC_VERTEX_PROJECT_ID",
                        "ANTHROPIC_VERTEX_BASE_URL",
                    },
                    Env = new Dictionary<string, string>
                    {
                        ["ANTHROPIC_BASE_URL"] = "{gateway.baseUrl}",
                        ["ANTHROPIC_AUTH_TOKEN"] = "{gateway.key}",
                    },
                    ModelsFromGateway = true,
                    Models = new List<string> { "opus", "sonnet", "haiku" },
                    SwitchModelCommand = "/model {model}",
                    ResumeCommand = "claude --resume {id}",
                },
                new()
                {
                    Id = "codex-gw",
                    Name = "Codex（网关）",
                    Exe = "codex",
                    LaunchArgs = "--model {model}",
                    ConfigDir = @"%LOCALAPPDATA%\ClaudeCodeGUI\cli\codex-gw",
                    ClearEnv = new List<string> { "OPENAI_API_KEY" },
                    Env = new Dictionary<string, string>
                    {
                        ["OPENAI_BASE_URL"] = "{gateway.baseUrl}/v1",
                        ["OPENAI_API_KEY"] = "{gateway.key}",
                    },
                    ModelsFromGateway = true,
                    Models = new List<string> { "gpt-5.6-sol" },
                    SwitchModelCommand = "/model {model}",
                    ResumeCommand = "codex resume {id}",
                },
            },
        };
    }
}
