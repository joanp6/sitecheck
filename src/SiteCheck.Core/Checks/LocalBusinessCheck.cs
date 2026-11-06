using System.Text.Json;
using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reports whether the page tells search engines, in their own format, that this is a local
/// business, where it is and when it opens.
/// </summary>
/// <remarks>
/// schema.org's <c>LocalBusiness</c> (or one of its kinds, such as <c>HairSalon</c> or
/// <c>HobbyShop</c>) is how a page says "this is a shop at this address, open these hours" in a
/// form Google reads without guessing. It feeds the address and opening hours Google can show for
/// a search such as "hair salon open now". It is read from JSON-LD
/// (<c>&lt;script type="application/ld+json"&gt;</c>, what most site builders and SEO plugins
/// write) and, more loosely, from microdata (<c>itemtype</c> and <c>itemprop</c> attributes).
/// <para>
/// Never more than a <see cref="CheckStatus.Warn"/>: visitors are not affected at all, and Google
/// can still learn the same facts from a Google Business Profile. A JSON-LD block that does not
/// parse is a warning too, but named as such: Google ignores it whole, so whatever the owner paid
/// someone to add does nothing.
/// </para>
/// </remarks>
public sealed partial class LocalBusinessCheck : ISiteCheck
{
    private const int MaxDepth = 16;

