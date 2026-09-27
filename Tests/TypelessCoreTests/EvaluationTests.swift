import Foundation
import Testing
@testable import TypelessCore

struct EvaluationTests {
    @Test func finalUsageAfterFinishAndEmptyChoices() {
        var a = EvaluationAttempt()
        a.consume(#"data: {"id":"gen-test","model":"served-model","choices":[{"delta":{"content":"你好"},"finish_reason":"stop"}]}"#)
        a.consume(#"data: {"choices":[],"usage":{"prompt_tokens":20,"completion_tokens":2,"total_tokens":22,"cost":0.00012}}"#)
        a.consume("data: [DONE]")
        #expect(a.requestID == "gen-test")
        #expect(a.responseModel == "served-model")
        #expect(a.usage?.prompt_tokens == 20)
        #expect(a.usage?.cost == 0.00012)
        #expect(a.receivedDone)
        #expect(a.rawOutput == "你好")
    }
    @Test func repeatedFinishDoesNotDuplicateOutputAndMissingCostIsUnknown() {
        var a = EvaluationAttempt()
        a.consume(#"data: {"choices":[{"delta":{"content":"x"},"finish_reason":"length"}]}"#)
        a.consume(#"data: {"choices":[{"delta":{"content":""},"finish_reason":"length"}],"usage":{"prompt_tokens":7,"completion_tokens":1}}"#)
        #expect(a.truncated)
        #expect(a.usage?.cost == nil)
        #expect(a.rawOutput == "x")
        a.consume(#"data: {"id":"g","error":{"message":"upstream failed"}}"#)
        #expect(a.error != nil)
        #expect(a.requestID == "g")
    }
    @Test func reservationsSurviveRestartAndIncludeFutureStages() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let url = dir.appendingPathComponent("ledger.json")
        var ledger: EvaluationBudget? = try EvaluationBudget(url: url, limit: 2)
        let id = try ledger!.reserve(0.8, future: 1.0)
        #expect(throws: (any Error).self) { try ledger!.reserve(0.3, future: 1.0) }
        try ledger!.settle(id, cost: nil, requestID: "gen-missing")
        #expect(ledger!.state.reserved == 0.8)
        #expect(throws: (any Error).self) { try EvaluationBudget(url: url, limit: 2) }
        ledger = nil
        let next = try EvaluationBudget(url: url, limit: 2)
        #expect(next.state.reserved == 0.8)
        try next.settle(id, cost: 0.2, requestID: "gen-missing")
        #expect(next.state.spent == 0.2)
        #expect(next.state.reserved == 0)
        #expect(throws: (any Error).self) { try next.reserve(1.81) }
        #expect(throws: (any Error).self) { try next.reserve(.nan) }
    }
    @Test func evaluationGuardsDoNotChangeProductionBody() throws {
        let client = APIClient(endpoint: .openRouter(apiKey: "test"))
        let info = APIClient.ModelInfo(id: "m", name: "m", supportedParameters: ["max_tokens", "temperature"])
        let options = PolishOptions(model: "m", modelInfo: info)
        let original = client.polishBody(transcript: "hello", options: options, includeOptional: true)
        let plan = try client.evaluationPlan(transcript: "hello", options: options, promptPrice: 0.000001, completionPrice: 0.000002)
        let evaluated = try JSONSerialization.jsonObject(with: Data(plan.bodyJSON.utf8)) as! [String: Any]
        #expect(evaluated["messages"] as? [[String: String]] == original["messages"] as? [[String: String]])
        #expect(evaluated["max_tokens"] as? Int == original["max_tokens"] as? Int)
        #expect((original["provider"] as? [String: Any])?["allow_fallbacks"] as? Bool == true)
        // Evaluation routes exactly like the app.
        #expect((evaluated["provider"] as? [String: Any])?["allow_fallbacks"] as? Bool == true)
        #expect((evaluated["provider"] as? [String: Any])?["max_price"] == nil)
        #expect(evaluated["usage"] == nil)
        #expect(plan.upperBoundUSD > 0)
    }
}

extension PipelineTests {
    @Test func evaluatedFailureIsNotRetriedAndBillingRecovered() async throws {
        var posts = 0
        MockOpenRouter.handler = { request, _ in
            if request.httpMethod == "GET" {
                return (200, Data(#"{"data":{"total_cost":0.002,"native_tokens_prompt":12,"native_tokens_completion":1}}"#.utf8))
            }
            posts += 1
            return (200, Data("data: {\"id\":\"gen-error\",\"error\":{\"message\":\"unsupported parameter\"}}\n\ndata: [DONE]\n\n".utf8))
        }
        let client = APIClient(endpoint: .openRouter(apiKey: "test"), session: MockOpenRouter.session())
        let info = APIClient.ModelInfo(id: "m", name: "m", supportedParameters: ["max_tokens"])
        let plan = try client.evaluationPlan(transcript: "x", options: .init(model: "m", modelInfo: info), promptPrice: 0.000001, completionPrice: 0.000002)
        let result = await client.evaluatePolish(plan: plan)
        #expect(posts == 1)
        #expect(result.error != nil)
        #expect(result.usage?.cost == 0.002)
        #expect(result.costSource == "generation.total_cost")
        #expect(result.retryCount == 0)
    }
}
