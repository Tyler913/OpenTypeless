import Foundation
import Testing
@testable import TypelessCore

struct UpdateCheckTests {
    /// Shaped like GitHub's `GET /repos/{owner}/{repo}/releases`, newest first.
    static let releasesJSON = """
    [
      {"tag_name": "1.1.0-beta", "name": "Beta", "body": "", "html_url": "https://github.com/Tyler913/OpenTypeless/releases/tag/1.1.0-beta",
       "draft": false, "prerelease": true,
       "assets": [{"name": "OpenTypeless-1.1.0-macOS-arm64.zip", "size": 10, "digest": null,
                   "browser_download_url": "https://example.com/beta-mac.zip"}]},
      {"tag_name": "1.0.3-windows", "name": "", "body": null, "html_url": "https://github.com/Tyler913/OpenTypeless/releases/tag/1.0.3-windows",
       "draft": false, "prerelease": false,
       "assets": [{"name": "OpenTypeless-1.0.3-windows-x64.zip", "size": 65000000,
                   "digest": "sha256:d8768d63fcd7e2f2106f7b6787ca6096285d9161f44f0b00ba96d5def45b5735",
                   "browser_download_url": "https://example.com/1.0.3-win-x64.zip"},
                  {"name": "OpenTypeless-1.0.3-windows-arm64.zip", "size": 64000000,
                   "digest": "sha256:1111111111111111111111111111111111111111111111111111111111111111",
                   "browser_download_url": "https://example.com/1.0.3-win-arm64.zip"}]},
      {"tag_name": "1.0.2", "name": "OpenTypeless 1.0.2", "body": "  Faster clean-up.\\n", "html_url": "https://github.com/Tyler913/OpenTypeless/releases/tag/1.0.2",
       "draft": false, "prerelease": false,
       "assets": [{"name": "OpenTypeless-1.0.2-macOS-arm64.zip", "size": 1810073,
                   "digest": "sha256:78F69C8DAE4FDC7F1CDE3CE8A31B04E293416A6972914A96D60978399590C22B",
                   "browser_download_url": "https://example.com/1.0.2-mac.zip"}]},
      {"tag_name": "1.0.0", "name": "OpenTypeless V1.0.0", "body": "First release", "html_url": "https://github.com/Tyler913/OpenTypeless/releases/tag/1.0.0",
       "draft": false, "prerelease": false,
       "assets": [{"name": "OpenTypeless-1.0.0-macOS-arm64.zip", "size": 1810073, "digest": "sha256:78f69c8dae4fdc7f1cde3ce8a31b04e293416a6972914a96d60978399590c22b",
                   "browser_download_url": "https://example.com/1.0.0-mac.zip"},
                  {"name": "OpenTypeless-1.0.0-windows-x64.zip", "size": 65148938, "digest": "sha256:d8768d63fcd7e2f2106f7b6787ca6096285d9161f44f0b00ba96d5def45b5735",
                   "browser_download_url": "https://example.com/1.0.0-win-x64.zip"}]}
    ]
    """

    private func releases() throws -> [GitHubRelease] {
        try UpdateCheck.decode(Data(Self.releasesJSON.utf8))
    }

    @Test func testVersionParsingAndOrder() {
        XCTAssertEqual(AppVersion("1.0.2")?.components, [1, 0, 2])
        XCTAssertEqual(AppVersion("v2.1")?.components, [2, 1])
        XCTAssertEqual(AppVersion("1.0.2-windows")?.components, [1, 0, 2])
        XCTAssertTrue(AppVersion("") == nil)
        XCTAssertTrue(AppVersion("1..2") == nil)
        XCTAssertTrue(AppVersion("1.x") == nil)
        XCTAssertTrue(AppVersion("1.0.10")! > AppVersion("1.0.9")!)
        XCTAssertTrue(AppVersion("1.1")! > AppVersion("1.0.99")!)
        XCTAssertTrue(AppVersion("1.0")! == AppVersion("1.0.0")!)
        XCTAssertEqual(Set([AppVersion("1.0")!, AppVersion("1.0.0")!]).count, 1)
        XCTAssertEqual(AppVersion("01.2.3")?.description, "1.2.3")
    }

