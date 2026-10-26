import XCTest
@testable import CodeIslandCore

final class CodexQuotaTests: XCTestCase {
    private func event(percent: String = "6", timestamp: String = "2026-10-26T10:16:09.316Z",
                       bucket: String = "codex") -> String {
        """
        {"timestamp":"\(timestamp)","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"\(bucket)","primary":{"used_percent":\(percent),"window_minutes":300,"resets_at":1793026244},"secondary":{"used_percent":1,"window_minutes":10080,"resets_at":1793613044}}}}
        """
    }

    func testActualRolloutShapeAndResetTimes() throws {
        let sample = try XCTUnwrap(CodexQuota.latest(in: Data(event().utf8)))
        XCTAssertEqual(sample.limit(.session)?.percent, 6)
        XCTAssertEqual(sample.limit(.weeklyAll)?.percent, 1)
        XCTAssertEqual(sample.limit(.session)?.resetsAt, Date(timeIntervalSince1970: 1793026244))
        XCTAssertNil(sample.limit(.session)?.shownPercent(now: Date(timeIntervalSince1970: 1793026245)))
    }

    func testSkipsIncompleteTailAndEmptyUpdates() throws {
        let data = event(percent: "4") + "\n" + event(percent: "9") +
            "\n{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":null}}\n{\"timestamp\":"
        XCTAssertEqual(CodexQuota.latest(in: Data(data.utf8))?.limit(.session)?.percent, 9)
    }

    func testOtherBucketCannotOverwriteCodex() {
        let data = event(percent: "6") + "\n" + event(percent: "80", bucket: "codex_other")
        XCTAssertEqual(CodexQuota.latest(in: Data(data.utf8))?.limit(.session)?.percent, 6)
    }

    func testUnknownWindowsAndInvalidValuesDoNotBecomeZero() {
        let data = event(percent: "true").replacingOccurrences(of: "10080", with: "60")
        XCTAssertNil(CodexQuota.latest(in: Data(data.utf8)))
        XCTAssertNil(CodexQuota.latest(in: Data("not JSON".utf8)))
    }

    func testScanChoosesSampleTimestampRatherThanFileModificationTime() throws {
        let root = URL(fileURLWithPath: "/Users/melonyfelony13/hermes/tmp/scratch/2026-10-26-codex-quota-tests-\(UUID().uuidString)")
        let sessions = root.appendingPathComponent("sessions/2026/10/26")
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let newer = sessions.appendingPathComponent("newer.jsonl")
        let older = sessions.appendingPathComponent("older.jsonl")
        try event(percent: "9").write(to: newer, atomically: true, encoding: .utf8)
        try event(percent: "3", timestamp: "2026-10-26T09:00:00Z").write(to: older, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.modificationDate: Date().addingTimeInterval(60)], ofItemAtPath: older.path)
        XCTAssertEqual(CodexQuota.scan(roots: [root.path])?.limit(.session)?.percent, 9)
    }
}
