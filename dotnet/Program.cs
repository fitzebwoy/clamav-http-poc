using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

var maxBytes = int.TryParse(
    Environment.GetEnvironmentVariable("MAX_BYTES"),
    out var mb
)
    ? mb
    : 30_000_000;

var maxConcurrentScans = int.TryParse(
    Environment.GetEnvironmentVariable("MAX_CONCURRENT_SCANS"),
    out var mcs
)
    ? mcs
    : 2;

var scanSemaphore = new SemaphoreSlim(maxConcurrentScans, maxConcurrentScans);
var clamAvDbDir = "/var/lib/clamav";

app.MapGet("/", () => Results.Ok(new
{
    service = "clamav-http-poc-dotnet",
    liveness = "/health",
    readiness = "/ready",
    scan = "/scan"
}));

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapGet("/ready", async () =>
{
    var ready = await ClamdPingAsync();
    if (!ready)
    {
        return Results.Problem(
            title: "clamd not ready",
            statusCode: StatusCodes.Status503ServiceUnavailable
        );
    }

    var definitions = await GetDefinitionVersionsAsync(clamAvDbDir);

    return Results.Ok(new
    {
        ready = true,
        engine = "clamav",
        api_langauge = "dotnet",
        instance_id = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"),
        app_name = Environment.GetEnvironmentVariable("CONTAINER_APP_NAME"),
        revision = Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION"),
        replica_name = Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME"),
        hostname = Environment.GetEnvironmentVariable("CONTAINER_APP_HOSTNAME"),
        port = Environment.GetEnvironmentVariable("CONTAINER_APP_PORT"),
        definitions
    });
});

app.MapPost("/scan", async (HttpRequest request) =>
{
    if (!await ClamdPingAsync())
    {
        return Results.Problem(
            title: "clamd not ready",
            statusCode: StatusCodes.Status503ServiceUnavailable
        );
    }

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { detail = "Expected multipart/form-data" });
    }

    if (request.ContentLength.HasValue && request.ContentLength.Value > maxBytes + (1024 * 1024))
    {
        return Results.Problem(
            title: "Request too large",
            statusCode: StatusCodes.Status413PayloadTooLarge
        );
    }

    if (!await scanSemaphore.WaitAsync(TimeSpan.FromMilliseconds(100)))
    {
        return Results.Problem(
            title: "Scanner busy, retry later",
            statusCode: StatusCodes.Status503ServiceUnavailable
        );
    }

    string? tempFile = null;

    try
    {
        var form = await request.ReadFormAsync();
        var file = form.Files["file"];

        if (file is null)
        {
            return Results.BadRequest(new { detail = "Missing file field named 'file'" });
        }

        if (file.Length > maxBytes)
        {
            return Results.Problem(
                title: "File exceeds MAX_BYTES",
                statusCode: StatusCodes.Status413PayloadTooLarge
            );
        }

        var safeFileName = Path.GetFileName(file.FileName);
        tempFile = Path.Combine("/tmp", $"{Guid.NewGuid()}_{safeFileName}");

        long total = 0;
        var started = Stopwatch.StartNew();

        await using (var output = File.Create(tempFile))
        await using (var input = file.OpenReadStream())
        {
            var buffer = new byte[1024 * 1024];
            int read;

            while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    return Results.Problem(
                        title: "File exceeds MAX_BYTES",
                        statusCode: StatusCodes.Status413PayloadTooLarge
                    );
                }

                await output.WriteAsync(buffer, 0, read);
            }
        }

        var scanResult = await RunClamdScanAsync(tempFile);
        started.Stop();

        var response = new Dictionary<string, object?>()
        {
            ["result"] = scanResult.Result,
            ["file_name"] = file.FileName,
            ["bytes_scanned"] = total,
            ["signature"] = scanResult.Signature,
            ["engine"] = "clamav",
            ["engine_detail"] = scanResult.EngineDetail,
            ["scan_duration_ms"] = scanResult.ScanDurationMs,
            ["api_langauge"] = "dotnet"
        };

        if (scanResult.Result == "error" && !string.IsNullOrWhiteSpace(scanResult.Stderr))
        {
            response["stderr"] = scanResult.Stderr;
        }

        return Results.Ok(response);
    }
    finally
    {
        scanSemaphore.Release();

        if (!string.IsNullOrWhiteSpace(tempFile) && File.Exists(tempFile))
        {
            try
            {
                File.Delete(tempFile);
            }
            catch
            {
            }
        }
    }
});

