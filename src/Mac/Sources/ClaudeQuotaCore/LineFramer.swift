import Foundation

/// Splits a byte stream into newline-delimited lines, with a hard cap so one malformed reply
/// cannot grow the buffer without bound. Mirrors `src/Windows/Data/LineFramer.cs`.
///
/// Over-long lines are dropped rather than truncated-and-parsed: a truncated JSON line either
/// fails to parse (noise) or, worse, parses into something that looks valid. `overflowCount`
/// lets the supervisor recycle a child that keeps producing them.
public final class LineFramer {
    public private(set) var overflowCount = 0
    private var buffer = Data()
    private let maximumLineBytes: Int
    private var discardingCurrentLine = false

    public init(maximumLineBytes: Int = 8 * 1024 * 1024) {
        self.maximumLineBytes = maximumLineBytes
    }

    /// Appends `data` and returns every complete line it produced, as UTF-8 strings.
    public func append(_ data: Data) -> [String] {
        var lines: [String] = []
        buffer.append(data)

        while let newlineIndex = buffer.firstIndex(of: UInt8(ascii: "\n")) {
            let lineData = buffer[buffer.startIndex..<newlineIndex]
            buffer.removeSubrange(buffer.startIndex...newlineIndex)

            if discardingCurrentLine {
                // The tail of a line we already gave up on.
                discardingCurrentLine = false
                continue
            }
            if lineData.count > maximumLineBytes {
                overflowCount += 1
                continue
            }
            var line = lineData
            if line.last == UInt8(ascii: "\r") { line = line.dropLast() }
            if line.isEmpty { continue }
            if let text = String(data: line, encoding: .utf8) { lines.append(text) }
        }

        if buffer.count > maximumLineBytes {
            // No newline in sight and already over the cap: drop what we have and everything up
            // to the next newline.
            overflowCount += 1
            buffer.removeAll(keepingCapacity: false)
            discardingCurrentLine = true
        }
        return lines
    }
}
