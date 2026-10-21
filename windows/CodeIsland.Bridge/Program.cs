using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIsland.Core;
using CodeIsland.Ipc;
using CodeIsland.Protocol;

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "serve";
if (mode == "serve")
{
    var machine = new SessionStateMachine();
    await using var server = CreateServer(machine);
    Console.WriteLine($"CodeIsland Bridge listening on {PipeEndpoint.Name()}");
    await server.RunAsync();
}
else if (mode == "send" && args.Length >= 2)
{
    var stdin = args.Contains("--stdin", StringComparer.OrdinalIgnoreCase);
    var source = GetOption(args, "--source");
    var eventName = GetOption(args, "--event");
    var userSid = GetOption(args, "--user-sid");
    var file = stdin ? null : args.Skip(1).FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal));
    // Agents write UTF-8 JSON; Console.In would decode it with the OEM code page (CP866 on a
    // Russian system) and turn every non-ASCII prompt and reply into box-drawing garbage.
    var input = stdin
        ? await new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false)).ReadToEndAsync()
        : await File.ReadAllTextAsync(file
        ?? throw new ArgumentException("send requires --stdin or an event JSON file."));
    var agentEvent = ParseEvent(input, source, eventName);
    if (agentEvent.ProcessId is null) agentEvent = agentEvent with { ProcessId = ParentProcess.Id() };
    var isClaude = string.Equals(source, "claude", StringComparison.OrdinalIgnoreCase);
    await using var client = new PipeClient(userSid);
    PipeMessage response;
    try
    {
        // The panel may not be running: a hook must never stall the agent waiting for it.
        await client.ConnectWithRetryAsync(isClaude ? 1 : 3, TimeSpan.FromMilliseconds(isClaude ? 400 : 1000),
            TimeSpan.FromMilliseconds(150));
        response = await client.SendAsync(
            new PipeMessage(PipeMessageType.Event, Guid.NewGuid().ToString("N"), Event: agentEvent),
            agentEvent.Type is AgentEventType.PermissionRequest or AgentEventType.Question
                ? TimeSpan.FromHours(8)
                : TimeSpan.FromSeconds(3));
    }
    catch (Exception) when (isClaude)
    {
        // No panel (or it went away): stay silent, Claude Code falls back to its own prompt.
        return 0;
    }
    var output = HookResponse(response, agentEvent, source);
    if (output.Length > 0) Console.WriteLine(output);
}
else if (mode == "self-test")
{
    var permissionEvent = new AgentEvent("permission-1", "session-1", AgentKind.Codex,
        AgentEventType.PermissionRequest, DateTimeOffset.UtcNow);
    foreach (var (action, behavior) in new[]
             {
                 (UserAction.Approve, "allow"),
                 (UserAction.AlwaysAllow, "always"),
                 (UserAction.Deny, "deny")
             })
    {
        var hookResponse = HookResponse(new PipeMessage(PipeMessageType.ActionResponse, "response-1",
            AckFor: permissionEvent.EventId, Action: action), permissionEvent, "codex");
        using var hookDocument = JsonDocument.Parse(hookResponse);
        var actual = hookDocument.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision")
            .GetProperty("behavior").GetString();
        if (actual != behavior) throw new InvalidOperationException($"Expected Codex behavior {behavior}, got {actual}.");
    }

    using var stop = new CancellationTokenSource();
    var machine = new SessionStateMachine();
    await using var server = CreateServer(machine);
    var serverTask = server.RunAsync(stop.Token);
    await using var client = new PipeClient();
    await client.ConnectWithRetryAsync(10, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(50));

    await ExpectAck(client, new PipeMessage(PipeMessageType.Hello, "hello-1"));
    var testEvent = new AgentEvent("event-1", "session-1", AgentKind.Codex,
        AgentEventType.SessionStart, DateTimeOffset.UtcNow, Environment.CurrentDirectory, "IPC self-test");
    var serializedEvent = JsonSerializer.Serialize(testEvent, CreateEventJsonOptions());
    testEvent = ParseEvent(serializedEvent, "codex", "SessionStart");
    var rawCodex = ParseEvent("""
        {"session_id":"codex-native-1","cwd":"C:\\work","tool_name":"shell","message":"running"}
        """, "codex", "PreToolUse");
    if (rawCodex.Agent != AgentKind.Codex || rawCodex.Type != AgentEventType.ToolStart
        || rawCodex.SessionId != "codex-native-1" || rawCodex.ToolName != "shell")
        throw new InvalidOperationException("Codex native hook payload normalization failed.");
    await ExpectAck(client, new PipeMessage(PipeMessageType.Event, "message-1", Event: testEvent));
    await ExpectAck(client, new PipeMessage(PipeMessageType.Heartbeat, "heartbeat-1"));

    if (!machine.TryGet("session-1", out var snapshot) || snapshot?.State != SessionState.Running)
        throw new InvalidOperationException("Event did not reach the session state machine.");
    Console.WriteLine("SELF-TEST PASS: handshake, event acknowledgement, heartbeat and state update verified.");
    await stop.CancelAsync();
    await serverTask;
}
else
{
    Console.Error.WriteLine("Usage: codeisland-bridge [serve | send <event.json> | send --stdin [--source codex] [--event SessionStart] [--user-sid SID] | self-test]");
    return 2;
}

