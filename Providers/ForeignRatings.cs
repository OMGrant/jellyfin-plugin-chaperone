using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Chaperone.Providers
{
    /// <summary>
    /// Maps foreign film/TV certifications to their nearest US equivalent. Used only when
    /// <c>MapForeignRatings</c> is on and no preferred-country or US certification exists.
    /// Approximate by nature: each country's bands line up only roughly with the MPA's.
    /// </summary>
    internal static class ForeignRatings
    {
        // country -> (foreign certification -> US movie rating). Keys are compared case-insensitively.
        private static readonly Dictionary<string, Dictionary<string, string>> Movie =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["CA"] = Map(("G", "G"), ("PG", "PG"), ("14A", "PG-13"), ("18A", "R"), ("R", "NC-17")),
                ["GB"] = Map(("U", "G"), ("PG", "PG"), ("12", "PG-13"), ("12A", "PG-13"), ("15", "R"), ("18", "NC-17"), ("R18", "NC-17")),
                ["DE"] = Map(("0", "G"), ("6", "PG"), ("12", "PG-13"), ("16", "R"), ("18", "NC-17")),
                ["JP"] = Map(("G", "G"), ("PG12", "PG-13"), ("R15+", "R"), ("R18+", "NC-17")),
                ["FR"] = Map(("U", "G"), ("TP", "G"), ("10", "PG"), ("12", "PG-13"), ("16", "R"), ("18", "NC-17")),
                ["AU"] = Map(("G", "G"), ("PG", "PG"), ("M", "PG-13"), ("MA15+", "R"), ("R18+", "NC-17")),
                ["NZ"] = Map(("G", "G"), ("PG", "PG"), ("M", "PG-13"), ("R13", "PG-13"), ("R15", "R"), ("R16", "R"), ("R18", "NC-17")),
                ["KR"] = Map(("ALL", "G"), ("12", "PG-13"), ("15", "R"), ("18", "NC-17"), ("Restricted Screening", "NC-17")),
                ["RU"] = Map(("0+", "G"), ("6+", "PG"), ("12+", "PG-13"), ("16+", "R"), ("18+", "NC-17")),
                ["BR"] = Map(("L", "G"), ("10", "PG"), ("12", "PG-13"), ("14", "PG-13"), ("16", "R"), ("18", "NC-17")),
                ["ES"] = Map(("A", "G"), ("APTA", "G"), ("7", "PG"), ("12", "PG-13"), ("16", "R"), ("18", "NC-17")),
                ["IT"] = Map(("T", "G"), ("6+", "PG"), ("14+", "PG-13"), ("18+", "R")),
                ["NL"] = Map(("AL", "G"), ("6", "PG"), ("9", "PG"), ("12", "PG-13"), ("14", "PG-13"), ("16", "R"), ("18", "NC-17")),
                ["SE"] = Map(("Btl", "G"), ("7", "PG"), ("11", "PG-13"), ("15", "R")),
                ["HU"] = Map(("KN", "G"), ("6", "PG"), ("12", "PG-13"), ("16", "R"), ("18", "NC-17")),
                ["MX"] = Map(("AA", "G"), ("A", "G"), ("B", "PG-13"), ("B-15", "R"), ("C", "NC-17"))
            };

        // Order to try countries in when several have a certification: closest systems to the US first.
        private static readonly string[] CountryOrder =
        {
            "CA", "GB", "AU", "NZ", "DE", "FR", "NL", "SE", "JP", "KR", "ES", "IT", "BR", "MX", "HU", "RU"
        };

        /// <summary>
        /// Returns the US equivalent of the most US-like foreign certification in <paramref name="byCountry"/>,
        /// or null when none of them is in the table. For series the movie band is translated to the
        /// US TV scale (G→TV-G, PG→TV-PG, PG-13→TV-14, R/NC-17→TV-MA).
        /// </summary>
        public static string? ToUs(IReadOnlyDictionary<string, string> byCountry, bool forSeries)
        {
            foreach (var country in CountryOrder)
            {
                if (byCountry.TryGetValue(country, out var cert)
                    && Movie.TryGetValue(country, out var table)
                    && table.TryGetValue(cert.Trim(), out var us))
                {
                    return forSeries ? ToTv(us) : us;
                }
            }

            return null;
        }

        private static string ToTv(string movieRating) => movieRating switch
        {
            "G" => "TV-G",
            "PG" => "TV-PG",
            "PG-13" => "TV-14",
            _ => "TV-MA"
        };

        private static Dictionary<string, string> Map(params (string Foreign, string Us)[] pairs)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (foreign, us) in pairs)
            {
                d[foreign] = us;
            }

            return d;
        }
    }
}
