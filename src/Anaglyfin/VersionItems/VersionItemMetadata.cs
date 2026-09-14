using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace Anaglyfin.VersionItems;

/// <summary>
/// What one item's library metadata says, in the form another item can be written with.
/// </summary>
/// <remarks>
/// <para>
/// A profile version is the same movie under a different picture, and a details panel that knows
/// it is a version and nothing else reads as a bug: no poster, no synopsis, no cast, no rating.
/// This type is the answer to "what must this version say to look like the movie it converts", read
/// off the item that owns the file the version converts. It is a snapshot, taken once per version
/// per pass, and it answers the two questions the reconciliation asks with the same list of fields:
/// <see cref="ApplyTo"/> writes them onto an item, <see cref="FieldsMatch"/> and
/// <see cref="ImagesMatch"/> say whether an item already says them.
/// </para>
/// <para>
/// <b>Everything here is copied, never re-derived.</b> Nothing in this type scrapes, probes, reads
/// a file or fetches a URL: the values are the ones the source item already carries in the library,
/// and the copies are handed to the persistence APIs that own them. A version has no file of its
/// own - its path is a marker URL - so it has no metadata source but the item it was built from, and
/// there is nothing for a provider to run against it.
/// </para>
/// <para>
/// <b>Cloned, not shared.</b> Arrays and dictionaries are copied rather than handed over, so the
/// version and the item it was built from do not share one <c>Genres</c> array that a later refresh
/// of either would edit for both of them.
/// </para>
/// <para>
/// <b>What is deliberately not here.</b> The item's name (a version's name is its profile label,
/// which is also the label the stock version picker shows), its file facts (the version converts a
/// file, it is not that file), its dates of its own making (<c>DateCreated</c>, when this item came
/// to exist), and the file-derived halves of an image row. An image entry is copied whole - same
/// file, same size, same blurhash, same modified time - but a version's image rows are compared on
/// the set of images they name, in no particular order, and not on those derived numbers: the server
/// recomputes them from the file whenever it touches an item, and a copy that insisted on its own
/// stale copy of a number the server had just refreshed would be rewritten on every pass forever.
/// </para>
/// </remarks>
public sealed class VersionItemMetadata
{
    /// <summary>
    /// The character an image identity is joined with. No file path and no spelled-out number
    /// carries a unit separator, so a joined identity can never spell another row's identity.
    /// </summary>
    private const char IdentitySeparator = '\u001F';

    /// <summary>
    /// The identity a hole in an image list answers with. A real row's identity always begins with
    /// the decimal form of its (non-negative) image type, so nothing real collides with this.
    /// </summary>
    private const string MissingImageIdentity = "\u001F";

    private readonly string? _overview;

    private readonly string? _originalTitle;

    private readonly string? _sortName;

    private readonly string? _forcedSortName;

    private readonly string? _tagline;

    private readonly string? _officialRating;

    private readonly string? _customRating;

    private readonly string? _homePageUrl;

    private readonly float? _communityRating;

    private readonly float? _criticRating;

    private readonly int? _productionYear;

    private readonly DateTime? _premiereDate;

    private readonly DateTime? _endDate;

    private readonly string[] _genres;

    private readonly string[] _tags;

    private readonly string[] _studios;

    private readonly string[] _productionLocations;

    private readonly Dictionary<string, string> _providerIds;

    private readonly ItemImageInfo[] _images;

    private VersionItemMetadata(
        string? overview,
        string? originalTitle,
        string? sortName,
        string? forcedSortName,
        string? tagline,
        string? officialRating,
        string? customRating,
        string? homePageUrl,
        float? communityRating,
        float? criticRating,
        int? productionYear,
        DateTime? premiereDate,
        DateTime? endDate,
        string[] genres,
        string[] tags,
        string[] studios,
        string[] productionLocations,
        Dictionary<string, string> providerIds,
        ItemImageInfo[] images)
    {
        _overview = overview;
        _originalTitle = originalTitle;
        _sortName = sortName;
        _forcedSortName = forcedSortName;
        _tagline = tagline;
        _officialRating = officialRating;
        _customRating = customRating;
        _homePageUrl = homePageUrl;
        _communityRating = communityRating;
        _criticRating = criticRating;
        _productionYear = productionYear;
        _premiereDate = premiereDate;
        _endDate = endDate;
        _genres = genres;
        _tags = tags;
        _studios = studios;
        _productionLocations = productionLocations;
        _providerIds = providerIds;
        _images = images;
    }