return 0;

static PipeServer CreateServer(SessionStateMachine machine) => new((message, _) =>
{
    var response = message.Type switch
    {
        PipeMessageType.Hello or PipeMessageType.Heartbeat =>
            new PipeMessage(PipeMessageType.Ack, Guid.NewGuid().ToString("N"), AckFor: message.MessageId),
        PipeMessageType.Event when message.Event is not null =>
            new PipeMessage(PipeMessageType.Ack, Guid.NewGuid().ToString("N"), AckFor: message.MessageId),
        _ => new PipeMessage(PipeMessageType.Error, Guid.NewGuid().ToString("N"), Error: "Unsupported message.")
    };
    if (message.Type == PipeMessageType.Event && message.Event is not null) machine.Apply(message.Event);
    return ValueTask.FromResult<PipeMessage?>(response);
});

static async Task ExpectAck(PipeClient client, PipeMessage message)
{
    var response = await client.SendAsync(message, TimeSpan.FromSeconds(2));
    if (response.Type != PipeMessageType.Ack || response.AckFor != message.MessageId)
        throw new InvalidOperationException($"Expected ACK for {message.MessageId}.");
}

static AgentEvent ParseEvent(string json, string? source = null, string? eventName = null)
{
    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;
    if (root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("eventId", out _)
        && root.TryGetProperty("sessionId", out _)
        && root.TryGetProperty("type", out _))
    {
        return JsonSerializer.Deserialize<AgentEvent>(json, CreateEventJsonOptions())
            ?? throw new InvalidOperationException("Input event JSON is empty.");
    }
    return RawAgentEventNormalizer.Normalize(json, source, eventName);
}

static string? GetOption(string[] values, string name)
{
    var index = Array.FindIndex(values, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

static JsonSerializerOptions CreateEventJsonOptions() => new(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
};

static string HookResponse(PipeMessage response, AgentEvent agentEvent, string? source)
{
    if (string.Equals(source, "claude", StringComparison.OrdinalIgnoreCase))
        return ClaudeHookResponse(response, agentEvent);
    if (agentEvent.Type != AgentEventType.PermissionRequest
        || !string.Equals(source, "codex", StringComparison.OrdinalIgnoreCase)
        || response.Type != PipeMessageType.ActionResponse)
        return PipeJson.Serialize(response);

    var behavior = response.Action switch
    {
        UserAction.Approve => "allow",
        UserAction.AlwaysAllow => "always",
        _ => "deny"
    };
    return JsonSerializer.Serialize(new
    {
        hookSpecificOutput = new
        {
            hookEventName = "PermissionRequest",
            decision = new { behavior }
        }
    });
}

// Claude Code reads hook stdout: plain text from SessionStart/UserPromptSubmit lands in the
// model's context, so every event except an answered permission request prints nothing.
static string ClaudeHookResponse(PipeMessage response, AgentEvent agentEvent)
{
    if (agentEvent.Type != AgentEventType.PermissionRequest || response.Type != PipeMessageType.ActionResponse)
        return string.Empty;

    var decision = new System.Text.Json.Nodes.JsonObject();
    switch (response.Action)
    {
        case UserAction.Approve:
            decision["behavior"] = "allow";
            break;
        case UserAction.AlwaysAllow:
            decision["behavior"] = "allow";
            // "Always" applies the rule Claude Code itself suggested for this request.
            if (agentEvent.Payload is { ValueKind: JsonValueKind.Object } payload
                && payload.TryGetProperty("permission_suggestions", out var suggestions)
                && suggestions.ValueKind == JsonValueKind.Array && suggestions.GetArrayLength() > 0)
                decision["updatedPermissions"] = System.Text.Json.Nodes.JsonNode.Parse(suggestions.GetRawText());
            break;
        default:
            decision["behavior"] = "deny";
            decision["message"] = "Denied from CodeIsland.";
            break;
    }
    var root = new System.Text.Json.Nodes.JsonObject
    {
        ["hookSpecificOutput"] = new System.Text.Json.Nodes.JsonObject
        {
            ["hookEventName"] = "PermissionRequest",
            ["decision"] = decision
        }
    };
    return root.ToJsonString();
}
