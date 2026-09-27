using System.Net;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

public sealed class GeoIpRegionsTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "GeoIP2-Country-Test.mmdb");

    [Fact]
    public void Une_adresse_connue_rend_son_continent()
    {
        var clock = new ManualClock();
        using var regions = new GeoIpRegions(Fixture, clock);
        clock.Set(regions.BuildDate!.Value.AddDays(1));

        Assert.Equal("EU", regions.RegionOf(IPAddress.Parse("81.2.69.160")));
    }

    [Fact]
    public void Une_adresse_inconnue_ne_rend_rien()
    {
        var clock = new ManualClock();
        using var regions = new GeoIpRegions(Fixture, clock);
        clock.Set(regions.BuildDate!.Value.AddDays(1));

        Assert.Null(regions.RegionOf(IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void Une_base_perimee_ne_rend_rien()
    {
        var clock = new ManualClock();
        using var regions = new GeoIpRegions(Fixture, clock);
        clock.Set(regions.BuildDate!.Value + GeoIpRegions.MaxAge + TimeSpan.FromDays(1));

        Assert.Null(regions.RegionOf(IPAddress.Parse("81.2.69.160")));
    }

    [Fact]
    public void Sans_fichier_rien_ne_leve()
    {
        using var regions = new GeoIpRegions(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.mmdb"), new ManualClock());

        Assert.Null(regions.BuildDate);
        Assert.Null(regions.RegionOf(IPAddress.Parse("81.2.69.160")));
    }

    [Theory]
    [InlineData("EU", "EU")]
    [InlineData("NA", "NA")]
    [InlineData("AN", null)]
    [InlineData("eu", null)]
    [InlineData(null, null)]
    public void Seuls_les_six_continents_habites_sont_retenus(string? code, string? expected)
        => Assert.Equal(expected, GeoIpRegions.Retained(code));
}
