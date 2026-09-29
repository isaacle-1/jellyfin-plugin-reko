using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Reko.Services.Seerr;
using Xunit;

namespace Jellyfin.Plugin.Reko.Tests;

/// <summary>
/// Pins the shape of the body sent to <c>POST /api/v1/request</c>.
/// </summary>
/// <remarks>
/// <para>
/// Overseerr and Jellyseerr validate their own request bodies with <c>express-openapi-validator</c>
/// and <c>validateRequests: true</c>. The schema declares <c>seasons</c> as a <c>oneOf</c> of an
/// array, a string and the literal <c>"all"</c> — and nothing else. A JSON <c>null</c> matches none
/// of the three, so a movie request that carries the property at all is rejected with a 400 before it
/// reaches the route.
/// </para>
/// <para>
/// That is not a hypothetical: the property is nullable, so the one thing the type did not say was
/// "omit me", and every movie request came back
/// <c>request/body/seasons must be array, ... must match exactly one schema in oneOf</c>. The fix is
/// an attribute on the property, which is exactly the kind of thing that gets lost in a refactor, so
/// it is asserted here.
/// </para>
/// </remarks>
public class SeerrRequestBodyTests
{
    /// <summary>
    /// The serializer options the Seerr client uses.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// A movie request must not mention seasons at all.
    /// </summary>
    [Fact]
    public void MovieRequestOmitsSeasonsEntirely()
    {
        var body = new SeerrRequestBody { MediaType = "movie", MediaId = 550, Seasons = null };

        var json = JsonSerializer.Serialize(body, Options);

        Assert.DoesNotContain("seasons", json, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A series request must send the seasons that were chosen.
    /// </summary>
    [Fact]
    public void SeriesRequestSendsTheChosenSeasons()
    {
        var body = new SeerrRequestBody
        {
            MediaType = "tv",
            MediaId = 1399,
            Seasons = new List<int> { 1, 2 }
        };

        var json = JsonSerializer.Serialize(body, Options);

        Assert.Contains("\"seasons\":[1,2]", json, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The rest of the body must survive: the validator rejects unknown properties just as firmly
    /// as it rejects a null, so a rename here is as breaking as the bug above.
    /// </summary>
    [Fact]
    public void TheDocumentedPropertiesAreAllPresent()
    {
        var json = JsonSerializer.Serialize(
            new SeerrRequestBody { MediaType = "tv", MediaId = 1399, Seasons = new List<int> { 1 } },
            Options);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("tv", root.GetProperty("mediaType").GetString());
        Assert.Equal(1399, root.GetProperty("mediaId").GetInt32());
        Assert.False(root.GetProperty("is4k").GetBoolean());
        Assert.Equal(1, root.GetProperty("seasons")[0].GetInt32());
    }
}
