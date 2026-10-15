using System.Text.RegularExpressions;

namespace NewtrendAi.Core.Privacy;

public static partial class Masker
{
    public static string? Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var authorization = new List<string>();
        var parked = AuthorizationBearer().Replace(value, match =>
        {
            authorization.Add(match.Value);
            return $"@@AUTH{authorization.Count - 1}@@";
        });

        var masked = Bearer().Replace(parked, "$1[masked]");
        masked = JsonSecret().Replace(masked, "$1\"[masked]\"");
        masked = QuotedAssignment().Replace(masked, "$1$2[masked]$2");
        masked = Assignment().Replace(masked, "$1[masked]");
        masked = SecretColumn().Replace(masked, "$1[masked]");
        masked = SensitiveHeader().Replace(masked, "$1[masked]");
        masked = QuerySecret().Replace(masked, "$1[masked]");
        for (var index = 0; index < authorization.Count; index++)
            masked = masked.Replace($"@@AUTH{index}@@", authorization[index], StringComparison.Ordinal);
        return masked;
    }

    public static void MaskExecution(Live.Execution execution)
    {
        if (execution.Error?.Message is not null)
            execution.Error.Message = Mask(execution.Error.Message);

        foreach (var step in execution.Steps)
        {
            step.SqlText = Mask(step.SqlText);
            step.HttpHeaders = Mask(step.HttpHeaders);
            step.HttpBody = Mask(step.HttpBody);
            step.HttpUrl = Mask(step.HttpUrl);
            step.Name = Mask(step.Name) ?? step.Name;
        }
    }

    [GeneratedRegex(@"(?im)^[ \t]*authorization[ \t]*:[ \t]*bearer[ \t]+[A-Za-z0-9\-._~+/]+=*[ \t\r]*$")]
    private static partial Regex AuthorizationBearer();

    [GeneratedRegex(@"(?i)(Bearer\s+)[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)((?:password|pwd|token|api[_-]?key|secret|authorization)\s*[=:]\s*)(?!['""'])[^,\s;&}]+")]
    private static partial Regex Assignment();

    [GeneratedRegex("(?i)((?:password|pwd|token|api[_-]?key|secret|authorization)\\s*[=:]\\s*)(['\"])[^'\"]*\\2")]
    private static partial Regex QuotedAssignment();

    [GeneratedRegex("(?i)(\"(?:password|token|api[_-]?key|secret|authorization)\"\\s*:\\s*)\"[^\"]*\"")]
    private static partial Regex JsonSecret();

    [GeneratedRegex(@"(?i)((?:password|token|api[_-]?key|secret)\s*\)\s*values\s*\(\s*'?)[^',)\s]+")]
    private static partial Regex SecretColumn();

    [GeneratedRegex(@"(?im)^\s*((?:authorization|proxy-authorization|cookie|set-cookie|x-api-key|x-auth-token)\s*:\s*).*$")]
    private static partial Regex SensitiveHeader();

    [GeneratedRegex(@"(?i)([?&](?:access_token|refresh_token|id_token|token|api[_-]?key|client_secret|password)=)[^&#\s]*")]
    private static partial Regex QuerySecret();
}
