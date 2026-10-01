using System.Net.Http.Json;
using System.Text.Json;

namespace AINewtrend.Core.Ai;

public interface IAiProvider
{
    Task<string?> CompleteAsync(string system, string evidence, CancellationToken cancellationToken = default);
}

public sealed class EvidenceAiProvider : IAiProvider
{
    public Task<string?> CompleteAsync(string system, string evidence, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}

public sealed class HttpAiProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly string _model;

    public HttpAiProvider(HttpClient http, string model)
    {
        _http = http;
        _model = model;
    }

    public async Task<string?> CompleteAsync(string system, string evidence, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("chat/completions", new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = evidence },
                },
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
