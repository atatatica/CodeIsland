using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using CodeIsland.Core;
using CodeIsland.Ipc;

namespace CodeIsland.Windows.Island;

public enum CardStatus { NeedsYou, Working, Thinking, Done, Idle, Stopped, Error }

/// What a session card shows — the macOS SessionSnapshot fields the island draws,
/// rebuilt from raw hook payloads (prompt, reply, tool detail, branch, host app).
public sealed class SessionInfo
{
    public required string SessionId { get; init; }
    public AgentKind Agent { get; set; }
    public string? Cwd { get; set; }
    public string Project => string.IsNullOrWhiteSpace(Cwd)
        ? Agent.ToString().ToLowerInvariant()
        : Path.GetFileName(Cwd.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Cwd;
    public string? Branch { get; set; }
    public string? Prompt { get; set; }
    public string? Reply { get; set; }
    public string? Tool { get; set; }
    public string? ToolDetail { get; set; }
    public CardStatus Status { get; set; } = CardStatus.Idle;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int? ProcessId { get; set; }
    public string? Host { get; set; }
    public string? TranscriptPath { get; set; }
    public int Subagents { get; set; }
    public string? PendingEventId { get; set; }
    public bool PendingIsQuestion { get; set; }
    public string? PendingTool { get; set; }
    public string? PendingDetail { get; set; }
    public bool PendingHasSuggestion { get; set; }
    public string ShortId => SessionId.Length > 4 ? SessionId[..4] : SessionId;
    public bool IsActive => Status is CardStatus.NeedsYou or CardStatus.Working or CardStatus.Thinking;
}

public sealed class SessionTracker
{
    private readonly DesktopSessionStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, SessionInfo> _sessions = new(StringComparer.Ordinal);
    public event EventHandler? Changed;

    public SessionTracker(DesktopSessionStore store, Dispatcher dispatcher)
    {
        _store = store;
        _dispatcher = dispatcher;
        _store.EventApplied += (_, e) => Apply(e);
        _store.Sessions.CollectionChanged += OnStoreChanged;
    }

    /// Newest first, as the island lists them; a session waiting on you goes to the top.
    public IReadOnlyList<SessionInfo> Sessions => _sessions.Values
        .OrderBy(s => s.Status == CardStatus.NeedsYou ? 0 : 1)
        .ThenByDescending(s => s.UpdatedAt)
        .ToArray();

    public int ActiveCount => _sessions.Values.Count(s => s.IsActive);
    public int TotalCount => _sessions.Count;
    public bool AnyNeedsYou => _sessions.Values.Any(s => s.Status == CardStatus.NeedsYou);

    /// The session the collapsed bar speaks for.
    public SessionInfo? Focus => Sessions.FirstOrDefault(s => s.IsActive) ?? Sessions.FirstOrDefault();

    public bool Resolve(SessionInfo session, UserAction action, string? text = null)
    {
        if (session.PendingEventId is not { } eventId) return false;
        if (!_store.Resolve(eventId, action, text)) return false;
        session.PendingEventId = null;
        session.Status = action == UserAction.Deny ? CardStatus.Thinking : CardStatus.Working;
        Raise();
        return true;
    }

    public void Dismiss(SessionInfo session)
    {
        _store.RemoveSession(session.SessionId);
        _sessions.Remove(session.SessionId);
        Raise();
    }

    private void OnStoreChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Sessions the store dropped (cleanup, eviction) leave the island too.
        var live = _store.Sessions.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
        var gone = _sessions.Keys.Where(id => !live.Contains(id)).ToArray();
        foreach (var id in gone) _sessions.Remove(id);
        foreach (var snapshot in _store.Sessions)
            if (_sessions.TryGetValue(snapshot.SessionId, out var info)) info.PendingEventId = snapshot.PendingEventId;
        if (gone.Length > 0) Raise();
    }

