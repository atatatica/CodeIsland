import SwiftUI
import CodeIslandCore

/// Omp — option-key mascot for the omp agent: a deep-violet terminal
/// with a warm light face panel and an orange ⌥ glyph on the shell.
struct OmpView: View {
    let status: AgentStatus
    var size: CGFloat = 27
    @State private var alive = false

    private static let shellC = Color(red: 0.34, green: 0.25, blue: 0.55)
    private static let shellDk = Color(red: 0.22, green: 0.15, blue: 0.38)
    private static let accentC = Color(red: 1.0, green: 0.62, blue: 0.25)
    private static let faceC = Color(red: 0.93, green: 0.89, blue: 0.80)
    private static let inkC = Color(red: 0.22, green: 0.15, blue: 0.38)
    private static let alertC = Color(red: 1.0, green: 0.55, blue: 0.18)
    private static let kbBase = Color(red: 0.10, green: 0.07, blue: 0.20)
    private static let kbKey = Color(red: 0.28, green: 0.21, blue: 0.48)
    private static let kbHi = Color(red: 1.0, green: 0.62, blue: 0.25)

    var body: some View {
        ZStack {
            switch status {
            case .idle:
                sleepScene
            case .processing, .running:
                workScene
            case .waitingApproval, .waitingQuestion:
                alertScene
            }
        }
        .frame(width: size, height: size)
        .clipped()
        .onAppear { alive = true }
        .onChange(of: status) {
            alive = false
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { alive = true }
        }
    }

    private struct V {
        let ox: CGFloat, oy: CGFloat, s: CGFloat
        let y0: CGFloat

        init(_ sz: CGSize, svgW: CGFloat = 16, svgH: CGFloat = 12, svgY0: CGFloat = 4) {
            s = min(sz.width / svgW, sz.height / svgH)
            ox = (sz.width - svgW * s) / 2
            oy = (sz.height - svgH * s) / 2
            y0 = svgY0
        }

        func r(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat, dy: CGFloat = 0) -> CGRect {
            CGRect(x: ox + x * s, y: oy + (y - y0 + dy) * s, width: w * s, height: h * s)
        }
    }

    private func lerp(_ keyframes: [(CGFloat, CGFloat)], at pct: CGFloat) -> CGFloat {
        guard let first = keyframes.first else { return 0 }
        if pct <= first.0 { return first.1 }
        for i in 1..<keyframes.count {
            if pct <= keyframes[i].0 {
                let t = (pct - keyframes[i - 1].0) / (keyframes[i].0 - keyframes[i - 1].0)
                return keyframes[i - 1].1 + (keyframes[i].1 - keyframes[i - 1].1) * t
            }
        }
        return keyframes.last?.1 ?? 0
    }

    private func drawShadow(_ c: GraphicsContext, v: V, width: CGFloat = 7, opacity: Double = 0.28) {
        c.fill(Path(v.r(8 - width / 2, 15, width, 1)), with: .color(.black.opacity(opacity)))
    }

    private func drawFeet(_ c: GraphicsContext, v: V, dy: CGFloat) {
        let footDy = dy * 0.25
        c.fill(Path(v.r(5.5, 13.8, 1.2, 1.5, dy: footDy)), with: .color(Self.shellDk.opacity(0.75)))
        c.fill(Path(v.r(9.3, 13.8, 1.2, 1.5, dy: footDy)), with: .color(Self.shellDk.opacity(0.75)))
    }

    /// Face panel frame on the shell, shared by body drawing and the face.
    private func faceFrame(cx: CGFloat, cy: CGFloat, scale: CGFloat) -> (x: CGFloat, y: CGFloat, w: CGFloat, h: CGFloat) {
        let w = 5.8 * scale, h = 2.8 * scale
        return (cx - w / 2, cy - h / 2 - 0.35, w, h)
    }

