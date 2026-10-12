import SwiftUI

// MARK: - Formatting

enum AgendaFormat {
    static func time(_ date: Date) -> String {
        date.formatted(date: .omitted, time: .shortened)
    }

    static func range(_ e: AgendaEvent) -> String {
        "\(time(e.start)) – \(time(e.end))"
    }

    /// "25m", "1h 10m", "2d".
    static func span(_ interval: TimeInterval) -> String {
        let minutes = max(1, Int((interval / 60).rounded(.up)))
        if minutes < 60 { return "\(minutes)m" }
        let hours = minutes / 60, rest = minutes % 60
        if hours < 24 { return rest == 0 ? "\(hours)h" : "\(hours)h \(rest)m" }
        return "\(hours / 24)d"
    }

    /// "in 25m" for an upcoming event, "25m left" for a running one.
    static func countdown(_ e: AgendaEvent, now: Date, l10n: L10n) -> String {
        if e.isOngoing(at: now) {
            return String(format: l10n["agenda_left"], span(e.end.timeIntervalSince(now)))
        }
        return String(format: l10n["agenda_in"], span(e.start.timeIntervalSince(now)))
    }

    static func weekdayShort(_ date: Date) -> String {
        date.formatted(.dateTime.weekday(.abbreviated))
    }

    static func weekdayLong(_ date: Date) -> String {
        date.formatted(.dateTime.weekday(.wide))
    }

    static func monthDay(_ date: Date) -> String {
        date.formatted(.dateTime.month(.wide).day())
    }
}

private enum AgendaStyle {
    static let primary = Color.white.opacity(0.92)
    static let secondary = Color.white.opacity(0.55)
    static let tertiary = Color.white.opacity(0.32)
    static let fill = Color.white.opacity(0.06)
    static let fillStrong = Color.white.opacity(0.1)
}

// MARK: - Agenda panel (idle island, opened on hover)

/// What the island shows on hover when no agent session is running: the date,
/// a week strip you scrub by hovering, and that day's events — the running or
/// next one up top as a hero card.
struct AgendaPanel: View {
    var appState: AppState
    let showClaudeQuota: Bool
    let showUsageStats: Bool

    @ObservedObject private var l10n = L10n.shared
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    private var store: CalendarStore { CalendarStore.shared }

    /// Day the list shows; nil = today. Set by hovering the week strip.
    @State private var hoveredDay: Date?
    @State private var appeared = false

    private var days: [Date] {
        let cal = Calendar.current
        let today = cal.startOfDay(for: Date())
        return (0..<CalendarStore.daysAhead).compactMap { cal.date(byAdding: .day, value: $0, to: today) }
    }

    var body: some View {
        VStack(spacing: 0) {
            agenda
            SessionListFooter(appState: appState, showUsageStats: showUsageStats, showClaudeQuota: showClaudeQuota)
                .stagger(4, appeared: appeared, reduceMotion: reduceMotion)
                .padding(.bottom, 2)
        }
        .onAppear {
            store.activate()
            withAnimation(reduceMotion ? nil : .spring(response: 0.45, dampingFraction: 0.8)) { appeared = true }
        }
        .onDisappear { appeared = false; hoveredDay = nil }
    }

    private var agenda: some View {
        TimelineView(.periodic(from: .now, by: 30)) { context in
            let now = context.date
            let selected = hoveredDay ?? Calendar.current.startOfDay(for: now)
            VStack(alignment: .leading, spacing: 10) {
                header(now: now, selected: selected)
                    .stagger(0, appeared: appeared, reduceMotion: reduceMotion)

                switch store.access {
                case .granted:
                    dayContent(selected: selected, now: now)
                        .id(selected)
                        .transition(.blurFade)
                case .unknown:
                    AgendaAccessCard(
                        icon: "calendar.badge.plus",
                        text: l10n["agenda_access_prompt"],
                        button: l10n["agenda_access_allow"]
                    ) { store.requestAccess() }
                    .stagger(1, appeared: appeared, reduceMotion: reduceMotion)
                case .denied:
                    AgendaAccessCard(
                        icon: "calendar.badge.exclamationmark",
                        text: l10n["agenda_access_denied"],
                        button: l10n["agenda_access_open_settings"]
                    ) { store.openPrivacySettings() }
                    .stagger(1, appeared: appeared, reduceMotion: reduceMotion)
                }
            }
            .padding(.horizontal, 14)
            .padding(.top, 10)
            .padding(.bottom, 6)
            .animation(reduceMotion ? nil : .spring(response: 0.32, dampingFraction: 0.86), value: selected)
        }
    }

