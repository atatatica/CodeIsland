import SwiftUI
import CodeIslandCore

struct CodexChipWidthKey: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = max(value, nextValue()) }
}

struct CodexQuotaChip: View {
    let monitor: CodexQuotaMonitor
    let kind: ClaudeQuotaLimit.Kind

    var body: some View {
        Group {
            if let snapshot = monitor.snapshot, let limit = snapshot.limit(kind) {
                QuotaChip(limit: limit, snapshot: snapshot, stale: monitor.isStale, showsPace: false)
            } else {
                Text("\(kind == .session ? "5h" : "W") —")
                    .font(.system(size: 10, weight: .medium, design: .monospaced))
                    .foregroundStyle(.white.opacity(0.5))
            }
        }
        .fixedSize()
        .help("Codex: использовано. Последний замер из локальной сессии.")
        .onAppear { monitor.retain() }
        .onDisappear { monitor.release() }
    }
}

/// Compact inline quota group; unknown or expired values never look like a fresh 0%.
struct ProviderQuotaRow: View {
    let title: String
    let snapshot: ClaudeQuotaSnapshot?
    let stale: Bool
    let unavailable: String
    let now: Date

    var body: some View {
        VStack(spacing: 4) {
            HStack(spacing: 5) {
                Text(title).foregroundStyle(.white.opacity(0.7))
                ForEach([ClaudeQuotaLimit.Kind.session, .weeklyAll], id: \.self) { kind in
                    let limit = snapshot?.limit(kind)
                    let value = limit?.shownPercent(now: now)
                    let color: Color = value.map { $0 >= 90 ? .red : ($0 >= 70 ? .orange : .green) } ?? .gray
                    HStack(spacing: 3) {
                        Text(kind == .session ? "5h" : "W").foregroundStyle(.white.opacity(0.45))
                        UsageTrack(fraction: value.map { $0 / 100 }, color: color)
                            .frame(width: 16, height: 5)
                        Text(ClaudeQuotaFormat.percent(value)).foregroundStyle(color)
                            .frame(width: 25, alignment: .trailing)
                    }
                    .help(windowHelp(limit, now: now))
                }
                if snapshot != nil && stale {
                    Image(systemName: "clock").foregroundStyle(.white.opacity(0.4))
                        .help("Последний замер: \(snapshot!.fetchedAt.formatted(date: .abbreviated, time: .shortened))")
                }
            }
            .font(.system(size: 9.5, weight: .medium, design: .monospaced))
            .lineLimit(1)
            .fixedSize(horizontal: true, vertical: false)
            .help(snapshot == nil ? "\(title): \(unavailable)" : "\(title): использованная доля лимита")

            Text("S \(resetCountdown(.session)) · W \(resetCountdown(.weeklyAll))")
                .font(.system(size: 8.5, weight: .regular, design: .monospaced))
                .foregroundStyle(.white.opacity(0.45))
                .lineLimit(1)
                .fixedSize()
                .help("До сброса: пятичасовой и недельный лимиты. После сброса ожидается новый замер.")
        }
    }

    private func resetCountdown(_ kind: ClaudeQuotaLimit.Kind) -> String {
        guard let date = snapshot?.limit(kind)?.resetsAt else { return "—" }
        return ClaudeQuotaFormat.countdown(until: date, now: now) ?? "—"
    }

    private func windowHelp(_ limit: ClaudeQuotaLimit?, now: Date) -> String {
        guard let limit else { return "\(title): нет данных" }
        if limit.hasReset(now: now) { return "\(title): окно сброшено, ожидается новый замер" }
        var text = "\(title): использовано \(ClaudeQuotaFormat.percent(limit.percent))"
        if let date = limit.resetsAt, let countdown = ClaudeQuotaFormat.countdown(until: date, now: now) {
            text += "; сброс через \(countdown)"
        }
        return text
    }
}

struct OpenRouterBalanceRow: View {
    let monitor: OpenRouterUsageMonitor

    var body: some View {
        HStack(spacing: 5) {
            Text("OpenRouter")
                .font(.system(size: 9.5, weight: .medium, design: .monospaced))
                .foregroundStyle(.white.opacity(0.7))
            OpenRouterChip(monitor: monitor, kind: .spentToday)
            OpenRouterChip(monitor: monitor, kind: .balance)
        }
        .fixedSize(horizontal: true, vertical: false)
        .help(monitor.tooltip)
        .accessibilityLabel("OpenRouter: за день \(OpenRouterUsageMonitor.money(monitor.spentToday)), баланс \(OpenRouterUsageMonitor.money(monitor.balance))")
    }
}

private struct UsageTrack: View {
    let fraction: Double?
    let color: Color

    var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(.white.opacity(0.14))
                if let fraction {
                    Capsule().fill(color).frame(width: geo.size.width * min(max(fraction, 0), 1))
                }
            }
        }
    }
}