app.Run();

static async Task<bool> ClamdPingAsync(string host = "127.0.0.1", int port = 3310)
{
    try
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await client.ConnectAsync(host, port, cts.Token);

        await using var stream = client.GetStream();
        var ping = System.Text.Encoding.ASCII.GetBytes("PING\n");
        await stream.WriteAsync(ping, 0, ping.Length, cts.Token);

        var buffer = new byte[16];
        var read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
        var response = System.Text.Encoding.ASCII.GetString(buffer, 0, read);

        return response.Contains("PONG", StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
        return false;
    }
}

static async Task<object> GetDefinitionVersionsAsync(string dbDir)
{
    var result = new Dictionary<string, int?>()
    {
        ["daily"] = null,
        ["main"] = null,
        ["bytecode"] = null
    };

    var patterns = new Dictionary<string, string[]>
    {
        ["daily"] = new[] { Path.Combine(dbDir, "daily.cld"), Path.Combine(dbDir, "daily.cvd") },
        ["main"] = new[] { Path.Combine(dbDir, "main.cld"), Path.Combine(dbDir, "main.cvd") },
        ["bytecode"] = new[] { Path.Combine(dbDir, "bytecode.cld"), Path.Combine(dbDir, "bytecode.cvd") }
    };

    var regex = new Regex(@"version[: ]+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    foreach (var kvp in patterns)
    {
        foreach (var path in kvp.Value)
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "sigtool",
                        ArgumentList = { "--info", path },
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    }
                };

                process.Start();

                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                var output = $"{stdout}\n{stderr}";
                var match = regex.Match(output);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var version))
                {
                    result[kvp.Key] = version;
                    break;
                }
            }
            catch
            {
            }
        }
    }

    return result;
}

static async Task<ScanResult> RunClamdScanAsync(string path)
{
    var stopwatch = Stopwatch.StartNew();

    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "clamdscan",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }
    };

    process.StartInfo.ArgumentList.Add("--fdpass");
    process.StartInfo.ArgumentList.Add("--no-summary");
    process.StartInfo.ArgumentList.Add(path);

    process.Start();

    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();

    await process.WaitForExitAsync();

    var stdout = (await stdoutTask).Trim();
    var stderr = (await stderrTask).Trim();

    stopwatch.Stop();

    var parsed = ParseClamdscanOutput(stdout);

    if (process.ExitCode == 0)
    {
        return new ScanResult(
            Result: "clean",
            EngineDetail: stdout,
            Signature: null,
            Stderr: null,
            ScanDurationMs: (int)stopwatch.ElapsedMilliseconds
        );
    }

    if (process.ExitCode == 1)
    {
        return new ScanResult(
            Result: "infected",
            EngineDetail: stdout,
            Signature: parsed.Signature,
            Stderr: null,
            ScanDurationMs: (int)stopwatch.ElapsedMilliseconds
        );
    }

    return new ScanResult(
        Result: "error",
        EngineDetail: stdout,
        Signature: null,
        Stderr: stderr,
        ScanDurationMs: (int)stopwatch.ElapsedMilliseconds
    );
}

static (string? Detail, string? Signature) ParseClamdscanOutput(string raw)
{
    if (string.IsNullOrWhiteSpace(raw))
        return (null, null);

    var parts = raw.Split(':', 2);
    var tail = parts.Length == 2 ? parts[1].Trim() : raw.Trim();

    string? signature = null;
    if (tail.EndsWith("FOUND", StringComparison.OrdinalIgnoreCase))
    {
        signature = tail[..^"FOUND".Length].Trim();
    }

    return (tail, signature);
}

internal record ScanResult(
    string Result,
    string? EngineDetail,
    string? Signature,
    string? Stderr,
    int ScanDurationMs
);