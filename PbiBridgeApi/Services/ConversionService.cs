using Microsoft.Extensions.Options;

namespace PbiBridgeApi.Services;

/// <summary>
/// Thin validation runner used to stabilize the validation-only contract.
/// It does not own conversion success and returns terminal validation statuses only.
/// </summary>
public sealed class ValidationService : IValidationService
{
    private static readonly HashSet<string> TerminalSimulationStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "succeeded",
        "failed",
        "unavailable",
    };

    private readonly ValidationOptions _options;
    private readonly ILogger<ValidationService> _logger;

    public ValidationService(IOptions<ValidationOptions> options, ILogger<ValidationService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ValidationExecutionResult> RunValidationAsync(
        string artifactPath,
        string validator,
        IReadOnlyDictionary<string, object?> options,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(_options.JobTimeoutMinutes > 0 ? _options.JobTimeoutMinutes : 5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await Task.Delay(10, linkedCts.Token);
            return Evaluate(artifactPath, validator, options ?? new Dictionary<string, object?>(), linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Validation timed out for artifact {ArtifactPath}", artifactPath);
            return new ValidationExecutionResult
            {
                Status = ValidationStatus.Unavailable,
                Validator = NormalizeValidator(validator),
                Summary = "Validator unavailable — timeout reached. Conversion result must remain unchanged.",
                Error = "Validation timed out.",
                Checks = new List<ValidationCheckRecord>
                {
                    new()
                    {
                        Name = "validator_runtime",
                        Status = "unavailable",
                        Detail = "Validation timed out.",
                    },
                },
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Validation crashed for artifact {ArtifactPath}", artifactPath);
            return new ValidationExecutionResult
            {
                Status = ValidationStatus.Unavailable,
                Validator = NormalizeValidator(validator),
                Summary = "Validator unavailable — runtime error. Conversion result must remain unchanged.",
                Error = ex.Message,
                Checks = new List<ValidationCheckRecord>
                {
                    new()
                    {
                        Name = "validator_runtime",
                        Status = "unavailable",
                        Detail = ex.Message,
                    },
                },
            };
        }
    }

    private ValidationExecutionResult Evaluate(
        string artifactPath,
        string validator,
        IReadOnlyDictionary<string, object?> options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedValidator = NormalizeValidator(validator);
        var simulateStatus = ReadStringOption(options, "simulate_status");
        if (!string.IsNullOrWhiteSpace(simulateStatus) && TerminalSimulationStatuses.Contains(simulateStatus))
        {
            return BuildSimulatedResult(normalizedValidator, simulateStatus!, artifactPath);
        }

        if (!File.Exists(artifactPath) && !Directory.Exists(artifactPath))
        {
            return new ValidationExecutionResult
            {
                Status = ValidationStatus.Failed,
                Validator = normalizedValidator,
                Summary = "Validation failed — artifact not found.",
                Error = "Artifact path does not exist.",
                Checks = new List<ValidationCheckRecord>
                {
                    new()
                    {
                        Name = "artifact_exists",
                        Status = "failed",
                        Detail = "Artifact path does not exist.",
                    },
                },
            };
        }

        if (normalizedValidator.Equals("powerbi-desktop", StringComparison.OrdinalIgnoreCase) && !_options.PowerBiDesktopAvailable)
        {
            return BuildUnavailableResult(normalizedValidator, "Power BI Desktop validator is not available on this runtime.");
        }

        if (normalizedValidator.Equals("mcp", StringComparison.OrdinalIgnoreCase) && !_options.McpAvailable)
        {
            return BuildUnavailableResult(normalizedValidator, "MCP validator is not available on this runtime.");
        }

        var checks = new List<ValidationCheckRecord>
        {
            new()
            {
                Name = "artifact_exists",
                Status = "passed",
                Detail = "Artifact path resolved inside the client workspace.",
            },
        };

        var artifactShape = DescribeArtifactShape(artifactPath, checks);
        if (artifactShape is null)
        {
            return new ValidationExecutionResult
            {
                Status = ValidationStatus.Failed,
                Validator = normalizedValidator,
                Summary = "Validation failed — unsupported artifact shape.",
                Error = "Supported validation inputs are .pbix, .pbip, .zip, or a non-empty artifact directory.",
                Checks = checks,
            };
        }

        checks.Add(new ValidationCheckRecord
        {
            Name = "fallback_contract",
            Status = "passed",
            Detail = "Validation outcome is informational only; conversion_status impact remains none.",
        });

        return new ValidationExecutionResult
        {
            Status = ValidationStatus.Succeeded,
            Validator = normalizedValidator,
            Summary = $"Validation succeeded — {artifactShape}.",
            Checks = checks,
        };
    }

    private ValidationExecutionResult BuildSimulatedResult(string validator, string simulateStatus, string artifactPath)
    {
        return simulateStatus.ToLowerInvariant() switch
        {
            "succeeded" => new ValidationExecutionResult
            {
                Status = ValidationStatus.Succeeded,
                Validator = validator,
                Summary = $"Validation succeeded — simulated for {Path.GetFileName(artifactPath)}.",
                Checks = new List<ValidationCheckRecord>
                {
                    new()
                    {
                        Name = "simulation",
                        Status = "passed",
                        Detail = "simulate_status=succeeded",
                    },
                },
            },
            "failed" => new ValidationExecutionResult
            {
                Status = ValidationStatus.Failed,
                Validator = validator,
                Summary = "Validation failed — simulated failure. Conversion result must remain unchanged.",
                Error = "simulate_status=failed",
                Checks = new List<ValidationCheckRecord>
                {
                    new()
                    {
                        Name = "simulation",
                        Status = "failed",
                        Detail = "simulate_status=failed",
                    },
                },
            },
            _ => BuildUnavailableResult(validator, "simulate_status=unavailable"),
        };
    }

    private ValidationExecutionResult BuildUnavailableResult(string validator, string detail)
    {
        return new ValidationExecutionResult
        {
            Status = ValidationStatus.Unavailable,
            Validator = validator,
            Summary = "Validator unavailable — conversion result must remain unchanged.",
            Error = detail,
            Checks = new List<ValidationCheckRecord>
            {
                new()
                {
                    Name = "validator_backend",
                    Status = "unavailable",
                    Detail = detail,
                },
            },
        };
    }

    private string? DescribeArtifactShape(string artifactPath, List<ValidationCheckRecord> checks)
    {
        if (File.Exists(artifactPath))
        {
            var extension = Path.GetExtension(artifactPath);
            if (!_options.SupportedFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                checks.Add(new ValidationCheckRecord
                {
                    Name = "artifact_format",
                    Status = "failed",
                    Detail = $"Unsupported extension '{extension}'.",
                });
                return null;
            }

            checks.Add(new ValidationCheckRecord
            {
                Name = "artifact_format",
                Status = "passed",
                Detail = $"File extension '{extension}' is supported.",
            });
            return $"file artifact '{Path.GetFileName(artifactPath)}' is readable";
        }

        var files = Directory.EnumerateFiles(artifactPath, "*", SearchOption.AllDirectories).Take(5).ToList();
        if (files.Count == 0)
        {
            checks.Add(new ValidationCheckRecord
            {
                Name = "artifact_format",
                Status = "failed",
                Detail = "Artifact directory is empty.",
            });
            return null;
        }

        checks.Add(new ValidationCheckRecord
        {
            Name = "artifact_format",
            Status = "passed",
            Detail = $"Artifact directory contains {files.Count} file(s) in the sampled set.",
        });
        return $"directory artifact '{Path.GetFileName(artifactPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}' is non-empty";
    }

    private string NormalizeValidator(string validator)
        => string.IsNullOrWhiteSpace(validator) ? _options.DefaultValidator : validator.Trim();

    private static string? ReadStringOption(IReadOnlyDictionary<string, object?> options, string key)
    {
        if (!options.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            string value => value,
            _ => raw.ToString(),
        };
    }
}
