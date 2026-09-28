using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

public class EvaluationTests
{
    [Fact]
    public void FinalUsageAfterFinishAndEmptyChoices()
    {
        var a = new EvaluationAttempt();
        a.Consume("""data: {"id":"gen-test","model":"served-model","choices":[{"delta":{"content":"你好"},"finish_reason":"stop"}]}""");
        a.Consume("""data: {"choices":[],"usage":{"prompt_tokens":20,"completion_tokens":2,"total_tokens":22,"cost":0.00012}}""");
        a.Consume("data: [DONE]");
        Assert.Equal("gen-test", a.RequestId);
        Assert.Equal("served-model", a.ResponseModel);
        Assert.Equal(20, a.Usage?.PromptTokens);
        Assert.Equal(0.00012, a.Usage?.Cost);
        Assert.True(a.ReceivedDone);
        Assert.Equal("你好", a.RawOutput);
    }

    [Fact]
    public void RepeatedFinishDoesNotDuplicateOutputAndMissingCostIsUnknown()
    {
        var a = new EvaluationAttempt();
        a.Consume("""data: {"choices":[{"delta":{"content":"x"},"finish_reason":"length"}]}""");
        a.Consume("""data: {"choices":[{"delta":{"content":""},"finish_reason":"length"}],"usage":{"prompt_tokens":7,"completion_tokens":1}}""");
        Assert.True(a.Truncated);
        Assert.Null(a.Usage?.Cost);
        Assert.Equal("x", a.RawOutput);
        a.Consume("""data: {"id":"g","error":{"message":"upstream failed"}}""");
        Assert.NotNull(a.Error);
        Assert.Equal("g", a.RequestId);
    }

    [Fact]
    public void ReservationsSurviveRestartAndIncludeFutureStages()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var ledger = new EvaluationBudget(path, 2);
            var id = ledger.Reserve(0.8, future: 1.0);
            Assert.ThrowsAny<Exception>(() => ledger.Reserve(0.3, future: 1.0));
            ledger.Settle(id, null, "gen-missing");
            Assert.Equal(0.8, ledger.State.Reserved);
            Assert.ThrowsAny<Exception>(() => new EvaluationBudget(path, 2));
            ledger.Dispose();
            using var next = new EvaluationBudget(path, 2);
            Assert.Equal(0.8, next.State.Reserved);
            next.Settle(id, 0.2, "gen-missing");
            Assert.Equal(0.2, next.State.Spent);
            Assert.Equal(0, next.State.Reserved);
            Assert.ThrowsAny<Exception>(() => next.Reserve(1.81));
            Assert.ThrowsAny<Exception>(() => next.Reserve(double.NaN));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EvaluationGuardsDoNotChangeProductionBody()
    {
        var client = new ApiClient(ProviderEndpoint.OpenRouter("test"));
        var info = new ModelInfo("m", "m", ["max_tokens", "temperature"]);
        var options = new PolishOptions("m", ModelInfo: info);
        var original = client.PolishBody("hello", options, true);
        var plan = client.EvaluationPlan("hello", options, 0.000001, 0.000002);
        var evaluated = JsonNode.Parse(plan.BodyJson)!.AsObject();
        Assert.True(JsonNode.DeepEquals(original["messages"], evaluated["messages"]));
        Assert.Equal((int)original["max_tokens"]!, (int)evaluated["max_tokens"]!);
        Assert.True((bool)original["provider"]!["allow_fallbacks"]!);
        // Evaluation routes exactly like the app.
        Assert.True((bool)evaluated["provider"]!["allow_fallbacks"]!);
        Assert.Null(evaluated["provider"]!["max_price"]);
        Assert.Null(evaluated["usage"]);
        Assert.True(plan.UpperBoundUsd > 0);
    }
}

[Collection("MockServer")]
public class EvaluationNetworkTests
{
    [Fact]
    public async Task EvaluatedFailureIsNotRetriedAndBillingRecovered()
    {
        var posts = 0;
        MockOpenRouter.Handler = (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return (200, MockOpenRouter.Utf8("""{"data":{"total_cost":0.002,"native_tokens_prompt":12,"native_tokens_completion":1}}"""));
            }
            Interlocked.Increment(ref posts);
            return (200, MockOpenRouter.Utf8("data: {\"id\":\"gen-error\",\"error\":{\"message\":\"unsupported parameter\"}}\n\ndata: [DONE]\n\n"));
        };
        var client = new ApiClient(ProviderEndpoint.OpenRouter("test"), MockOpenRouter.Client());
        var info = new ModelInfo("m", "m", ["max_tokens"]);
        var plan = client.EvaluationPlan("x", new PolishOptions("m", ModelInfo: info), 0.000001, 0.000002);
        var result = await client.EvaluatePolish(plan);
        Assert.Equal(1, posts);
        Assert.NotNull(result.Error);
        Assert.Equal(0.002, result.Usage?.Cost);
        Assert.Equal("generation.total_cost", result.CostSource);
        Assert.Equal(0, result.RetryCount);
    }
}
