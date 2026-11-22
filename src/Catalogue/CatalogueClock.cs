using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ScriptDesk.Catalogue;

// A fresh TLS-authenticated response supplies time only. No caller-supplied time,
// license, key, device identity or configurable endpoint crosses this boundary.
internal static class CatalogueClock
{
    internal const string Endpoint="https://hexskyrimmp.com/api/setup/time";
    static DateTimeOffset? anchor;
    static long started;
    internal static bool Anchored=>anchor.HasValue;
    internal static DateTimeOffset Now=>anchor!.Value.Add(Stopwatch.GetElapsedTime(started));
    internal static async Task<DateTimeOffset> Read(HttpClient client,string nonce)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,Endpoint);
        request.Content=new StringContent(JsonSerializer.Serialize(new{schema=1,nonce}),Encoding.UTF8,"application/json");
        using var response=await client.SendAsync(request);
        if(response.StatusCode!=System.Net.HttpStatusCode.OK)throw new InvalidDataException("HEX time verification is unavailable. Check your connection and Windows date and time, then retry.");
        var bytes=await response.Content.ReadAsByteArrayAsync();
        if(bytes.Length>2048)throw new InvalidDataException("Invalid HEX time response.");
        using var document=JsonDocument.Parse(bytes);
        var value=document.RootElement;
        if(value.ValueKind!=JsonValueKind.Object||value.EnumerateObject().Count()!=3||
           !value.TryGetProperty("schema",out var schema)||!schema.TryGetInt32(out var version)||version!=1||
           !value.TryGetProperty("nonce",out var echo)||echo.ValueKind!=JsonValueKind.String||echo.GetString()!=nonce||
           !value.TryGetProperty("utc",out var time)||!time.TryGetInt64(out var milliseconds))
            throw new InvalidDataException("Invalid HEX time response.");
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }
    internal static DateTimeOffset Synchronize()
    {
        if(Anchored)return Now;
        using var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false};
        using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(8),MaxResponseContentBufferSize=2048};
        var begin=Stopwatch.GetTimestamp();
        var time=Read(client,Guid.NewGuid().ToString("N")).GetAwaiter().GetResult();
        // Count the whole request conservatively; latency can never extend access.
        started=begin;anchor=time;
        return Now;
    }
}