    /// <summary>
    /// Reads what one item's metadata says.
    /// </summary>
    /// <param name="source">The item to read: the one whose file a version converts.</param>
    /// <returns>A snapshot of that item's metadata, with every list its own copy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <c>null</c>.</exception>
    /// <remarks>
    /// The one place that decides which of an item's fields a version carries. Two things are worth
    /// naming: the title travels as <c>OriginalTitle</c> because a version's own name is its profile
    /// label and the panel needs the movie's name from somewhere, and the sort name is read through
    /// the source's own getter, which is where the server keeps whatever the item sorts under.
    /// </remarks>
    public static VersionItemMetadata FromSource(BaseItem source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new VersionItemMetadata(
            source.Overview,
            FirstNonBlank(source.OriginalTitle, source.Name),
            source.SortName,
            source.ForcedSortName,
            source.Tagline,
            source.OfficialRating,
            source.CustomRating,
            source.HomePageUrl,
            source.CommunityRating,
            source.CriticRating,
            source.ProductionYear,
            source.PremiereDate,
            source.EndDate,
            Clone(source.Genres),
            Clone(source.Tags),
            Clone(source.Studios),
            Clone(source.ProductionLocations),
            CloneProviderIds(source.ProviderIds),
            CloneImages(source.ImageInfos));
    }

    /// <summary>
    /// Writes the snapshot onto an item.
    /// </summary>
    /// <param name="target">The item to bring round to what the source says.</param>
    /// <remarks>
    /// Every field is written, including the ones that already match: this is the answer to "make
    /// this item say what the source says", and a caller that wanted to know whether anything would
    /// change asks <see cref="FieldsMatch"/> first. The name is not written here - see the type
    /// remarks - and neither is anything about the item's file.
    /// </remarks>
    public void ApplyTo(BaseItem target)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.Overview = _overview;
        target.OriginalTitle = _originalTitle;

        // ForcedSortName first, and SortName after it: setting the forced name drops the cached sort
        // name the item was answering with, so a SortName written first would be thrown away and the
        // item would go back to sorting under its own label.
        target.ForcedSortName = _forcedSortName;
        target.SortName = _sortName;

        target.Tagline = _tagline;
        target.OfficialRating = _officialRating;
        target.CustomRating = _customRating;
        target.HomePageUrl = _homePageUrl;
        target.CommunityRating = _communityRating;
        target.CriticRating = _criticRating;
        target.ProductionYear = _productionYear;
        target.PremiereDate = _premiereDate;
        target.EndDate = _endDate;

        target.Genres = (string[])_genres.Clone();
        target.Tags = (string[])_tags.Clone();
        target.Studios = (string[])_studios.Clone();
        target.ProductionLocations = (string[])_productionLocations.Clone();

        target.ProviderIds = new Dictionary<string, string>(_providerIds, StringComparer.OrdinalIgnoreCase);

