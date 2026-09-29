using System.Text.RegularExpressions;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Web.Pages;

namespace ShizuAppStoreServer.Web.Tests;

public sealed class AppsPageTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task Apps_ListsAndFilters()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            db.Add(Seeds.Category(2, "shell", "Shell", parentId: 1));

            var foo = Seeds.App(1, "foo", "Foo", 2);
            foo.SourceKind = SourceKind.GitHub;
            foo.IsRecommended = true;
            foo.Stars = 1200;
            db.Add(foo);
            db.Add(Seeds.Download(1, "https://example.com/foo.apk"));

            var bar = Seeds.App(2, "bar", "Bar", 1);
            bar.HasPaid = true;
            bar.HasIap = true;
            bar.HasAds = true;
            db.Add(bar);
            db.Add(Seeds.Download(2, "https://example.com/bar.apk"));

            db.Add(Seeds.App(3, "baz", "Baz", 1));
        });

        var client = factory.NewClient();

        var all = await GetBodyAsync(client, "/apps");
        Assert.Contains("Foo", all);
        Assert.Contains("Bar", all);
        Assert.Contains("Baz", all);
        Assert.Contains("href=\"/apps/foo\"", all);
        Assert.Contains(">IAP<", all);
        Assert.Contains(">Paid<", all);
        Assert.Contains(">Ads<", all);
        Assert.Contains("mdui-dropdown", all);
        Assert.Contains("All categories", all);
        Assert.Contains("href=\"/apps?category=tools\"", all);
        Assert.Contains("href=\"/apps?sort=recentlyupdated\"", all);
        Assert.Contains("href=\"/apps?price=free\"", all);
        Assert.Contains("material-symbols-rounded", all);
        Assert.Contains("aria-hidden=\"true\">", all);
        Assert.Contains("<span slot=\"icon\">", all);
        Assert.Contains(">category</span>", all);
        Assert.Contains("class=\"category-cloud\"", all);

        var fooIndex = all.IndexOf("href=\"/apps/foo\"", StringComparison.Ordinal);
        var barIndex = all.IndexOf("href=\"/apps/bar\"", StringComparison.Ordinal);
        Assert.True(fooIndex >= 0 && barIndex >= 0 && fooIndex < barIndex);
        Assert.True(all.IndexOf("href=\"/apps?recommended=true\"", StringComparison.Ordinal)
            < all.IndexOf("All prices", StringComparison.Ordinal));
        Assert.DoesNotContain("name=\"sort\"", all);

        var search = await GetBodyAsync(client, "/apps?q=baz");
        Assert.Contains("Baz", search);
        Assert.DoesNotContain(">Bar<", search);

        var stars = await GetBodyAsync(client, "/apps?sort=stars");
        Assert.Contains("1.2k", stars);
        Assert.Contains("&#x2605;", stars);
        Assert.Contains("value=\"stars\"", stars);
        Assert.Contains(">star</span>", stars);

        var nameSort = await GetBodyAsync(client, "/apps?sort=name");
        Assert.Contains("class=\"filter-chip selected\"", nameSort);
        Assert.Contains(">sort_by_alpha</span>", nameSort);
        Assert.Contains("name=\"sort\" value=\"name\"", nameSort);

        var free = await GetBodyAsync(client, "/apps?price=free");
        Assert.Contains("Foo", free);
        Assert.DoesNotContain(">Bar<", free);

        var recommended = await GetBodyAsync(client, "/apps?recommended=true");
        Assert.Contains("Foo", recommended);
        Assert.DoesNotContain(">Bar<", recommended);

        var rootCategory = await GetBodyAsync(client, "/apps?category=tools");
        Assert.Contains("Foo", rootCategory);
        Assert.Contains("Bar", rootCategory);

        var childCategory = await GetBodyAsync(client, "/apps?category=shell");
        Assert.Contains("Foo", childCategory);
        Assert.DoesNotContain(">Bar<", childCategory);
    }

    [Fact]
    public async Task Apps_ShowsEmptyState()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            db.Add(Seeds.App(1, "foo", "Foo", 1));
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps?q=zzz");

        Assert.Contains("No apps match that search", html);
    }

    [Fact]
    public async Task Apps_Paginates()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            for (var i = 1; i <= AppsModel.PageSize + 2; i++)
            {
                var app = Seeds.App(i, $"app-{i}", $"App {i}", 1);
                app.Stars = 1000 - i;
                db.Add(app);
            }
        });

        var client = factory.NewClient();

        var first = await GetBodyAsync(client, "/apps");
        Assert.Contains("class=\"pagination\"", first);
        Assert.Equal(AppsModel.PageSize, Regex.Matches(first, "class=\"surface-card app-list-row\"").Count);
        Assert.Contains("href=\"/apps?page=2\"", first);
        Assert.Contains("rel=\"next\"", first);
        Assert.Contains("href=\"/apps/app-48\"", first);
        Assert.DoesNotContain("href=\"/apps/app-49\"", first);

        var second = await GetBodyAsync(client, "/apps?page=2");
        Assert.Contains("href=\"/apps/app-49\"", second);
        Assert.Contains("href=\"/apps/app-50\"", second);
        Assert.DoesNotContain("href=\"/apps/app-48\"", second);
        Assert.DoesNotContain("href=\"/apps?page=2\"", second);
        Assert.Contains("rel=\"prev\"", second);
    }

    [Fact]
    public async Task Apps_PaginationWindow()
    {
        await factory.ResetAsync(db =>
        {
            db.Add(Seeds.Category(1, "tools", "Tools"));
            for (var i = 1; i <= AppsModel.PageSize * 10; i++)
            {
                var app = Seeds.App(i, $"app-{i}", $"App {i}", 1);
                app.Stars = 10000 - i;
                db.Add(app);
            }
        });

        var html = await GetBodyAsync(factory.NewClient(), "/apps?page=5");

        Assert.Contains("href=\"/apps?page=4\"", html);
        Assert.Contains("href=\"/apps?page=6\"", html);
        Assert.Contains("href=\"/apps?page=10\"", html);
        Assert.DoesNotContain("href=\"/apps?page=2\"", html);
        Assert.DoesNotContain("href=\"/apps?page=3\"", html);
        Assert.DoesNotContain("href=\"/apps?page=9\"", html);
        Assert.Equal(2, Regex.Matches(html, "class=\"pagination-gap\"").Count);
    }

    private static async Task<string> GetBodyAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
