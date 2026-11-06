using Microsoft.EntityFrameworkCore.Storage;
using KiComLibBackend.Services.Reviews;
using KiComLibBackend.Data;
using KiComLibBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace KiComLibBackend.Services
{
    /// <summary>
    /// Builds the KiCad HTTP-library fields for a catalog part.
    /// The same values are what a schematic field is compared against for field_outdated.
    /// </summary>
    public class KiCadFieldBuilder
    {
        private readonly ApplicationDbContext _context;
        private readonly ReviewService _reviews;
        private readonly ReviewDatabase _reviewDb;

        public KiCadFieldBuilder(ApplicationDbContext context, ReviewService reviews, ReviewDatabase reviewDb)
        {
            _context = context;
            _reviews = reviews;
            _reviewDb = reviewDb;
        }

        public async Task<KiCadHttpPart> BuildAsync(Part part, CancellationToken cancellationToken = default)
        {
            return await BuildForPartIdAsync(part.Id!.Value, cancellationToken)
                ?? throw new ReviewException(404, "Part no longer exists");
        }

        private async Task<KiCadHttpPart> BuildCoreAsync(Part part, CancellationToken cancellationToken)
        {
            var result = new KiCadHttpPart();

            result.Id = part.Id.ToString();
            result.Name = part.Name;

            result.symbolIdStr = "";
            if (part.Symbol != null)
            {
                LibraryFile? symLibFile = await _context.LibraryFile.AsNoTracking().FirstOrDefaultAsync(l => l.Id == part.Symbol.LibraryFileId, cancellationToken);
                var symLib = symLibFile != null ? symLibFile.Filename : "";
                var symName = part.Symbol != null ? part.Symbol.Name : "";
                if (symLib != "" && symName != "")
                    result.symbolIdStr = symLib + ":" + symName;
            }

            if (part.Footprint?.HeadRevision != null)
                result.ExcludeFromBom = part.Footprint.HeadRevision.ExcludeFromBom ? "True" : "False";
            else
                result.ExcludeFromBom = "False";

            result.ExcludeFromBoard = "False";
            result.ExcludeFromSim = "True";

            var footprintStr = "";
            if (part.Footprint != null)
            {
                LibraryFile? footLibFile = await _context.LibraryFile.AsNoTracking().FirstOrDefaultAsync(l => l.Id == part.Footprint.LibraryFileId, cancellationToken);
                var footLib = footLibFile != null ? footLibFile.Filename : "";
                var footName = part.Footprint != null ? part.Footprint.Name : "";
                if (footLib != "" && footName != "")
                    footprintStr = footLib + ":" + footName;
            }

            var referenceStr = "";
            if (part.Symbol?.HeadRevision != null)
                referenceStr = part.Symbol.HeadRevision.Reference ?? "";

            var datasheetStr = "";
            if (part.Datasheet != null)
                datasheetStr = part.Datasheet;

            var manufacturerStr = "";
            var mpnStr = "";
            var distributorStr = "";
            var dpnStr = "";
            List<PartNumber> partNumbers = await _context.PartNumber.AsNoTracking()
                .Where(p => p.PartId == part.Id)
                .OrderBy(p => p.Id)
                .Take(2)
                .ToListAsync(cancellationToken);
            PartNumber? partNumber = partNumbers.FirstOrDefault();
            if (partNumber != null)
            {
                mpnStr = partNumber.ManufacturerPartNumber;
                Manufacturer? manufacturer = await _context.Manufacturer.AsNoTracking().FirstOrDefaultAsync(m => m.Id == partNumber.ManufacturerId);
                manufacturerStr = manufacturer != null ? manufacturer.Name : "";

                OrderNumber? orderNumber = await _context.OrderNumber.AsNoTracking().OrderBy(p => p.Id).FirstOrDefaultAsync(p => p.PartNumberId == partNumber.Id);
                if (orderNumber != null)
                {
                    dpnStr = orderNumber.OrderNumberValue;
                    Distributor? distributor = await _context.Distributor.AsNoTracking().FirstOrDefaultAsync(d => d.Id == orderNumber.DistributorId);
                    distributorStr = distributor != null ? distributor.Name : "";
                }
            }

            (string alternativeManufacturer, string alternativeMpn) = await AlternativeSourceAsync(part, partNumbers, cancellationToken);

            result.Description = part.Comment != null ? part.Comment : "";
            result.Keywords = part.Keywords != null ? part.Keywords : "";

            result.Fields = new Dictionary<string, FieldDetail>
            {
                ["footprint"] = new FieldDetail { Value = footprintStr, Visible = "False" },
                ["datasheet"] = new FieldDetail { Value = datasheetStr, Visible = "False" },
                ["value"] = new FieldDetail { Value = part.Value },
                ["reference"] = new FieldDetail { Value = referenceStr },
                ["description"] = new FieldDetail { Value = part.Comment, Visible = "False" },
                ["keywords"] = new FieldDetail { Value = part.Keywords != null ? part.Keywords : "", Visible = "False" },
                ["Manufacturer"] = new FieldDetail { Value = manufacturerStr, Visible = "False" },
                ["MPN"] = new FieldDetail { Value = mpnStr, Visible = "False" },
                ["Distributor"] = new FieldDetail { Value = distributorStr, Visible = "False" },
                ["DPN"] = new FieldDetail { Value = dpnStr, Visible = "False" },
                ["AlternativeManufacturer"] = new FieldDetail { Value = alternativeManufacturer, Visible = "False" },
                ["AlternativeMPN"] = new FieldDetail { Value = alternativeMpn, Visible = "False" },
                ["KiComLibUUID"] = new FieldDetail { Value = part.Uuid?.ToString(), Visible = "False" },
            };

            var review = await _reviews.Current(part.Uuid!.Value, cancellationToken);
            result.Fields["KiComLibRevision"] = new FieldDetail { Value = review["definitionDigest"]!.GetValue<string>(), Visible = "False" };
            result.Fields["KiComLibReviewState"] = new FieldDetail { Value = review["state"]!.GetValue<string>(), Visible = "False" };
            return result;
        }

        public async Task<KiCadHttpPart?> BuildForPartIdAsync(int partId, CancellationToken cancellationToken = default)
        {
            return await _reviewDb.Snapshot<KiCadHttpPart?>(async () =>
            {
                // Existing write transactions use the shared catalog lock. Fresh reads
                // use REPEATABLE READ. Never reuse previously tracked catalog entities.
                if (_context.Database.CurrentTransaction!.GetDbTransaction().IsolationLevel == System.Data.IsolationLevel.ReadCommitted)
                    await _reviewDb.Execute("SELECT pg_advisory_xact_lock(1936418162, 1)", cancellationToken);
                Part? part = await _context.Part.AsNoTracking()
                    .Include(p => p.Symbol)!.ThenInclude(s => s!.HeadRevision)
                    .Include(p => p.Footprint)!.ThenInclude(f => f!.HeadRevision)
                    .FirstOrDefaultAsync(p => p.Id == partId, cancellationToken);
                return part == null ? null : await BuildCoreAsync(part, cancellationToken);
            }, cancellationToken);
        }

        public static string FieldValue(KiCadHttpPart part, string field)
        {
            if (part.Fields != null && part.Fields.TryGetValue(field, out FieldDetail? detail))
            {
                return detail.Value ?? "";
            }

            return "";
        }

        /// <summary>
        /// Names of schematic fields that differ from the HTTP library today.
        /// Compared fields are Value, MPN, Manufacturer and Footprint.
        /// </summary>
        public static List<string> DifferingFields(ProjectComponent component, KiCadHttpPart library)
        {
            List<string> names = new();
            if (!Same(component.Value, FieldValue(library, "value")))
            {
                names.Add("Value");
            }

            if (!Same(component.Mpn, FieldValue(library, "MPN")))
            {
                names.Add("MPN");
            }

            if (!Same(component.Manufacturer, FieldValue(library, "Manufacturer")))
            {
                names.Add("Manufacturer");
            }

            if (!Same(component.Footprint, FieldValue(library, "footprint")))
            {
                names.Add("Footprint");
            }

            return names;
        }

        public static bool Same(string? left, string? right)
        {
            return string.Equals((left ?? "").Trim(), (right ?? "").Trim(), StringComparison.Ordinal);
        }

        /// <summary>
        /// First manufacturer/MPN of the first alternative part.
        /// Without an alternative, the second MPN of this part. Otherwise empty.
        /// </summary>
        private async Task<(string Manufacturer, string Mpn)> AlternativeSourceAsync(Part part, List<PartNumber> ownNumbers, CancellationToken cancellationToken)
        {
            int alternativeId = (part.Alternatives ?? []).FirstOrDefault(id => id > 0 && id != part.Id);
            if (alternativeId > 0)
            {
                PartNumber? alternativeNumber = await _context.PartNumber.AsNoTracking()
                    .Where(p => p.PartId == alternativeId)
                    .OrderBy(p => p.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                return await ManufacturerAndMpnAsync(alternativeNumber);
            }

            if (ownNumbers.Count > 1)
            {
                return await ManufacturerAndMpnAsync(ownNumbers[1]);
            }

            return ("", "");
        }

        private async Task<(string Manufacturer, string Mpn)> ManufacturerAndMpnAsync(PartNumber? partNumber)
        {
            if (partNumber == null)
            {
                return ("", "");
            }

            Manufacturer? manufacturer = await _context.Manufacturer.AsNoTracking().FirstOrDefaultAsync(m => m.Id == partNumber.ManufacturerId);
            return (manufacturer?.Name ?? "", partNumber.ManufacturerPartNumber ?? "");
        }
    }
}
