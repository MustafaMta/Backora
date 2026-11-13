using Microsoft.Extensions.DependencyInjection;
using Backora.Components;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Web.Components.Pages.Storage;
using Bunit;
using Microsoft.AspNetCore.Components.Web;

namespace Backora.Web.Tests;

/// <summary>The searchable select and the Stored files page (live S3 listing, search, folders).</summary>
public class StoredFilesTests : BunitContext
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private sealed record Srv(long Id, string Name, string Host);

    private static S3DestinationDto Dest(long id, string name, string bucket) => new(
        id, name, "https://s3.eu-central-1.amazonaws.com", "eu-central-1", bucket, "AK…", false, "backora", Now.AddHours(-1), true, null, 0, 0, 0, 1, Now.AddDays(-30),
        EncryptionModeDto.None, IsEnabled: true);

    [Fact]
    public void SearchSelect_filters_highlights_and_selects()
    {
        this.AddBackora(Permissions.JobsRead);
        Srv? picked = null;
        var items = new List<Srv> { new(1, "billing-db", "10.0.0.1"), new(2, "Inventory Cluster", "10.0.0.3"), new(3, "zabbix", "10.0.0.9") };
        var cut = Render<SearchSelect<Srv>>(p => p.Add(x => x.Items, items).Add(x => x.Text, s => s.Name).Add(x => x.Sub, s => s.Host)
            .Add(x => x.Key, s => s.Id).Add(x => x.OnSelect, s => picked = s));

        cut.Find(".ui-ss__trigger").Click();
        Assert.Equal(3, cut.FindAll(".ui-ss__opt").Count);
        cut.Find(".ui-ss__search input").Input("cluster");
        Assert.Single(cut.FindAll(".ui-ss__opt"));
        Assert.Equal("Cluster", cut.Find(".ui-ss__opt mark").TextContent);
        cut.Find(".ui-ss__search input").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        Assert.Equal(2, picked?.Id);
        Assert.Empty(cut.FindAll(".ui-ss__pop"));
    }

    [Fact]
    public void StoredFiles_lists_folders_and_files_from_the_site()
    {
        var backend = this.AddBackora(Permissions.StorageRead);
        backend.OnJson(HttpMethod.Get, "/api/v1/storage/s3/6/objects", new S3BrowseResponse(6, "Amazon DR", "dr-archive", "backora/", [
            new("backora/billing-db/", "billing-db", true, 42_949_672_960, Now.AddHours(-2), 14),
            new("backora/zabbix/", "zabbix", true, 10_737_418_240, Now.AddHours(-9), 31),
            new("backora/manifest.json", "manifest.json", false, 2048, Now.AddHours(-3)),
            new("backora/zabbix-2026-11-11_02.tar.age", "zabbix-2026-11-11_02.tar.age", false, 5_368_709_120, Now.AddHours(-9)),
        ], null, false, false, 4, new S3BrowseTotals(47, 58_993_184_768, 2, Now.AddHours(-2), Now.AddDays(-30), true)));
        backend.OnJson(HttpMethod.Get, "/api/v1/storage/s3", new List<S3DestinationDto> { Dest(6, "Amazon DR", "dr-archive"), Dest(2, "ECS", "backora") });

        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/files?site=6");
        var cut = Render<StoredFiles>();
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".sf-table .ui-list__row").Count));
        Assert.Contains("dr-archive", cut.Find(".sf-crumb--root").TextContent);
        Assert.Contains("Encrypted archive", cut.Markup);
        Assert.Contains("5.00 GB", cut.Markup);
        Assert.Contains("54.9 GB", cut.Find(".sf-stat--hero b").TextContent);          // recursive total, not just this folder's files
        Assert.Equal("47", cut.FindAll(".sf-stat b")[1].TextContent.Trim());
        Assert.Contains("14 files", cut.Markup);                                       // per-folder file count
        var dump = Environment.GetEnvironmentVariable("BACKORA_MARKUP_DIR");
        if (dump is not null) File.WriteAllText(Path.Combine(dump, "storedfiles.html"), cut.Markup);
    }
}
