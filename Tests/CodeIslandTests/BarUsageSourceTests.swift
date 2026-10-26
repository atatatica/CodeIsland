import XCTest
import CodeIslandCore
@testable import CodeIsland

@MainActor
final class BarUsageSourceTests: XCTestCase {
    func testLastIdleAgentSelectsItsOwnQuota() {
        let state = AppState()
        var claude = SessionSnapshot()
        claude.source = "claude"
        claude.status = .idle
        claude.lastActivity = Date(timeIntervalSince1970: 100)
        var codex = claude
        codex.source = "codex"
        codex.lastActivity = Date(timeIntervalSince1970: 200)
        state.sessions = ["claude": claude, "codex": codex]
        state.activeSessionId = "claude"
        XCTAssertTrue(state.codexOnBar)
        XCTAssertFalse(state.hermesOnBar)
        state.sessions["claude"]?.lastActivity = Date(timeIntervalSince1970: 300)
        XCTAssertFalse(state.codexOnBar)
    }

    func testDisplayedWorkingAgentWinsOverMoreRecentIdleAgent() {
        let state = AppState()
        var hermes = SessionSnapshot()
        hermes.source = "hermes"
        hermes.status = .running
        hermes.lastActivity = Date(timeIntervalSince1970: 100)
        var codex = hermes
        codex.source = "codex"
        codex.status = .idle
        codex.lastActivity = Date(timeIntervalSince1970: 200)
        state.sessions = ["hermes": hermes, "codex": codex]
        state.activeSessionId = "hermes"
        XCTAssertTrue(state.hermesOnBar)
        XCTAssertFalse(state.codexOnBar)
    }
}
