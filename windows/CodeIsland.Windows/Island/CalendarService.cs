using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Windows.Threading;
using System.Xml.Linq;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Color = System.Windows.Media.Color;

namespace CodeIsland.Windows.Island;

/// One event as the island shows it (the macOS AgendaEvent).
public sealed record AgendaEvent(string Id, string Title, DateTime Start, DateTime End, bool IsAllDay,
    string? Location, Color Color, string CalendarTitle)
{
    public bool IsOngoing(DateTime now) => !IsAllDay && Start <= now && now < End;

    public double Progress(DateTime now)
    {
        var total = (End - Start).TotalSeconds;
        return total <= 0 ? 0 : Math.Clamp((now - Start).TotalSeconds / total, 0, 1);
    }
}

public enum CalendarAccess { NotConfigured, Connecting, Granted, Failed }

/// iCloud calendars over CalDAV, read only: today and the next six days, as the
/// macOS island reads them from EventKit. The Apple ID app-specific password lives in
/// Credential Manager; nothing is written back to the calendars.
public sealed class CalendarService : IDisposable
{
    public const int DaysAhead = 7;
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace Cal = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace Apple = "http://apple.com/ns/ical/";
    private readonly DispatcherTimer _timer;
    private HttpClient? _http;
    private List<CalendarRef>? _calendars;
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;
    private bool _inFlight;

    private sealed record CalendarRef(Uri Url, string Title, Color Color);

