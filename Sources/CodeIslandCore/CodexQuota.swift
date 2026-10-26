import Foundation
import CoreFoundation

/// Read-only quota samples already recorded by Codex. Never opens auth.json.
public enum CodexQuota {
    public static func latest(in data: Data) -> ClaudeQuotaSnapshot? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        for line in data.split(separator: 10).reversed() {
            guard let event = try? JSONSerialization.jsonObject(with: Data(line)) as? [String: Any],
                  event["type"] as? String == "event_msg",
                  let payload = event["payload"] as? [String: Any],
                  payload["type"] as? String == "token_count",
                  let quota = payload["rate_limits"] as? [String: Any],
                  (quota["limit_id"] as? String ?? "codex") == "codex",
                  let timestamp = event["timestamp"] as? String else { continue }
            let date = formatter.date(from: timestamp) ?? ISO8601DateFormatter().date(from: timestamp)
            guard let date else { continue }
            let limits: [ClaudeQuotaLimit] = ["primary", "secondary"].compactMap { key in
                guard let window = quota[key] as? [String: Any],
                      let percent = number(window["used_percent"]), percent.isFinite, percent >= 0,
                      let minutes = number(window["window_minutes"]),
                      minutes == 300 || minutes == 10080 else { return nil }
                return ClaudeQuotaLimit(
                    kind: minutes == 300 ? .session : .weeklyAll,
                    percent: percent,
                    resetsAt: number(window["resets_at"]).map(Date.init(timeIntervalSince1970:))
                )
            }
            if !limits.isEmpty { return ClaudeQuotaSnapshot(limits: limits, fetchedAt: date) }
        }
        return nil
    }

    private static func number(_ value: Any?) -> Double? {
        guard let n = value as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID() else { return nil }
        return n.doubleValue
    }

    /// Only recent rollout tails, bounded to 2 MiB each. Runs off the UI thread.
    public static func scan(roots: [String]) -> ClaudeQuotaSnapshot? {
        let fm = FileManager.default
        let keys: [URLResourceKey] = [.contentModificationDateKey, .isRegularFileKey]
        var files: [(URL, Date)] = []
        for root in roots {
            let sessions = URL(fileURLWithPath: root).appendingPathComponent("sessions")
            guard let enumerator = fm.enumerator(at: sessions, includingPropertiesForKeys: keys,
                                                options: [.skipsHiddenFiles]) else { continue }
            for case let url as URL in enumerator where url.pathExtension == "jsonl" {
                guard let values = try? url.resourceValues(forKeys: Set(keys)),
                      values.isRegularFile == true else { continue }
                files.append((url, values.contentModificationDate ?? .distantPast))
            }
        }
        var latest: ClaudeQuotaSnapshot?
        for (url, _) in files.sorted(by: { $0.1 > $1.1 }).prefix(12) {
            guard let handle = try? FileHandle(forReadingFrom: url) else { continue }
            defer { try? handle.close() }
            guard let size = try? handle.seekToEnd() else { continue }
            do {
                try handle.seek(toOffset: size > 2_097_152 ? size - 2_097_152 : 0)
                guard let data = try handle.readToEnd(), let sample = Self.latest(in: data) else { continue }
                if latest == nil || sample.fetchedAt > latest!.fetchedAt { latest = sample }
            } catch { continue }
        }
        return latest
    }
}
