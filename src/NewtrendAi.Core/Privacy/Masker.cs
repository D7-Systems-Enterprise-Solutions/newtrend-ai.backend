using System.Text.RegularExpressions;

namespace NewtrendAi.Core.Privacy;

public static partial class Masker
{
    public static string? Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var masked = Bearer().Replace(value, "$1[masked]");
        masked = Assignment().Replace(masked, "$1[masked]");
        masked = JsonSecret().Replace(masked, "$1\"[masked]\"");
        masked = SecretColumn().Replace(masked, "$1[masked]");
        return masked;
    }

    public static void MaskExecution(Live.Execution execution)
    {
        if (execution.Error?.Message is not null)
            execution.Error.Message = Mask(execution.Error.Message);

        foreach (var step in execution.Steps)
        {
            step.SqlText = Mask(step.SqlText);
            step.Name = Mask(step.Name) ?? step.Name;
        }
    }

    [GeneratedRegex(@"(?i)(Bearer\s+)[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)((?:password|pwd|token|api[_-]?key|secret|authorization)\s*[=:]\s*)\S+")]
    private static partial Regex Assignment();

    [GeneratedRegex("(?i)(\"(?:password|token|api[_-]?key|secret|authorization)\"\\s*:\\s*)\"[^\"]*\"")]
    private static partial Regex JsonSecret();

    [GeneratedRegex(@"(?i)((?:password|token|api[_-]?key|secret)\s*\)\s*values\s*\(\s*'?)[^',)\s]+")]
    private static partial Regex SecretColumn();
}