    public CalendarAccess Access { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<AgendaEvent> Events { get; private set; } = [];
    public event EventHandler? Changed;

    public CalendarService(Dispatcher dispatcher)
    {
        Access = CredentialStore.Read(CredentialStore.CalendarTarget) is null ? CalendarAccess.NotConfigured : CalendarAccess.Connecting;
        // The collapsed bar's next-event line needs fresh data even when the island stays shut.
        _timer = new DispatcherTimer(TimeSpan.FromMinutes(10), DispatcherPriority.Background, (_, _) => _ = ReloadAsync(), dispatcher);
    }

    public void Start()
    {
        if (Access == CalendarAccess.NotConfigured) return;
        _timer.Start();
        _ = ReloadAsync();
    }

    /// The agenda opened: refetch unless the last fetch is under a minute old.
    public void Activate()
    {
        if (Access is CalendarAccess.Granted or CalendarAccess.Failed && DateTimeOffset.UtcNow - _lastFetch > TimeSpan.FromSeconds(60))
            _ = ReloadAsync();
    }

    /// Tests the login against iCloud before it is stored.
    public async Task<string?> ConnectAsync(string appleId, string appPassword)
    {
        try
        {
            var http = CreateClient(appleId, appPassword);
            var calendars = await DiscoverAsync(http).ConfigureAwait(true);
            if (calendars.Count == 0) return "No calendars found for this Apple ID.";
            CredentialStore.Write(CredentialStore.CalendarTarget, appleId, appPassword);
            _http?.Dispose();
            _http = http;
            _calendars = calendars;
            _timer.Start();
            await ReloadAsync().ConfigureAwait(true);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Apple rejected the login. Use an app-specific password from account.apple.com.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    public void Disconnect()
    {
        CredentialStore.Delete(CredentialStore.CalendarTarget);
        _http?.Dispose();
        _http = null;
        _calendars = null;
        Events = [];
        Access = CalendarAccess.NotConfigured;
        _timer.Stop();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<AgendaEvent> EventsOn(DateTime day)
    {
        var dayStart = day.Date;
        var dayEnd = dayStart.AddDays(1);
        return Events.Where(e => e.IsAllDay
                ? e.Start.Date <= dayStart && dayStart < e.End.Date
                : e.Start < dayEnd && e.End > dayStart)
            .ToArray();
    }

    /// The event running now, or else the next one to start.
    public AgendaEvent? FocusEvent(DateTime now)
    {
        var timed = Events.Where(e => !e.IsAllDay && e.End > now).ToArray();
        return timed.FirstOrDefault(e => e.IsOngoing(now)) ?? timed.FirstOrDefault(e => e.Start > now);
    }

    private async Task ReloadAsync()
    {
        if (_inFlight) return;
        _inFlight = true;
        try
        {
            if (_http is null)
            {
                if (CredentialStore.Read(CredentialStore.CalendarTarget) is not { } login)
                {
                    Access = CalendarAccess.NotConfigured;
                    return;
                }
                _http = CreateClient(login.User, login.Secret);
            }
            _calendars ??= await DiscoverAsync(_http).ConfigureAwait(true);
            var start = DateTime.Today;
            var end = start.AddDays(DaysAhead);
            var events = new List<AgendaEvent>();
            foreach (var calendar in _calendars)
                events.AddRange(await FetchAsync(_http, calendar, start, end).ConfigureAwait(true));
            Events = events
                .OrderBy(e => e.Start.Date).ThenBy(e => e.IsAllDay ? 0 : 1).ThenBy(e => e.Start)
                .ToArray();
            _lastFetch = DateTimeOffset.UtcNow;
            Access = CalendarAccess.Granted;
            LastError = null;
        }
        catch (UnauthorizedAccessException)
        {
            Access = CalendarAccess.Failed;
            LastError = "Apple rejected the saved login.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException or InvalidOperationException)
        {
            Access = Events.Count > 0 ? CalendarAccess.Granted : CalendarAccess.Failed;
            LastError = ex.Message;
            _calendars = null;
        }
        finally
        {
            _inFlight = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static HttpClient CreateClient(string user, string password)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CodeIsland-Windows/1.0");
        return http;
    }

    private static async Task<List<CalendarRef>> DiscoverAsync(HttpClient http)
    {
        var root = new Uri("https://caldav.icloud.com/");
        var principalDoc = await DavAsync(http, "PROPFIND", root, "0",
            "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:current-user-principal/></d:prop></d:propfind>").ConfigureAwait(false);
        var principalHref = principalDoc.Descendants(Dav + "current-user-principal").Descendants(Dav + "href").FirstOrDefault()?.Value
            ?? throw new InvalidOperationException("iCloud did not return a principal.");
        var principal = new Uri(root, principalHref);

        var homeDoc = await DavAsync(http, "PROPFIND", principal, "0",
            "<d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><c:calendar-home-set/></d:prop></d:propfind>").ConfigureAwait(false);
        var homeHref = homeDoc.Descendants(Cal + "calendar-home-set").Descendants(Dav + "href").FirstOrDefault()?.Value
            ?? throw new InvalidOperationException("iCloud did not return a calendar home.");
        var home = new Uri(principal, homeHref);

        var listDoc = await DavAsync(http, "PROPFIND", home, "1",
            "<d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\" xmlns:a=\"http://apple.com/ns/ical/\">" +
            "<d:prop><d:displayname/><d:resourcetype/><a:calendar-color/><c:supported-calendar-component-set/></d:prop></d:propfind>").ConfigureAwait(false);
        var calendars = new List<CalendarRef>();
        foreach (var response in listDoc.Descendants(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value;
            var prop = response.Descendants(Dav + "prop").FirstOrDefault();
            if (href is null || prop is null) continue;
            if (prop.Element(Dav + "resourcetype")?.Element(Cal + "calendar") is null) continue;
            var components = prop.Element(Cal + "supported-calendar-component-set")?.Elements(Cal + "comp")
                .Select(c => (string?)c.Attribute("name")).ToArray() ?? [];
            if (components.Length > 0 && !components.Contains("VEVENT")) continue;
            var title = prop.Element(Dav + "displayname")?.Value;
            calendars.Add(new CalendarRef(new Uri(home, href), string.IsNullOrWhiteSpace(title) ? "Calendar" : title,
                ParseColor(prop.Element(Apple + "calendar-color")?.Value)));
        }
        return calendars;
    }

    private static async Task<IEnumerable<AgendaEvent>> FetchAsync(HttpClient http, CalendarRef calendar, DateTime start, DateTime end)
    {
        var from = start.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var to = end.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var doc = await DavAsync(http, "REPORT", calendar.Url, "1",
            "<c:calendar-query xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">" +
            "<d:prop><d:getetag/><c:calendar-data/></d:prop>" +
            "<c:filter><c:comp-filter name=\"VCALENDAR\"><c:comp-filter name=\"VEVENT\">" +
            $"<c:time-range start=\"{from}\" end=\"{to}\"/></c:comp-filter></c:comp-filter></c:filter></c:calendar-query>").ConfigureAwait(false);

        var result = new List<AgendaEvent>();
        foreach (var data in doc.Descendants(Cal + "calendar-data"))
        {
            Ical.Net.Calendar? ics;
            try { ics = Ical.Net.Calendar.Load(data.Value); }
            catch (Exception) { continue; }
            if (ics is null) continue;
            foreach (var occurrence in ics.GetOccurrences(new CalDateTime(start.AddDays(-1)), new CalDateTime(end)))
            {
                if (occurrence.Source is not CalendarEvent ev) continue;
                if (string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
                var period = occurrence.Period;
                var allDay = ev.IsAllDay;
                var s = allDay ? period.StartTime.Date : period.StartTime.AsUtc.ToLocalTime();
                var e = period.EndTime is { } endTime
                    ? (allDay ? endTime.Date : endTime.AsUtc.ToLocalTime())
                    : s + (period.Duration > TimeSpan.Zero ? period.Duration : allDay ? TimeSpan.FromDays(1) : TimeSpan.Zero);
                if (e <= start || s >= end) continue;
                result.Add(new AgendaEvent($"{ev.Uid}@{s:O}", string.IsNullOrWhiteSpace(ev.Summary) ? "—" : ev.Summary.Trim(),
                    s, e, allDay, string.IsNullOrWhiteSpace(ev.Location) ? null : ev.Location.Trim(), calendar.Color, calendar.Title));
            }
        }
        return result.DistinctBy(e => e.Id);
    }

    /// One WebDAV request; follows iCloud's partition redirects (pXX-caldav.icloud.com).
    private static async Task<XDocument> DavAsync(HttpClient http, string method, Uri url, string depth, string body)
    {
        for (var hop = 0; hop < 5; hop++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml")
            };
            request.Headers.Add("Depth", depth);
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException();
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            if (response.StatusCode != (HttpStatusCode)207 && !response.IsSuccessStatusCode)
                throw new HttpRequestException($"CalDAV {method} failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return XDocument.Parse(xml);
        }
        throw new HttpRequestException("Too many CalDAV redirects.");
    }

    /// "#RRGGBB" or Apple's "#RRGGBBAA".
    private static Color ParseColor(string? value)
    {
        var hex = value?.Trim().TrimStart('#');
        if (hex is { Length: >= 6 } && uint.TryParse(hex[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return Color.FromRgb(0x1B, 0xAD, 0xF8);
    }

    public void Dispose()
    {
        _timer.Stop();
        _http?.Dispose();
    }
}