    // MARK: Header — big date + week strip

    private func header(now: Date, selected: Date) -> some View {
        HStack(alignment: .center, spacing: 12) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(selected.formatted(.dateTime.day()))
                    .font(.system(size: 30, weight: .semibold, design: .rounded))
                    .foregroundStyle(AgendaStyle.primary)
                    .contentTransition(.numericText())
                VStack(alignment: .leading, spacing: 0) {
                    Text(AgendaFormat.weekdayLong(selected).capitalized)
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(AgendaStyle.primary)
                    Text(AgendaFormat.monthDay(selected))
                        .font(.system(size: 11))
                        .foregroundStyle(AgendaStyle.secondary)
                }
                .contentTransition(.interpolate)
            }
            Spacer(minLength: 8)
            WeekStrip(
                days: days,
                selected: selected,
                today: Calendar.current.startOfDay(for: now),
                eventColors: { day in
                    store.access == .granted ? store.events(on: day).prefix(3).map { Color(nsColor: $0.color) } : []
                },
                onHover: { day in hoveredDay = day }
            )
        }
    }

    // MARK: Day content — hero + list

    @ViewBuilder
    private func dayContent(selected: Date, now: Date) -> some View {
        let isToday = Calendar.current.isDate(selected, inSameDayAs: now)
        let all = store.events(on: selected)
        let allDay = all.filter(\.isAllDay)
        // Today: what's over drops out; the hero is what runs now or comes next.
        let timed = all.filter { !$0.isAllDay && (!isToday || $0.end > now) }
        let hero = isToday ? timed.first : nil
        let rest = Array(timed.dropFirst(hero == nil ? 0 : 1))

        VStack(alignment: .leading, spacing: 6) {
            if !allDay.isEmpty {
                AllDayRow(events: allDay)
                    .stagger(1, appeared: appeared, reduceMotion: reduceMotion)
            }
            if let hero {
                AgendaHeroCard(event: hero, now: now) { store.open(hero) }
                    .stagger(1, appeared: appeared, reduceMotion: reduceMotion)
            }
            ForEach(Array(rest.prefix(4).enumerated()), id: \.element.id) { index, event in
                AgendaRow(event: event, now: now) { store.open(event) }
                    .stagger(2 + index, appeared: appeared, reduceMotion: reduceMotion)
            }
            if rest.count > 4 {
                Text(String(format: l10n["agenda_more"], rest.count - 4))
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(AgendaStyle.tertiary)
                    .padding(.leading, 10)
            }
            if hero == nil && rest.isEmpty && allDay.isEmpty {
                AgendaEmptyState(text: isToday && !all.isEmpty ? l10n["agenda_done_today"] : l10n["agenda_free_day"])
                    .stagger(1, appeared: appeared, reduceMotion: reduceMotion)
            }
        }
    }
}

// MARK: - Week strip

private struct WeekStrip: View {
    let days: [Date]
    let selected: Date
    let today: Date
    let eventColors: (Date) -> [Color]
    let onHover: (Date?) -> Void

