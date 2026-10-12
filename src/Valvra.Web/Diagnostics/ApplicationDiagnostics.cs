using System.Data.Common;
using System.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace Valvra.Web.Diagnostics;

public sealed class FileLoggingOptions
{
    public bool Enabled { get; set; } = true;
    public string Path { get; set; } = "Logs/valvra-.jsonl";
    public string MinimumLevel { get; set; } = "Information";
    public long FileSizeLimitBytes { get; set; } = 10 * 1024 * 1024;
    public int RetainedFileCountLimit { get; set; } = 31;
}

// Deliberately separate from arbitrary ILogger events and ambient scopes. A password
// manager cannot safely persist framework exception messages, SQL or request paths.
public sealed class ApplicationDiagnostics : IDisposable
{
    private readonly Logger logger;

    public ApplicationDiagnostics(FileLoggingOptions options, string configurationDirectory, string webRoot)
    {
        var configuration = new LoggerConfiguration();
        if (options.Enabled)
        {
            if (!Enum.TryParse<LogEventLevel>(options.MinimumLevel, true, out var level) || !Enum.IsDefined(level)
                || string.IsNullOrWhiteSpace(options.Path) || options.FileSizeLimitBytes < 1024
                || options.RetainedFileCountLimit is < 1 or > 10000)
                throw new InvalidOperationException("Invalid FileLogging settings. Check level, path, size and retention.");
            var path = System.IO.Path.GetFullPath(options.Path, System.IO.Path.GetFullPath(configurationDirectory));
            var publicRoot = System.IO.Path.GetFullPath(webRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (path.Equals(publicRoot, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(publicRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("FileLogging path must be outside wwwroot.");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // Fail visibly at startup rather than accepting an unwritable destination.
            using (var probe = new FileStream(path + ".probe-" + Guid.NewGuid().ToString("N"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { probe.WriteByte(0); }
            configuration.MinimumLevel.Is(level)
                .Enrich.WithProperty("Application", "Valvra")
                .Enrich.WithProperty("ProcessId", Environment.ProcessId)
                .WriteTo.File(new JsonFormatter(), path, rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: options.FileSizeLimitBytes, rollOnFileSizeLimit: true,
                    retainedFileCountLimit: options.RetainedFileCountLimit, shared: true);
        }
        logger = configuration.CreateLogger();
    }

    public void Lifecycle(string phase) => logger.Information("Application lifecycle {Phase}", phase);

    public void SetupCheck(string check, bool passed, string? correlationId) =>
        logger.Write(passed ? LogEventLevel.Information : LogEventLevel.Warning,
            "Setup check {Check} completed; passed {Passed}; correlation {CorrelationId}", check, passed, correlationId);

    public void Failure(string phase, Exception exception, string? correlationId = null, int? statusCode = null)
    {
        // Never pass the actual exception object to Serilog. Even Data/ToString and
        // nested messages can contain credentials, submitted secrets or SQL values.
        var chain = new List<object>();
        for (Exception? current = exception; current is not null && chain.Count < 8; current = current.InnerException)
        {
            var frames = new StackTrace(current, false).GetFrames().Take(24)
                .Select(frame => frame.GetMethod())
                .Select(method => method?.DeclaringType?.FullName + "." + method?.Name).ToArray();
            chain.Add(new { Type = current.GetType().FullName, current.HResult,
                DatabaseErrorCode = (current as DbException)?.ErrorCode,
                SqlServerNumber = (current as Microsoft.Data.SqlClient.SqlException)?.Number,
                SqlState = (current as Npgsql.PostgresException)?.SqlState,
                LdapErrorCode = (current as System.DirectoryServices.Protocols.LdapException)?.ErrorCode,
                Frames = frames });
        }
        logger.Write(statusCode is < 500 ? LogEventLevel.Warning : LogEventLevel.Error,
            "Operation failed in {Phase}; correlation {CorrelationId}; status {StatusCode}; diagnostics {@FailureChain}", phase, correlationId, statusCode, chain);
    }

    public void Request(HttpContext context, double elapsedMilliseconds, bool aborted)
    {
        // A route template contains placeholders, never user supplied path segments.
        var route = (context.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
        var method = context.Request.Method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS"
            ? context.Request.Method : "OTHER";
        var status = aborted ? 499 : context.Response.StatusCode;
        var level = status >= 500 ? LogEventLevel.Error : status >= 400 ? LogEventLevel.Warning : LogEventLevel.Information;
        logger.Write(level, "HTTP {Method} {Route} completed {StatusCode} in {ElapsedMilliseconds} ms; correlation {CorrelationId}; aborted {Aborted}",
            method, route, status, Math.Round(elapsedMilliseconds, 3), context.TraceIdentifier, aborted);
    }

    public void Dispose() => logger.Dispose();
}
