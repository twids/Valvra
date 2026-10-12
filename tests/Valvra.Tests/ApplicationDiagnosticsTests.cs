using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Valvra.Web.Diagnostics;
using Xunit;

namespace Valvra.Tests;

public sealed class ApplicationDiagnosticsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "valvra-log-tests-" + Guid.NewGuid().ToString("N"));
    private ApplicationDiagnostics Create(FileLoggingOptions? options = null) => new(options ?? new(), root, Path.Combine(root, "wwwroot"));
    private string[] Files() => Directory.GetFiles(Path.Combine(root, "Logs"), "*.jsonl");
    private string Read() => string.Join("\n", Files().Select(File.ReadAllText));

    [Fact]
    public void NestedFailuresAndRequestsExcludeSecretBearingData()
    {
        const string secret = "DO-NOT-LOG-password-token-SQL";
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/resources/" + secret;
        context.Request.QueryString = new QueryString("?password=" + secret);
        context.Request.Headers.Authorization = secret;
        context.Request.Headers.Cookie = secret;
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(secret));
        context.TraceIdentifier = "test-correlation";
        context.Response.StatusCode = 503;
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/resources/{id}"), 0, EndpointMetadataCollection.Empty, secret));
        Exception error;
        try { ThrowPrivateFailure(secret); throw new InvalidOperationException("Unreachable"); }
        catch (ArgumentException inner) { error = new InvalidOperationException(secret, inner); }
        error.Data[secret] = secret;
        using (var log = Create())
        {
            log.Lifecycle("Started");
            log.Failure("Request", error, context.TraceIdentifier, 503);
            log.Request(context, 12.3456, false);
            log.SetupCheck("Active Directory", false, context.TraceIdentifier);
        }
        var output = Read();
        Assert.DoesNotContain(secret, output);
        Assert.Contains("System.InvalidOperationException", output);
        Assert.Contains("System.ArgumentException", output);
        Assert.Contains(nameof(ThrowPrivateFailure), output);
        var records = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal(4, records.Length);
            var request = records[2].RootElement.GetProperty("Properties");
            Assert.Equal("/api/resources/{id}", request.GetProperty("Route").GetString());
            Assert.Equal("test-correlation", request.GetProperty("CorrelationId").GetString());
            Assert.Equal(503, request.GetProperty("StatusCode").GetInt32());
            Assert.Equal(12.346, request.GetProperty("ElapsedMilliseconds").GetDouble());
        }
        finally { foreach (var record in records) record.Dispose(); }
    }

    private static void ThrowPrivateFailure(string secret) => throw new ArgumentException(secret);

    [Fact]
    public void AbsoluteCustomDestinationIsUsed()
    {
        var path = Path.Combine(root, "private-diagnostics", "application-.jsonl");
        using (var log = Create(new() { Path = path })) log.Lifecycle("Started");
        var file = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.jsonl"));
        Assert.Contains("Started", File.ReadAllText(file));
        Assert.False(Directory.Exists(Path.Combine(root, "Logs")));
    }

    [Fact]
    public void SizeRollingRetainsBoundedFilesAndFlushesOnDispose()
    {
        using (var log = Create(new() { FileSizeLimitBytes = 1024, RetainedFileCountLimit = 3 }))
            for (var i = 0; i < 100; i++) log.Lifecycle("Started");
        Assert.Equal(3, Files().Length);
        Assert.All(Files(), file => Assert.NotEmpty(File.ReadAllText(file)));
    }

    [Fact]
    public void LevelFilteringAndDisabledLoggingAreRespected()
    {
        using (var log = Create(new() { Enabled = false })) log.Lifecycle("Started");
        Assert.False(Directory.Exists(root));
        using (var log = Create(new() { MinimumLevel = "Error" }))
        {
            log.Lifecycle("Started");
            log.Failure("Host", new InvalidOperationException("private"));
        }
        Assert.DoesNotContain("Application lifecycle", Read());
        Assert.Contains("Operation failed", Read());
    }

    [Theory]
    [InlineData("not-a-level", 1024, 3)]
    [InlineData("Information", 0, 3)]
    [InlineData("Information", 1024, 0)]
    public void InvalidSettingsAreRejected(string level, long size, int count) =>
        Assert.Throws<InvalidOperationException>(() => Create(new() { MinimumLevel = level, FileSizeLimitBytes = size, RetainedFileCountLimit = count }));

    [Fact]
    public void PublicAndUnwritableDestinationsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Create(new() { Path = Path.Combine(root, "wwwroot", "log.jsonl") }));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "existing-file");
        File.WriteAllText(file, "occupied");
        Assert.Throws<IOException>(() => Create(new() { Path = Path.Combine(file, "log.jsonl") }));
    }

    [Fact]
    public void CanceledRequestsAndUnknownRoutesAreRecordedWithoutRawPaths()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/unknown/private-value";
        context.Request.Method = "PRIVATE-METHOD";
        using (var log = Create()) log.Request(context, 10, true);
        var output = Read();
        Assert.Contains("499", output);
        Assert.Contains("unmatched", output);
        Assert.DoesNotContain("private-value", output);
        Assert.DoesNotContain("PRIVATE-METHOD", output);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