    @Namespace private var highlight
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: 2) {
            ForEach(days, id: \.self) { day in
                let isSelected = Calendar.current.isDate(day, inSameDayAs: selected)
                let isToday = Calendar.current.isDate(day, inSameDayAs: today)
                VStack(spacing: 2) {
                    Text(AgendaFormat.weekdayShort(day).prefix(2).uppercased())
                        .font(.system(size: 8.5, weight: .semibold))
                        .foregroundStyle(isSelected ? .black.opacity(0.55) : AgendaStyle.tertiary)
                    Text(day.formatted(.dateTime.day()))
                        .font(.system(size: 12, weight: isToday ? .bold : .medium, design: .rounded))
                        .foregroundStyle(isSelected ? .black : (isToday ? AgendaStyle.primary : AgendaStyle.secondary))
                    HStack(spacing: 2) {
                        ForEach(Array(eventColors(day).enumerated()), id: \.offset) { _, color in
                            Circle().fill(color).frame(width: 3.5, height: 3.5)
                        }
                    }
                    .frame(height: 4)
                }
                .frame(width: 30, height: 42)
                .background {
                    if isSelected {
                        RoundedRectangle(cornerRadius: 9, style: .continuous)
                            .fill(.white.opacity(0.9))
                            .matchedGeometryEffect(id: "day", in: highlight)
                    }
                }
                .contentShape(Rectangle())
                .onHover { inside in
                    if inside { onHover(day) }
                }
            }
        }
        .padding(2)
        .background(RoundedRectangle(cornerRadius: 11, style: .continuous).fill(AgendaStyle.fill))
        // Leaving the strip snaps back to today.
        .onHover { inside in if !inside { onHover(nil) } }
        .animation(reduceMotion ? nil : .spring(response: 0.28, dampingFraction: 0.82), value: selected)
    }
}

// MARK: - Cards

/// The event running now (with a live progress bar) or the next one up.
private struct AgendaHeroCard: View {
    let event: AgendaEvent
    let now: Date
    let onTap: () -> Void

    @ObservedObject private var l10n = L10n.shared
    @State private var hovered = false
    @State private var pulse = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var tint: Color { Color(nsColor: event.color) }
    private var ongoing: Bool { event.isOngoing(at: now) }

    var body: some View {
        HStack(alignment: .center, spacing: 10) {
            RoundedRectangle(cornerRadius: 2, style: .continuous)
                .fill(tint)
                .frame(width: 4)
                .shadow(color: tint.opacity(0.7), radius: pulse ? 6 : 2)

            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(ongoing ? l10n["agenda_now"] : l10n["agenda_next"])
                        .font(.system(size: 9.5, weight: .bold))
                        .tracking(0.6)
                        .foregroundStyle(tint)
                    if ongoing {
                        Circle().fill(tint).frame(width: 5, height: 5)
                            .opacity(pulse ? 1 : 0.35)
                    }
                }
                Text(event.title)
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(AgendaStyle.primary)
                    .lineLimit(1)
                HStack(spacing: 6) {
                    Text(AgendaFormat.range(event))
                    if let location = event.location {
                        Text("·")
                        Label(location, systemImage: "mappin")
                            .labelStyle(.titleAndIcon)
                            .lineLimit(1)
                    }
                }
                .font(.system(size: 11))
                .foregroundStyle(AgendaStyle.secondary)

                if ongoing {
                    GeometryReader { geo in
                        ZStack(alignment: .leading) {
                            Capsule().fill(.white.opacity(0.1))
                            Capsule().fill(tint)
                                .frame(width: max(4, geo.size.width * event.progress(at: now)))
                        }
                    }
                    .frame(height: 3)
                    .padding(.top, 3)
                }
            }

            Spacer(minLength: 6)

