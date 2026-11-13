using System.Net;
using System.Text.RegularExpressions;
using Backora.Contracts.V1.Auth;
using Backora.Web.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Backora.Web.Tests;

/// <summary>End-to-end BFF tests: real frontend host, fake backend API behind the anonymous client.</summary>
public sealed partial class BffLoginFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeBackend _backend = new();

    public BffLoginFlowTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureTestServices(s =>
            {
                s.AddHttpClient(BffClaims.BackendAnonymousClient).ConfigurePrimaryHttpMessageHandler(() => _backend);
                // Authenticated calls go through the real TokenHandler, but to the fake backend.
                s.AddScoped(sp => new BackendHttp(
                    new TokenHandler(sp.GetRequiredService<SessionAccessor>(), sp.GetRequiredService<SessionStore>(), sp.GetRequiredService<ITokenRefresher>()) { InnerHandler = _backend },
                    new BackendOptions { BaseUrl = "http://api.test" }));
            });
        });
    }

    private static TokenResponse Tokens() => new("access-1", DateTimeOffset.UtcNow.AddMinutes(15), "refresh-1", DateTimeOffset.UtcNow.AddDays(1),
        new CurrentUserDto(1, "admin@example.com", "Admin", ["PlatformAdministrator"], ["dashboard.read"], false, false, null));

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<Dictionary<string, string>> LoginFormFields(HttpClient client, string url = "/login")
    {
        var html = await client.GetStringAsync(url);
        var fields = new Dictionary<string, string>();
        foreach (Match m in HiddenInput().Matches(html)) fields[WebUtility.HtmlDecode(m.Groups["name"].Value)] = WebUtility.HtmlDecode(m.Groups["value"].Value);
        return fields;
    }

    [Fact]
    public async Task Successful_login_sets_session_cookie_and_redirects_to_local_return_url()
    {
        _backend.OnJson(HttpMethod.Post, "/api/v1/auth/login", new LoginResponse(false, null, Tokens()));
        _backend.On(HttpMethod.Get, "/api/v1/dashboard", req => Task.FromResult(req.Headers.Authorization?.Parameter == "access-1"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var client = Client();
        var fields = await LoginFormFields(client, "/login?returnUrl=%2Fjobs");
        Assert.Contains("__RequestVerificationToken", fields.Keys);
        fields["Model.Email"] = "admin@example.com";
        fields["Model.Password"] = "correct horse battery";

        var response = await client.PostAsync("/login?returnUrl=%2Fjobs", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        AssertLocal(response, "/jobs");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("backora.session=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access-1", cookie, StringComparison.Ordinal);

        var login = Assert.Single(_backend.Requests, r => r.Request.RequestUri!.AbsolutePath == "/api/v1/auth/login");
        Assert.Contains("admin@example.com", login.Body, StringComparison.Ordinal);

        var dashboard = await client.GetAsync("/");
        Assert.True(dashboard.StatusCode == HttpStatusCode.OK, $"GET / returned {(int)dashboard.StatusCode} -> {dashboard.Headers.Location}");
        Assert.Contains(">Overview", await dashboard.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // The browser-facing session cookie authenticated the page and the BFF attached the server-side token.
        var apiCall = Assert.Single(_backend.Requests, r => r.Request.RequestUri!.AbsolutePath == "/api/v1/dashboard");
        Assert.Equal("access-1", apiCall.Request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Open_redirect_return_urls_are_neutralised()
    {
        _backend.OnJson(HttpMethod.Post, "/api/v1/auth/login", new LoginResponse(false, null, Tokens()));
        var client = Client();
        var fields = await LoginFormFields(client, "/login?returnUrl=https%3A%2F%2Fevil.example%2F");
        fields["Model.Email"] = "admin@example.com";
        fields["Model.Password"] = "pw";
        var response = await client.PostAsync("/login", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        AssertLocal(response, "/");
    }

    [Fact]
    public async Task Invalid_credentials_show_error_and_issue_no_session()
    {
        _backend.OnProblem(HttpMethod.Post, "/api/v1/auth/login", HttpStatusCode.Unauthorized, new { title = "Unauthorized", detail = "Invalid email or password." });
        var client = Client();
        var fields = await LoginFormFields(client);
        fields["Model.Email"] = "admin@example.com";
        fields["Model.Password"] = "wrong";
        var response = await client.PostAsync("/login", new FormUrlEncodedContent(fields));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Invalid email or password.", html, StringComparison.Ordinal);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("backora.session=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Two_factor_challenge_redirects_to_second_step()
    {
        _backend.OnJson(HttpMethod.Post, "/api/v1/auth/login", new LoginResponse(true, "2fa-token", null));
        _backend.OnJson(HttpMethod.Post, "/api/v1/auth/login/2fa", Tokens());
        var client = Client();
        var fields = await LoginFormFields(client);
        fields["Model.Email"] = "admin@example.com";
        fields["Model.Password"] = "pw";
        var response = await client.PostAsync("/login", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = Local(response);
        Assert.StartsWith("/login/2fa", location, StringComparison.Ordinal);

        var twoFa = await LoginFormFields(client, location);
        twoFa["Model.Code"] = "123 456";
        var verified = await client.PostAsync(location, new FormUrlEncodedContent(twoFa));
        Assert.Equal(HttpStatusCode.Redirect, verified.StatusCode);
        var call = Assert.Single(_backend.Requests, r => r.Request.RequestUri!.AbsolutePath == "/api/v1/auth/login/2fa");
        Assert.Contains("\"twoFactorToken\":\"2fa-token\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"123456\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unauthenticated_pages_redirect_to_login_and_logout_requires_antiforgery()
    {
        var client = Client();
        var page = await client.GetAsync("/jobs");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("/login", page.Headers.Location!.ToString(), StringComparison.Ordinal);

        var logout = await client.PostAsync("/account/logout", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
    }

    [Fact]
    public async Task Security_headers_are_present()
    {
        var response = await Client().GetAsync("/login");
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    /// <summary>Redirect target must stay on this host; returns path + query.</summary>
    private static string Local(HttpResponseMessage response)
    {
        var loc = response.Headers.Location!;
        if (loc.IsAbsoluteUri)
        {
            Assert.Equal("localhost", loc.Host);
            return loc.PathAndQuery;
        }
        return loc.OriginalString;
    }

    private static void AssertLocal(HttpResponseMessage response, string expectedPath) => Assert.Equal(expectedPath, Local(response));

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInput();
}
