using System;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Reko.Services.Tmdb;
using Xunit;

namespace Jellyfin.Plugin.Reko.Tests;

/// <summary>
/// Asserts Reko's TMDB models against real response bodies.
/// </summary>
/// <remarks>
/// The bodies here are TMDB's own published examples, trimmed but otherwise byte-for-byte in shape:
/// <list type="bullet">
///   <item><description>
///     <c>release_dates</c> for movie 550, from
///     <c>developer.themoviedb.org/reference/movie-release-dates</c>.
///   </description></item>
///   <item><description>
///     <c>content_ratings</c> for series 1399, from
///     <c>developer.themoviedb.org/reference/tv-series-content-ratings</c>.
///   </description></item>
/// </list>
///
/// <para>
/// These are shape tests, not value tests. A model that transcribes the JSON correctly decodes; a
/// model that guesses does not, and the failure is otherwise invisible until a person opens a title
/// page and watches it come back empty.
/// </para>
/// </remarks>
public class TmdbModelTests
{
    /// <summary>
    /// The serializer options the TMDB client uses, mirrored so a test failure means the model is
    /// wrong rather than the test disagreeing with production about how JSON is read.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A movie detail response with the appended sub-resources Reko asks for. Trimmed to the fields
    /// Reko reads; the omitted fields are not what these tests are about.
    /// </summary>
    private const string MovieDetailJson = """
    {
      "id": 550,
      "title": "Fight Club",
      "original_title": "Fight Club",
      "overview": "A ticking-time-bomb insomniac.",
      "release_date": "1999-10-15",
      "poster_path": "/pB8BM7pdSp6B6Ih7QZ4DrQ3PmJK.jpg",
      "backdrop_path": "/hZkgoQYus5vegHoetLkCJzb17zJ.jpg",
      "vote_average": 8.4,
      "vote_count": 26280,
      "popularity": 61.4,
      "genres": [
        { "id": 18, "name": "Drama" },
        { "id": 53, "name": "Thriller" }
      ],
      "credits": {
        "id": 550,
        "cast": [
          { "id": 819, "name": "Edward Norton", "character": "The Narrator", "order": 0,
            "profile_path": "/8nytsqL59SFJTVYVrN72k6qkGgJ.jpg" }
        ],
        "crew": []
      },
      "videos": {
        "id": 550,
        "results": [
          { "id": "5337c10d4a1d6c3f0001e3a", "key": "BdJKm16Co6M", "name": "Trailer",
            "site": "YouTube", "size": 1080, "type": "Trailer", "iso_639_1": "en" }
        ]
      },
      "images": {
        "id": 550,
        "backdrops": [
          { "aspect_ratio": 1.78, "file_path": "/hZkgoQYus5vegHoetLkCJzb17zJ.jpg",
            "height": 1080, "iso_639_1": null, "vote_average": 5.4, "vote_count": 12, "width": 1920 }
        ],
        "logos": [
          { "aspect_ratio": 2.76, "file_path": "/2PTb0m1Jk6o1hjM7EmY4FZxE1hs.jpg",
            "height": 1000, "iso_639_1": "en", "vote_average": 5.4, "vote_count": 3, "width": 2760 }
        ],
        "posters": []
      },
      "recommendations": { "page": 1, "results": [], "total_pages": 0, "total_results": 0 },
      "similar": { "page": 1, "results": [], "total_pages": 0, "total_results": 0 },
      "release_dates": {
        "id": 550,
        "results": [
          { "iso_3166_1": "CH",
            "release_dates": [
              { "certification": "18", "descriptors": [], "iso_639_1": "de",
                "note": "", "release_date": "1999-11-04T00:00:00.000Z", "type": 3 }
            ] },
          { "iso_3166_1": "US",
            "release_dates": [
              { "certification": "", "descriptors": [], "iso_639_1": "",
                "note": "CMJ Film Festival", "release_date": "1999-09-21T00:00:00.000Z", "type": 1 },
              { "certification": "", "descriptors": [], "iso_639_1": "",
                "note": "Westwood, California (Premiere)", "release_date": "1999-10-06T00:00:00.000Z", "type": 1 },
              { "certification": "R", "descriptors": [], "iso_639_1": "",
                "note": "", "release_date": "1999-10-15T00:00:00.000Z", "type": 3 }
            ] },
          { "iso_3166_1": "GB",
            "release_dates": [
              { "certification": "18", "descriptors": [], "iso_639_1": "",
                "note": "", "release_date": "1999-11-12T00:00:00.000Z", "type": 3 }
            ] }
        ]
      },
      "watch/providers": {
        "id": 550,
        "results": {
          "US": {
            "link": "https://www.themoviedb.org/movie/550-fight-club/watch?locale=US",
            "rent": [
              { "logo_path": "/5NyLm42TmCqCMOZFvH4fcoSNKEW.jpg", "provider_id": 10,
                "provider_name": "Amazon Video", "display_priority": 13 }
            ],
            "flatrate": [
              { "logo_path": "/jPXksae158ukMLFhhlNvzsvaEyt.jpg", "provider_id": 257,
                "provider_name": "fuboTV", "display_priority": 5 }
            ],
            "buy": [
              { "logo_path": "/peURlLlr8jggOwK53fJ5wdQl05y.jpg", "provider_id": 2,
                "provider_name": "Apple TV", "display_priority": 4 }
            ]
          },
          "GB": {
            "link": "https://www.themoviedb.org/movie/550-fight-club/watch?locale=GB"
          }
        }
      }
    }
    """;

