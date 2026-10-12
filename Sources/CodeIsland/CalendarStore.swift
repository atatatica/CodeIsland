import AppKit
import EventKit
import Observation

/// One calendar event as the island shows it — a value copy of `EKEvent`, so
/// views never hold EventKit objects that can go stale after a store change.
struct AgendaEvent: Identifiable, Equatable {
    let id: String
    let title: String
    let start: Date
    let end: Date
    let isAllDay: Bool
    let location: String?
    let color: NSColor
    let calendarTitle: String

    func isOngoing(at now: Date) -> Bool { !isAllDay && start <= now && now < end }

    /// 0…1 through the event, for the progress bar of the one running now.
    func progress(at now: Date) -> Double {
        let total = end.timeIntervalSince(start)
        guard total > 0 else { return 0 }
        return min(max(now.timeIntervalSince(start) / total, 0), 1)
    }
}

/// Reads today's and the coming days' events from Calendar (EventKit).
///
/// Access is asked for only once the user turns the calendar on and opens the
/// island — never at launch — and every fetch is local: EventKit reads the
/// calendars macOS already syncs, nothing goes over the network.
@MainActor
@Observable
final class CalendarStore {
    static let shared = CalendarStore()

    enum Access: Equatable {
        case unknown, granted, denied
    }

    /// How many days ahead the agenda and the week strip cover.
    static let daysAhead = 7

    private(set) var access: Access = .unknown
    /// Events from the start of today to the end of `daysAhead`, sorted.
    private(set) var events: [AgendaEvent] = []
    /// The expanded session list is showing the agenda instead (header
    /// toggle). Reset whenever the island collapses.
    var agendaInList = false

    @ObservationIgnored private let store = EKEventStore()
    @ObservationIgnored private var changeObserver: NSObjectProtocol?
    @ObservationIgnored private var dayObserver: NSObjectProtocol?
    @ObservationIgnored private var lastFetch: Date = .distantPast

    private init() {
        access = Self.currentAccess()
        if access == .granted { startObserving() }
    }

    private static func currentAccess() -> Access {
        switch EKEventStore.authorizationStatus(for: .event) {
        case .fullAccess, .authorized: return .granted
        case .notDetermined: return .unknown
        default: return .denied
        }
    }

    /// Called when the agenda appears: asks for access the first time, then
    /// refetches unless the last fetch is fresh (EventKit change notifications
    /// cover edits in between).
    func activate() {
        switch access {
        case .unknown:
            requestAccess()
        case .granted:
            if Date().timeIntervalSince(lastFetch) > 60 { reload() }
        case .denied:
            // The user may have flipped it in System Settings meanwhile.
            let now = Self.currentAccess()
            if now != access {
                access = now
                if now == .granted { startObserving(); reload() }
            }
        }
    }

    func requestAccess() {
        store.requestFullAccessToEvents { [weak self] granted, _ in
            Task { @MainActor in
                guard let self else { return }
                self.access = granted ? .granted : .denied
                if granted {
                    self.startObserving()
                    self.reload()
                }
            }
        }
    }

    func openPrivacySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Calendars") {
            NSWorkspace.shared.open(url)
        }
    }

    /// Opens Calendar.app on the event's day.
    func open(_ event: AgendaEvent) {
        let c = Calendar.current.dateComponents([.year, .month, .day], from: event.start)
        // Built field by field: parsing a date string in AppleScript depends
        // on the user's locale.
        let script = """
        set d to current date
        set day of d to 1
        set year of d to \(c.year ?? 2000)
        set month of d to \(c.month ?? 1)
        set day of d to \(c.day ?? 1)
        tell application "Calendar"
            activate
            switch view to day view
            view calendar at d
        end tell
        """
        DispatchQueue.global(qos: .userInitiated).async {
            var error: NSDictionary?
            NSAppleScript(source: script)?.executeAndReturnError(&error)
            if error != nil {
                DispatchQueue.main.async {
                    NSWorkspace.shared.open(URL(fileURLWithPath: "/System/Applications/Calendar.app"))
                }
            }
        }
    }

    func reload() {
        guard access == .granted else { return }
        let cal = Calendar.current
        let startOfToday = cal.startOfDay(for: Date())
        guard let end = cal.date(byAdding: .day, value: Self.daysAhead, to: startOfToday) else { return }
        let predicate = store.predicateForEvents(withStart: startOfToday, end: end, calendars: nil)
        let fetched = store.events(matching: predicate)
            .filter { $0.status != .canceled }
            .map { e in
                AgendaEvent(
                    id: (e.eventIdentifier ?? UUID().uuidString) + "@\(e.startDate.timeIntervalSince1970)",
                    title: e.title?.isEmpty == false ? e.title! : "—",
                    start: e.startDate,
                    end: e.endDate,
                    isAllDay: e.isAllDay,
                    location: e.location?.isEmpty == false ? e.location : nil,
                    color: e.calendar.map { NSColor(cgColor: $0.cgColor) ?? .systemBlue } ?? .systemBlue,
                    calendarTitle: e.calendar?.title ?? ""
                )
            }
            .sorted {
                (cal.startOfDay(for: $0.start), $0.isAllDay ? 0 : 1, $0.start)
                    < (cal.startOfDay(for: $1.start), $1.isAllDay ? 0 : 1, $1.start)
            }
        lastFetch = Date()
        if fetched != events { events = fetched }
    }

    private func startObserving() {
        guard changeObserver == nil else { return }
        changeObserver = NotificationCenter.default.addObserver(
            forName: .EKEventStoreChanged, object: store, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.reload() }
        }
        // Midnight: today's list rolls over.
        dayObserver = NotificationCenter.default.addObserver(
            forName: .NSCalendarDayChanged, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.reload() }
        }
    }

    // MARK: - Queries

    func events(on day: Date) -> [AgendaEvent] {
        let cal = Calendar.current
        let dayStart = cal.startOfDay(for: day)
        return events.filter { e in
            // All-day events span [start, end); show on every day they cover.
            if e.isAllDay { return e.start <= dayStart && dayStart < e.end }
            // Timed: starts that day, or started earlier and runs into it.
            return cal.isDate(e.start, inSameDayAs: day) || (e.start < dayStart && e.end > dayStart)
        }
    }

    /// The event running now, or else the next one still to start today or later.
    func focusEvent(at now: Date) -> AgendaEvent? {
        let timed = events.filter { !$0.isAllDay && $0.end > now }
        return timed.first { $0.isOngoing(at: now) } ?? timed.first { $0.start > now }
    }
}
