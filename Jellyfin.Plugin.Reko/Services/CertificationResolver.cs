using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Reko.Services.Tmdb;

namespace Jellyfin.Plugin.Reko.Services;

/// <summary>
/// Resolves the certification to show for a title in a given region.
/// </summary>
/// <remarks>
/// Split out of <see cref="CardFactory"/> because it is pure: it reads two TMDB payloads and the
/// configured region, and touches nothing else. The factory that calls it needs a library manager, a
/// user data manager and a set of image URLs, none of which have anything to do with picking a
/// certification — and requiring all of them just to test this is how a two-line rule ends up
/// untested.
/// </remarks>
public static class CertificationResolver
{
    /// <summary>
    /// Resolves the certification for <paramref name="region"/>.
    /// </summary>
    /// <param name="region">The ISO 3166-1 region code, for example <c>US</c>.</param>
    /// <param name="movieReleaseDates">A movie's release dates, or null.</param>
    /// <param name="tvContentRatings">A series' content ratings, or null.</param>
    /// <returns>
    /// The certification, or null when the region is not listed or every entry for it is blank.
    /// </returns>
    public static string? Resolve(
        string region,
        TmdbReleaseDates? movieReleaseDates,
        TmdbContentRatings? tvContentRatings)
    {
        if (movieReleaseDates is not null)
        {
            var group = movieReleaseDates.Results.FirstOrDefault(
                r => string.Equals(r.Country, region, StringComparison.OrdinalIgnoreCase));

            if (group is not null)
            {
                // TMDB lists every release of a film in a country, and the earliest ones are
                // routinely uncertificated festival premieres. Taking the first entry rather than
                // the first *certificated* one shows no rating at all for most films, which reads as
                // "unrated" rather than "not found".
                var certification = group.ReleaseDates
                    .Select(r => r.Certification)
                    .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

                if (!string.IsNullOrWhiteSpace(certification))
                {
                    return certification.Trim();
                }
            }
        }

        if (tvContentRatings is not null)
        {
            var rating = tvContentRatings.Results
                .FirstOrDefault(r => string.Equals(r.Country, region, StringComparison.OrdinalIgnoreCase))
                ?.Rating;

            if (!string.IsNullOrWhiteSpace(rating))
            {
                return rating.Trim();
            }
        }

        return null;
    }
}
