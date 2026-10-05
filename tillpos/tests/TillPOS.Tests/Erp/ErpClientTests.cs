using System.Net;
using System.Text.Json;
using TillPOS.Erp;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Erp;

public class ErpClientTests
{
    private static (ErpClient Client, StubHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new ErpClient(new HttpClient(handler), new ErpConnection(new Uri("https://erp.test/"), "key1", "secret1")), handler);
    }

    [Fact]
    public async Task Sends_token_auth_and_posts_get_list()
    {
        var (client, handler) = Make(_ => StubHandler.Json("""{"message":[{"name":"RICE5"},{"name":"MILK"}]}"""));

        var rows = await client.GetListAsync(new ListQuery("Item", ["name"], [["disabled", "=", 0]], "modified asc", 0, 500));

        Assert.Equal(new[] { "RICE5", "MILK" }, rows.Select(r => r.GetProperty("name").GetString()));
        var (req, body) = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://erp.test/api/method/frappe.client.get_list", req.RequestUri!.ToString());
        Assert.Equal("token key1:secret1", req.Headers.Authorization!.ToString());
        using var sent = JsonDocument.Parse(body!);
        Assert.Equal("Item", sent.RootElement.GetProperty("doctype").GetString());
        Assert.Equal(500, sent.RootElement.GetProperty("limit_page_length").GetInt32());
        Assert.Equal("disabled", sent.RootElement.GetProperty("filters")[0][0].GetString());
    }

    [Fact]
    public async Task Get_doc_escapes_doctype_and_name()
    {
        var (client, handler) = Make(_ => StubHandler.Json("""{"data":{"name":"Offer 10/24"}}"""));
        var doc = await client.GetDocAsync("Pricing Rule", "Offer 10/24");
        Assert.Equal("Offer 10/24", doc.GetProperty("name").GetString());
        Assert.Equal("/api/resource/Pricing%20Rule/Offer%2010%2F24", handler.Requests.Single().Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Ping_returns_user_and_server_date()
    {
        var (client, _) = Make(_ =>
        {
            var r = StubHandler.Json("""{"message":"till1@shop.local"}""");
            r.Headers.Date = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
            return r;
        });
        var info = await client.PingAsync();
        Assert.Equal("till1@shop.local", info.User);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero), info.ServerTime);
    }

    [Fact]
    public async Task Error_response_becomes_erp_exception_with_server_message()
    {
        const string body = """{"exc_type":"ValidationError","_server_messages":"[\"{\\\"message\\\": \\\"Item <b>RICE5</b> is disabled\\\"}\"]"}""";
        var (client, _) = Make(_ => StubHandler.Json(body, HttpStatusCode.ExpectationFailed));

        var ex = await Assert.ThrowsAsync<ErpException>(() => client.GetDocAsync("Item", "RICE5"));

        Assert.Equal(417, ex.StatusCode);
        Assert.Equal("ValidationError", ex.ExcType);
        Assert.Equal("Item RICE5 is disabled", ex.Message);
    }

    [Fact]
    public async Task Non_json_error_still_gives_a_readable_message()
    {
        var (client, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>502 Bad Gateway</html>") });
        var ex = await Assert.ThrowsAsync<ErpException>(() => client.PingAsync());
        Assert.Equal(502, ex.StatusCode);
        Assert.Contains("HTTP 502", ex.Message);
    }
}