    // LocalBusiness and the kinds of it a small shop or salon is likely to declare. A type outside
    // this list is still found when the block also says LocalBusiness, as builders often do.
    private static readonly HashSet<string> BusinessTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "LocalBusiness",
        "HealthAndBeautyBusiness", "HairSalon", "BeautySalon", "NailSalon", "DaySpa", "TattooParlor", "HealthClub",
        "Store", "HobbyShop", "ToyStore", "BookStore", "ClothingStore", "ShoeStore", "JewelryStore", "GiftShop",
        "ElectronicsStore", "ComputerStore", "MobilePhoneStore", "Florist", "GroceryStore", "ConvenienceStore",
        "PetStore", "SportingGoodsStore", "MusicStore", "OutletStore", "SecondHandStore",
        "FoodEstablishment", "Restaurant", "CafeOrCoffeeShop", "Bakery", "BarOrPub", "FastFoodRestaurant", "IceCreamShop",
        "ProfessionalService", "AutoRepair", "AutomotiveBusiness", "DryCleaningOrLaundry", "EntertainmentBusiness",
        "SportsActivityLocation", "LodgingBusiness", "MedicalBusiness", "Dentist", "Optician", "Pharmacy",
        "HomeAndConstructionBusiness", "Electrician", "Plumber", "RealEstateAgent", "TravelAgency", "ChildCare",
    };

    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    private readonly HttpClient _httpClient;

    public LocalBusinessCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public string Name => "local-business";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (page, unread) = await PageReader
            .ReadAsync(_httpClient, url, "read the page's business details", cancellationToken)
            .ConfigureAwait(false);

        if (page is null)
        {
            return unread!;
        }

        try
        {
            return Evaluate(page.Html);
        }
        catch (RegexMatchTimeoutException)
        {
            return CheckOutcome.Error("The page's HTML is too tangled to read in reasonable time.");
        }
    }

    private sealed record Business(string Type, bool HasAddress, bool HasHours);

    private static CheckOutcome Evaluate(string html)
    {
        var visible = Comment().Replace(html, " ");
        var businesses = new List<Business>();
        var unreadable = 0;

        foreach (Match block in JsonLd().Matches(visible))
        {
            try
            {
                using var document = JsonDocument.Parse(block.Groups["json"].Value, Lenient);
                Collect(document.RootElement, businesses, depth: 0);
            }
            catch (JsonException)
            {
                unreadable++;
            }
        }

        businesses.AddRange(Microdata(visible));

        var broken = unreadable == 0
            ? string.Empty
            : $" {(unreadable == 1 ? "One block" : $"{unreadable} blocks")} of structured data on the page could not be read (the JSON is malformed), and Google ignores such a block whole.";

        if (businesses.Count == 0)
        {
            return CheckOutcome.Warn(
                "The page does not tell Google it is a local business (schema.org LocalBusiness, or a kind of it such as HairSalon), so its address and opening hours have to be guessed." + broken);
        }

        // Several declarations, such as one per shop, pass if any one of them is complete.
        var best = businesses.OrderByDescending(b => (b.HasAddress ? 1 : 0) + (b.HasHours ? 1 : 0)).First();

        var missing = (best.HasAddress, best.HasHours) switch
        {
            (true, true) => null,
            (false, true) => "the address",
            (true, false) => "the opening hours",
            _ => "the address or the opening hours",
        };

        if (missing is null)
        {
            return unreadable == 0
                ? CheckOutcome.Pass($"The page tells Google it is a {best.Type}, with its address and opening hours.")
                : CheckOutcome.Warn($"The page tells Google it is a {best.Type}, with its address and opening hours." + broken);
        }

        return CheckOutcome.Warn(
            $"The page tells Google it is a {best.Type}, but not {missing}, so Google cannot show them from the page itself. Add them to the same declaration." + broken);
    }

    private static void Collect(JsonElement element, List<Business> businesses, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, businesses, depth + 1);
                }

                break;

            case JsonValueKind.Object:
                if (BusinessType(element) is { } type)
                {
                    businesses.Add(new Business(type, HasAddress(element), HasHours(element)));
                }

                // A business can sit inside @graph, or as the publisher or location of another thing.
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        Collect(property.Value, businesses, depth + 1);
                    }
                }

                break;
        }
    }

    /// <summary>The most specific business type the object declares, or <see langword="null"/>.</summary>
    private static string? BusinessType(JsonElement element)
    {
        if (!element.TryGetProperty("@type", out var declared))
        {
            return null;
        }

        string[] types = declared.ValueKind switch
        {
            JsonValueKind.String => [declared.GetString()!],
            JsonValueKind.Array => declared.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToArray(),
            _ => Array.Empty<string>(),
        };

        var names = types.Select(ShortName).ToArray();
        var known = names.Where(BusinessTypes.Contains).ToArray();

        if (known.Length == 0)
        {
            return null;
        }

        // ["LocalBusiness", "HairSalon"] reads best as the salon; an unlisted kind next to
        // LocalBusiness, such as "CardShop", is still the owner's own word for it.
        return known.FirstOrDefault(name => !name.Equals("LocalBusiness", StringComparison.OrdinalIgnoreCase))
               ?? names.FirstOrDefault(name => !name.Equals("LocalBusiness", StringComparison.OrdinalIgnoreCase))
               ?? "LocalBusiness";
    }

    // "https://schema.org/HairSalon" and "schema:HairSalon" are the same type as "HairSalon".
    private static string ShortName(string type)
    {
        var trimmed = type.Trim();
        var cut = trimmed.LastIndexOfAny(['/', ':', '#']);

        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }

    private static bool HasAddress(JsonElement business) =>
        business.TryGetProperty("address", out var address) && address.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrWhiteSpace(address.GetString()),
            JsonValueKind.Object => HasText(address, "streetAddress") || HasText(address, "addressLocality"),
            JsonValueKind.Array => address.EnumerateArray().Any(a =>
                (a.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(a.GetString()))
                || (a.ValueKind == JsonValueKind.Object && (HasText(a, "streetAddress") || HasText(a, "addressLocality")))),
            _ => false,
        };

    private static bool HasHours(JsonElement business) =>
        (business.TryGetProperty("openingHoursSpecification", out var specification)
         && specification.ValueKind switch
         {
             JsonValueKind.Object => true,
             JsonValueKind.Array => specification.GetArrayLength() > 0,
             _ => false,
         })
        || (business.TryGetProperty("openingHours", out var hours)
            && hours.ValueKind switch
            {
                JsonValueKind.String => !string.IsNullOrWhiteSpace(hours.GetString()),
                JsonValueKind.Array => hours.EnumerateArray().Any(h => h.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(h.GetString())),
                _ => false,
            });

    private static bool HasText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());

    /// <summary>
    /// Businesses declared as microdata. Read loosely: an <c>itemprop</c> anywhere on the page
    /// counts, since telling which item each property belongs to needs a real HTML parser.
    /// </summary>
    private static IEnumerable<Business> Microdata(string html)
    {
        var type = HtmlTags.All(html)
            .Select(tag => tag.GetValueOrDefault("itemtype"))
            .OfType<string>()
            .SelectMany(value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(ShortName)
            .FirstOrDefault(BusinessTypes.Contains);

        if (type is null)
        {
            yield break;
        }

        var properties = HtmlTags.All(html)
            .Select(tag => tag.GetValueOrDefault("itemprop"))
            .OfType<string>()
            .SelectMany(value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        yield return new Business(
            type,
            properties.Contains("address") || properties.Contains("streetAddress"),
            properties.Contains("openingHours") || properties.Contains("openingHoursSpecification"));
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Comment();

    [GeneratedRegex(
        """<script\b[^>]*\btype\s*=\s*["']?application/ld\+json["']?[^>]*>(?<json>.*?)</script\s*>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex JsonLd();
}
