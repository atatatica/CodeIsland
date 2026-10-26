import Foundation
import SwiftUI

/// OpenRouter spend and balance for the collapsed bar while Hermes works.
///
/// - Balance: `/api/v1/credits` (`total_credits - total_usage`, whole account).
/// - Spent today: `total_usage` now minus `total_usage` at the start of the
///   local day. The baseline is kept in UserDefaults, so app restarts keep it.
///   `/api/v1/key` `usage_daily` counts only this Mac's key (the VPS has its
///   own), so it only seeds the baseline when there is none for today.
///
/// Fetches run only while a view asks for them (`retain` / `release`).
@MainActor
@Observable
final class OpenRouterUsageMonitor {
    private(set) var spentToday: Double?
    private(set) var balance: Double?
    private(set) var totalCredits: Double?
    /// This key's spend in OpenRouter's (UTC) day.
    private(set) var keyDaily: Double?
    private(set) var lastFetched: Date?
    private(set) var lastError: String?

    @ObservationIgnored private var users = 0
    @ObservationIgnored private var loop: Task<Void, Never>?
    @ObservationIgnored private var inFlight = false
    @ObservationIgnored private let defaults = UserDefaults.standard

    static let refreshInterval: TimeInterval = 60
    private static let baselineDayKey = "openRouterBaselineDay"
    private static let baselineUsageKey = "openRouterBaselineUsage"
    private static let lastTotalKey = "openRouterLastTotalUsage"
    private static let lastTotalAtKey = "openRouterLastTotalUsageAt"

    // MARK: Thresholds (Misha, 24.10.2026)

    enum Level { case good, ok, bad }

    static func spendLevel(_ spent: Double) -> Level {
        spent < 1 ? .good : (spent <= 3 ? .ok : .bad)
    }

    static func balanceLevel(_ balance: Double) -> Level {
        balance > 30 ? .good : (balance > 10 ? .ok : .bad)
    }

    /// Gemini mascot palette (GeminiView): blue, purple, rose.
    static func color(_ level: Level) -> Color {
        switch level {
        case .good: return Color(red: 0.278, green: 0.588, blue: 0.894)  // #4796E4
        case .ok:   return Color(red: 0.518, green: 0.478, blue: 0.808)  // #847ACE
        case .bad:  return Color(red: 0.765, green: 0.404, blue: 0.498)  // #C3677F
        }
    }

    static func money(_ value: Double?) -> String {
        guard let value else { return "—" }
        return value >= 100 ? String(format: "$%.0f", value) : String(format: "$%.2f", value)
    }

    var tooltip: String {
        var lines = ["OpenRouter"]
        lines.append("Потрачено сегодня: \(Self.money(spentToday)) (с 00:00, все ключи)")
        var balanceLine = "Баланс: \(Self.money(balance))"
        if let totalCredits { balanceLine += " из \(Self.money(totalCredits))" }
        lines.append(balanceLine)
        if let keyDaily { lines.append("Ключ Мака за сутки UTC: \(Self.money(keyDaily))") }
        if let lastError { lines.append("Ошибка: \(lastError)") }
        return lines.joined(separator: "\n")
    }

    var isStale: Bool {
        guard let lastFetched else { return true }
        return lastError != nil || Date().timeIntervalSince(lastFetched) > Self.refreshInterval * 5
    }

    // MARK: Lifecycle

    func retain() {
        users += 1
        guard loop == nil else { return }
        loop = Task { [weak self] in
            // Do not start file/network work in the panel's opening frames.
            try? await Task.sleep(for: .milliseconds(700))
            while !Task.isCancelled {
                await self?.refreshIfDue()
                try? await Task.sleep(for: .seconds(15))
            }
        }
    }

    func release() {
        users = max(0, users - 1)
        if users == 0 {
            loop?.cancel()
            loop = nil
        }
    }

    private func refreshIfDue() async {
        if inFlight { return }
        if let lastFetched, lastError == nil,
           Date().timeIntervalSince(lastFetched) < Self.refreshInterval { return }
        inFlight = true
        defer { inFlight = false }
        do {
            let result = try await Self.fetch()
            apply(result)
            lastError = nil
        } catch {
            lastError = (error as? FetchError)?.text ?? error.localizedDescription
        }
        lastFetched = Date()
    }