    private func drawBody(_ c: GraphicsContext, v: V, dy: CGFloat, scale: CGFloat = 1.0) {
        let cx: CGFloat = 8
        let cy: CGFloat = 9.5
        let bw: CGFloat = 8.8 * scale
        let bh: CGFloat = 6.6 * scale
        let bodyRect = v.r(cx - bw / 2, cy - bh / 2, bw, bh, dy: dy)
        c.fill(Path(roundedRect: bodyRect, cornerRadius: 1.6 * v.s), with: .color(Self.shellC))
        c.fill(Path(v.r(cx - bw / 2 + 0.6, cy - bh / 2 + 0.6, bw - 1.2, 0.7, dy: dy)), with: .color(.white.opacity(0.18)))
        c.fill(Path(v.r(cx - bw / 2, cy + bh / 2 - 1.2, bw, 1.2, dy: dy)), with: .color(Self.shellDk.opacity(0.8)))
        // Floating orange nub above the shell — Omp's sprout equivalent.
        c.fill(Path(ellipseIn: v.r(7.5, 5.4, 1.0, 0.8, dy: dy)), with: .color(Self.accentC))
        // Warm light face panel.
        let f = faceFrame(cx: cx, cy: cy, scale: scale)
        c.fill(Path(roundedRect: v.r(f.x, f.y, f.w, f.h, dy: dy), cornerRadius: 0.5 * v.s), with: .color(Self.faceC))
        // Signature ⌥ glyph on the shell, top-right of the body.
        var gl = Path()
        gl.move(to: CGPoint(x: v.ox + 10.8 * v.s, y: v.oy + (7.55 - v.y0 + dy) * v.s))
        gl.addLine(to: CGPoint(x: v.ox + 11.7 * v.s, y: v.oy + (6.95 - v.y0 + dy) * v.s))
        c.stroke(gl, with: .color(Self.accentC), style: StrokeStyle(lineWidth: 0.5 * v.s, lineCap: .round))
        c.fill(Path(ellipseIn: v.r(10.35, 7.4, 0.55, 0.55, dy: dy)), with: .color(Self.accentC))
        c.fill(Path(ellipseIn: v.r(11.7, 6.8, 0.55, 0.55, dy: dy)), with: .color(Self.accentC))
    }

    private func drawOmpFace(_ c: GraphicsContext, v: V, dy: CGFloat, scale: CGFloat = 1.0, eyeScale: CGFloat = 1.0, color: Color = Self.inkC) {
        let f = faceFrame(cx: 8, cy: 9.5, scale: scale)
        let e = 0.95 * scale
        let eyeH = max(0.2, e * eyeScale)
        c.fill(Path(v.r(f.x + f.w * 0.22, f.y + 0.6 + (e - eyeH) * 0.3, e, eyeH, dy: dy)), with: .color(color))
        c.fill(Path(v.r(f.x + f.w * 0.62, f.y + 0.6 + (e - eyeH) * 0.3, e, eyeH, dy: dy)), with: .color(color))
        c.fill(Path(v.r(f.x + f.w * 0.40, f.y + f.h - 0.78, f.w * 0.20, 0.28, dy: dy)), with: .color(color))
    }

    private var sleepScene: some View {
        ZStack {
            MascotTimeline(interval: 0.12) { t in
                // De-synced dual-sine drift — unique rhythm per mascot (#15).
                let float = sin(t * 2 * .pi / 4.31) * 0.62 + sin(t * 2 * .pi / 7.13) * 0.30
                let blinkCycle = t.truncatingRemainder(dividingBy: 4.4)
                let blink: CGFloat = (blinkCycle > 3.9 && blinkCycle < 4.1) ? 0.15 : 1.0
                Canvas { c, sz in
                    let v = V(sz)
                    drawShadow(c, v: v, width: 6.4 + abs(float) * 0.3, opacity: 0.18)
                    drawFeet(c, v: v, dy: float)
                    drawBody(c, v: v, dy: float, scale: 0.94)
                    drawOmpFace(c, v: v, dy: float, scale: 0.94, eyeScale: blink)
                }
            }
            MascotTimeline(interval: 0.12) { t in
                ZStack {
                    ForEach(0..<3, id: \.self) { i in
                        let ci = Double(i)
                        let cycle = 2.8 + ci * 0.25
                        let delay = ci * 0.9
                        let phase = max(0, ((t - delay).truncatingRemainder(dividingBy: cycle)) / cycle)
                        let fontSize = max(6, size * CGFloat(0.18 + phase * 0.10))
                        let opacity = phase < 0.8 ? (0.72 - ci * 0.1) : (1.0 - phase) * 3.5 * (0.72 - ci * 0.1)
                        Text("z")
                            .font(.system(size: fontSize, weight: .black, design: .monospaced))
                            .foregroundStyle(Self.shellDk.opacity(opacity))
                            .offset(x: size * CGFloat(0.16 + ci * 0.08), y: -size * CGFloat(0.18 + phase * 0.36))
                    }
                }
            }
        }
    }

