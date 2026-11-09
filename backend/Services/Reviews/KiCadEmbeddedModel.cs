namespace KiComLibBackend.Services.Reviews;

/// <summary>
/// Resolve a model stored inside the footprint. Its encoded bytes and transforms
/// are retained by the footprint blob, rather than a separate model3d record.
/// Native model decoding/checksum and dimensional review remain KiCad/agent work.
/// </summary>
internal static class KiCadEmbeddedModel
{
    public static bool IsReference(KiCadExpression model) =>
        model.Argument()?.StartsWith("kicad-embed://", StringComparison.Ordinal) == true;

    public static void Validate(KiCadExpression footprint, KiCadExpression model)
    {
        string name = model.Argument()!["kicad-embed://".Length..];
        if (string.IsNullOrWhiteSpace(name)) throw new FormatException("Embedded model reference has no filename");
        var containers = footprint.Named("embedded_files").ToArray();
        if (containers.Length != 1) throw new FormatException("Embedded model requires one embedded_files section");
        var files = containers[0].Named("file").Where(file => file.Named("name").Any(n => n.Argument() == name)).ToArray();
        if (files.Length != 1) throw new FormatException("Embedded model is missing or ambiguous: " + name);
        var file = files[0];
        if (Field(file, "name").Argument() != name || Field(file, "type").Argument() != "model")
            throw new FormatException("Embedded model must resolve to a file of type model: " + name);
        var data = Field(file, "data");
        if (data.Children.Skip(1).Any(c => c.Atom == null)) throw new FormatException("Invalid embedded model data");
        string encoded = string.Concat(data.Children.Skip(1).Select(c => c.Atom));
        if (encoded.Length < 3 || encoded[0] != '|' || encoded[^1] != '|')
            throw new FormatException("Embedded model payload is missing: " + name);
        byte[] compressed = Convert.FromBase64String(encoded[1..^1]);
        if (compressed.Length < 9 || !compressed.AsSpan(0, 4).SequenceEqual(new byte[] { 0x28, 0xb5, 0x2f, 0xfd }))
            throw new FormatException("Embedded model payload must be a base64-encoded Zstandard frame: " + name);
        string checksum = Field(file, "checksum").Argument() ?? "";
        if (checksum.Length is not (32 or 64) || !checksum.All(Uri.IsHexDigit))
            throw new FormatException("Embedded model checksum is missing or malformed: " + name);
        // Validate the declared placement even though there is no database association.
        foreach (string transform in new[] { "offset", "scale", "rotate" })
        {
            var declarations = model.Named(transform).ToArray();
            if (declarations.Length > 1) throw new FormatException("Duplicate embedded model transform");
            if (declarations.Length == 0) continue;
            var xyz = Field(declarations[0], "xyz");
            if (xyz.Children.Count != 4) throw new FormatException("Embedded model transform requires three coordinates");
            for (int i = 1; i <= 3; i++)
                if (!double.TryParse(xyz.Argument(i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
                    throw new FormatException("Invalid embedded model transform");
        }
        var opacity = model.Named("opacity").ToArray();
        if (opacity.Length > 1 || (opacity.Length == 1 && (!double.TryParse(opacity[0].Argument(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double opacityValue) || !double.IsFinite(opacityValue) || opacityValue < 0 || opacityValue > 1)))
            throw new FormatException("Invalid embedded model opacity");
    }

    private static KiCadExpression Field(KiCadExpression parent, string name)
    {
        var fields = parent.Named(name).ToArray();
        if (fields.Length != 1) throw new FormatException("Embedded model requires one " + name + " field");
        return fields[0];
    }
}
