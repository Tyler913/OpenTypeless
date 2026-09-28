// swift-tools-version: 6.0
import Foundation
import PackageDescription

// The macOS 27 SDK implements SwiftUI's @State as a macro whose plugin ships with Xcode but not with
// the Command Line Tools. When building with the CLT toolchain, borrow the plugin from Xcode.app.
let xcodeSwiftUIPlugins = "/Applications/Xcode.app/Contents/Developer/Platforms/MacOSX.platform/Developer/usr/lib/swift/host/plugins"
let usingCLT = (ProcessInfo.processInfo.environment["DEVELOPER_DIR"] ?? "").isEmpty
    && FileManager.default.fileExists(atPath: "/Library/Developer/CommandLineTools/usr/bin/swiftc")
let cltTestingPlugins = "/Library/Developer/CommandLineTools/usr/lib/swift/host/plugins/testing"
// Every target gets the same plugin search paths: SwiftPM shares one dependency scanner across targets
// in a build, and differing paths made the swift-testing macros intermittently "not found".
let swiftSettings: [SwiftSetting] = [.swiftLanguageMode(.v5)]
    + (usingCLT && FileManager.default.fileExists(atPath: xcodeSwiftUIPlugins + "/libSwiftUIMacros.dylib")
        ? [.unsafeFlags(["-plugin-path", xcodeSwiftUIPlugins, "-plugin-path", cltTestingPlugins])] : [])

let package = Package(
    name: "OpenTypeless",
    platforms: [.macOS("26.0")],
    targets: [
        .target(
            name: "TypelessCore",
            swiftSettings: swiftSettings
        ),
        .executableTarget(
            name: "OpenTypeless",
            dependencies: ["TypelessCore"],
            swiftSettings: swiftSettings,
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("AVFoundation"),
                .linkedFramework("Carbon"),
            ]
        ),
        .testTarget(
            name: "TypelessCoreTests",
            dependencies: ["TypelessCore"],
            swiftSettings: swiftSettings
        ),
    ]
)
