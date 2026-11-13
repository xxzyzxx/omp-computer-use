using System.Diagnostics;
using System.Text.Json;
using PlaywrightWindows.Mcp;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Safety;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class RegistryCancellationTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(3);

    private static ServerConfig TestConfig() => new() { BlockHarnessTerminal = false, GrantsPath = "" };

    private static ToolRegistry NewRegistry(ServerConfig config, AuditLog? audit = null, SafetyState? state = null)
    {
        state ??= new SafetyState(config);
        var gate = new SafetyGate(state, new AccessPolicy(config), new DenyPrompter(), new NullOverlay(), config);
        return new ToolRegistry(gate, config, audit);
    }

    private sealed class DelegateTool(string name, Func<ToolContext, Task<McpToolResult>> run, TimeSpan? timeout = null) : ToolBase
    {
        public override string Name => name;
        public override string Description => "Cancellation test operation";
        public override object InputSchema => new { type = "object" };
        public override ToolKind Kind => ToolKind.Action;
        public override TimeSpan? Timeout => timeout;
        public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx) => run(ctx);
    }

    // The provider wait ignores cancellation, just like an in-flight native call. The guard must
    // prevent the side effect when that wait eventually ends, even after the response was returned.
    private sealed class GatedMutationTool(TimeSpan? timeout = null) : ToolBase
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Mutated;
        public override string Name => "gated_mutation";
        public override string Description => "Simulates a blocked provider before a mutation";
        public override object InputSchema => new { type = "object" };
        public override ToolKind Kind => ToolKind.Action;
        public override TimeSpan? Timeout => timeout;

        public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
        {
            Started.TrySetResult();
            try
            {
                await Release.Task;
                ctx.ThrowIfCancelled();
                Mutated = true;
                return TextResult("mutation completed");
            }
            finally { Finished.TrySetResult(); }
        }

        public async Task UnblockAsync()
        {
            Release.TrySetResult();
            await Finished.Task.WaitAsync(TestDeadline);
        }
    }

    private static string? ResultCode(McpToolResult result)
    {
        using var document = JsonDocument.Parse(result.Content.First(c => c.Type == "text").Text!);
        return document.RootElement.GetProperty("code").GetString();
    }

    [Fact]
    public async Task Timeout_CancelsWorker_BeforeBlockedMutationCanRun()
    {
        var tool = new GatedMutationTool(TimeSpan.FromMilliseconds(150));
        var registry = NewRegistry(TestConfig());
        registry.RegisterTool(tool);
        try
        {
            var result = await registry.ExecuteToolAsync(tool.Name, null).WaitAsync(TestDeadline);
            Assert.True(result.IsError);
            Assert.Equal("timeout", ResultCode(result));
            await tool.UnblockAsync();
            Assert.False(tool.Mutated);
        }
        finally { await tool.UnblockAsync(); }
    }

    [Fact]
    public async Task Timeout_IsNotMisreportedAsClientCancellation_WhenWorkerUnwindsImmediately()
    {
        var registry = NewRegistry(TestConfig());
        registry.RegisterTool(new DelegateTool("cancellable", async ctx =>
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ctx.Cancel);
            return ToolBase.JsonResult(new { ok = true });
        }, TimeSpan.FromMilliseconds(150)));
        var result = await registry.ExecuteToolAsync("cancellable", null).WaitAsync(TestDeadline);
        Assert.True(result.IsError);
        Assert.Equal("timeout", ResultCode(result));
    }

    [Fact]
    public async Task Kill_ReturnsAbortedPromptly_AndGuardPreventsMutationAfterUnblock()
    {
        var config = TestConfig();
        var state = new SafetyState(config);
        var registry = NewRegistry(config, state: state);
        var tool = new GatedMutationTool();
        registry.RegisterTool(tool);
        try
        {
            var call = registry.ExecuteToolAsync(tool.Name, null);
            await tool.Started.Task.WaitAsync(TestDeadline);
            var sw = Stopwatch.StartNew();
            state.TriggerAbort("test kill");
            var result = await call.WaitAsync(TestDeadline);
            Assert.True(sw.ElapsedMilliseconds < 200, $"Abort response took {sw.ElapsedMilliseconds} ms.");
            Assert.True(result.IsError);
            Assert.Equal("aborted", ResultCode(result));
            await tool.UnblockAsync();
            Assert.False(tool.Mutated);
        }
        finally { await tool.UnblockAsync(); }
    }

    [Fact]
    public async Task ClientCancel_CancelsBlockedWorkerBeforeItCanMutate()
    {
        var registry = NewRegistry(TestConfig());
        var tool = new GatedMutationTool();
        registry.RegisterTool(tool);
        using var cts = new CancellationTokenSource();
        try
        {
            var call = registry.ExecuteToolAsync(tool.Name, null, cts.Token);
            await tool.Started.Task.WaitAsync(TestDeadline);
            cts.Cancel();
            var result = await call.WaitAsync(TestDeadline);
            Assert.True(result.IsError);
            Assert.Equal("cancelled", ResultCode(result));
            await tool.UnblockAsync();
            Assert.False(tool.Mutated);
        }
        finally { await tool.UnblockAsync(); }
    }

    [Fact]
    public async Task TimeoutResponse_AuditMatchesReturnedOutcome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-audit-" + Guid.NewGuid().ToString("N"));
        using var audit = new AuditLog(dir);
        var registry = NewRegistry(TestConfig(), audit);
        var tool = new GatedMutationTool(TimeSpan.FromMilliseconds(150));
        registry.RegisterTool(tool);
        try
        {
            var result = await registry.ExecuteToolAsync(tool.Name, null).WaitAsync(TestDeadline);
            Assert.Equal("timeout", ResultCode(result));
            await tool.UnblockAsync();
            var path = Path.Combine(dir, $"audit-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var line = reader.ReadLine();
            Assert.NotNull(line);
            Assert.Null(reader.ReadLine());
            using var entry = JsonDocument.Parse(line);
            Assert.Equal("timeout", entry.RootElement.GetProperty("code").GetString());
            Assert.Equal(tool.Name, entry.RootElement.GetProperty("tool").GetString());
        }
        finally
        {
            await tool.UnblockAsync();
            audit.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

}
