using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace ClaudeCodeGUI.Agents
{
    /// <summary>
    /// One entry in the profile dropdown: which CLI to hand the shell to, against which
    /// endpoint, with which private config directory, and which models it may offer.
    ///
    /// The shell is always PowerShell — a profile never replaces it, it only decides what
    /// gets run *inside* it. That is the whole point of the PowerShell core: if the CLI
    /// dies or was never installed, you are still sitting at a usable prompt.
    /// </summary>
    public sealed class AgentProfile
    {
        /// <summary>Stable key. Stored on every session this profile produces.</summary>
        public string Id { get; set; } = "";

        /// <summary>What the dropdown shows.</summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// The command to run inside PowerShell, e.g. "claude". Null or empty means
        /// "just a shell" — the profile every other one degrades to.
        /// </summary>
        public string? Exe { get; set; }

        /// <summary>
        /// Arguments appended at launch. "{model}" is substituted; if no model is selected
        /// the whole argument string is dropped rather than passed with a hole in it.
        /// </summary>
        public string? LaunchArgs { get; set; }

        /// <summary>
        /// The CLI's private config directory for this profile, or null to use the CLI's own
        /// default. Supports %VARS%. Setting it is what lets the official CLI and a gateway
        /// build run side by side without trampling each other's auth — and it also moves
        /// where that CLI writes its transcripts, which is why <see cref="HistoryRoots"/>
        /// exists and why sessions carry a profile id.
        /// </summary>
        public string? ConfigDir { get; set; }

        /// <summary>
        /// Which variable carries <see cref="ConfigDir"/>. Left null it is inferred from the
        /// exe (claude → CLAUDE_CONFIG_DIR, codex → CODEX_HOME); CLIs rename these between
        /// versions, so it stays overridable from the file.
        /// </summary>
        public string? ConfigDirEnvVar { get; set; }

        /// <summary>
        /// Injected into the child process at creation. Values may reference a secret as
        /// "${credential:name}", another variable as "${env:NAME}", the gateway as
        /// "{gateway.baseUrl}" / "{gateway.key}", or the dropdown's model as "{model}".
        /// Never put a raw key here; profiles.json is a plain file you will eventually send
        /// to a colleague.
        /// </summary>
        public Dictionary<string, string> Env { get; set; } = new();

        /// <summary>
        /// Variables to *remove* from the inherited environment before <see cref="Env"/> is
        /// applied. Not the same as setting them empty: an empty ANTHROPIC_API_KEY is still
        /// a key as far as the CLI is concerned, and it will fight a gateway token. This
        /// machine has ANTHROPIC_API_KEY and ANTHROPIC_VERTEX_* set globally, so a gateway
        /// profile that does not clear them silently talks to the wrong endpoint.
        /// </summary>
        public List<string> ClearEnv { get; set; } = new();

        /// <summary>Model names offered for this profile. Empty means the dropdown is disabled.</summary>
        public List<string> Models { get; set; } = new();

        /// <summary>
        /// Take the model list from the gateway's cached /v1/models instead of
        /// <see cref="Models"/>. A gateway that gains a model should show up after one
        /// refresh, not after a rebuild.
        /// </summary>
        public bool ModelsFromGateway { get; set; }

        /// <summary>
        /// What to type into a *running* session to change model, e.g. "/model {model}".
        /// Null means the CLI cannot do it, and the model dropdown goes read-only once the
        /// session is up instead of pretending.
        /// </summary>
        public string? SwitchModelCommand { get; set; }

        /// <summary>How to resume one of this profile's sessions; "{id}" is substituted.</summary>
        public string? ResumeCommand { get; set; }

        // ── computed at load, not serialised ──────────────────

        [JsonIgnore] public bool IsPlainShell => string.IsNullOrWhiteSpace(Exe);

        [JsonIgnore] public bool CanSwitchModelLive => !string.IsNullOrWhiteSpace(SwitchModelCommand);

        /// <summary>Full path the exe resolved to, or null when it is not installed.</summary>
        [JsonIgnore] public string? ResolvedExePath { get; set; }

        [JsonIgnore] public bool IsAvailable => IsPlainShell || ResolvedExePath != null;

        /// <summary>Dropdown text. Says so out loud when the CLI is not on PATH.</summary>
        [JsonIgnore] public string DisplayName => IsAvailable ? Name : Name + "（未安装）";

        /// <summary>Models actually offered, after the gateway cache is taken into account.</summary>
        [JsonIgnore] public IReadOnlyList<string> EffectiveModels { get; set; } = Array.Empty<string>();

        /// <summary>The bare CLI name behind <see cref="Exe"/> ("claude", "codex"), or "".</summary>
        [JsonIgnore]
        public string CliName => Path.GetFileNameWithoutExtension(Exe ?? "").ToLowerInvariant();

        /// <summary>The config-dir variable this profile sets, or null if it sets none.</summary>
        public string? ConfigDirVariable()
        {
            if (string.IsNullOrWhiteSpace(ConfigDir)) return null;
            if (!string.IsNullOrWhiteSpace(ConfigDirEnvVar)) return ConfigDirEnvVar!.Trim();

            return CliName switch
            {
                "claude" => "CLAUDE_CONFIG_DIR",
                "codex"  => "CODEX_HOME",
                _        => null,
            };
        }

        /// <summary>The config dir with %VARS% expanded, or null.</summary>
        public string? ExpandedConfigDir() =>
            string.IsNullOrWhiteSpace(ConfigDir)
                ? null
                : Environment.ExpandEnvironmentVariables(ConfigDir.Trim());

        /// <summary>
        /// Where this profile's CLI writes its transcripts. Empty for a plain shell.
        /// A profile with its own config dir writes somewhere else entirely, so the indexer
        /// has to be told rather than assuming ~/.claude — that assumption is exactly how a
        /// gateway profile's history would go missing from the sidebar.
        /// </summary>
        public IEnumerable<string> HistoryRoots()
        {
            if (IsPlainShell) yield break;

            string exe = CliName;
            string? cfg = ExpandedConfigDir();

            if (cfg != null)
            {
                yield return exe == "codex"
                    ? Path.Combine(cfg, "sessions")
                    : Path.Combine(cfg, "projects");
                yield break;
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (exe == "codex") yield return Path.Combine(home, ".codex", "sessions");
            else if (exe == "claude") yield return Path.Combine(home, ".claude", "projects");
        }

        /// <summary>
        /// The command PowerShell runs on startup, or null for a bare shell. Built into the
        /// command line rather than written to stdin: a write right after Start() races the
        /// shell's own initialisation, and there is no reliable way to detect that the prompt
        /// is ready — only a guessed delay, which silently drops the command when wrong.
        /// </summary>
        public string? LaunchCommand(string? model)
        {
            if (IsPlainShell) return null;

            // The path Which() resolved, not the bare name: it is the file we proved exists,
            // and it pins the launch to that exact one. Quoted, and prefixed with & because a
            // quoted string on its own is just a string literal to PowerShell, not a command.
            // Falling back to the bare name when nothing resolved is deliberate — the shell's
            // own "not recognized" error is more use than us refusing to try.
            string cmd = ResolvedExePath is { Length: > 0 } full
                ? $"& \"{full}\""
                : Exe!.Trim();

            string args = (LaunchArgs ?? "").Trim();
            if (args.Length == 0) return cmd;

            if (args.Contains("{model}"))
            {
                if (string.IsNullOrWhiteSpace(model)) return cmd;   // no model picked: CLI default
                args = args.Replace("{model}", model);
            }
            return cmd + " " + args;
        }

        /// <summary>The keystrokes for an in-session model switch, or null if unsupported.</summary>
        public string? SwitchCommand(string? model) =>
            CanSwitchModelLive && !string.IsNullOrWhiteSpace(model)
                ? SwitchModelCommand!.Replace("{model}", model)
                : null;

        /// <summary>This profile's resume line for a session id, or null to use the adapter's.</summary>
        public string? ResumeCommandFor(string sessionId) =>
            string.IsNullOrWhiteSpace(ResumeCommand)
                ? null
                : ResumeCommand!.Replace("{id}", sessionId);

        public override string ToString() => DisplayName;
    }
}