    private void Apply(AgentEvent e)
    {
        var payload = e.Payload is { ValueKind: JsonValueKind.Object } p ? p : (JsonElement?)null;
        var hook = Str(payload, "hook_event_name");

        if (hook == "SessionEnd")
        {
            // Claude Code closed: the macOS island drops the card at once.
            _sessions.Remove(e.SessionId);
            _dispatcher.BeginInvoke(() => _store.RemoveSession(e.SessionId));
            Raise();
            return;
        }

        if (!_sessions.TryGetValue(e.SessionId, out var s))
        {
            s = new SessionInfo { SessionId = e.SessionId, StartedAt = e.Timestamp };
            _sessions[e.SessionId] = s;
        }
        s.Agent = e.Agent == AgentKind.Unknown ? s.Agent : e.Agent;
        s.UpdatedAt = e.Timestamp;
        if (!string.IsNullOrWhiteSpace(e.WorkingDirectory) && s.Cwd != e.WorkingDirectory)
        {
            s.Cwd = e.WorkingDirectory;
            s.Branch = GitBranch(s.Cwd);
        }
        if (e.ProcessId is { } pid && s.ProcessId != pid)
        {
            s.ProcessId = pid;
            var host = ProcessTree.HostName(pid);
            if (host is not null) s.Host = host;
        }
        s.Host ??= e.TerminalKind;
        s.TranscriptPath = Str(payload, "transcript_path") ?? s.TranscriptPath;
        var snapshot = _store.Sessions.FirstOrDefault(x => x.SessionId == e.SessionId);
        s.PendingEventId = snapshot?.PendingEventId;

        switch (hook)
        {
            case "SessionStart":
                s.Status = CardStatus.Idle;
                break;
            case "UserPromptSubmit":
                s.Prompt = OneLine(Str(payload, "prompt") ?? e.Text);
                s.Reply = null;
                s.Tool = null;
                s.ToolDetail = null;
                s.Status = CardStatus.Thinking;
                break;
            case "PreToolUse":
                s.Tool = e.ToolName;
                s.ToolDetail = ToolDetail(payload);
                s.Status = CardStatus.Working;
                LoadReplyAsync(s);
                break;
            case "PostToolUse":
            case "PostToolUseFailure":
                s.Tool = null;
                s.ToolDetail = null;
                if (s.Status != CardStatus.NeedsYou) s.Status = CardStatus.Thinking;
                LoadReplyAsync(s);
                break;
            case "PermissionRequest":
                s.Status = CardStatus.NeedsYou;
                s.PendingIsQuestion = false;
                s.PendingTool = e.ToolName;
                s.PendingDetail = ToolDetail(payload);
                s.PendingHasSuggestion = payload?.TryGetProperty("permission_suggestions", out var sug) == true
                    && sug.ValueKind == JsonValueKind.Array && sug.GetArrayLength() > 0;
                break;
            case "Notification":
                // Claude Code's idle reminder arrives after Stop; it changes nothing on the card.
                break;
            case "Stop":
                s.Tool = null;
                s.ToolDetail = null;
                s.Status = CardStatus.Done;
                var last = Str(payload, "last_assistant_message");
                if (!string.IsNullOrWhiteSpace(last)) s.Reply = Flatten(last);
                else LoadReplyAsync(s);
                break;
            case "StopFailure":
                s.Status = CardStatus.Error;
                s.Tool = null;
                break;
            case "SubagentStart":
                s.Subagents++;
                break;
            case "SubagentStop":
                s.Subagents = Math.Max(0, s.Subagents - 1);
                break;
            case "PreCompact":
                s.Tool = "Compacting";
                s.ToolDetail = null;
                s.Status = CardStatus.Working;
                break;
            default:
                // Agents without Claude-style hook names: fall back to the normalised event.
                ApplyGeneric(s, e, snapshot);
                break;
        }
        Raise();
    }

    private static void ApplyGeneric(SessionInfo s, AgentEvent e, SessionSnapshot? snapshot)
    {
        switch (e.Type)
        {
            case AgentEventType.ToolStart:
                s.Tool = e.ToolName;
                s.ToolDetail = OneLine(e.Text);
                s.Status = CardStatus.Working;
                break;
            case AgentEventType.ToolEnd:
                s.Tool = null;
                s.Status = CardStatus.Thinking;
                break;
            case AgentEventType.PermissionRequest:
                s.Status = CardStatus.NeedsYou;
                s.PendingTool = e.ToolName;
                s.PendingDetail = OneLine(e.Text);
                break;
            case AgentEventType.Question:
                s.Status = CardStatus.NeedsYou;
                s.PendingIsQuestion = true;
                s.PendingDetail = OneLine(e.Text);
                break;
            case AgentEventType.SessionEnd:
                s.Status = CardStatus.Done;
                s.Tool = null;
                break;
            case AgentEventType.Error:
                s.Status = CardStatus.Error;
                break;
            case AgentEventType.Message when !string.IsNullOrWhiteSpace(e.Text):
                s.Reply = Flatten(e.Text);
                break;
        }
        if (snapshot?.LastMessage is { Length: > 0 } message && s.Reply is null) s.Reply = Flatten(message);
    }

