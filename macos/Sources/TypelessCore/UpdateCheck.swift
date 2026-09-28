import Foundation

/// A release version like `1.0.2`, compared number by number (so 1.0.10 is newer than 1.0.9).
public struct AppVersion: Comparable, Hashable, Sendable, CustomStringConvertible {
    public let components: [Int]

    /// Accepts `1.0.2`, `v1.0.2` and tags like `1.0.2-windows`.
    public init?(_ string: String) {
        var text = string.trimmingCharacters(in: .whitespaces)
        if text.hasPrefix("v") || text.hasPrefix("V") { text.removeFirst() }
        if let dash = text.firstIndex(of: "-") { text = String(text[..<dash]) }
        let parts = text.split(separator: ".", omittingEmptySubsequences: false).map { Int($0) }
        guard (1...4).contains(parts.count), parts.allSatisfy({ ($0 ?? -1) >= 0 }) else { return nil }
        components = parts.map { $0! }
    }

    public var description: String { components.map(String.init).joined(separator: ".") }

    public static func < (a: AppVersion, b: AppVersion) -> Bool {
        for i in 0..<max(a.components.count, b.components.count) {
            let x = i < a.components.count ? a.components[i] : 0
            let y = i < b.components.count ? b.components[i] : 0
            if x != y { return x < y }
        }
        return false
    }

    public static func == (a: AppVersion, b: AppVersion) -> Bool { !(a < b) && !(b < a) }

    public func hash(into hasher: inout Hasher) {
        var trimmed = components
        while trimmed.count > 1, trimmed.last == 0 { trimmed.removeLast() }
        hasher.combine(trimmed)
    }
}

/// The subset of GitHub's release JSON the updater needs.
public struct GitHubRelease: Decodable, Sendable {
    public struct Asset: Decodable, Sendable {
        public let name: String
        public let size: Int
        /// `sha256:<hex>`, published by GitHub for every uploaded file.
        public let digest: String?
        public let browserDownloadURL: URL

        enum CodingKeys: String, CodingKey {
            case name, size, digest
            case browserDownloadURL = "browser_download_url"
        }
    }

    public let tagName: String
    public let name: String?
    public let body: String?
    public let htmlURL: URL
    public let draft: Bool
    public let prerelease: Bool
    public let assets: [Asset]

    enum CodingKeys: String, CodingKey {
        case tagName = "tag_name"
        case name, body, draft, prerelease, assets
        case htmlURL = "html_url"
    }
}

/// A newer release this copy of the app can install.
public struct AvailableUpdate: Equatable, Sendable {
    public let version: AppVersion
    public let title: String
    public let notes: String
    /// The release page, for reading the notes or downloading by hand.
    public let pageURL: URL
    public let assetName: String
    public let downloadURL: URL
    public let size: Int
    /// Lowercase hex SHA-256 of the zip, or nil if GitHub didn't publish one (then it can't be installed automatically).
    public let sha256: String?

    public init(version: AppVersion, title: String, notes: String, pageURL: URL, assetName: String,
                downloadURL: URL, size: Int, sha256: String?) {
        self.version = version
        self.title = title
        self.notes = notes
        self.pageURL = pageURL
        self.assetName = assetName
        self.downloadURL = downloadURL
        self.size = size
        self.sha256 = sha256
    }
}

/// Finds newer versions among the GitHub releases of this repository.
///
/// Releases are matched by their zip's file name, `OpenTypeless-<version>-<platform>.zip`, not by tag, so it works
/// whether a release carries one app (`1.0.2`, `1.0.2-windows`) or both. The release workflow already refuses to
/// publish a zip whose version doesn't match its tag.
public enum UpdateCheck {
    public static let repository = "Tyler913/OpenTypeless"
    public static let releasesURL = URL(string: "https://api.github.com/repos/\(repository)/releases?per_page=30")!
    public static let releasesPageURL = URL(string: "https://github.com/\(repository)/releases")!

    /// The file-name suffix of this platform's zip: `macOS-arm64`, `windows-x64`, `windows-arm64`.
    public static let macOSPlatform = "macOS-arm64"

    public static func decode(_ data: Data) throws -> [GitHubRelease] {
        try JSONDecoder().decode([GitHubRelease].self, from: data)
    }

    /// The version in `OpenTypeless-<version>-<platform>.zip`, or nil if the file is for another platform.
    public static func assetVersion(_ name: String, platform: String) -> AppVersion? {
        let prefix = "OpenTypeless-", suffix = "-\(platform).zip"
        guard name.hasPrefix(prefix), name.hasSuffix(suffix), name.count > prefix.count + suffix.count else { return nil }
        return AppVersion(String(name.dropFirst(prefix.count).dropLast(suffix.count)))
    }

    /// The newest published, non-prerelease release that has a zip for `platform` and is newer than `current`.
    public static func newest(in releases: [GitHubRelease], platform: String, current: AppVersion) -> AvailableUpdate? {
        var best: AvailableUpdate?
        for release in releases where !release.draft && !release.prerelease {
            for asset in release.assets {
                guard let version = assetVersion(asset.name, platform: platform), version > current,
                      best.map({ version > $0.version }) ?? true else { continue }
                let title = (release.name ?? "").trimmingCharacters(in: .whitespaces)
                best = AvailableUpdate(
                    version: version,
                    title: title.isEmpty ? "OpenTypeless \(version)" : title,
                    notes: (release.body ?? "").trimmingCharacters(in: .whitespacesAndNewlines),
                    pageURL: release.htmlURL,
                    assetName: asset.name,
                    downloadURL: asset.browserDownloadURL,
                    size: asset.size,
                    sha256: sha256(fromDigest: asset.digest)
                )
            }
        }
        return best
    }

    /// `sha256:ABC…` → `abc…`; nil for other algorithms or malformed values.
    public static func sha256(fromDigest digest: String?) -> String? {
        guard let digest, digest.lowercased().hasPrefix("sha256:") else { return nil }
        let hex = digest.dropFirst("sha256:".count).lowercased()
        guard hex.count == 64, hex.allSatisfy({ $0.isHexDigit }) else { return nil }
        return hex
    }

    /// Asks GitHub for the release list. No sign-in needed; unauthenticated requests are limited to 60 an hour.
    public static func fetchReleases(session: URLSession = .shared) async throws -> [GitHubRelease] {
        var request = URLRequest(url: releasesURL, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 30)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("OpenTypeless", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw APIError.badResponse("no HTTP response") }
        guard http.statusCode == 200 else {
            if http.statusCode == 403 || http.statusCode == 429 {
                throw APIError.http(status: http.statusCode, message: L("GitHub 请求过于频繁，稍后再试", "GitHub rate limit reached, try again later"))
            }
            throw APIError.http(status: http.statusCode, message: String(decoding: data.prefix(200), as: UTF8.self))
        }
        do {
            return try decode(data)
        } catch {
            throw APIError.badResponse(error.localizedDescription)
        }
    }
}
