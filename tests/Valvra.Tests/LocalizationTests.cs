using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Valvra.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("en", null, "sv-SE", "en")]
    [InlineData(null, "en", "sv-SE", "en")]
    [InlineData(null, null, "de-DE,en-US;q=0.8", "en")]
    [InlineData("invalid", "sv", "en-GB", "sv")]
    [InlineData(null, null, "en;q=0,sv;q=0.8", "sv")]
    [InlineData(null, null, "malformed;;;", "sv")]
    public async Task SupportedRequestCultureHonorsExplicitPreferenceAndSafeFallback(string? header, string? cookie, string browser, string expected)
    {
        await using var host = new TestWebHost(); using var client = host.Client("user");
        if (header is not null) client.DefaultRequestHeaders.Add("X-Valvra-Language", header);
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", "Valvra.Language=" + cookie);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", browser);
        var html = await client.GetStringAsync("/");
        Assert.Contains($"<html lang=\"{expected}\">", html);
        Assert.Contains(expected == "en" ? "Your resources" : "Dina resurser", html);
    }

    [Theory]
    [InlineData("en", "Invalid security token. Reload the page.", "Access denied.")]
    [InlineData("sv", "Ogiltig säkerhetstoken. Ladda om sidan.", "Åtkomst nekad.")]
    public async Task LocalizedErrorsKeepCsrfAndAuthorizationEnforcement(string language, string csrfError, string deniedError)
    {
        await using var host = new TestWebHost(); using var client = host.Client("outsider");
        client.DefaultRequestHeaders.Add("X-Valvra-Language", language);
        var csrf = await client.PostAsJsonAsync("/api/groups", new { name = "Spara" });
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode); Assert.Contains(csrfError, await csrf.Content.ReadAsStringAsync());
        await host.AddCsrfAsync(client);
        var denied = await client.PostAsJsonAsync("/api/secrets/" + Guid.NewGuid() + "/reveal", new { });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Contains(deniedError, await denied.Content.ReadAsStringAsync());
        Assert.Contains(host.Transport.Events, x => x.Action == "Access.Denied" && x.ActorId == "outsider");
    }

    [Fact]
    public async Task EnglishPresentationPreservesUserDataAndInvariantAuditActions()
    {
        await using var host = new TestWebHost(); using var client = host.Client("user");
        client.DefaultRequestHeaders.Add("X-Valvra-Language", "en"); await host.AddCsrfAsync(client);
        var created = await client.PostAsJsonAsync("/api/groups", new { name = "Dina resurser", parentId = (Guid?)null });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var groups = await client.GetFromJsonAsync<JsonElement>("/api/groups");
        Assert.Equal("Dina resurser", groups[0].GetProperty("name").GetString());
        Assert.Contains(host.Transport.Events, x => x.Action == "Group.Create" && x.Outcome == "Committed");
    }
}