    /// <summary>
    /// A series detail response with the appended sub-resources Reko asks for.
    /// </summary>
    private const string SeriesDetailJson = """
    {
      "id": 1399,
      "name": "Game of Thrones",
      "original_name": "Game of Thrones",
      "overview": "Seven noble families fight for control of the mythical land of Westeros.",
      "first_air_date": "2011-04-17",
      "poster_path": "/1XS1oqL89opfnbLl8WnZY1O1uJx.jpg",
      "backdrop_path": "/rkB4LyZHo1NHXFEDHl9vSD9r1lI.jpg",
      "vote_average": 8.4,
      "vote_count": 21999,
      "popularity": 369.594,
      "genres": [ { "id": 10765, "name": "Sci-Fi & Fantasy" } ],
      "seasons": [
        { "air_date": "2011-04-17", "episode_count": 10, "id": 3624, "name": "Season 1",
          "overview": "Trouble is brewing.", "poster_path": "/zwaj4egrhnXOBIit1tyb4Sbt3KP.jpg",
          "season_number": 1, "vote_average": 7.7 }
      ],
      "network": {
        "id": 49, "name": "HBO", "logo_path": "/tuomPhY2UtuPTbnFnIX5Q6PDdCB.jpg",
        "origin_country": "US"
      },
      "aggregate_credits": {
        "id": 1399,
        "cast": [
          { "id": 4788, "name": "Emilia Clarke", "roles": [ { "character": "Daenerys Targaryen", "episode_count": 73 } ],
            "total_episode_count": 73, "order": 0, "profile_path": "/rAZOdken40jjjJ8mqYdd0LDqgLT.jpg" }
        ],
        "crew": []
      },
      "videos": { "id": 1399, "results": [] },
      "images": { "id": 1399, "backdrops": [], "logos": [], "posters": [] },
      "recommendations": { "page": 1, "results": [], "total_pages": 0, "total_results": 0 },
      "similar": { "page": 1, "results": [], "total_pages": 0, "total_results": 0 },
      "content_ratings": {
        "results": [
          { "descriptors": [], "iso_3166_1": "DE", "rating": "16" },
          { "descriptors": [], "iso_3166_1": "AU", "rating": "R18+" },
          { "descriptors": [], "iso_3166_1": "FR", "rating": "16" },
          { "descriptors": [], "iso_3166_1": "US", "rating": "TV-MA" },
          { "descriptors": [], "iso_3166_1": "GB", "rating": "18" }
        ],
        "id": 1399
      }
    }
    """;

