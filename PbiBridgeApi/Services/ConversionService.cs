using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace PbiBridgeApi.Services;

/// <summary>
/// Invokes the tableau2pbi Python module as a subprocess.
/// DA-015 STRICT: conversion logic lives entirely in Python — this class only calls the subprocess.
/// DA-016 related: timeout enforced via JobTimeoutMinutes config.
/// </summary>
public sealed class ConversionService : IConversionService
{
    private readonly string _pythonPath;
    private readonly string _tableau2PbiPath;
    private readonly int _timeoutMinutes;
    private readonly ILogger<ConversionService> _logger;

    public ConversionService(IOptions<ConversionOptions> options, ILogger<ConversionService> logger)
    {
        _logger = logger;
        var opts = options.Value;
        _pythonPath = string.IsNullOrWhiteSpace(opts.PythonPath) ? "python" : opts.PythonPath;
        _tableau2PbiPath = opts.Tableau2PbiPath ?? string.Empty;
        _timeoutMinutes = opts.JobTimeoutMinutes > 0 ? opts.JobTimeoutMinutes : 10;
    }

    /// <inheritdoc />
    public async Task<(string Stdout, string Stderr, int ExitCode)> RunConversionAsync(
        string sourcePath,
        string outputPath,
        Dictionary<string, object?> options,
        CancellationToken cancellationToken = default)
    {
        // DA-015: call subprocess — never reimplement the tableau2pbi logic.
        // Current CLI contract comes from src/convert.py:
        //   python <convert.py> <input> --out <output-dir>
        // If Tableau2PbiPath is not configured, we keep the legacy module mode for compatibility.
        string args;
        if (string.IsNullOrWhiteSpace(_tableau2PbiPath))
        {
            args = $"-m tableau2pbi.cli --input \"{sourcePath}\" --output \"{outputPath}\"";
        }
        else
        {
            args = $"\"{_tableau2PbiPath}\" \"{sourcePath}\" --out \"{outputPath}\"";
        }

        // Minor: log subprocess invocation at Debug level only — avoids leaking paths in Info logs
        _logger.LogDebug("Launching subprocess: {Python} {Args}", _pythonPath, args);

        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdoutBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderrBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(_timeoutMinutes));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("tableau2pbi subprocess timed out or was cancelled. Killing process.");
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (stdoutBuilder.ToString(), "TIMEOUT: process killed after timeout.", -1);
        }

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();
        var exitCode = process.ExitCode;

        _logger.LogInformation("Subprocess exited with code {Code}", exitCode);

        // Minor: stderr may contain sensitive data — log at Debug level only
        if (!string.IsNullOrWhiteSpace(stderr))
            _logger.LogDebug("Subprocess stderr (first 500 chars): {Stderr}",
                stderr.Length > 500 ? stderr[..500] + "…" : stderr);

        return (stdout, stderr, exitCode);
    }
}
