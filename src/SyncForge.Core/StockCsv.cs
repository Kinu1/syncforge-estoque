using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SyncForge.Core;

public static partial class StockCsv
{
    public const int MaxBytes = 64 * 1024;
    public const int MaxRows = 25;
    private const int MaxQuantity = 1_000_000;

    public static ImportPreview Parse(string? csv)
    {
        var id = Guid.NewGuid();
        if (csv is null || Encoding.UTF8.GetByteCount(csv) > MaxBytes)
        {
            return new ImportPreview(id, [], [new(1, "Envie um arquivo CSV UTF-8 de até 64 KiB.")]);
        }

        using var lines = new StringReader(csv);
        var header = lines.ReadLine()?.TrimStart('\uFEFF');
        var columns = header is not null ? ReadFields(header) : null;
        if (columns is null || !columns.SequenceEqual(
            ["sku", "physical_quantity", "reserved_quantity"], StringComparer.OrdinalIgnoreCase))
        {
            return new ImportPreview(id, [], [new(1, "Use os cabeçalhos sku,physical_quantity,reserved_quantity.")]);
        }

        var rows = new List<ImportRow>();
        var errors = new List<ImportError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rowCount = 0;
        var line = 1;
        while (lines.ReadLine() is { } rawLine)
        {
            line++;
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }
            if (++rowCount > MaxRows)
            {
                errors.Add(new ImportError(line, "Envie no máximo 25 linhas de estoque."));
                break;
            }

            var fields = ReadFields(rawLine);
            if (fields is null || fields.Count != 3)
            {
                errors.Add(new ImportError(line, "A linha deve ter exatamente três campos CSV."));
                continue;
            }

            var sku = NormalizeSku(fields[0]);
            if (sku is null)
            {
                errors.Add(new ImportError(line, "Use um SKU de até 48 caracteres com letras, números, ponto, hífen ou sublinhado."));
                continue;
            }
            if (!seen.Add(sku))
            {
                errors.Add(new ImportError(line, "SKU repetido no arquivo."));
                continue;
            }
            if (!TryQuantity(fields[1], out var physical) || !TryQuantity(fields[2], out var reserved) || reserved > physical)
            {
                errors.Add(new ImportError(line, "Informe quantidades inteiras entre 0 e 1.000.000. A reserva deve caber no estoque físico."));
                continue;
            }
            rows.Add(new ImportRow(line, sku, physical, reserved));
        }

        if (rowCount == 0)
        {
            errors.Add(new ImportError(2, "Inclua pelo menos uma linha de estoque."));
        }

        return new ImportPreview(id, [.. rows], [.. errors]);
    }

    public static string? NormalizeSku(string? raw)
    {
        var value = raw?.Trim().ToUpperInvariant();
        return value is not null && SkuPattern().IsMatch(value) ? value : null;
    }

    private static bool TryQuantity(string value, out int quantity) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out quantity) && quantity <= MaxQuantity;

    // Stock records have three single-line fields. Quotes and escaped quotes follow CSV rules.
    private static List<string>? ReadFields(string line)
    {
        var fields = new List<string>(3);
        var index = 0;
        while (true)
        {
            var field = new StringBuilder();
            if (index < line.Length && line[index] == '"')
            {
                index++;
                var closed = false;
                while (index < line.Length)
                {
                    var character = line[index++];
                    if (character != '"')
                    {
                        field.Append(character);
                    }
                    else if (index < line.Length && line[index] == '"')
                    {
                        index++;
                        field.Append('"');
                    }
                    else
                    {
                        closed = true;
                        break;
                    }
                }
                if (!closed || index < line.Length && line[index] != ',')
                {
                    return null;
                }
            }
            else
            {
                while (index < line.Length && line[index] != ',')
                {
                    if (line[index] == '"')
                    {
                        return null;
                    }
                    field.Append(line[index++]);
                }
            }

            fields.Add(field.ToString().Trim());
            if (index == line.Length)
            {
                return fields;
            }
            if (fields.Count == 3)
            {
                return null;
            }
            index++;
        }
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{0,47}$", RegexOptions.CultureInvariant)]
    private static partial Regex SkuPattern();
}