            Text(AgendaFormat.countdown(event, now: now, l10n: l10n))
                .font(.system(size: 12, weight: .semibold, design: .rounded))
                .monospacedDigit()
                .foregroundStyle(ongoing ? tint : AgendaStyle.primary)
                .contentTransition(.numericText())
                .padding(.horizontal, 8)
                .padding(.vertical, 4)
                .background(Capsule().fill(tint.opacity(0.16)))
        }
        .padding(10)
        .background(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(
                    LinearGradient(
                        colors: [tint.opacity(hovered ? 0.26 : 0.18), tint.opacity(0.04)],
                        startPoint: .leading, endPoint: .trailing
                    )
                )
        )
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(tint.opacity(0.25), lineWidth: 0.5)
        )
        .scaleEffect(hovered ? 1.01 : 1)
        .contentShape(Rectangle())
        .onHover { h in withAnimation(NotchAnimation.micro) { hovered = h } }
        .onTapGesture(perform: onTap)
        .help(event.calendarTitle)
        .onAppear {
            guard !reduceMotion else { return }
            withAnimation(.easeInOut(duration: 1.2).repeatForever(autoreverses: true)) { pulse = true }
        }
    }
}

private struct AgendaRow: View {
    let event: AgendaEvent
    let now: Date
    let onTap: () -> Void

    @State private var hovered = false

    var body: some View {
        HStack(spacing: 10) {
            Capsule().fill(Color(nsColor: event.color)).frame(width: 3, height: 26)
            VStack(alignment: .leading, spacing: 1) {
                Text(event.title)
                    .font(.system(size: 12.5, weight: .medium))
                    .foregroundStyle(AgendaStyle.primary)
                    .lineLimit(1)
                if let location = event.location {
                    Text(location)
                        .font(.system(size: 10.5))
                        .foregroundStyle(AgendaStyle.tertiary)
                        .lineLimit(1)
                }
            }
            Spacer(minLength: 6)
            Text(AgendaFormat.range(event))
                .font(.system(size: 11, weight: .medium, design: .rounded))
                .monospacedDigit()
                .foregroundStyle(AgendaStyle.secondary)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
        .background(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(hovered ? AgendaStyle.fillStrong : AgendaStyle.fill)
        )
        .contentShape(Rectangle())
        .onHover { h in withAnimation(NotchAnimation.micro) { hovered = h } }
        .onTapGesture(perform: onTap)
        .help(event.calendarTitle)
    }
}

private struct AllDayRow: View {
    let events: [AgendaEvent]

    var body: some View {
        HStack(spacing: 6) {
            ForEach(events.prefix(3)) { e in
                HStack(spacing: 5) {
                    Circle().fill(Color(nsColor: e.color)).frame(width: 6, height: 6)
                    Text(e.title).lineLimit(1)
                }
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(AgendaStyle.secondary)
                .padding(.horizontal, 8)
                .padding(.vertical, 4)
                .background(Capsule().fill(Color(nsColor: e.color).opacity(0.14)))
            }
            Spacer(minLength: 0)
        }
    }
}

private struct AgendaEmptyState: View {
    let text: String
    @State private var float = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "sparkles")
                .font(.system(size: 15, weight: .medium))
                .foregroundStyle(
                    LinearGradient(colors: [.yellow.opacity(0.9), .orange.opacity(0.8)], startPoint: .top, endPoint: .bottom)
                )
                .offset(y: float ? -1.5 : 1.5)
                .rotationEffect(.degrees(float ? 6 : -6))
            Text(text)
                .font(.system(size: 12.5, weight: .medium))
                .foregroundStyle(AgendaStyle.secondary)
            Spacer()
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 12)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(AgendaStyle.fill))
        .onAppear {
            guard !reduceMotion else { return }
            withAnimation(.easeInOut(duration: 1.8).repeatForever(autoreverses: true)) { float = true }
        }
    }
}

private struct AgendaAccessCard: View {
    let icon: String
    let text: String
    let button: String
    let action: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: icon)
                .font(.system(size: 16))
                .foregroundStyle(AgendaStyle.secondary)
            Text(text)
                .font(.system(size: 12))
                .foregroundStyle(AgendaStyle.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 6)
            Button(action: action) {
                Text(button)
                    .font(.system(size: 11.5, weight: .semibold))
                    .foregroundStyle(.black)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 5)
                    .background(Capsule().fill(.white.opacity(0.9)))
            }
            .buttonStyle(.plain)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(AgendaStyle.fill))
    }
}