    [Fact]
    public void MovieDetailDecodesReleaseDates()
    {
        var detail = JsonSerializer.Deserialize<TmdbMovieDetail>(MovieDetailJson, Options);

        Assert.NotNull(detail);
        Assert.Equal(550, detail.Id);
        Assert.Equal("Fight Club", detail.Title);
        Assert.NotNull(detail.ReleaseDates);

        // results is an array of per-country groups. Modelling it as a map keyed by country code
        // deserialises nothing and throws on the whole document, which loses the title page
        // entirely — the failure looks like a network problem and is not one.
        Assert.Equal(3, detail.ReleaseDates.Results.Count);
        Assert.All(detail.ReleaseDates.Results, r => Assert.NotNull(r.Country));
        Assert.Contains(detail.ReleaseDates.Results, r => r.Country == "US");
    }

    [Fact]
    public void ReleaseDatesKeepTheOrderTmdbReturnsThem()
    {
        var detail = JsonSerializer.Deserialize<TmdbMovieDetail>(MovieDetailJson, Options);
        var us = detail!.ReleaseDates!.Results.First(r => r.Country == "US");

        // The US group opens with two uncertificated festival premieres. Taking the first entry
        // rather than the first *certificated* one shows no rating at all for most films.
        Assert.Equal(3, us.ReleaseDates.Count);
        Assert.Equal(string.Empty, us.ReleaseDates[0].Certification);
        Assert.Equal("CMJ Film Festival", us.ReleaseDates[0].Note);
        Assert.Equal(1, us.ReleaseDates[0].Type);
        Assert.Equal("R", us.ReleaseDates[2].Certification);
        Assert.Equal(3, us.ReleaseDates[2].Type);
    }

    [Fact]
    public void SeriesDetailDecodesContentRatings()
    {
        var detail = JsonSerializer.Deserialize<TmdbSeriesDetail>(SeriesDetailJson, Options);

        Assert.NotNull(detail);
        Assert.Equal(1399, detail.Id);
        Assert.Equal("Game of Thrones", detail.Name);

        // Same shape mistake as release_dates, and the same consequence: every series title page.
        Assert.NotNull(detail.ContentRatings);
        Assert.Equal(5, detail.ContentRatings!.Results.Count);
        Assert.Contains(detail.ContentRatings.Results, r => r.Country == "US" && r.Rating == "TV-MA");
    }

    [Fact]
    public void WatchProvidersStayAMapBecauseTmdbKeysThemByCountry()
    {
        var detail = JsonSerializer.Deserialize<TmdbMovieDetail>(MovieDetailJson, Options);
        var providers = detail!.WatchProviders;

        // The deliberate counterexample to the two above: this one really is an object keyed by
        // region code. Locked down so a future "fix" does not make it consistent and wrong.
        Assert.NotNull(providers);
        Assert.True(providers!.Results.TryGetValue("US", out var us));
        Assert.Equal("fuboTV", us!.Flatrate![0].ProviderName);
        Assert.Equal("Amazon Video", us.Rent![0].ProviderName);
        Assert.Equal("Apple TV", us.Buy![0].ProviderName);

        // A region with no providers in any category is present but empty, not absent.
        Assert.True(providers.Results.TryGetValue("GB", out var gb));
        Assert.Equal("https://www.themoviedb.org/movie/550-fight-club/watch?locale=GB", gb!.Link);
    }

    [Fact]
    public void AppendedSubResourcesAllLand()
    {
        var detail = JsonSerializer.Deserialize<TmdbMovieDetail>(MovieDetailJson, Options);

        Assert.NotNull(detail);
        Assert.Equal("Edward Norton", detail.Credits!.Cast![0].Name);
        Assert.Equal("BdJKm16Co6M", detail.Videos!.Results![0].Key);
        Assert.Equal("en", detail.Images!.Logos![0].Iso6391);
        Assert.Equal(2, detail.Genres!.Count);
    }

    [Fact]
    public void AppendedSubResourcesAllLandForSeries()
    {
        var detail = JsonSerializer.Deserialize<TmdbSeriesDetail>(SeriesDetailJson, Options);

        Assert.NotNull(detail);
        Assert.Equal("Emilia Clarke", detail.AggregateCredits!.Cast![0].Name);
        Assert.Single(detail.Seasons!);
        Assert.Equal("HBO", detail.Network!.Name);
    }
}