    @Test func testAssetVersionMatchesOnlyThisPlatform() {
        XCTAssertEqual(UpdateCheck.assetVersion("OpenTypeless-1.0.2-macOS-arm64.zip", platform: "macOS-arm64"), AppVersion("1.0.2"))
        XCTAssertTrue(UpdateCheck.assetVersion("OpenTypeless-1.0.2-windows-x64.zip", platform: "macOS-arm64") == nil)
        XCTAssertTrue(UpdateCheck.assetVersion("OpenTypeless-1.0.2-windows-arm64.zip", platform: "windows-x64") == nil)
        XCTAssertTrue(UpdateCheck.assetVersion("OpenTypeless--macOS-arm64.zip", platform: "macOS-arm64") == nil)
        XCTAssertTrue(UpdateCheck.assetVersion("Other-1.0.2-macOS-arm64.zip", platform: "macOS-arm64") == nil)
        XCTAssertTrue(UpdateCheck.assetVersion("OpenTypeless-1.0.2-macOS-x64.zip", platform: "macOS-arm64") == nil)
        XCTAssertTrue(UpdateCheck.assetVersion("OpenTypeless-1.0.2-macOS-arm64.dmg", platform: "macOS-arm64") == nil)
        XCTAssertEqual(UpdateCheck.macOSPlatform(arm64: false), "macOS-x64")
    }

    @Test func testPicksNewestStableReleaseForMac() throws {
        let all = try releases()
        let update = try #require(UpdateCheck.newest(in: all, platform: "macOS-arm64", current: AppVersion("1.0.1")!))
        XCTAssertEqual(update.version, AppVersion("1.0.2"))   // 1.1.0 is a prerelease, 1.0.3 is Windows-only
        XCTAssertEqual(update.title, "OpenTypeless 1.0.2")
        XCTAssertEqual(update.notes, "Faster clean-up.")
        XCTAssertEqual(update.assetName, "OpenTypeless-1.0.2-macOS-arm64.zip")
        XCTAssertEqual(update.downloadURL.absoluteString, "https://example.com/1.0.2-mac.zip")
        XCTAssertEqual(update.size, 1810073)
        XCTAssertEqual(update.sha256, "78f69c8dae4fdc7f1cde3ce8a31b04e293416a6972914a96d60978399590c22b")
        XCTAssertEqual(update.pageURL.absoluteString, "https://github.com/Tyler913/OpenTypeless/releases/tag/1.0.2")
    }

    @Test func testPicksMatchingWindowsArchitecture() throws {
        let all = try releases()
        let x64 = try #require(UpdateCheck.newest(in: all, platform: "windows-x64", current: AppVersion("1.0.0")!))
        XCTAssertEqual(x64.version, AppVersion("1.0.3"))
        XCTAssertEqual(x64.title, "OpenTypeless 1.0.3")   // empty release name falls back
        XCTAssertEqual(x64.notes, "")
        let arm = try #require(UpdateCheck.newest(in: all, platform: "windows-arm64", current: AppVersion("1.0.0")!))
        XCTAssertEqual(arm.downloadURL.absoluteString, "https://example.com/1.0.3-win-arm64.zip")
    }

    @Test func testNothingWhenUpToDateOrAhead() throws {
        let all = try releases()
        XCTAssertTrue(UpdateCheck.newest(in: all, platform: "macOS-arm64", current: AppVersion("1.0.2")!) == nil)
        XCTAssertTrue(UpdateCheck.newest(in: all, platform: "macOS-arm64", current: AppVersion("1.5")!) == nil)
        XCTAssertTrue(UpdateCheck.newest(in: [], platform: "macOS-arm64", current: AppVersion("1.0")!) == nil)
    }

    @Test func testDigestParsing() {
        XCTAssertEqual(UpdateCheck.sha256(fromDigest: "sha256:" + String(repeating: "Ab", count: 32)), String(repeating: "ab", count: 32))
        XCTAssertTrue(UpdateCheck.sha256(fromDigest: nil) == nil)
        XCTAssertTrue(UpdateCheck.sha256(fromDigest: "sha512:abcd") == nil)
        XCTAssertTrue(UpdateCheck.sha256(fromDigest: "sha256:abcd") == nil)
        XCTAssertTrue(UpdateCheck.sha256(fromDigest: "sha256:" + String(repeating: "zz", count: 32)) == nil)
    }
}
