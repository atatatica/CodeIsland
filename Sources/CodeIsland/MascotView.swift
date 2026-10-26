import SwiftUI
import CodeIslandCore

// MARK: - Mascot Animation Speed Environment

private struct MascotSpeedKey: EnvironmentKey {
    static let defaultValue: Double = 1.0
}

extension EnvironmentValues {
    var mascotSpeed: Double {
        get { self[MascotSpeedKey.self] }
        set { self[MascotSpeedKey.self] = newValue }
    }
}

/// Routes a CLI source identifier to the correct pixel mascot view.
struct MascotView: View {
    let source: String
    let status: MascotAgentStatus
    var size: CGFloat = 27
    /// Expanded lists keep status poses without a live display-link per card.
    var animated: Bool = true
    @Environment(\.mascotAnimationsActive) private var parentAnimationsActive
    @AppStorage(SettingsKey.mascotSpeed) private var speedPct = SettingsDefaults.mascotSpeed
    @ObservedObject private var animationGate = MascotAnimationGate.shared

    var body: some View {
        let resolved = SessionSnapshot.normalizedSupportedSource(source) ?? source
        Group {
            switch resolved {
            case "codex":
                DexView(status: status, size: size)
            case "grok":
                GrokView(status: status, size: size)
            case "gemini", "google-antigravity":
                // Google Antigravity is Gemini-based — reuse the Gemini mascot.
                GeminiView(status: status, size: size)
            case "cursor", "cursor-cli":
                CursorView(status: status, size: size)
            case "trae", "traecn", "traecli":
                TraeView(status: status, size: size)
            case "copilot":
                CopilotView(status: status, size: size)
            case "qoder", "qoder-cli", "qoderwork":
                QoderView(status: status, size: size)
            case "droid":
                DroidView(status: status, size: size)
            case "codebuddy":
                BuddyView(status: status, size: size)
            case "codybuddycn":
                BuddyView(status: status, size: size)
            case "stepfun":
                StepFunView(status: status, size: size)
            case "opencode":
                OpenCodeView(status: status, size: size)
            case "mimo":
                MiMoView(status: status, size: size)
            case "qwen":
                QwenView(status: status, size: size)
            case "antigravity":
                AntiGravityView(status: status, size: size)
            case "workbuddy":
                WorkBuddyView(status: status, size: size)
            case "hermes":
                // Misha prefers the friendly Gemini mascot for Hermes (24.10.2026).
                GeminiView(status: status, size: size)
            case "openclaw":
                OpenClawView(status: status, size: size)
            case "kiro":
                KiroView(status: status, size: size)
            case "kimi":
                KimiView(status: status, size: size)
            case "pi", "omp":
                PiView(status: status, size: size)
            case "cline":
                ClineView(status: status, size: size)
            case "aiwork", "aiwork-cli":
                AiWorkView(status: status, size: size)
            case "minimax":
                MinimaxView(status: status, size: size)
            default:
                ClawdView(status: status, size: size)
            }
        }
        .environment(\.mascotSpeed, Double(speedPct) / 100.0)
        .environment(\.mascotAnimationsActive, animated && parentAnimationsActive && animationGate.animationsActive)
        .environment(\.mascotAnimationEpoch, animationGate.epoch)
        .transaction { transaction in
            if !animated || !parentAnimationsActive || !animationGate.animationsActive {
                // Some mascots also contain repeatForever alert pulses outside
                // MascotTimeline. Stop those as well as the periodic canvases.
                transaction.animation = nil
                transaction.disablesAnimations = true
            }
        }
    }
}
