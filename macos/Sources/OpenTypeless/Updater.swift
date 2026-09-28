import AppKit
import CryptoKit
import Security
import TypelessCore

/// Keeps the app up to date from GitHub Releases.
///
/// Checks shortly after launch and then once a day, downloads a newer zip in the background, and installs it
/// when the user clicks Restart to update: the new bundle is unpacked and verified next to the running one, and a
/// small helper swaps it into place once the app has quit, then opens it again. Nothing is installed while a
/// dictation is being recorded or processed.
///
/// Safety checks before anything is replaced: the zip's SHA-256 must match the digest GitHub publishes, the unpacked
/// app must carry the same bundle ID and the expected version with a valid signature, and, when this copy is signed
/// with a certificate rather than ad hoc, the new one must be signed with that same certificate.
@MainActor
final class Updater: ObservableObject {
    static let shared = Updater()

    enum Phase: Equatable {
        case idle
        case checking
        case upToDate
        /// A newer version exists but can't be installed from inside the app (see `manualReason`).
        case available
        case downloading(Double)
        /// Downloaded and verified; installs on Restart to update.
        case ready
        case installing
        case failed(String)
    }

    @Published private(set) var phase: Phase = .idle
    @Published private(set) var update: AvailableUpdate?
    /// Why the found update has to be downloaded by hand, if it does.
    @Published private(set) var manualReason: String?
    /// Restart to update was clicked during a dictation; it installs as soon as that finishes.
    @Published private(set) var installPending = false
    @Published private(set) var lastChecked: Date? = UserDefaults.standard.object(forKey: Keys.lastCheck) as? Date

    /// Set by the app delegate: true while no dictation is being recorded or processed.
    var canInstallNow: @MainActor () -> Bool = { true }

    let currentVersion: AppVersion? = (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String).flatMap(AppVersion.init)

    private enum Keys {
        static let lastCheck = "lastUpdateCheck"
        static let skipped = "skippedUpdateVersion"
        /// The version being installed when the app quit, to tell after the restart whether it worked.
        static let installing = "installingUpdateVersion"
    }

    private var stagedApp: URL?
    private var timer: Timer?
    private var work: Task<Void, Never>?

    private init() {}

    /// A copy built from source and run straight out of `.build` has nothing to update.
    var isDevelopmentBuild: Bool {
        currentVersion == nil || !Bundle.main.bundlePath.hasSuffix(".app") || Bundle.main.bundlePath.contains("/.build/")
    }

    var currentVersionText: String { currentVersion?.description ?? L("开发版本", "development build") }

    /// `~/Library/Caches/OpenTypeless/Updates`: downloads, the unpacked app and the install helper.
    private var updatesDirectory: URL {
        FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("OpenTypeless/Updates", isDirectory: true)
    }

    // MARK: Schedule