// MARK: - Next-event strip (session list)

/// One line above the session list footer: the event running now or the next
/// one within the coming hours. Hidden when nothing is close.
struct NextEventStrip: View {
    @ObservedObject private var l10n = L10n.shared
    private var store: CalendarStore { CalendarStore.shared }
    /// Events further out than this aren't worth the room in the list.
    private static let horizon: TimeInterval = 6 * 3600

    var body: some View {
        TimelineView(.periodic(from: .now, by: 30)) { context in
            let now = context.date
            if store.access == .granted,
               let e = store.focusEvent(at: now),
               e.isOngoing(at: now) || e.start.timeIntervalSince(now) < Self.horizon {
                let tint = Color(nsColor: e.color)
                HStack(spacing: 8) {
                    Image(systemName: e.isOngoing(at: now) ? "record.circle" : "calendar")
                        .font(.system(size: 10.5, weight: .semibold))
                        .foregroundStyle(tint)
                    Text(AgendaFormat.time(e.start))
                        .font(.system(size: 11, weight: .semibold, design: .rounded))
                        .monospacedDigit()
                        .foregroundStyle(AgendaStyle.primary)
                    Text(e.title)
                        .font(.system(size: 11))
                        .foregroundStyle(AgendaStyle.secondary)
                        .lineLimit(1)
                    Spacer(minLength: 6)
                    Text(AgendaFormat.countdown(e, now: now, l10n: l10n))
                        .font(.system(size: 10.5, weight: .medium, design: .rounded))
                        .monospacedDigit()
                        .foregroundStyle(tint)
                        .contentTransition(.numericText())
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(tint.opacity(0.1)))
                .padding(.horizontal, 12)
                .padding(.top, 2)
                .contentShape(Rectangle())
                .onTapGesture { store.open(e) }
                .transition(.blurFade)
            }
        }
        .onAppear { store.activate() }
    }
}

// MARK: - Staggered entrance

private struct Stagger: ViewModifier {
    let index: Int
    let appeared: Bool
    let reduceMotion: Bool

    func body(content: Content) -> some View {
        content
            .opacity(appeared ? 1 : 0)
            .offset(y: appeared || reduceMotion ? 0 : -6)
            .blur(radius: appeared || reduceMotion ? 0 : 3)
            .animation(
                reduceMotion ? nil : .spring(response: 0.42, dampingFraction: 0.82).delay(Double(index) * 0.045),
                value: appeared
            )
    }
}

private extension View {
    func stagger(_ index: Int, appeared: Bool, reduceMotion: Bool) -> some View {
        modifier(Stagger(index: index, appeared: appeared, reduceMotion: reduceMotion))
    }
}

// MARK: - Header toggle (expanded session list ⇄ agenda)

/// Swaps the expanded session list for the agenda. Resting the pointer on it
/// switches — the island is hover-driven — and a click toggles too.
struct AgendaToggleButton: View {
    @ObservedObject private var l10n = L10n.shared
    @State private var hoverTask: Task<Void, Never>?
    private var store: CalendarStore { CalendarStore.shared }

    var body: some View {
        let showing = store.agendaInList
        NotchIconButton(
            icon: showing ? "rectangle.stack" : "calendar",
            tooltip: showing ? l10n["agenda_show_sessions"] : l10n["agenda_show_calendar"]
        ) {
            hoverTask?.cancel()
            withAnimation(NotchAnimation.open) { store.agendaInList.toggle() }
        }
        .contentTransition(.symbolEffect(.replace))
        .onHover { inside in
            hoverTask?.cancel()
            guard inside else { return }
            hoverTask = Task { @MainActor in
                try? await Task.sleep(nanoseconds: 280_000_000)
                guard !Task.isCancelled else { return }
                withAnimation(NotchAnimation.open) { store.agendaInList.toggle() }
            }
        }
    }
}
