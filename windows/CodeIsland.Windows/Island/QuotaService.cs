using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Threading;

namespace CodeIsland.Windows.Island;

public enum QuotaKind { Session, WeeklyAll, WeeklyScoped }

public sealed record QuotaLimit(QuotaKind Kind, double Percent, string Severity, DateTimeOffset? ResetsAt, string? ScopeLabel)
{
    public double WindowSeconds => Kind == QuotaKind.Session ? 5 * 3600 : 7 * 86400;
    public bool HasReset(DateTimeOffset now) => ResetsAt is { } r && r <= now;

    public System.Windows.Media.Color Color
    {
        get
        {
            if (Percent >= 100) return IslandStyle.QuotaCritical;
            return Severity.ToLowerInvariant() switch
            {
                "normal" or "" => Percent >= 80 ? IslandStyle.QuotaWarning : IslandStyle.QuotaNormal,
                "warning" or "warn" or "elevated" => IslandStyle.QuotaWarning,
                _ => IslandStyle.QuotaCritical
            };
        }
    }
}

public sealed record QuotaSnapshot(IReadOnlyList<QuotaLimit> Limits, DateTimeOffset FetchedAt)
{
    public QuotaLimit? Get(QuotaKind kind) => Limits.FirstOrDefault(l => l.Kind == kind);

    /// The /api/oauth/usage response: the normalised limits[] array, else the legacy keys.
    public static QuotaSnapshot? Parse(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var limits = new List<QuotaLimit>();
        if (root.TryGetProperty("limits", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var kind = Str(item, "kind") switch
                {
                    "session" => QuotaKind.Session,
                    "weekly_all" => QuotaKind.WeeklyAll,
                    "weekly_scoped" => QuotaKind.WeeklyScoped,
                    _ => (QuotaKind?)null
                };
                if (kind is null || Num(item, "percent") is not { } percent) continue;
                string? scope = null;
                if (item.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.Object
                    && s.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
                    scope = Str(model, "display_name") ?? Str(model, "id");
                limits.Add(new QuotaLimit(kind.Value, percent, Str(item, "severity") ?? "normal", Date(item, "resets_at"), scope));
            }
        }
        if (limits.Count == 0)
        {
            void Legacy(string key, QuotaKind kind, string? label = null)
            {
                if (root.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Object && Num(item, "utilization") is { } u)
                    limits.Add(new QuotaLimit(kind, u, "normal", Date(item, "resets_at"), label));
            }
            Legacy("five_hour", QuotaKind.Session);
            Legacy("seven_day", QuotaKind.WeeklyAll);
            Legacy("seven_day_opus", QuotaKind.WeeklyScoped, "Opus");
            if (limits.All(l => l.Kind != QuotaKind.WeeklyScoped)) Legacy("seven_day_sonnet", QuotaKind.WeeklyScoped, "Sonnet");
        }
        var unique = limits.GroupBy(l => l.Kind).Select(g => g.First()).ToArray();
        return unique.Length == 0 ? null : new QuotaSnapshot(unique, now);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        } : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

public static class QuotaFormat
{
    public static string Percent(double value) => $"{(int)Math.Round(value)}%";

    /// "2d 4h" / "1h20m" / "45m"; null once the reset has passed.
    public static string? Countdown(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        var remaining = resetsAt - now;
        if (remaining.TotalSeconds < 1) return null;
        var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
        if (minutes < 60) return $"{minutes}m";
        var hours = minutes / 60;
        if (hours < 24) return minutes % 60 == 0 ? $"{hours}h" : $"{hours}h{minutes % 60:00}m";
        return hours % 24 == 0 ? $"{hours / 24}d" : $"{hours / 24}d {hours % 24}h";
    }
}

public enum QuotaState { Unknown, Ok, NeedsLogin, Error }

/// Polls Claude Code's plan usage with the CLI's own login token: read only, never refreshed.
/// Schedule from the macOS ClaudeQuotaScheduler: debounce 15 s, throttle 60 s,
/// at most every 600 s when idle, error backoff 60 → 900 s.
public sealed class QuotaService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _timer;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;
    private TimeSpan _backoff = TimeSpan.Zero;
    private bool _inFlight;
    private bool _dirty = true;
    private DateTimeOffset _dirtySince = DateTimeOffset.MinValue;
    public Func<bool> IsBusy { get; set; } = () => false;

    public QuotaSnapshot? Snapshot { get; private set; }
    public QuotaState State { get; private set; }
    public event EventHandler? Changed;

    public QuotaService(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
    }

    public void Start()
    {
        _timer.Start();
        Tick();
    }

    /// An agent turn ended or started: usage moved, fetch soon (debounced).
    public void Nudge()
    {
        if (!_dirty) _dirtySince = DateTimeOffset.UtcNow;
        _dirty = true;
    }

    private void Tick()
    {
        if (_inFlight) return;
        var now = DateTimeOffset.UtcNow;
        if (now < _nextAllowed) return;
        var sinceLast = now - _lastAttempt;
        var due = Snapshot is null && State != QuotaState.NeedsLogin
            || _dirty && now - _dirtySince >= TimeSpan.FromSeconds(15) && sinceLast >= TimeSpan.FromSeconds(60)
            || IsBusy() && sinceLast >= TimeSpan.FromSeconds(60)
            || sinceLast >= TimeSpan.FromSeconds(600)
            || Snapshot?.Limits.Any(l => l.HasReset(now)) == true && sinceLast >= TimeSpan.FromSeconds(60);
        if (due) _ = FetchAsync();
    }

    private async Task FetchAsync()
    {
        _inFlight = true;
        _lastAttempt = DateTimeOffset.UtcNow;
        _dirty = false;
        try
        {
            var token = ReadToken();
            if (token is null)
            {
                SetState(QuotaState.NeedsLogin);
                _nextAllowed = DateTimeOffset.UtcNow.AddSeconds(60);
                return;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await Http.SendAsync(request).ConfigureAwait(true);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Claude Code refreshes its token the next time it runs; until then say so.
                SetState(QuotaState.NeedsLogin);
                _nextAllowed = DateTimeOffset.UtcNow.AddSeconds(120);
                return;
            }
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            var snapshot = QuotaSnapshot.Parse(json, DateTimeOffset.UtcNow);
            if (snapshot is null) throw new InvalidDataException("No limits in the usage response.");
            Snapshot = snapshot;
            _backoff = TimeSpan.Zero;
            SetState(QuotaState.Ok);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or IOException)
        {
            _backoff = _backoff == TimeSpan.Zero ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(Math.Min(900, _backoff.TotalSeconds * 2));
            _nextAllowed = DateTimeOffset.UtcNow + _backoff;
            SetState(QuotaState.Error);
        }
        finally
        {
            _inFlight = false;
        }
    }

    private void SetState(QuotaState state)
    {
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string? ReadToken()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
            return oauth.TryGetProperty("accessToken", out var token) && token.ValueKind == JsonValueKind.String
                ? token.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _timer.Stop();
}