        target.ImageInfos = CloneImages(_images);
    }

    /// <summary>
    /// Whether an item already says what the source says.
    /// </summary>
    /// <param name="current">The item to read.</param>
    /// <returns><c>true</c> when no field would change.</returns>
    /// <remarks>
    /// This is the same list as <see cref="ApplyTo"/>'s, read instead of written, and it has to stay
    /// level with it: a field written but not compared is a field rewritten on every pass, and a
    /// field compared but not written is a drift nobody repairs.
    /// </remarks>
    public bool FieldsMatch(BaseItem current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return TextEquals(current.Overview, _overview)
               && TextEquals(current.OriginalTitle, _originalTitle)
               && TextEquals(current.SortName, _sortName)
               && TextEquals(current.ForcedSortName, _forcedSortName)
               && TextEquals(current.Tagline, _tagline)
               && TextEquals(current.OfficialRating, _officialRating)
               && TextEquals(current.CustomRating, _customRating)
               && TextEquals(current.HomePageUrl, _homePageUrl)
               && NullableEquals(current.CommunityRating, _communityRating)
               && NullableEquals(current.CriticRating, _criticRating)
               && NullableEquals(current.ProductionYear, _productionYear)
               && NullableEquals(current.PremiereDate, _premiereDate)
               && NullableEquals(current.EndDate, _endDate)
               && SameList(current.Genres, _genres)
               && SameList(current.Tags, _tags)
               && SameList(current.Studios, _studios)
               && SameList(current.ProductionLocations, _productionLocations)
               && SameProviderIds(current.ProviderIds, _providerIds);
    }

    /// <summary>
    /// Whether an item already names the same images, whatever order it names them in.
    /// </summary>
    /// <param name="current">The item to read.</param>
    /// <returns><c>true</c> when the item's image list names the snapshot's set of images.</returns>
    /// <remarks>
    /// <para>
    /// Compared on what the list points at rather than on everything the row records: width, height,
    /// blurhash and modified time travel with the copy but belong to the file, and the server
    /// recomputes them from it. Insisting on the copied numbers would make every pass find a
    /// difference the server had just corrected, and rewrite the same rows forever.
    /// </para>
    /// <para>
    /// <b>A set, not a sequence.</b> Image rows are keyed by what they name, not by a position - the
    /// item model carries no list index on the row, and the database hands them back in whatever
    /// order its query happens to produce. A comparison that walked the two lists index by index
    /// would therefore call the same set of images different on every read the engine chose a new
    /// order for, and rewrite the item on every pass forever. What is compared is the multiset of
    /// what the rows name - kind of image and file - sorted into a canonical order first, which is
    /// deterministic whatever order either list arrived in. Two rows naming the same kind of image
    /// at the same file are indistinguishable to the multiset (that is what makes the answer
    /// order-independent), and a row that is missing, added, or pointed at another file still
    /// changes the multiset and is repaired.
    /// </para>
    /// </remarks>
    public bool ImagesMatch(BaseItem current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var have = current.ImageInfos ?? Array.Empty<ItemImageInfo>();

        if (have.Length != _images.Length)
        {
            return false;
        }

        return SortedImageIdentities(have).SequenceEqual(SortedImageIdentities(_images), StringComparer.Ordinal);
    }

    /// <summary>
    /// The one text a field should carry, out of the candidates in order of who is asked first.
    /// </summary>
    private static string? FirstNonBlank(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string[] Clone(string[]? values)
        => values is null || values.Length == 0 ? Array.Empty<string>() : (string[])values.Clone();

    private static Dictionary<string, string> CloneProviderIds(IDictionary<string, string>? values)
    {
        // The same comparer the item model uses for provider ids, so a copy answers the way the
        // original does - a provider id is keyed by a name whose casing belongs to the provider.
        var clone = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (values is null)
        {
            return clone;
        }

        foreach (var pair in values)
        {
            clone[pair.Key] = pair.Value;
        }

        return clone;
    }

    private static ItemImageInfo[] CloneImages(ItemImageInfo[]? images)
    {
        if (images is null || images.Length == 0)
        {
            return Array.Empty<ItemImageInfo>();
        }

        var clones = new List<ItemImageInfo>(images.Length);

        foreach (var image in images)
        {
            // A hole in the list is carried as no image at all rather than as a hole in the copy:
            // an image row with nothing in it names no file, and an item whose image list holds one
            // is an item the server is already going to drop it from.
            if (image is null)
            {
                continue;
            }

            clones.Add(new ItemImageInfo
            {
                Path = image.Path,
                Type = image.Type,
                DateModified = image.DateModified,
                Width = image.Width,
                Height = image.Height,
                BlurHash = image.BlurHash
            });
        }

        return clones.ToArray();
    }

    /// <summary>
    /// The identities of a list's images, sorted: the canonical form of the multiset, which two
    /// lists naming the same images answer with whatever order they hold them in.
    /// </summary>
    /// <remarks>
    /// An ordinal sort over a key that begins with the numeric image type is a total, deterministic
    /// order, so no tiebreak on the file-derived numbers is needed to make the comparison
    /// reproducible - and those numbers must stay out of the identity anyway, because the server
    /// recomputes them from the file and a copy cannot insist on its own stale copy of them.
    /// </remarks>
    private static List<string> SortedImageIdentities(ItemImageInfo[] images)
    {
        var identities = new List<string>(images.Length);

        foreach (var image in images)
        {
            identities.Add(image is null ? MissingImageIdentity : ImageIdentity(image));
        }

        identities.Sort(StringComparer.Ordinal);

        return identities;
    }

    /// <summary>
    /// What one image row names: the kind of image and the file it points at - the whole of what
    /// the row is written to say, and none of what the server derives from the file.
    /// </summary>
    private static string ImageIdentity(ItemImageInfo image)
        => string.Concat(
            ((int)image.Type).ToString(CultureInfo.InvariantCulture),
            IdentitySeparator.ToString(),
            image.Path ?? string.Empty);

    private static bool SameProviderIds(IDictionary<string, string>? current, Dictionary<string, string> wanted)
    {
        var have = current ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // An entry somebody emptied is the same answer as no entry: the field is blank either way,
        // and a source item read back from the database can hold either shape of that answer.
        var wantedEntries = wanted.Where(pair => !string.IsNullOrEmpty(pair.Value)).ToArray();
        var currentEntries = have.Where(pair => !string.IsNullOrEmpty(pair.Value)).ToArray();

        if (wantedEntries.Length != currentEntries.Length)
        {
            return false;
        }

        foreach (var pair in wantedEntries)
        {
            if (!have.TryGetValue(pair.Key, out var value) || !string.Equals(value, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameList(string[]? current, string[] wanted)
        => (current ?? Array.Empty<string>()).SequenceEqual(wanted, StringComparer.Ordinal);

    private static bool TextEquals(string? current, string? wanted)
        => string.Equals(current ?? string.Empty, wanted ?? string.Empty, StringComparison.Ordinal);

    private static bool NullableEquals<T>(T? current, T? wanted)
        where T : struct
        => Nullable.Equals(current, wanted);
}

/// <summary>
/// The credits of one item, in the form another item can be credited with.
/// </summary>
/// <remarks>
/// <para>
/// Credits are the other half of an item's metadata that is not on the item: they live in their own
/// tables, keyed by item id, so the cast and crew of the file a version converts have to be asked
/// for and written under the version's id. This type does both halves of that - the copy to write
/// and the comparison that notices whether the version's credits are the ones they should be - and
/// nothing else: the reads and the write belong to <see cref="IProfileVersionItemStore"/>.
/// </para>
/// <para>
/// A copy keeps the person it credits, which is the row the source credits, so a version points at
/// the same person entity rather than starting a second one with the same name; what it changes is
/// the item the credit hangs off.
/// </para>
/// </remarks>
public static class VersionItemCredits
{
    /// <summary>
    /// The character a credit identity is joined with. Names and roles arrive trimmed and the
    /// numeric fields are spelled in the invariant culture, so no stored value carries a unit
    /// separator and no combination of fields can spell another credit's identity.
    /// </summary>
    private const char IdentitySeparator = '\u001F';

    /// <summary>
    /// The identity a hole in a credit list answers with. A real credit's identity always begins
    /// with the decimal form of its (non-negative) kind, so nothing real collides with this.
    /// </summary>
    private const string MissingCreditIdentity = "\u001F";

    /// <summary>
    /// Copies one item's credits onto another item.
    /// </summary>
    /// <param name="source">The credits as the source item carries them.</param>
    /// <param name="targetItemId">The item to credit with them.</param>
    /// <returns>The credits to write, in the source's own order.</returns>
    /// <remarks>
    /// Names and roles arrive trimmed because the credit write trims them before it stores them: a
    /// copy that kept the padding would never match what comes back, and a comparison that can never
    /// match is a row rewritten on every pass. A credit whose name is blank is dropped rather than
    /// written, because there is no person to credit and the write refuses one.
    /// </remarks>
    public static IReadOnlyList<PersonInfo> Clone(IReadOnlyList<PersonInfo>? source, Guid targetItemId)
    {
        if (source is null || source.Count == 0)
        {
            return Array.Empty<PersonInfo>();
        }

        var clones = new List<PersonInfo>(source.Count);

        foreach (var person in source)
        {
            if (person is null || string.IsNullOrWhiteSpace(person.Name))
            {
                continue;
            }

            clones.Add(new PersonInfo
            {
                // The person travels with the credit: same person, same row, so one Halle Berry.
                Id = person.Id,
                ItemId = targetItemId,
                Name = person.Name.Trim(),
                Role = person.Role?.Trim() ?? string.Empty,
                Type = person.Type,
                SortOrder = person.SortOrder,
                ImageUrl = person.ImageUrl,
                ProviderIds = new Dictionary<string, string>(person.ProviderIds ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
            });
        }

        return clones;
    }

    /// <summary>
    /// Whether an item is already credited with exactly these credits, in any read order.
    /// </summary>
    /// <param name="current">The credits the item carries.</param>
    /// <param name="wanted">The credits it should carry.</param>
    /// <returns><c>true</c> when nothing would change.</returns>
    /// <remarks>
    /// <para>
    /// Compared on the four values a credit is stored with - who, in what role, of what kind, and
    /// where in the item's list - because those are what a client is shown and what the credit write
    /// puts down. An image or a provider id on a person belongs to the person and not to this
    /// item's credit, and the credit write does not store either, so comparing them would find a
    /// difference no write could settle.
    /// </para>
    /// <para>
    /// <b>A multiset, not a sequence.</b> The order a credit sits in is carried by the credit itself
    /// - <c>SortOrder</c> is one of the four values - and not by the order the people repository
    /// happens to hand the rows back in; that repository owes nobody a row order. Compared index by
    /// index, one read in a new order would look like every credit moving at once, and the item
    /// would be rewritten on every pass forever. So each side is reduced to a sorted canonical form of its
    /// deterministic identities (kind, name, role and sort order joined with a separator none of the
    /// text fields can carry) and the two forms are compared elementwise: same rows in any read
    /// order answers <c>true</c>, and a credit that is missing, added, renamed, re-roled or
    /// re-ordered still changes the form and is repaired.
    /// </para>
    /// </remarks>
    public static bool Matches(IReadOnlyList<PersonInfo>? current, IReadOnlyList<PersonInfo>? wanted)
    {
        var have = current ?? Array.Empty<PersonInfo>();
        var want = wanted ?? Array.Empty<PersonInfo>();

        if (have.Count != want.Count)
        {
            return false;
        }

        return SortedCreditIdentities(have).SequenceEqual(SortedCreditIdentities(want), StringComparer.Ordinal);
    }

    /// <summary>
    /// The identities of a credit list, sorted: the canonical form of the multiset, which two lists
    /// crediting the same people answer with whatever order they were read back in.
    /// </summary>
    private static List<string> SortedCreditIdentities(IReadOnlyList<PersonInfo> people)
    {
        var identities = new List<string>(people.Count);

        foreach (var person in people)
        {
            // A hole in the list answers with a text no real credit can produce - the identity of a
            // real one always begins with the decimal form of its (non-negative) kind - so a hole
            // matches only another hole and never quietly matches a person.
            identities.Add(person is null ? MissingCreditIdentity : CreditIdentity(person));
        }

        identities.Sort(StringComparer.Ordinal);

        return identities;
    }

    /// <summary>
    /// What one credit row says, in the form its stored values answer with: the text fields exactly
    /// as the credit write stores them (it trims, and a blank travels as no text), so a padded
    /// source credit and the trimmed row it produces stay the distinguishable pair they are.
    /// </summary>
    private static string CreditIdentity(PersonInfo person)
        => string.Join(
            IdentitySeparator,
            ((int)person.Type).ToString(CultureInfo.InvariantCulture),
            person.Name ?? string.Empty,
            person.Role ?? string.Empty,
            person.SortOrder?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
}
