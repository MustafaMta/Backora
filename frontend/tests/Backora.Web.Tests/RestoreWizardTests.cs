using System.Text.Json;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Web.Components.Pages.Recovery;
using Backora.Web.Components.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Backora.Web.Tests;

/// <summary>Restore gating: precheck, verified key for encrypted copies, typed confirmation, one idempotency key.</summary>
public class RestoreWizardTests : BunitContext
{
    private const string Key = "AGE-SECRET-KEY-1QQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQ";

    private static OffloadSummaryDto Encrypted() => TestData.RecoveryPoint() with { Encrypted = true, EncryptionKeyFingerprint = "fp-1", SourceOffloadId = 40, DestinationName = "DR Site A" };

    [Fact]
    public void Encrypted_dr_copy_needs_a_verified_key_and_the_exact_phrase_before_restore_starts()
    {
        var backend = this.AddBackora(Permissions.RestoresExecute, Permissions.RecoveryPointsRead);
        var point = Encrypted();
        backend.OnJson(HttpMethod.Get, "/api/v1/recovery-points", TestData.Page(point));
        backend.OnJson(HttpMethod.Post, "/api/v1/restores/precheck", TestData.Precheck(true) with { RecoveryPoint = point });
        backend.OnJson(HttpMethod.Post, "/api/v1/recovery-points/42/verify-key", new KeyVerificationDto(true, "fp-1", "Key matches"));
        backend.OnJson(HttpMethod.Post, "/api/v1/restores", TestData.Restore());

        var cut = Render<RestoreWizard>();
        void Choose() => cut.FindAll(".ui-choice:not([disabled])")[0].Click();

        // 1 — what to recover (the job), 2 — the recovery point, 3 — the copy (DR site A, encrypted)
        cut.WaitForAssertion(() => Assert.Contains("What do you want to recover?", cut.Markup, StringComparison.Ordinal));
        Choose(); Button("Continue").Click();
        cut.WaitForAssertion(() => Assert.Contains("Choose a recovery point", cut.Markup, StringComparison.Ordinal));
        Choose(); Button("Continue").Click();
        cut.WaitForAssertion(() => Assert.Contains("DR copy, replicated from", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Needs your private key", cut.Markup, StringComparison.Ordinal);
        Button("Continue").Click(); // the only copy is preselected

        // 4 — destination: checks run, nothing can replace a live database
        cut.WaitForAssertion(() => Assert.Contains("All checks passed.", cut.Markup, StringComparison.Ordinal));
        Assert.True(cut.FindAll(".ui-choice").Single(c => c.TextContent.Contains("Replace a live database", StringComparison.Ordinal)).HasAttribute("disabled"));
        Button("Continue").Click();

        // 5 — impact + key: malformed key is rejected locally; a matching key moves on
        cut.WaitForAssertion(() => Assert.Contains("Review the impact", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Replaces existing data", cut.Markup, StringComparison.Ordinal);
        cut.Find("#private-key").Input("AGE-SECRET-KEY-1 not a key");
        Assert.True(Button("Check key").HasAttribute("disabled"));
        cut.Find("#private-key").Input(Key);
        Button("Check key").Click();

        // Confirm step: needs a reason and the exact phrase.
        cut.WaitForAssertion(() => Assert.Contains("Confirm and restore", cut.Markup, StringComparison.Ordinal));
        cut.Find("#reason").Input("INC-1");
        cut.Find("#confirm").Input("PG-PROD");
        Assert.True(Button("Start restore").HasAttribute("disabled"));
        cut.Find("#confirm").Input("pg-prod");
        Assert.False(Button("Start restore").HasAttribute("disabled"));
        Button("Start restore").Click();

        static bool IsCreate((HttpRequestMessage Request, string? Body) r) => r.Request.Method == HttpMethod.Post && r.Request.RequestUri!.AbsolutePath == "/api/v1/restores";
        cut.WaitForAssertion(() => Assert.Single(backend.Requests, IsCreate));
        var call = backend.Requests.Single(IsCreate);
        Assert.True(call.Request.Headers.Contains("Idempotency-Key"));
        Assert.DoesNotContain(Key, call.Request.RequestUri!.ToString(), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(call.Body!);
        Assert.Equal(Key, json.RootElement.GetProperty("decryptionKey").GetString());
        Assert.Equal("pg-prod", json.RootElement.GetProperty("confirmationPhrase").GetString());

        AngleSharp.Dom.IElement Button(string text) => cut.FindAll(".create-card__foot button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal));
    }

    [Fact]
    public void Failed_precheck_blocks_the_restore()
    {
        var backend = this.AddBackora(Permissions.RestoresExecute, Permissions.RecoveryPointsRead);
        backend.OnJson(HttpMethod.Get, "/api/v1/recovery-points", TestData.Page(TestData.RecoveryPoint()));
        backend.OnJson(HttpMethod.Post, "/api/v1/restores/precheck", TestData.Precheck(false));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/restores/new?recoveryPointId=42");
        var cut = Render<RestoreWizard>();
        cut.WaitForAssertion(() => Assert.Contains("can't be restored right now", cut.Markup, StringComparison.Ordinal));
        Assert.True(cut.FindAll(".create-card__foot button").First(b => b.TextContent.Contains("Continue", StringComparison.Ordinal)).HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(Key, true)]
    [InlineData("AGE-SECRET-KEY-1 short", false)]
    [InlineData("age1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq", false)]
    [InlineData(null, false)]
    public void Key_format_check(string? key, bool valid) => Assert.Equal(valid, KeyField.LooksValid(key));
}