    private var workScene: some View {
        MascotTimeline(interval: 0.03) { t in
            let bounce = sin(t * 2 * .pi / 0.44) * 0.9
            let blink = max(0.1, MascotMotion.blink(t, seed: 0x0a7))
            let keyPhase = Int(t / 0.1) % 6
            Canvas { c, sz in
                let v = V(sz, svgW: 16, svgH: 14, svgY0: 3)
                drawShadow(c, v: v, width: 7 - abs(bounce) * 0.25, opacity: max(0.1, 0.32 - abs(bounce) * 0.02))
                drawFeet(c, v: v, dy: bounce)
                c.fill(Path(v.r(0, 13, 15, 3)), with: .color(Self.kbBase))
                for row in 0..<2 {
                    let ky = 13.45 + CGFloat(row) * 1.15
                    for col in 0..<6 {
                        c.fill(Path(v.r(0.5 + CGFloat(col) * 2.4, ky, 1.8, 0.68)), with: .color(Self.kbKey))
                    }
                }
                c.fill(Path(v.r(0.5 + CGFloat(keyPhase % 6) * 2.4, 13.45 + CGFloat(keyPhase / 3) * 1.15, 1.8, 0.68)), with: .color(Self.kbHi.opacity(0.95)))
                drawBody(c, v: v, dy: bounce)
                drawOmpFace(c, v: v, dy: bounce, eyeScale: blink)
            }
        }
    }

    private var alertScene: some View {
        ZStack {
            Circle()
                .fill(Self.alertC.opacity(alive ? 0.12 : 0))
                .frame(width: size * 0.8)
                .blur(radius: size * 0.05)
                .animation(.easeInOut(duration: 0.5).repeatForever(autoreverses: true), value: alive)
            MascotTimeline(interval: 0.03) { t in
                let pct = t.truncatingRemainder(dividingBy: 3.5) / 3.5
                let jumpY = lerp([(0,0),(0.03,0),(0.18,-7.5),(0.26,1.3),(0.34,-5.5),(0.42,0.8),(0.5,-2.5),(0.58,0.2),(0.68,0),(1,0)], at: pct)
                let shakeX: CGFloat = (pct > 0.16 && pct < 0.56) ? sin(pct * 80) * 0.55 : 0
                let bangOp = lerp([(0,0),(0.03,1),(0.56,1),(0.64,0),(1,0)], at: pct)
                let faceColor = bangOp > 0.4 ? Self.alertC : Self.inkC
                Canvas { c, sz in
                    let v = V(sz, svgW: 16, svgH: 14, svgY0: 3)
                    drawShadow(c, v: v, width: 7 * (1.0 - abs(min(0, jumpY)) * 0.04), opacity: max(0.08, 0.4 - abs(min(0, jumpY)) * 0.04))
                    drawFeet(c, v: v, dy: jumpY)
                    c.translateBy(x: shakeX * v.s, y: 0)
                    drawBody(c, v: v, dy: jumpY)
                    drawOmpFace(c, v: v, dy: jumpY, color: faceColor)
                    c.translateBy(x: -shakeX * v.s, y: 0)
                    if bangOp > 0.01 {
                        c.fill(Path(v.r(12.8, 4 + jumpY * 0.15, 1.8, 3.4)), with: .color(Self.alertC.opacity(bangOp)))
                        c.fill(Path(v.r(12.8, 8.1 + jumpY * 0.15, 1.8, 1.3)), with: .color(Self.alertC.opacity(bangOp)))
                    }
                }
            }
        }
    }
}
