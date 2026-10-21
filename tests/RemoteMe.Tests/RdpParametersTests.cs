using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using RemoteMe.Web.Catalog;
using RemoteMe.Web.Configuration;
using RemoteMe.Web.Environments;
using RemoteMe.Web.Guacamole;

namespace RemoteMe.Tests;

public class RdpParametersTests
{
    [Fact]
    public void RemoteApp_and_credentials_come_from_server_configuration()
    {
        var rdp = new RdpOptions { Hostname = "rds01", Domain = "CONTOSO", LoadBalanceInfo = "tsv://x" };
        var app = new AppDefinition { Id = "calc", Name = "Calc", Program = "||calc", Arguments = "/x", WorkingDirectory = "C:\\" };
        var account = new PoolAccount("kiosk01", "pw", domain: null);

        var p = RdpParameters.Build(rdp, app, account, "key", new ClientInfo { Timezone = "Europe/Paris" });

        Assert.Equal("rds01", p["hostname"]);
        Assert.Equal("kiosk01", p["username"]);
        Assert.Equal("pw", p["password"]);
        Assert.Equal("CONTOSO", p["domain"]);
        Assert.Equal("tsv://x", p["load-balance-info"]);
        Assert.Equal("||calc", p["remote-app"]);
        Assert.Equal("/x", p["remote-app-args"]);
        Assert.Equal("C:\\", p["remote-app-dir"]);
        Assert.Equal("Europe/Paris", p["timezone"]);
        Assert.False(p.ContainsKey("enable-drive"));
    }

    [Fact]
    public void Empty_program_opens_a_full_desktop()
    {
        var p = RdpParameters.Build(
            new RdpOptions { Hostname = "h" }, new AppDefinition { Id = "desk", Name = "Bureau" },
            new PoolAccount("u", "p", null), "key", new ClientInfo());

        Assert.False(p.ContainsKey("remote-app"));
    }

    [Fact]
    public void App_overrides_win_over_common_settings()
    {
        var rdp = new RdpOptions { Hostname = "rds01", ExtraParameters = { ["color-depth"] = "16", ["enable-wallpaper"] = "true" } };
        var app = new AppDefinition
        {
            Id = "erp", Name = "ERP", Program = "||erp", Hostname = "rds-erp",
            Parameters = { ["color-depth"] = "24" },
        };

        var p = RdpParameters.Build(rdp, app, new PoolAccount("u", "p", "D"), "key", new ClientInfo());

        Assert.Equal("rds-erp", p["hostname"]);
        Assert.Equal("24", p["color-depth"]);
        Assert.Equal("true", p["enable-wallpaper"]);
        Assert.Equal("D", p["domain"]);
    }

    [Fact]
    public void Virtual_drive_is_scoped_to_the_environment_not_the_account()
    {
        var rdp = new RdpOptions { Hostname = "h", EnableDrive = true, DriveRootPath = "/drives/" };
        var p = RdpParameters.Build(
            rdp, new AppDefinition { Id = "a", Name = "A" }, new PoolAccount("kiosk01", "p", null), "env123", new ClientInfo());

        Assert.Equal("/drives/env123", p["drive-path"]);
        Assert.Equal("true", p["create-drive-path"]);
    }

    [Fact]
    public void Client_info_is_clamped_and_filtered()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["width"] = "99999",
            ["height"] = "-5",
            ["dpi"] = "abc",
            ["tz"] = "Europe/Paris;rm -rf",
            ["audio"] = new StringValues(new[] { "audio/L16", "audio/evil" }),
            ["image"] = new StringValues(new[] { "image/webp", "image/gif" }),
        });

        var info = ClientInfo.FromQuery(query);

        Assert.Equal(7680, info.Width);
        Assert.Equal(768, info.Height);
        Assert.Equal(96, info.Dpi);
        Assert.Null(info.Timezone);
        Assert.Equal(new[] { "audio/L16" }, info.AudioMimetypes);
        Assert.Equal(new[] { "image/webp" }, info.ImageMimetypes);
    }

    [Theory]
    [InlineData("Europe/Paris")]
    [InlineData("America/Argentina/Buenos_Aires")]
    [InlineData("UTC")]
    [InlineData("Etc/GMT+5")]
    public void Valid_timezones_are_kept(string tz)
    {
        var info = ClientInfo.FromQuery(new QueryCollection(new Dictionary<string, StringValues> { ["tz"] = tz }));
        Assert.Equal(tz, info.Timezone);
    }
}
