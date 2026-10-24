import AppKit

let outputDirectory = CommandLine.arguments[1]
try FileManager.default.createDirectory(atPath: outputDirectory, withIntermediateDirectories: true)

func drawPlaceholderIcon(size: Int, filename: String) throws {
    let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil,
        pixelsWide: size,
        pixelsHigh: size,
        bitsPerSample: 8,
        samplesPerPixel: 4,
        hasAlpha: true,
        isPlanar: false,
        colorSpaceName: .deviceRGB,
        bytesPerRow: 0,
        bitsPerPixel: 0
    )!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
    NSGraphicsContext.current?.imageInterpolation = .high
    let transform = NSAffineTransform()
    transform.scale(by: CGFloat(size) / 1024)
    transform.concat()

    NSColor(calibratedRed: 0.10, green: 0.16, blue: 0.27, alpha: 1).setFill()
    NSBezierPath(roundedRect: NSRect(x: 28, y: 28, width: 968, height: 968), xRadius: 210, yRadius: 210).fill()

    let antenna = NSBezierPath()
    antenna.lineWidth = 34
    antenna.lineCapStyle = .round
    NSColor(calibratedRed: 0.88, green: 0.92, blue: 0.98, alpha: 1).setStroke()
    antenna.move(to: NSPoint(x: 400, y: 748)); antenna.line(to: NSPoint(x: 326, y: 850))
    antenna.move(to: NSPoint(x: 624, y: 748)); antenna.line(to: NSPoint(x: 698, y: 850))
    antenna.stroke()

    NSColor(calibratedRed: 0.88, green: 0.92, blue: 0.98, alpha: 1).setFill()
    NSBezierPath(roundedRect: NSRect(x: 138, y: 190, width: 748, height: 570), xRadius: 82, yRadius: 82).fill()
    NSColor(calibratedRed: 0.10, green: 0.16, blue: 0.27, alpha: 1).setFill()
    NSBezierPath(roundedRect: NSRect(x: 180, y: 278, width: 664, height: 430), xRadius: 44, yRadius: 44).fill()

    let play = NSBezierPath()
    play.move(to: NSPoint(x: 452, y: 370))
    play.line(to: NSPoint(x: 452, y: 616))
    play.line(to: NSPoint(x: 652, y: 493))
    play.close()
    NSColor(calibratedRed: 1, green: 0.60, blue: 0.28, alpha: 1).setFill()
    play.fill()

    NSColor(calibratedRed: 0.88, green: 0.92, blue: 0.98, alpha: 1).setFill()
    for x in [424.0, 512.0, 600.0] {
        NSBezierPath(ovalIn: NSRect(x: x, y: 116, width: 28, height: 28)).fill()
    }

    NSGraphicsContext.restoreGraphicsState()
    if let data = bitmap.representation(using: .png, properties: [:]) {
        try data.write(to: URL(fileURLWithPath: outputDirectory).appendingPathComponent(filename))
    }
}

for (pixelSize, name) in [
    (16, "icon_16x16.png"), (32, "icon_16x16@2x.png"),
    (32, "icon_32x32.png"), (64, "icon_32x32@2x.png"),
    (128, "icon_128x128.png"), (256, "icon_128x128@2x.png"),
    (256, "icon_256x256.png"), (512, "icon_256x256@2x.png"),
    (512, "icon_512x512.png"), (1024, "icon_512x512@2x.png")
] {
    try drawPlaceholderIcon(size: pixelSize, filename: name)
}
