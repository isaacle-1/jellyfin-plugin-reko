using System.Text.Json;
using Jellyfin.Plugin.Reko.Services;
using Jellyfin.Plugin.Reko.Services.Tmdb;
using Xunit;

namespace Jellyfin.Plugin.Reko.Tests;

/// <summary>
/// Asserts that the certification lookup works against the real per-country shapes.
/// </summary>
/// <remarks>
/// Tested through <see cref="CertificationResolver"/> rather than through the card factory that
/// calls it, because the resolver is pure and the factory's other three dependencies have nothing to
/// do with choosing a certification.
/// </remarks>
public class CertificationResolverTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private const string UsReleaseDates = """
    {
      "id": 550,
      "results": [
        { "iso_3166_1": "US", "release_dates": [
          { "certification": "", "note": "CMJ Film Festival",
            "release_date": "1999-09-21T00:00:00.000Z", "type": 1 },
          { "certification": "R", "note": "", "release_date": "1999-10-15T00:00:00.000Z", "type": 3 } ] },
        { "iso_3166_1": "GB", "release_dates": [
          { "certification": "18", "note": "", "release_date": "1999-11-12T00:00:00.000Z", "type": 3 } ] }
      ]
    }
    """;

    private const string UsContentRatings = """
    { "id": 1399, "results": [ { "iso_3166_1": "US", "rating": "TV-MA" } ] }
    """;

    private static TmdbReleaseDates Dates() => JsonSerializer.Deserialize<TmdbReleaseDates>(UsReleaseDates, Options)!;

    private static TmdbContentRatings Ratings() => JsonSerializer.Deserialize<TmdbContentRatings>(UsContentRatings, Options)!;

    [Theory]
    [InlineData("US", "R")]
    [InlineData("GB", "18")]
    [InlineData("us", "R")]
    public void ResolvesTheMovieCertificationForTheConfiguredRegion(string region, string expected)
    {
        Assert.Equal(expected, CertificationResolver.Resolve(region, Dates(), null));
    }

    [Fact]
    public void SkipsUncertificatedPremieres()
    {
        // The first US entry is a festival premiere with no certification. Returning it would show
        // no rating on the card for most films, which reads as "unrated" rather than "not found".
        Assert.Equal("R", CertificationResolver.Resolve("US", Dates(), null));
    }

    [Fact]
    public void ReturnsNothingForARegionTmdbDoesNotList()
    {
        Assert.Null(CertificationResolver.Resolve("NZ", Dates(), null));
    }

    [Fact]
    public void FallsBackToTvRatingsWhenTheMovieHasNoReleaseDates()
    {
        Assert.Equal("TV-MA", CertificationResolver.Resolve("US", null, Ratings()));
    }

    [Fact]
    public void PrefersTheMovieCertification()
    {
        Assert.Equal("R", CertificationResolver.Resolve("US", Dates(), Ratings()));
    }

    [Fact]
    public void DoesNotThrowWhenBothAreNull()
    {
        Assert.Null(CertificationResolver.Resolve("US", null, null));
    }

    [Fact]
    public void ToleratesACountryGroupWithNoDatedReleases()
    {
        var dates = new TmdbReleaseDates
        {
            Results = [new TmdbReleaseDateCountry { Country = "US", ReleaseDates = [] }]
        };

        Assert.Null(CertificationResolver.Resolve("US", dates, null));
    }

    [Fact]
    public void ToleratesACertificationThatIsOnlyWhitespace()
    {
        // TMDB pads some certifications with trailing spaces, for example "R-18 ".
        var dates = new TmdbReleaseDates
        {
            Results =
            [
                new TmdbReleaseDateCountry
                {
                    Country = "US",
                    ReleaseDates = [new TmdbReleaseDateEntry { Certification = "   " }]
                }
            ]
        };

        Assert.Null(CertificationResolver.Resolve("US", dates, null));
    }

    [Fact]
    public void TrimsTheCertificationItReturns()
    {
        var dates = new TmdbReleaseDates
        {
            Results =
            [
                new TmdbReleaseDateCountry
                {
                    Country = "TW",
                    ReleaseDates = [new TmdbReleaseDateEntry { Certification = "R-18 " }]
                }
            ]
        };

        Assert.Equal("R-18", CertificationResolver.Resolve("TW", dates, null));
    }
}
