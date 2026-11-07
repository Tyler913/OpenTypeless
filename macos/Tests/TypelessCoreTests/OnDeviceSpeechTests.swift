import Foundation
import Testing
@testable import TypelessCore

/// A recognizer that answers from a script instead of Apple's models, which CI runners may not be able to download.
final class FakeRecognizer: OnDeviceRecognizer, @unchecked Sendable {
    struct Call { let samples: Int; let locale: String; let vocabulary: [String] }

    private let lock = NSLock()
    private var calls: [Call] = []
    private var failures: [APIError]
    let supported: [String]
    let answer: @Sendable (Int) -> String

    init(supported: [String] = OnDeviceSpeechTests.supported, failures: [APIError] = [],
         answer: @escaping @Sendable (Int) -> String = { "chunk \($0)" }) {
        self.supported = supported
        self.failures = failures
        self.answer = answer
    }

    var seen: [Call] { lock.withLock { calls } }

    func supportedLocales() async -> [String] { supported }

    func transcribe(samples: [Int16], locale: String, vocabulary: [String]) async throws -> String {
        let (index, failure) = lock.withLock { () -> (Int, APIError?) in
            calls.append(Call(samples: samples.count, locale: locale, vocabulary: vocabulary))
            return (calls.count - 1, failures.isEmpty ? nil : failures.removeFirst())
        }
        if let failure { throw failure }
        return answer(index)
    }
}

@Suite struct OnDeviceSpeechTests {
    static let supported = ["de-DE", "en-AU", "en-GB", "en-US", "es-ES", "es-MX", "fr-CA", "fr-FR", "ja-JP", "ko-KR",
                            "pt-BR", "pt-PT", "zh-CN", "zh-HK", "zh-TW"]

    // MARK: Setting

    @Test func engineIsReadFromTheSharedSetting() {
        #expect(SpeechEngine.defaultsKey == "speechEngine")
        #expect(SpeechEngine.provider.rawValue == "provider")
        #expect(SpeechEngine.onDevice.rawValue == "onDevice")
        #expect(SpeechEngine(stored: nil) == .provider)
        #expect(SpeechEngine(stored: "provider") == .provider)
        #expect(SpeechEngine(stored: "onDevice") == .onDevice)
        // A value a newer version wrote, or a typo: as before there was a choice.
        #expect(SpeechEngine(stored: "cloud") == .provider)
        #expect(SpeechEngine(stored: "") == .provider)
    }

    // MARK: Setup needed

    @Test func onDeviceNeedsNoKey() {
        #expect(SpeechSetup.ready(engine: .onDevice, providerConfigured: false))
        #expect(SpeechSetup.ready(engine: .onDevice, providerConfigured: true))
        #expect(!SpeechSetup.ready(engine: .provider, providerConfigured: false))
        #expect(SpeechSetup.ready(engine: .provider, providerConfigured: true))
    }

    @Test func cleanUpWithoutAKeyIsSkippedOnlyOnDevice() {
        // No key for the clean-up: on-device dictations go in as recognised, without an error.
        #expect(!SpeechSetup.cleansUp(enabled: true, engine: .onDevice, providerConfigured: false))
        // A provider for speech-to-text: tried, and its missing key reported, as before.
        #expect(SpeechSetup.cleansUp(enabled: true, engine: .provider, providerConfigured: false))
        #expect(SpeechSetup.cleansUp(enabled: true, engine: .onDevice, providerConfigured: true))
        #expect(!SpeechSetup.cleansUp(enabled: false, engine: .onDevice, providerConfigured: true))
        #expect(!SpeechSetup.cleansUp(enabled: false, engine: .provider, providerConfigured: true))
    }

    // MARK: Locale

    private func locale(_ language: String, _ preferred: [String], _ supported: [String] = Self.supported) -> String? {
        OnDeviceLocale.resolve(language: language, preferred: preferred, supported: supported)
    }

    @Test func aLanguageTakesTheUsersRegionElseItsLikeliestOne() {
        #expect(locale("en", ["en-GB"]) == "en-GB")
        #expect(locale("en", ["zh-Hans-CN"]) == "en-US")
        #expect(locale("zh", ["en-US"]) == "zh-CN")
        #expect(locale("zh", ["zh-Hant-TW", "en-US"]) == "zh-TW")
        #expect(locale("pt", ["en-US"]) == "pt-BR")
        #expect(locale("pt", ["pt-PT"]) == "pt-PT")
        #expect(locale("fr", ["en-CA", "fr-CA"]) == "fr-CA")
        #expect(locale("ja", []) == "ja-JP")
        #expect(locale("en-GB", ["en-US"]) == "en-GB")
        #expect(locale(" ko ", []) == "ko-KR")
    }

    @Test func autoDetectFollowsTheSystemLanguagesThenEnglish() {
        #expect(locale("", ["zh-Hans-CN", "en-US"]) == "zh-CN")
        #expect(locale("", ["zh-Hant-HK"]) == "zh-HK")
        #expect(locale("", ["sv-SE", "de-DE"]) == "de-DE")
        #expect(locale("", ["sv-SE"]) == "en-US")
        #expect(locale("", []) == "en-US")
    }

    @Test func aLanguageWithoutAModelHasNoLocale() {
        #expect(locale("ru", ["ru-RU"]) == nil)
        #expect(locale("", ["sv-SE"], ["ja-JP"]) == nil)
        #expect(locale("en", ["en-US"], []) == nil)
    }