    private func apply(_ r: FetchResult) {
        totalCredits = r.totalCredits
        balance = r.totalCredits - r.totalUsage
        keyDaily = r.keyDaily

        let now = Date()
        let today = Self.dayString(now)
        if defaults.string(forKey: Self.baselineDayKey) != today {
            // Fetched just before midnight: that total is the day's start.
            // Otherwise this key's daily spend is the best guess.
            let lastTotal = defaults.object(forKey: Self.lastTotalKey) as? Double
            let lastAt = defaults.object(forKey: Self.lastTotalAtKey) as? Date
            let baseline: Double
            if let lastTotal, let lastAt, now.timeIntervalSince(lastAt) < 15 * 60,
               Self.dayString(lastAt) != today {
                baseline = lastTotal
            } else {
                baseline = r.totalUsage - (r.keyDaily ?? 0)
            }
            defaults.set(today, forKey: Self.baselineDayKey)
            defaults.set(baseline, forKey: Self.baselineUsageKey)
        }
        let baseline = defaults.double(forKey: Self.baselineUsageKey)
        spentToday = max(0, r.totalUsage - baseline)
        defaults.set(r.totalUsage, forKey: Self.lastTotalKey)
        defaults.set(now, forKey: Self.lastTotalAtKey)
    }

    private static func dayString(_ date: Date) -> String {
        let c = Calendar.current.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0)
    }

    // MARK: Network

    struct FetchResult {
        var totalCredits: Double
        var totalUsage: Double
        var keyDaily: Double?
    }

    struct FetchError: Error { let text: String }

    nonisolated static func fetch() async throws -> FetchResult {
        guard let key = apiKey() else { throw FetchError(text: "нет OPENROUTER_API_KEY в ~/.hermes/.env") }
        let credits = try await get("credits", key: key)
        guard let tc = number(credits["total_credits"]), let tu = number(credits["total_usage"]) else {
            throw FetchError(text: "нет total_credits/total_usage")
        }
        // The key's own daily spend only seeds a missing baseline: don't fail on it.
        let keyInfo = try? await get("key", key: key)
        return FetchResult(totalCredits: tc, totalUsage: tu, keyDaily: keyInfo.flatMap { number($0["usage_daily"]) })
    }

    nonisolated private static func get(_ path: String, key: String) async throws -> [String: Any] {
        var request = URLRequest(url: URL(string: "https://openrouter.ai/api/v1/\(path)")!)
        request.timeoutInterval = 20
        request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        let (data, response) = try await URLSession.shared.data(for: request)
        if let http = response as? HTTPURLResponse, http.statusCode != 200 {
            throw FetchError(text: "HTTP \(http.statusCode) /\(path)")
        }
        let json = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        return (json?["data"] as? [String: Any]) ?? [:]
    }

    nonisolated private static func number(_ value: Any?) -> Double? {
        (value as? NSNumber)?.doubleValue
    }

    /// The key Hermes itself uses: last uncommented OPENROUTER_API_KEY in
    /// `$HERMES_HOME/.env` (default `~/.hermes/.env`).
    nonisolated static func apiKey() -> String? {
        let home = ProcessInfo.processInfo.environment["HERMES_HOME"] ?? NSHomeDirectory() + "/.hermes"
        guard let text = try? String(contentsOfFile: home + "/.env", encoding: .utf8) else { return nil }
        var found: String?
        for raw in text.split(whereSeparator: \.isNewline) {
            var line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("export ") { line = String(line.dropFirst(7)).trimmingCharacters(in: .whitespaces) }
            guard line.hasPrefix("OPENROUTER_API_KEY=") else { continue }
            var value = String(line.dropFirst("OPENROUTER_API_KEY=".count)).trimmingCharacters(in: .whitespaces)
            if value.count >= 2, let f = value.first, f == "\"" || f == "'", value.last == f {
                value = String(value.dropFirst().dropLast())
            }
            if !value.isEmpty { found = value }
        }
        return found
    }
}
