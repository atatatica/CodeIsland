import Foundation
import Observation
import CodeIslandCore

@MainActor
@Observable
final class CodexQuotaMonitor {
    private(set) var snapshot: ClaudeQuotaSnapshot?
    @ObservationIgnored private var users = 0
    @ObservationIgnored private var loop: Task<Void, Never>?
    @ObservationIgnored private var lastScan: Date?

    var isStale: Bool {
        guard let snapshot else { return true }
        return Date().timeIntervalSince(snapshot.fetchedAt) > 300
    }

    func retain() {
        users += 1
        guard loop == nil else { return }
        loop = Task { [weak self] in
            // The panel is opening: keep cached values until its spring settles.
            try? await Task.sleep(for: .milliseconds(700))
            while !Task.isCancelled {
                if self?.lastScan.map({ Date().timeIntervalSince($0) >= 15 }) ?? true {
                    let roots = ConfigInstaller.codexHomes()
                    let sample = await Task.detached(priority: .utility) { CodexQuota.scan(roots: roots) }.value
                    guard !Task.isCancelled else { return }
                    self?.lastScan = Date()
                    if let sample, self?.snapshot != sample { self?.snapshot = sample }
                }
                try? await Task.sleep(for: .seconds(15))
            }
        }
    }

    func release() {
        users = max(0, users - 1)
        if users == 0 { loop?.cancel(); loop = nil }
    }

    deinit { loop?.cancel() }
}