    @Test func anAppProfilesLanguagePicksTheLocale() async throws {
        let global = DictationSettings(polishEnabled: true, pressEnter: false, language: "", extraInstructions: "")
        let profile = AppProfile(app: "com.example.chat", name: "Chat", language: "ja")
        let recognizer = FakeRecognizer()
        let first = self.pipeline(recognizer, language: global.applying(profile).language)
        first.append(LatencyAudio.tone(2))
        _ = try await first.finish()
        #expect(recognizer.seen.map(\.locale) == ["ja-JP"])

        let other = FakeRecognizer()
        let auto = self.pipeline(other, language: global.applying(nil).language, preferred: ["fr-FR"])
        auto.append(LatencyAudio.tone(2))
        _ = try await auto.finish()
        #expect(other.seen.map(\.locale) == ["fr-FR"])
    }

    // MARK: Pipeline

    private func pipeline(_ recognizer: FakeRecognizer, language: String?, preferred: [String] = ["en-US"],
                          vocabulary: [String] = [], preset: [Int: String] = [:]) -> TranscriptionPipeline {
        TranscriptionPipeline(client: OnDeviceRoute(recognizer: recognizer, preferredLanguages: preferred),
                              options: .init(model: "", language: language.flatMap { $0.isEmpty ? nil : $0 },
                                             vocabulary: vocabulary),
                              policy: OnDeviceRoute.policy, preset: preset, maxConcurrentRequests: 1, speculate: false)
    }

    @Test func chunksAreRecognisedInOrderAndCostNothing() async throws {
        let recognizer = FakeRecognizer(answer: { _ in "Hello there." })
        let pipeline = self.pipeline(recognizer, language: "en", vocabulary: ["OpenTypeless", "SwiftUI"])
        // Two chunks: cut by 28 s at the latest.
        LatencyAudio.feed(pipeline, LatencyAudio.tone(30) + LatencyAudio.tone(5))
        let text = try await pipeline.finish()
        #expect(text == "Hello there. Hello there.")
        #expect(pipeline.isOnDevice)
        #expect(recognizer.seen.count == 2)
        #expect(recognizer.seen.allSatisfy { $0.locale == "en-US" && $0.vocabulary == ["OpenTypeless", "SwiftUI"] })

        // $0, whatever price the provider's model has, and nothing left unpriced.
        let usages = pipeline.requestUsages()
        #expect(usages.count == 2)
        #expect(usages.allSatisfy { $0.cost == 0 && ($0.audioSeconds ?? 0) > 0 })
        let cost = pipeline.transcriptionCost(price: ModelPrice(inputPerMillion: 2, outputPerMillion: 10, perMinute: 0.006),
                                              preferReported: false)
        #expect(cost.cost == 0)
        #expect(cost.unpriced == 0)
    }

    @Test func aProvidersCostIsStillPriced() async throws {
        // The same accounting for a provider route prices by the minute, so the $0 above is the route's doing.
        let usage = RequestUsage(audioSeconds: 60)
        let priced = CostEstimator.transcriptions([usage], price: ModelPrice(perMinute: 0.006), preferReported: false)
        #expect(abs(priced.cost - 0.006) < 1e-12)
    }

    @Test func anUnsupportedLanguageFailsClearlyAndCanBeRetried() async throws {
        let recognizer = FakeRecognizer()
        let pipeline = self.pipeline(recognizer, language: "ru")
        pipeline.append(LatencyAudio.tone(2))
        do {
            _ = try await pipeline.finish()
            Issue.record("expected a failure")
        } catch let failure as PipelineFailure {
            #expect(failure.underlying == "On-device recognition doesn't support this language.")
            #expect(failure.failedChunks == [0])
        }
        // Nothing reached the recognizer, and it isn't taken for a passing failure: a file stops at it.
        #expect(recognizer.seen.isEmpty)
        #expect(pipeline.chunkCounts().refused == 1)
        #expect(!APIError.unavailable("x").isRetryable)

        // Retried with a language there is a model for, it goes through.
        let retry = self.pipeline(recognizer, language: "de")
        retry.append(LatencyAudio.tone(2))
        #expect(try await retry.finish() == "chunk 0")
        #expect(recognizer.seen.map(\.locale) == ["de-DE"])
    }

    @Test func aRecognizerFailureIsTriedOnceMore() async throws {
        let recognizer = FakeRecognizer(failures: [.recognition("busy")], answer: { _ in "ok" })
        let pipeline = self.pipeline(recognizer, language: "en")
        pipeline.append(LatencyAudio.tone(2))
        #expect(try await pipeline.finish() == "ok")
        #expect(recognizer.seen.count == 2)
    }

    @Test func aRetryReusesTheTranscriptsItHas() async throws {
        let recognizer = FakeRecognizer(answer: { _ in "new" })
        let pipeline = self.pipeline(recognizer, language: "en", preset: [0: "kept"])
        LatencyAudio.feed(pipeline, LatencyAudio.tone(30) + LatencyAudio.tone(5))
        #expect(try await pipeline.finish() == "kept new")
        #expect(recognizer.seen.count == 1)
    }

    @Test func silenceIsNeverRecognised() async throws {
        let recognizer = FakeRecognizer()
        let pipeline = self.pipeline(recognizer, language: "en")
        pipeline.append(LatencyAudio.silence(3))
        #expect(try await pipeline.finish() == "")
        #expect(recognizer.seen.isEmpty)
    }

    // MARK: Text

    @Test func spacesInChineseAndJapaneseAreTakenOut() {
        #expect(OnDeviceRoute.tidy("你好 ，这是一个 测试 。") == "你好，这是一个测试。")
        #expect(OnDeviceRoute.tidy("これは テスト です。") == "これはテストです。")
        #expect(OnDeviceRoute.tidy("我用 Swift 写代码") == "我用 Swift 写代码")
        #expect(OnDeviceRoute.tidy(" Hello , world. ") == "Hello , world.")
        #expect(OnDeviceRoute.tidy("안녕하세요 반갑습니다") == "안녕하세요 반갑습니다")
    }
}
