// Example: create, inspect, verify and extract a raw CHD from Swift.
//   swiftc -I ../../include -L <build> -lchdman -Xlinker -rpath -Xlinker <build> Chdman.swift main.swift -o chdman-swift
// SPDX-License-Identifier: BSD-3-Clause

import CChdman
import Foundation

let dir = FileManager.default.temporaryDirectory.appendingPathComponent("chdman-swift-example")
try? FileManager.default.removeItem(at: dir)
try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
let image = dir.appendingPathComponent("disk.img").path
let chd = dir.appendingPathComponent("disk.chd").path
let back = dir.appendingPathComponent("disk.out").path

// 1 MiB test image: half pattern, half zeroes
var bytes = [UInt8](repeating: 0, count: 1 << 20)
for i in 0..<(bytes.count / 2) { bytes[i] = UInt8(truncatingIfNeeded: i * 7) }
try Data(bytes).write(to: URL(fileURLWithPath: image))

print("libchdman \(String(cString: chdman_version()))")
let chdman = try Chdman()
chdman.onProgress = { phase, fraction, _ in
    if phase == CHDMAN_PHASE_COMPRESSING { print(String(format: "  compressing %.0f%%", fraction * 100)) }
    return false // return true to cancel
}

try chdman.createRaw(input: image, output: chd, unitSize: 512, force: true)
let info = try chdman.info(chd)
print("created \(info.mediaType) CHD v\(info.version): \(info.logicalBytes) -> \(info.fileBytes) bytes, \(info.compression)")
print("raw SHA-1 \(info.rawSHA1)")
print("verify: \(try chdman.verify(chd) == CHDMAN_VERIFY_OK ? "OK" : "FAILED")")
try chdman.extractRaw(input: chd, output: back, force: true)
print("round trip identical: \(try Data(contentsOf: URL(fileURLWithPath: back)) == Data(bytes))")

do {
    try chdman.createRaw(input: image, output: chd, unitSize: 512) // exists, no force
} catch let error as ChdmanError {
    print("expected error -> \(error)")
}
