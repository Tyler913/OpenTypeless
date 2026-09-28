import Foundation
import TypelessCore

/// Isolated OpenRouter benchmark; never initializes AppSettings or reads the Keychain.
/// See eval/README.md for budget, output privacy, repetition and dry-run options.
enum PolishEval {
    struct Case: Codable {
        let id: String
        let input: String
        var mustContain: [String]?
        var mustNotContain: [String]?
        var anyOf: [[String]]?
    }
    struct Job: Codable {
        let id: String
        let round: Int
        let plan: EvaluationPlan
    }
    struct Result: Codable {
        let id: String
        let round: Int
        let model: String
        let reservationID: String
        let parameters: EvaluationPlan
        let attempt: EvaluationAttempt
        let failures: [String]
        var latinRetention: Double?
        var similarity: Double?
    }
    static func run() async -> Int32 {
        let args = CommandLine.arguments
        func value(_ flag: String) -> String? {
            args.firstIndex(of: flag).flatMap { args.indices.contains($0 + 1) ? args[$0 + 1] : nil }
        }
        func save<T: Encodable>(_ obj: T, at path: String) throws {
            let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
            try encoder.encode(obj).write(to: URL(fileURLWithPath: path), options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path)
        }
        do {
            guard value("--provider") ?? "openrouter" == "openrouter",
                  let casesPath = value("--eval-polish"), let outPath = value("--out"),
                  let modelNames = value("--models") ?? value("--model") else {
                throw APIError.badResponse("required: --eval-polish CASES --models IDS --out PRIVATE_PATH; OpenRouter only")
            }
            // All detailed artifacts must live outside the working repository, including symlinks.
            // This file is macos/Sources/OpenTypeless/PolishEval.swift, four levels below the repository root.
            let cwd = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().resolvingSymlinksInPath().path
            for path in [outPath, value("--budget-ledger")].compactMap({ $0 }) {
                let resolved = URL(fileURLWithPath: path).resolvingSymlinksInPath().path
                guard !resolved.hasPrefix(cwd + "/"), resolved != cwd else {
                    throw APIError.badResponse("detailed output / budget ledger must be outside repository")
                }
            }
            let cases = try JSONDecoder().decode([Case].self, from: Data(contentsOf: URL(fileURLWithPath: casesPath)))
            guard !cases.isEmpty, Set(cases.map(\.id)).count == cases.count else {
                throw APIError.badResponse("empty cases or duplicate IDs")
            }
            let rounds = Int(value("--rounds") ?? "1") ?? 0
            guard (1...10).contains(rounds) else { throw APIError.badResponse("invalid rounds") }
            let models = modelNames.split(separator: ",").map(String.init)
            guard !models.isEmpty, Set(models).count == models.count else { throw APIError.badResponse("duplicate/empty models") }
            let dryRun = args.contains("--dry-run")
            // Opt-in: `--key-from-keychain` uses the key saved by the app instead of the environment.
            let key = args.contains("--key-from-keychain")
                ? (Credentials.load()["openrouter"] ?? "")
                : (ProcessInfo.processInfo.environment["OPENROUTER_API_KEY"] ?? "")
            guard dryRun || !key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                throw APIError.missingAPIKey("OPENROUTER_API_KEY environment variable")
            }
            let config = URLSessionConfiguration.ephemeral
            config.timeoutIntervalForResource = 240
            let client = APIClient(endpoint: .openRouter(apiKey: key), session: URLSession(configuration: config))
            let catalogData: Data
            if let path = value("--catalog") { catalogData = try Data(contentsOf: URL(fileURLWithPath: path)) }
            else {
                let (data, response) = try await URLSession.shared.data(from: URL(string: "https://openrouter.ai/api/v1/models")!)
                guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw APIError.badResponse("catalog unavailable") }
                catalogData = data
            }
            let catalog = (try JSONSerialization.jsonObject(with: catalogData) as? [String: Any])?["data"] as? [[String: Any]] ?? []
            let prompt = try value("--prompt-file").map { try String(contentsOfFile: $0, encoding: .utf8) }
            let providerTag = value("--provider-tag")
            let providerRouting = try value("--provider-routing").map { json -> [String: Any] in
                guard let object = try JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any] else {
                    throw APIError.badResponse("--provider-routing must be a JSON object")
                }
                return object
            }
            var endpointQuote: [String: Any]?
            if let providerTag {
                guard models.count == 1, let path = value("--endpoint-catalog"),
                      let obj = try JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: path))) as? [String: Any],
                      let data = obj["data"] as? [String: Any],
                      let rows = data["endpoints"] as? [[String: Any]],
                      let quote = rows.first(where: { $0["tag"] as? String == providerTag && $0["model_id"] as? String == models[0] && $0["status"] as? Int == 0 }) else {
                    throw APIError.badResponse("explicit route requires a verified healthy endpoint quote for one model")
                }
                endpointQuote = quote
            }
            var options: [String: PolishOptions] = [:]
            var prices: [String: (Double, Double)] = [:]
            var tierThresholds: [String: Int] = [:]
            for model in models {
                guard let row = catalog.first(where: { $0["id"] as? String == model }),
                      let pricing = (endpointQuote ?? row)["pricing"] as? [String: Any],
                      let ip = (pricing["prompt"] as? String).flatMap(Double.init),
                      let op = (pricing["completion"] as? String).flatMap(Double.init),
                      Double(pricing["request"] as? String ?? "0") == 0 else {
                    throw APIError.badResponse("model absent or unverifiable pricing: \(model)")
                }
                let reasoning = row["reasoning"] as? [String: Any] ?? [:]
                let info = APIClient.ModelInfo(id: model, name: model,
                    supportedParameters: (endpointQuote ?? row)["supported_parameters"] as? [String] ?? [],
                    reasoningMandatory: reasoning["mandatory"] as? Bool ?? false,
                    reasoningEfforts: reasoning["supported_efforts"] as? [String] ?? [])
                options[model] = PolishOptions(model: model, modelInfo: info, systemPromptOverride: prompt)
                prices[model] = (ip, op)
                tierThresholds[model] = (pricing["overrides"] as? [[String: Any]])?.compactMap { $0["min_prompt_tokens"] as? Int }.min()
            }
            var jobs: [Job] = []
            // Latin-style rotation per case AND round, all requests sequential on a reused session.
            for round in 1...rounds {
                for (index, c) in cases.enumerated() {
                    let offset = (index + round - 1) % models.count
                    for i in 0..<models.count {
                        let model = models[(i + offset) % models.count], price = prices[model]!
                        let plan = try client.evaluationPlan(transcript: c.input, options: options[model]!,
                                                            promptPrice: price.0, completionPrice: price.1, providerTag: providerTag,
                                                            providerRouting: providerRouting)
                        if let threshold = tierThresholds[model], plan.inputTokenBound >= threshold {
                            throw APIError.badResponse("input crosses unbudgeted price tier")
                        }
                        jobs.append(Job(id: c.id, round: round, plan: plan))
                    }
                }
            }
            if dryRun {
                try save(jobs, at: outPath)
                CLI.log(String(format: "dry-run: %d requests, conservative upper $%.6f", jobs.count,
                               jobs.map { $0.plan.upperBoundUSD }.reduce(0, +)))
                return 0
            }
            guard let ledgerPath = value("--budget-ledger"),
                  let budgetLimit = Double(value("--budget-usd") ?? "2"),
                  let future = Double(value("--future-reserve-usd") ?? "0"), future >= 0 else {
                throw APIError.badResponse("required: --budget-ledger PRIVATE_PATH; invalid budget/reserve")
            }
            guard !FileManager.default.fileExists(atPath: outPath) else {
                throw APIError.badResponse("output exists; use a new stage filename to avoid losing evidence")
            }
            let budget = try EvaluationBudget(url: URL(fileURLWithPath: ledgerPath), limit: budgetLimit)
            try budget.check(next: jobs.map { $0.plan.upperBoundUSD }.reduce(0, +), future: future)
            var results: [Result] = []
            try save(results, at: outPath)
            for job in jobs {
                let c = cases.first { $0.id == job.id }!
                let reservation = try budget.reserve(job.plan.upperBoundUSD, future: future)
                let attempt = await client.evaluatePolish(plan: job.plan)
                var failures: [String] = []
                for s in c.mustContain ?? [] where !attempt.output.contains(s) { failures.append("missing “\(s)”") }
                for s in c.mustNotContain ?? [] where attempt.output.contains(s) { failures.append("still has “\(s)”") }
                for g in c.anyOf ?? [] where !g.contains(where: attempt.output.contains) { failures.append("none of \(g)") }
                if Prompts.looksLikeAnAnswer(input: c.input, output: attempt.output) { failures.append("looks like an answer") }
                if attempt.truncated { failures.append("truncated") }
                if attempt.error != nil { failures.append("request error; see private attempt") }
                let ok = attempt.error == nil && !attempt.output.isEmpty
                results.append(Result(id: c.id, round: job.round, model: job.plan.model, reservationID: reservation,
                                      parameters: job.plan, attempt: attempt, failures: failures,
                                      latinRetention: ok ? PolishMetrics.latinRetention(
                                          input: c.input, output: attempt.output, ignoring: c.mustNotContain ?? []) : nil,
                                      similarity: ok ? PolishMetrics.similarity(c.input, attempt.output) : nil))
                try save(results, at: outPath)
                try budget.settle(reservation, cost: attempt.usage?.cost, requestID: attempt.requestID)
                CLI.log(String(format: "%d/%d %@ r%d %@ %.2fs · paid $%.6f · unresolved $%.6f",
                               results.count, jobs.count, job.plan.model, job.round,
                               failures.isEmpty ? "PASS" : "FAIL", attempt.seconds, budget.state.spent, budget.state.reserved))
            }
            summarize(results, models: models)
            return results.allSatisfy { $0.failures.isEmpty } ? 0 : 2
        } catch {
            CLI.log("evaluation stopped: \(error.localizedDescription)")
            return 1
        }
    }

    /// Per-model totals: checks passed, how much of the speaker's English and wording survived, and
    /// first-token / total latency percentiles.
    static func summarize(_ results: [Result], models: [String]) {
        func mean(_ xs: [Double]) -> String { xs.isEmpty ? "-" : String(format: "%.3f", xs.reduce(0, +) / Double(xs.count)) }
        func pct(_ xs: [Double], _ p: Double) -> String {
            guard !xs.isEmpty else { return "-" }
            let sorted = xs.sorted()
            return String(format: "%.2fs", sorted[min(sorted.count - 1, Int(Double(sorted.count) * p))])
        }
        for model in models {
            let rows = results.filter { $0.model == model }
            let ttft = rows.compactMap(\.attempt.firstTokenSeconds)
            let total = rows.filter { $0.attempt.error == nil }.map(\.attempt.seconds)
            CLI.log("\(model): pass \(rows.filter { $0.failures.isEmpty }.count)/\(rows.count)"
                + " · English kept \(mean(rows.compactMap(\.latinRetention)))"
                + " · similarity \(mean(rows.compactMap(\.similarity)))"
                + " · first token p50 \(pct(ttft, 0.5)) p90 \(pct(ttft, 0.9))"
                + " · total p50 \(pct(total, 0.5)) p90 \(pct(total, 0.9))")
        }
    }
}
