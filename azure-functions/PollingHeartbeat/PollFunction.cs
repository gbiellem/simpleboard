using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace SimpleBoard.PollingHeartbeat;

/// <summary>
/// Hourly keep-alive: inserts a heartbeat row into public.polling and reads
/// back the current rows. Purpose is solely to keep the Supabase project's
/// API active so its free-tier auto-pause never triggers -- rows themselves
/// are disposable and get wiped daily by a pg_cron job in the database.
/// </summary>
public class PollFunction
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PollFunction> _logger;

    public PollFunction(IHttpClientFactory httpClientFactory, ILogger<PollFunction> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [Function("PollFunction")]
    public async Task Run([TimerTrigger("0 0 * * * *")] TimerInfo timer)
    {
        var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_URL")
            ?? throw new InvalidOperationException("SUPABASE_URL app setting is not configured.");
        var publishableKey = Environment.GetEnvironmentVariable("SUPABASE_PUBLISHABLE_KEY")
            ?? throw new InvalidOperationException("SUPABASE_PUBLISHABLE_KEY app setting is not configured.");
        var ntfyTopicUrl = Environment.GetEnvironmentVariable("NTFY_URL")
            ?? throw new InvalidOperationException("NTFY_URL app setting is not configured.");

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(supabaseUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Add("apikey", publishableKey);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {publishableKey}");

            var insertResponse = await client.PostAsJsonAsync("rest/v1/polling", new { });
            insertResponse.EnsureSuccessStatusCode();

            var selectResponse = await client.GetAsync("rest/v1/polling?select=*&order=polled_at.desc");
            selectResponse.EnsureSuccessStatusCode();

            var rows = await selectResponse.Content.ReadFromJsonAsync<JsonElement>();
            var rowCount = rows.ValueKind == JsonValueKind.Array ? rows.GetArrayLength() : 0;

            _logger.LogInformation("Polling heartbeat sent. Current row count: {RowCount}", rowCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Polling heartbeat failed.");
            await NotifyAsync(ntfyTopicUrl, $"SimpleBoard polling heartbeat failed: {ex.Message}");
            throw;
        }
    }

    private async Task NotifyAsync(string ntfyTopicUrl, string message)
    {
        try
        {
            using var ntfyClient = _httpClientFactory.CreateClient();
            await ntfyClient.PostAsync(ntfyTopicUrl, new StringContent(message, Encoding.UTF8));
        }
        catch (Exception notifyEx)
        {
            _logger.LogError(notifyEx, "Failed to publish ntfy.sh alert.");
        }
    }
}