    func start() {
        guard !isDevelopmentBuild else { return }
        try? FileManager.default.removeItem(at: updatesDirectory)
        reportPreviousInstall()
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(10))
            self?.checkIfDue(launch: true)
        }
        timer = Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.checkIfDue(launch: false) }
        }
    }

    private func checkIfDue(launch: Bool) {
        guard AppSettings.shared.autoCheckUpdates, work == nil else { return }
        switch phase {
        case .idle, .upToDate, .failed: break
        default: return
        }
        let due = launch || lastChecked.map { Date().timeIntervalSince($0) >= 24 * 3600 } ?? true
        if due { check(userInitiated: false) }
    }

    /// After an install restart: if the version didn't change, the helper couldn't swap the app.
    private func reportPreviousInstall() {
        guard let raw = UserDefaults.standard.string(forKey: Keys.installing) else { return }
        UserDefaults.standard.removeObject(forKey: Keys.installing)
        if let target = AppVersion(raw), let currentVersion, currentVersion < target {
            phase = .failed(L("没能安装 \(target)，已继续使用 \(currentVersion)", "Couldn't install \(target); still on \(currentVersion)"))
        }
    }

    // MARK: Check and download

    func checkNow() { check(userInitiated: true) }

    private func check(userInitiated: Bool) {
        guard work == nil, let currentVersion else { return }
        phase = .checking
        work = Task {
            defer { work = nil }
            do {
                let releases = try await UpdateCheck.fetchReleases()
                lastChecked = Date()
                UserDefaults.standard.set(lastChecked, forKey: Keys.lastCheck)
                let skipped = UserDefaults.standard.string(forKey: Keys.skipped).flatMap(AppVersion.init)
                guard let found = UpdateCheck.newest(in: releases, platform: UpdateCheck.macOSPlatform, current: currentVersion),
                      userInitiated || found.version != skipped else {
                    update = nil
                    phase = .upToDate
                    return
                }
                update = found
                manualReason = installBlocker(for: found)
                if manualReason != nil {
                    phase = .available
                    return
                }
                try await download(found)
            } catch {
                phase = .failed(APIError.from(error).localizedDescription)
            }
        }
    }

    /// Why `update` can't be installed in place, or nil if it can.
    private func installBlocker(for update: AvailableUpdate) -> String? {
        let path = Bundle.main.bundlePath
        let folder = (path as NSString).deletingLastPathComponent
        if isDevelopmentBuild { return L("开发版本不会自动更新", "Development builds don't update themselves") }
        if update.sha256 == nil { return L("GitHub 没有提供这个文件的校验值", "GitHub published no checksum for this download") }
        if path.contains("/AppTranslocation/") {
            return L("macOS 正在从临时位置运行 OpenTypeless，请先把它移到“应用程序”文件夹",
                     "macOS is running OpenTypeless from a temporary location; move it to Applications first")
        }
        if !FileManager.default.isWritableFile(atPath: folder) || !FileManager.default.isWritableFile(atPath: path) {
            return L("没有权限替换 \(folder) 中的 OpenTypeless", "No permission to replace OpenTypeless in \(folder)")
        }
        return nil
    }

    private func download(_ update: AvailableUpdate) async throws {
        let fm = FileManager.default
        let folder = updatesDirectory.appendingPathComponent(update.version.description, isDirectory: true)
        try? fm.removeItem(at: folder)
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        phase = .downloading(0)

        let zip = folder.appendingPathComponent(update.assetName)
        try await fetch(update.downloadURL, to: zip)
        let hash = try await Task.detached { try Self.sha256(of: zip) }.value
        guard hash == update.sha256 else {
            throw APIError.badResponse(L("下载的文件校验失败", "the download doesn't match its published checksum"))
        }

        let unpacked = folder.appendingPathComponent("unpacked", isDirectory: true)
        try await Self.run("/usr/bin/ditto", ["-x", "-k", zip.path, unpacked.path])
        let app = unpacked.appendingPathComponent("OpenTypeless.app", isDirectory: true)
        try Self.verify(app, version: update.version)
        try? fm.removeItem(at: zip)

        stagedApp = app
        phase = .ready
    }

    private func fetch(_ url: URL, to destination: URL) async throws {
        var observation: NSKeyValueObservation?
        defer { observation?.invalidate() }
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            let task = URLSession.shared.downloadTask(with: url) { location, response, error in
                if let error { return continuation.resume(throwing: error) }
                guard let location, let http = response as? HTTPURLResponse else {
                    return continuation.resume(throwing: APIError.badResponse("no HTTP response"))
                }
                guard http.statusCode == 200 else {
                    return continuation.resume(throwing: APIError.http(status: http.statusCode, message: url.lastPathComponent))
                }
                // The temporary file is deleted when this handler returns.
                do {
                    try FileManager.default.moveItem(at: location, to: destination)
                    continuation.resume()
                } catch {
                    continuation.resume(throwing: error)
                }
            }
            var lastPercent = -1
            observation = task.progress.observe(\.fractionCompleted) { progress, _ in
                let fraction = progress.fractionCompleted
                let percent = Int(fraction * 100)
                guard percent != lastPercent else { return }
                lastPercent = percent
                Task { @MainActor [weak self] in
                    if case .downloading = self?.phase { self?.phase = .downloading(fraction) }
                }
            }
            task.resume()
        }
    }

    // MARK: Install

    var isReady: Bool { phase == .ready }

    /// Quits, swaps in the new version and opens it. During a dictation, waits until it has finished.
    func installAndRelaunch() {
        guard phase == .ready, let stagedApp, let update else { return }
        guard canInstallNow() else {
            installPending = true
            return
        }
        installPending = false
        do {
            let script = updatesDirectory.appendingPathComponent("install.sh")
            try Self.installScript.write(to: script, atomically: true, encoding: .utf8)
            let helper = Process()
            helper.executableURL = URL(fileURLWithPath: "/bin/zsh")
            helper.arguments = [script.path, String(ProcessInfo.processInfo.processIdentifier),
                                Bundle.main.bundlePath, stagedApp.path, updatesDirectory.path]
            helper.standardInput = FileHandle.nullDevice
            helper.standardOutput = FileHandle.nullDevice
            helper.standardError = FileHandle.nullDevice
            try helper.run()
        } catch {
            phase = .failed(L("无法启动安装：", "Couldn't start the install: ") + error.localizedDescription)
            return
        }
        UserDefaults.standard.set(update.version.description, forKey: Keys.installing)
        phase = .installing
        NSApp.terminate(nil)
    }

    /// Called whenever a dictation finishes.
    func sessionBecameIdle() {
        if installPending { installAndRelaunch() }
    }

    /// Nothing is in flight, so the found version can be skipped.
    var canSkip: Bool { update != nil && (phase == .available || phase == .ready) }

    /// Stops offering this version; a manual check still shows it.
    func skipUpdate() {
        guard canSkip, let update else { return }
        UserDefaults.standard.set(update.version.description, forKey: Keys.skipped)
        self.update = nil
        stagedApp = nil
        installPending = false
        manualReason = nil
        phase = .upToDate
        try? FileManager.default.removeItem(at: updatesDirectory)
    }

    /// Waits for the app to quit, swaps the bundles (putting the old one back if that fails) and opens the result.
    private static let installScript = """
    #!/bin/zsh
    pid=$1 target=$2 new=$3 work=$4
    for _ in {1..150}; do kill -0 "$pid" 2>/dev/null || break; sleep 0.2; done
    kill -0 "$pid" 2>/dev/null && exit 1
    old="$work/previous.app"
    rm -rf "$old"
    if mv "$target" "$old"; then
        if mv "$new" "$target"; then
            rm -rf "$old"
        else
            rm -rf "$target"
            mv "$old" "$target"
        fi
    fi
    xattr -dr com.apple.quarantine "$target" 2>/dev/null
    open "$target"
    """

    // MARK: Verification

    nonisolated static func sha256(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    /// Same app, expected version, intact signature, and the same signing certificate as this copy (unless ad hoc).
    private static func verify(_ app: URL, version: AppVersion) throws {
        let reject = { (reason: String) in APIError.badResponse(L("下载的 app 无效：", "the downloaded app is invalid: ") + reason) }
        guard let info = NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist")) else {
            throw reject("no Info.plist")
        }
        guard info["CFBundleIdentifier"] as? String == Bundle.main.bundleIdentifier else { throw reject("bundle ID") }
        guard (info["CFBundleShortVersionString"] as? String).flatMap(AppVersion.init) == version else { throw reject("version") }

        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(app as CFURL, [], &code) == errSecSuccess, let code else { throw reject("signature") }
        let flags = SecCSFlags(rawValue: UInt32(kSecCSCheckAllArchitectures) | UInt32(kSecCSCheckNestedCode) | UInt32(kSecCSStrictValidate))
        let status = SecStaticCodeCheckValidity(code, flags, ownRequirement())
        if status == errSecCSReqFailed {
            throw reject(L("签名证书与当前版本不同", "it is signed with a different certificate than this copy"))
        }
        guard status == errSecSuccess else { throw reject("signature (\(status))") }
    }

    /// This copy's designated requirement, which names its signing certificate. Nil when signed ad hoc: that
    /// requirement is a hash of this exact build, which no other version can match.
    private static func ownRequirement() -> SecRequirement? {
        var code: SecCode?
        var staticCode: SecStaticCode?
        var info: CFDictionary?
        guard SecCodeCopySelf([], &code) == errSecSuccess, let code,
              SecCodeCopyStaticCode(code, [], &staticCode) == errSecSuccess, let staticCode,
              SecCodeCopySigningInformation(staticCode, SecCSFlags(rawValue: UInt32(kSecCSSigningInformation)), &info) == errSecSuccess,
              let dict = info as? [String: Any] else { return nil }
        let signatureFlags = (dict[kSecCodeInfoFlags as String] as? NSNumber)?.uint32Value ?? 0
        guard signatureFlags & SecCodeSignatureFlags.adhoc.rawValue == 0,
              dict[kSecCodeInfoCertificates as String] != nil else { return nil }
        var requirement: SecRequirement?
        guard SecCodeCopyDesignatedRequirement(staticCode, [], &requirement) == errSecSuccess else { return nil }
        return requirement
    }

    private static func run(_ tool: String, _ arguments: [String]) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            let process = Process()
            process.executableURL = URL(fileURLWithPath: tool)
            process.arguments = arguments
            process.standardOutput = FileHandle.nullDevice
            process.standardError = FileHandle.nullDevice
            process.terminationHandler = { process in
                if process.terminationStatus == 0 {
                    continuation.resume()
                } else {
                    continuation.resume(throwing: APIError.badResponse("\(tool) exited with \(process.terminationStatus)"))
                }
            }
            do { try process.run() } catch { continuation.resume(throwing: error) }
        }
    }
}