    private void LoadReplyAsync(SessionInfo s)
    {
        var path = s.TranscriptPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        Task.Run(() => TranscriptReader.LastReply(path)).ContinueWith(task =>
        {
            if (task.Status != TaskStatus.RanToCompletion || task.Result is not { } reply) return;
            _dispatcher.BeginInvoke(() =>
            {
                if (!_sessions.ContainsKey(s.SessionId) || s.Reply == reply) return;
                s.Reply = reply;
                Raise();
            });
        }, TaskScheduler.Default);
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    private static string? Str(JsonElement? element, string name) =>
        element is { } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// The one line that says what a tool is doing: the command, the file, the pattern.
    private static string? ToolDetail(JsonElement? payload)
    {
        if (payload is not { } p || !p.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[] { "command", "file_path", "notebook_path", "path", "pattern", "url", "query", "description", "prompt" })
        {
            if (!input.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (key is "file_path" or "notebook_path" or "path") return ShortPath(text);
            return OneLine(text);
        }
        return null;
    }

    private static string ShortPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 3 ? string.Join('/', parts) : string.Join('/', parts[^3..]);
    }

    public static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = Regex.Replace(text, @"\s+", " ").Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    /// Markdown flattened to words for a one- or two-line preview.
    public static string? Flatten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Regex.Replace(text, @"```[\s\S]*?```", " ");
        t = Regex.Replace(t, @"[*_`#>|]+", "");
        t = Regex.Replace(t, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return OneLine(t);
    }

    public static string? GitBranch(string? cwd)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(cwd) ? null : new DirectoryInfo(cwd);
            for (var depth = 0; dir is not null && depth < 12; depth++, dir = dir.Parent)
            {
                var git = Path.Combine(dir.FullName, ".git");
                string? head = null;
                if (Directory.Exists(git)) head = Path.Combine(git, "HEAD");
                else if (File.Exists(git))
                {
                    var pointer = File.ReadAllText(git).Trim();
                    if (pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                    {
                        var gitDir = pointer[7..].Trim();
                        if (!Path.IsPathRooted(gitDir)) gitDir = Path.Combine(dir.FullName, gitDir);
                        head = Path.Combine(gitDir, "HEAD");
                    }
                }
                if (head is null || !File.Exists(head)) continue;
                var value = File.ReadAllText(head).Trim();
                return value.StartsWith("ref: refs/heads/", StringComparison.Ordinal) ? value[16..]
                    : value.Length >= 7 ? value[..7] : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }
}

/// Reads the newest assistant text from a Claude Code JSONL transcript.
public static class TranscriptReader
{
    public static string? LastReply(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var start = Math.Max(0, length - 512 * 1024);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "user" && IsRealPrompt(root)) return null;
                    if (type != "assistant" || !root.TryGetProperty("message", out var message)) continue;
                    if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                    var text = string.Join(" ", content.EnumerateArray()
                        .Where(c => c.TryGetProperty("type", out var ct) && ct.GetString() == "text")
                        .Select(c => c.TryGetProperty("text", out var tx) ? tx.GetString() : null)
                        .Where(x => !string.IsNullOrWhiteSpace(x)));
                    if (!string.IsNullOrWhiteSpace(text)) return SessionTracker.Flatten(text);
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// A user turn typed by a person, not a tool result fed back to the model.
    private static bool IsRealPrompt(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) return false;
        if (content.ValueKind == JsonValueKind.String) return true;
        return content.ValueKind == JsonValueKind.Array && content.EnumerateArray()
            .All(c => !(c.TryGetProperty("type", out var ct) && ct.GetString() == "tool_result"));
    }
}
