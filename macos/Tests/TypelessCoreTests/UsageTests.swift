import Foundation
import Testing
@testable import TypelessCore

/// Word counts, request usage and pricing, and the ledger behind the Home page.
@Suite struct UsageTests {
    /// testdata/usage-cases.json, shared with the Windows tests so both apps agree.
    static func cases() throws -> [String: Any] {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/usage-cases.json"))
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    @Test func wordCountsMatchSharedCases() throws {
        for item in try Self.cases()["wordCounts"] as! [[String: Any]] {
            let text = item["text"] as! String
            #expect(WordCount.count(text) == item["words"] as! Int, "\(text)")
        }
    }

    @Test func costsMatchSharedCases() throws {
        for item in try Self.cases()["costs"] as! [[String: Any]] {
            let u = item["usage"] as! [String: Any]
            let usage = RequestUsage(inputTokens: u["input"] as? Int, outputTokens: u["output"] as? Int,
                                     audioSeconds: RequestUsage.number(u["seconds"]), cost: RequestUsage.number(u["cost"]),
                                     estimated: u["estimated"] as? Bool ?? false)
            let price = ModelPrice(json: item["price"])
            let preferReported = item["preferReported"] as! Bool
            let cost = item["kind"] as! String == "chat"
                ? CostEstimator.chat(usage, price: price, preferReported: preferReported)
                : CostEstimator.transcription(usage, price: price, preferReported: preferReported)
            if let expected = RequestUsage.number(item["expected"]) {
                #expect(cost.map { abs($0 - expected) < 1e-12 } == true, "\(item): got \(String(describing: cost))")
            } else {
                #expect(cost == nil, "\(item)")
            }
        }
    }

    @Test func timeSavedMatchesSharedCases() throws {
        for item in try Self.cases()["timeSaved"] as! [[String: Any]] {
            let totals = UsageTotals(words: item["words"] as! Int, dictations: 1, speakingSeconds: RequestUsage.number(item["seconds"])!)
            let wpm = RequestUsage.number(item["typingWPM"])!
            #expect(abs(totals.typingSeconds(wordsPerMinute: wpm) - RequestUsage.number(item["typingSeconds"])!) < 1e-6)
            #expect(abs(totals.savedSeconds(wordsPerMinute: wpm) - RequestUsage.number(item["savedSeconds"])!) < 1e-6)
            if let speaking = RequestUsage.number(item["speakingWPM"]) {
                #expect(totals.speakingWordsPerMinute.map { abs($0 - speaking) < 1e-6 } == true)
            } else {
                #expect(totals.speakingWordsPerMinute == nil)
            }
        }
    }

    private func json(_ text: String) -> Any { try! JSONSerialization.jsonObject(with: Data(text.utf8)) }

    @Test func parsesUsageFromTranscriptionsAndChat() {
        let stt = RequestUsage.parse(json(#"{"seconds":9.2,"total_tokens":113,"input_tokens":83,"output_tokens":30,"cost":0.000508}"#))
        #expect(stt == RequestUsage(inputTokens: 83, outputTokens: 30, audioSeconds: 9.2, cost: 0.000508))
        let chat = RequestUsage.parse(json(#"{"prompt_tokens":194,"completion_tokens":2,"total_tokens":196,"cost":0.95}"#))
        #expect(chat == RequestUsage(inputTokens: 194, outputTokens: 2, cost: 0.95))
        #expect(RequestUsage.parse(json(#"{"type":"duration"}"#)) == nil)
        #expect(RequestUsage.parse(nil) == nil)
        #expect(RequestUsage.parse(json(#"{"cost":-1}"#)) == nil)

        let events = SSEParser().parse(line: #"data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"cost":0.0001}}"#)
        #expect(events.contains(.usage(RequestUsage(inputTokens: 10, outputTokens: 5, cost: 0.0001))))
        #expect(events.contains(.finished(reason: "stop")))
    }

    @Test func estimatesTokensFromText() {
        #expect(TokenEstimate.count("") == 0)
        #expect(TokenEstimate.count("你好世界") == 4)
        #expect(TokenEstimate.count("hello world") == 3) // 11 characters / 4, rounded up
    }

    @Test func openRouterBodyHasNoStreamOptions() {
        let body = APIClient(endpoint: .openRouter(apiKey: "k")).polishBody(transcript: "x", options: PolishOptions(model: "m"), includeOptional: true)
        #expect(body["stream_options"] == nil) // OpenRouter always reports usage; the parameter is deprecated there
        let custom = APIClient(endpoint: ProviderEndpoint(id: .custom, baseURL: URL(string: "https://example.com/v1")!, apiKey: ""))
            .polishBody(transcript: "x", options: PolishOptions(model: "m"), includeOptional: true)
        #expect((custom["stream_options"] as? [String: Bool])?["include_usage"] == true)
    }

    @Test func parsesOpenRouterPrices() {
        let data = Data("""
            {"data":[
              {"id":"google/gemini-3.8-flash","pricing":{"prompt":"0.00000075","completion":"0.00000375","web_search":"0.014"}},
              {"id":"openrouter/auto","pricing":{"prompt":"-1","completion":"-1"}},
              {"id":"free/model","pricing":{"prompt":"0","completion":"0"}},
              {"id":"broken"}
            ]}
            """.utf8)
        let models = PriceCatalog.parseModels(data)
        #expect(models.count == 2)
        #expect(abs(models["google/gemini-3.8-flash"]!.inputPerMillion! - 0.75) < 1e-9)
        #expect(abs(models["google/gemini-3.8-flash"]!.outputPerMillion! - 3.75) < 1e-9)
        #expect(models["free/model"]?.inputPerMillion == 0)

        let catalog = PriceCatalog(fetchedAt: Date(timeIntervalSince1970: 1_790_000_000), models: models)
        let restored = PriceCatalog(jsonData: catalog.jsonData()!)
        #expect(restored == catalog)
        #expect(catalog.isOlder(than: 6 * 3600, now: catalog.fetchedAt.addingTimeInterval(7 * 3600)))
        #expect(PriceCatalog(jsonData: Data("not json".utf8)) == nil)
    }

    @Test func modelPriceRoundTripsAndKeys() throws {
        let price = ModelPrice(inputPerMillion: 0.5, outputPerMillion: 1.5, perMinute: 0.006)
        let stored = try JSONSerialization.jsonObject(with: JSONEncoder().encode(price))
        #expect(ModelPrice(json: stored) == price)
        #expect(ModelPrice(json: [String: Any]()) == nil)
        #expect(ModelPrice.key(.custom, " my-model ") == "custom|my-model")
    }

    @Test func calendarDays() {
        let day = CalendarDay(year: 2026, month: 2, day: 28)
        #expect(day.adding(days: 1) == CalendarDay(year: 2026, month: 3, day: 1))
        #expect(day.adding(days: -59) == CalendarDay(year: 2025, month: 12, day: 31))
        #expect(CalendarDay(year: 2026, month: 9, day: 27).weekday == 0) // a Sunday
        #expect(CalendarDay(key: "2026-09-28") == CalendarDay(year: 2026, month: 9, day: 28))
        #expect(CalendarDay(key: "nonsense") == nil)
        #expect(CalendarDay(year: 2026, month: 1, day: 2).key == "2026-01-02")
    }

    @Test func ledgerTotalsByDayAndMonth() {
        var ledger = UsageLedger()
        let today = CalendarDay(year: 2026, month: 9, day: 28)
        ledger.addDictation(on: today, words: 120, seconds: 40)
        ledger.addDictation(on: today, words: 80, seconds: 20)
        ledger.addCost(on: today, transcription: 0.001, cleanup: 0.002, unpriced: 0)
        ledger.addDictation(on: CalendarDay(year: 2026, month: 9, day: 1), words: 500, seconds: 100)
        ledger.addCost(on: CalendarDay(year: 2026, month: 9, day: 1), transcription: 0, cleanup: 0, unpriced: 2)
        ledger.addDictation(on: CalendarDay(year: 2026, month: 8, day: 31), words: 1000, seconds: 300)
        ledger.addCost(on: CalendarDay(year: 2026, month: 8, day: 31), transcription: 0, cleanup: 0, unpriced: 0)

        let day = ledger.today(today)
        #expect(day.words == 200 && day.dictations == 2 && day.speakingSeconds == 60)
        #expect(abs(day.cost - 0.003) < 1e-9)
        let month = ledger.month(today)
        #expect(month.words == 700 && month.dictations == 3 && month.unpriced == 2)
        #expect(ledger.totals().words == 1700)
        #expect(ledger.days.count == 3)

        #expect(UsageLedger(jsonData: ledger.jsonData()!) == ledger)
        #expect(UsageLedger(jsonData: Data("{broken".utf8)).isEmpty)
    }

    @Test func ledgerSavesAndLoads() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("usage-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: url) }
        #expect(UsageLedger.load(from: url) == nil)
        var ledger = UsageLedger()
        ledger.addDictation(on: CalendarDay(year: 2026, month: 1, day: 2), words: 3, seconds: 1.5)
        ledger.save(to: url)
        #expect(UsageLedger.load(from: url)?.totals().words == 3)
        #expect(try String(contentsOf: url, encoding: .utf8).contains("\"2026-01-02\""))
    }

    @Test func streaks() {
        var ledger = UsageLedger()
        let today = CalendarDay(year: 2026, month: 9, day: 28)
        #expect(ledger.streaks(today: today) == (0, 0))
        for offset in [1, 2, 3, 10, 11, 12, 13, 14] { ledger.addDictation(on: today.adding(days: -offset), words: 10, seconds: 5) }
        // Today has nothing yet, so the run up to yesterday still counts.
        #expect(ledger.streaks(today: today) == (3, 5))
        ledger.addDictation(on: today, words: 1, seconds: 1)
        #expect(ledger.streaks(today: today) == (4, 5))
        #expect(ledger.streaks(today: today.adding(days: 2)) == (0, 5))
    }

    @Test func heatmapEndsWithThisWeekAndShadesByQuartile() {
        var ledger = UsageLedger()
        let today = CalendarDay(year: 2026, month: 9, day: 30) // a Wednesday
        ledger.addDictation(on: today, words: 400, seconds: 10)
        ledger.addDictation(on: today.adding(days: -1), words: 100, seconds: 10)
        ledger.addDictation(on: today.adding(days: -2), words: 200, seconds: 10)
        ledger.addDictation(on: today.adding(days: -3), words: 300, seconds: 10)
        ledger.addDictation(on: today.adding(days: -100), words: 999, seconds: 10) // outside the grid

        let sundayFirst = ledger.heatmap(today: today, weeks: 4, firstWeekday: 0)
        #expect(sundayFirst.count == 4)
        #expect(sundayFirst.allSatisfy { $0.count == 7 })
        #expect(sundayFirst[0][0].date.weekday == 0)
        let last = sundayFirst[3]
        #expect(last[0].date == CalendarDay(year: 2026, month: 9, day: 27))
        #expect(last[3].date == today && !last[3].isFuture && last[4].isFuture)
        #expect(last[0..<4].map(\.level) == [3, 2, 1, 4]) // 300, 200, 100, 400 words
        #expect(sundayFirst[0][0].level == 0)
        #expect(last[3].words == 400 && last[3].dictations == 1) // what hovering a day shows
        #expect(last[4].dictations == 0)

        let mondayFirst = ledger.heatmap(today: today, weeks: 1, firstWeekday: 1)
        #expect(mondayFirst[0][0].date == CalendarDay(year: 2026, month: 9, day: 28))
        #expect(mondayFirst[0][6].date == CalendarDay(year: 2026, month: 10, day: 4))
    }

    @Test func heatmapLevels() {
        #expect(UsageLedger.thresholds([0, 0]) == [0, 0, 0])
        let thresholds = UsageLedger.thresholds([0, 10, 20, 30, 40, 50])
        #expect(thresholds == [20, 30, 40])
        #expect(UsageLedger.level(0, thresholds: thresholds) == 0)
        #expect(UsageLedger.level(5, thresholds: thresholds) == 1)
        #expect(UsageLedger.level(41, thresholds: thresholds) == 4)
        #expect(UsageLedger.level(7, thresholds: UsageLedger.thresholds([7])) == 1)
    }
}

@Suite struct UsageFormatTests {
    @Test func money() {
        #expect(UsageFormat.money(0) == "$0")
        #expect(UsageFormat.money(0.00004) == "< $0.0001")
        #expect(UsageFormat.money(0.0042) == "$0.0042")
        #expect(UsageFormat.money(0.0341) == "$0.034")
        #expect(UsageFormat.money(1.2) == "$1.20")
        #expect(UsageFormat.money(1234.5) == "$1,234.50")
        #expect(UsageFormat.rate(0.75) == "$0.75")
        #expect(UsageFormat.rate(0.006) == "$0.006")
        #expect(UsageFormat.rate(15) == "$15")
        #expect(UsageFormat.count(12345) == "12,345")
    }

    @Test func transcriptionsSumAndCountUnpriced() {
        let usages = [RequestUsage(audioSeconds: 30, cost: 0.001), RequestUsage(audioSeconds: 30), RequestUsage(audioSeconds: 60, cost: 0.002)]
        let reported = CostEstimator.transcriptions(usages, price: nil, preferReported: true)
        #expect(abs(reported.cost - 0.003) < 1e-9 && reported.unpriced == 1)
        let perMinute = CostEstimator.transcriptions(usages, price: ModelPrice(perMinute: 0.006), preferReported: false)
        #expect(abs(perMinute.cost - 0.012) < 1e-9 && perMinute.unpriced == 0)
    }
}

@Suite struct CancelPolicyTests {
    @Test func keepsLongRecordingsForADay() {
        #expect(!CancelPolicy.keeps(recordedSeconds: 9.9))
        #expect(CancelPolicy.keeps(recordedSeconds: 10))
        #expect(CancelPolicy.keeps(recordedSeconds: 125))
        let at = Date(timeIntervalSince1970: 1_790_000_000)
        #expect(CancelPolicy.expiry(of: at) == at.addingTimeInterval(86_400))
        #expect(!CancelPolicy.isExpired(recordedAt: at, now: at.addingTimeInterval(23.9 * 3600)))
        #expect(CancelPolicy.isExpired(recordedAt: at, now: at.addingTimeInterval(24 * 3600)))
    }
}

// These share MockOpenRouter.handler with the pipeline tests, so they must run in the same serialized suite.
extension PipelineTests {
    @Test func transcriptionReportsUsageAndAudioLength() async throws {
        MockOpenRouter.handler = { _, _ in (200, Data(#"{"text":"hi","usage":{"seconds":1.5,"cost":0.0002}}"#.utf8)) }
        let client = APIClient(endpoint: .openRouter(apiKey: "k"), session: MockOpenRouter.session())
        let samples = [Int16](repeating: 0, count: AudioFormat.sampleCount(forSeconds: 2))
        let reported = try await client.transcribeDetailedWithRetry(samples: samples, options: .init(model: "m"))
        #expect(reported.usage == RequestUsage(audioSeconds: 1.5, cost: 0.0002))

        // A server that reports nothing still gives the audio length, so per-minute prices work.
        MockOpenRouter.handler = { _, _ in (200, Data(#"{"text":"hi"}"#.utf8)) }
        let bare = try await client.transcribeDetailedWithRetry(samples: samples, options: .init(model: "m"))
        #expect(abs((bare.usage?.audioSeconds ?? 0) - 2) < 0.001)
        #expect(bare.usage?.estimated == true)
        #expect(try await client.transcribeWithRetry(samples: samples, options: .init(model: "m")) == "hi")
    }

    @Test func polishReportsStreamUsageOrEstimatesIt() async throws {
        MockOpenRouter.handler = { _, _ in
            (200, Data(("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n"
                + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":900,\"completion_tokens\":3,\"cost\":0.00042}}\n\n"
                + "data: [DONE]\n\n").utf8))
        }
        let openRouter = APIClient(endpoint: .openRouter(apiKey: "k"), session: MockOpenRouter.session())
        let reported = try await openRouter.polish(transcript: "x", options: PolishOptions(model: "m"))
        #expect(reported.usage == RequestUsage(inputTokens: 900, outputTokens: 3, cost: 0.00042))

        MockOpenRouter.handler = { _, _ in (200, Data("data: {\"choices\":[{\"delta\":{\"content\":\"好的\"}}]}\n\ndata: [DONE]\n\n".utf8)) }
        let custom = APIClient(endpoint: ProviderEndpoint(id: .custom, baseURL: URL(string: "https://example.com/v1")!, apiKey: ""),
                               session: MockOpenRouter.session())
        let estimated = try await custom.polish(transcript: "x", options: PolishOptions(model: "m"))
        #expect(estimated.usage?.estimated == true)
        #expect(estimated.usage?.outputTokens == 2)
        #expect((estimated.usage?.inputTokens ?? 0) > 500) // the system prompt is most of it
    }

    @Test func polishDropsStreamOptionsWhenRejected() async throws {
        var bodies: [[String: Any]] = []
        MockOpenRouter.handler = { _, body in
            let json = try! JSONSerialization.jsonObject(with: body) as! [String: Any]
            bodies.append(json)
            if json["stream_options"] != nil {
                return (400, Data(#"{"error":{"message":"Unrecognized request argument supplied: stream_options"}}"#.utf8))
            }
            return (200, Data("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n".utf8))
        }
        let client = APIClient(endpoint: ProviderEndpoint(id: .openai, baseURL: URL(string: "https://example.com/v1")!, apiKey: "k"),
                               session: MockOpenRouter.session())
        let result = try await client.polish(transcript: "x", options: PolishOptions(model: "m"))
        #expect(result.text == "ok")
        #expect(bodies.count == 2)
        #expect(bodies.last?["stream_options"] == nil)
    }

    @Test func pipelineCollectsUsageOfEveryRequest() async throws {
        MockOpenRouter.handler = { _, _ in (200, Data(#"{"text":"part","usage":{"cost":0.001}}"#.utf8)) }
        let pipeline = TranscriptionPipeline(client: APIClient(endpoint: .openRouter(apiKey: "k"), session: MockOpenRouter.session()),
                                             options: .init(model: "m"))
        let samples = (0..<AudioFormat.sampleCount(forSeconds: 65)).map { i -> Int16 in
            AudioFormat.seconds(forSampleCount: i).truncatingRemainder(dividingBy: 20) < 0.5 ? 0 : Int16(6000 * sin(Double(i) * 0.2))
        }
        pipeline.append(samples)
        _ = try await pipeline.finish()
        let usages = pipeline.requestUsages()
        #expect(usages.count >= 3)
        #expect(usages.allSatisfy { $0.cost == 0.001 })
        #expect(abs(usages.reduce(0) { $0 + ($1.audioSeconds ?? 0) } - 65) < 0.5)
    }

    @Test func fetchesBothOpenRouterModelLists() async throws {
        var urls: [String] = []
        MockOpenRouter.handler = { request, _ in
            let url = request.url!
            urls.append(url.path + (url.query.map { "?" + $0 } ?? ""))
            let id = (url.query ?? "").contains("transcription") ? "stt/model" : "chat/model"
            return (200, Data(#"{"data":[{"id":"\#(id)","pricing":{"prompt":"0.000001","completion":"0"}}]}"#.utf8))
        }
        let catalog = try await PriceCatalog.fetch(baseURL: URL(string: "https://openrouter.ai/api/v1")!, session: MockOpenRouter.session())
        #expect(urls == ["/api/v1/models", "/api/v1/models?output_modalities=transcription"])
        #expect(catalog.price("stt/model") != nil && catalog.price("chat/model") != nil)
    }
}
