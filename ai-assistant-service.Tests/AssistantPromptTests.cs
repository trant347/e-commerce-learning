using ai_assistant_service.Configuration;
using ai_assistant_service.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace ai_assistant_service.Tests;

public class AssistantPromptTests
{
    [Theory]
    [InlineData("missing_call_id")]
    [InlineData("missing_result_id")]
    [InlineData("duplicate_call")]
    [InlineData("duplicate_result")]
    [InlineData("dangling_result")]
    [InlineData("unresolved_at_end")]
    [InlineData("unresolved_before_turn")]
    [InlineData("duplicate_across_examples")]
    [InlineData("result_before_call")]
    public async Task InvalidSeedPairs_PreventStartup(string problem)
    {
        var options = ValidPrompt();
        var turns = options.FewShotExamples[0].Turns;
        switch (problem)
        {
            case "missing_call_id": turns[0].ToolCalls![0].Id = ""; break;
            case "missing_result_id": turns[1].ToolCallId = null; break;
            case "duplicate_call": turns[0].ToolCalls!.Add(turns[0].ToolCalls![0]); break;
            case "duplicate_result": turns.Add(turns[1]); break;
            case "dangling_result": turns[1].ToolCallId = "unknown"; break;
            case "unresolved_at_end": turns.RemoveAt(1); break;
            case "unresolved_before_turn": turns.Insert(1, new() { Role = "user", Content = "interrupt" }); break;
            case "duplicate_across_examples": options.FewShotExamples.Add(options.FewShotExamples[0]); break;
            case "result_before_call": turns.Reverse(); break;
        }

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddAiModelRouting(Configuration());
                services.PostConfigure<PromptOptions>(bound => bound.FewShotExamples = options.FewShotExamples);
            })
            .Build();
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("PromptOptions:FewShotExamples:", exception.Message);
    }

    [Fact]
    public async Task ValidMultipleCallsAndResults_StartAndSnapshotWithoutExecutingTools()
    {
        var options = ValidPrompt();
        var turns = options.FewShotExamples[0].Turns;
        turns[0].ToolCalls!.Add(new FewShotToolCall { Id = "second", Name = "bookings" });
        turns.Add(new FewShotTurn { Role = "tool", Content = "[]", ToolCallId = "second" });
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddAiModelRouting(Configuration());
                services.PostConfigure<PromptOptions>(bound => bound.FewShotExamples = options.FewShotExamples);
            })
            .Build();
        await host.StartAsync();
        var snapshot = host.Services.GetRequiredService<AssistantPrompt>();
        turns.Clear();
        Assert.Equal(4, snapshot.Messages.Count);
        Assert.Equal(["first", "second"], snapshot.Messages[1].ToolCalls!.Select(call => call.Id));
        Assert.Equal(["first", "second"], snapshot.Messages.Skip(2).Select(message => message.ToolCallId));
        await host.StopAsync();
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ollama:BaseUrl"] = "http://ollama-test:11434",
            ["AiModels:Tasks:chat:Provider"] = "Ollama",
            ["AiModels:Tasks:chat:Model"] = "model",
            ["AiModels:Tasks:chat:TimeoutSeconds"] = "60",
            ["AiModels:Tasks:chat:RequestTimeoutSeconds"] = "300",
            ["AiModels:Tasks:chat:MaxOutputTokens"] = "128"
        }).Build();

    private static PromptOptions ValidPrompt() => new()
    {
        FewShotExamples = [new FewShotExample
        {
            Turns = [
                new() { Role = "assistant", ToolCalls = [new() { Id = "first", Name = "search" }] },
                new() { Role = "tool", Content = "[]", ToolCallId = "first" }
            ]
        }]
    };
}
