using System.Text;
using System.Text.Json;
using AIUsage.Core.Services;

namespace AIUsage.Core.Providers.Devin;

public sealed class DevinUsageClient
{
    public const string CloudService = "exa.seat_management_pb.SeatManagementService";
    public const string CloudCompatVersion = "1.108.2";

    private readonly IHttpClient _http;

    public DevinUsageClient(IHttpClient? http = null)
    {
        _http = http ?? new SystemHttpClient();
    }

    public async Task<HttpResponseResult> FetchUserStatusAsync(DevinAuth auth, string apiServerUrl)
    {
        if (!Uri.TryCreate($"{apiServerUrl}/{CloudService}/GetUserStatus", UriKind.Absolute, out var url))
        {
            throw new DevinUsageError(DevinUsageErrorKind.InvalidResponse);
        }

        var body = JsonSerializer.Serialize(new
        {
            metadata = new
            {
                apiKey = auth.ApiKey,
                ideName = "devin",
                ideVersion = CloudCompatVersion,
                extensionName = "devin",
                extensionVersion = CloudCompatVersion,
                locale = "en"
            }
        });

        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "POST",
            Url = url,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json", ["Connect-Protocol-Version"] = "1" },
            Body = Encoding.UTF8.GetBytes(body),
            Timeout = TimeSpan.FromSeconds(15)
        }).ConfigureAwait(false);
    }
}
