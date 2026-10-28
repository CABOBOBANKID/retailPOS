using System.IO.Compression;
using System.Xml.Linq;

namespace AzumahCloud.Api.Services;

/// <summary>
/// Small dependency-free XLSX reader for the LAN restock workflow.
/// It reads the first worksheet and supports shared strings, inline strings and
/// ordinary numeric/string cells. No internet or Excel installation is required.
/// </summary>
public static class XlsxRestockReader
{
    public static List<Dictionary<string, string>> Read(Stream stream)
    {
        using ZipArchive zip = new(stream, ZipArchiveMode.Read, leaveOpen: false);
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        List<string> shared = ReadSharedStrings(zip, main);
        string sheetPath = FindFirstSheetPath(zip, main, rel, packageRel);
        ZipArchiveEntry sheet = zip.GetEntry(sheetPath)
            ?? throw new InvalidDataException("The first Excel worksheet could not be found.");

        XDocument doc;
        using (Stream s = sheet.Open()) doc = XDocument.Load(s);

        XElement? sheetData = doc.Root?.Element(main + "sheetData");
        if (sheetData is null) throw new InvalidDataException("The Excel worksheet has no sheet data.");

        List<Dictionary<string, string>> rows = new();
        Dictionary<string, int>? headers = null;

        foreach (XElement row in sheetData.Elements(main + "row"))
        {
            Dictionary<string, string> cells = new(StringComparer.OrdinalIgnoreCase);
            foreach (XElement cell in row.Elements(main + "c"))
            {
                string reference = cell.Attribute("r")?.Value ?? "";
                string column = ColumnName(reference);
                string value = CellValue(cell, main, shared);
                cells[column] = value;
            }

            if (cells.Count == 0) continue;
            if (headers is null)
            {
                headers = cells.ToDictionary(
                    x => NormalizeHeader(x.Value),
                    x => ColumnNumber(x.Key),
                    StringComparer.OrdinalIgnoreCase);
                continue;
            }

            Dictionary<string, string> record = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string header, int column) in headers)
            {
                string columnName = ColumnLetters(column);
                record[header] = cells.TryGetValue(columnName, out string? value) ? value.Trim() : "";
            }
            if (record.Values.Any(v => !string.IsNullOrWhiteSpace(v))) rows.Add(record);
        }
        return rows;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip, XNamespace main)
    {
        ZipArchiveEntry? entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return new();

        using Stream s = entry.Open();
        XDocument doc = XDocument.Load(s);
        return doc.Descendants(main + "si")
            .Select(si => string.Concat(si.Descendants(main + "t").Select(t => t.Value)))
            .ToList();
    }

    private static string FindFirstSheetPath(ZipArchive zip, XNamespace main, XNamespace rel, XNamespace packageRel)
    {
        ZipArchiveEntry workbook = zip.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("Invalid Excel file: workbook.xml is missing.");
        ZipArchiveEntry relationships = zip.GetEntry("xl/_rels/workbook.xml.rels")
            ?? throw new InvalidDataException("Invalid Excel file: workbook relationships are missing.");

        string relationshipId;
        using (Stream s = workbook.Open())
        {
            XDocument doc = XDocument.Load(s);
            XElement? sheet = doc.Root?.Element(main + "sheets")?.Elements(main + "sheet").FirstOrDefault();
            relationshipId = sheet?.Attribute(rel + "id")?.Value
                ?? throw new InvalidDataException("The Excel workbook contains no worksheets.");
        }

        using (Stream s = relationships.Open())
        {
            XDocument doc = XDocument.Load(s);
            XElement? relationship = doc.Root?.Elements(packageRel + "Relationship")
                .FirstOrDefault(x => string.Equals(x.Attribute("Id")?.Value, relationshipId, StringComparison.Ordinal));
            string target = relationship?.Attribute("Target")?.Value
                ?? throw new InvalidDataException("The first worksheet relationship is invalid.");

            target = target.Replace('\\', '/').TrimStart('/');
            if (!target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                target = "xl/" + target.TrimStart('/');
            return target;
        }
    }

    private static string CellValue(XElement cell, XNamespace main, List<string> shared)
    {
        string type = cell.Attribute("t")?.Value ?? "";
        if (type == "inlineStr")
            return string.Concat(cell.Descendants(main + "t").Select(t => t.Value));

        string raw = cell.Element(main + "v")?.Value ?? "";
        if (type == "s" && int.TryParse(raw, out int index) && index >= 0 && index < shared.Count)
            return shared[index];

        return raw;
    }

    private static string NormalizeHeader(string value)
    {
        return new string(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    private static string ColumnName(string reference)
    {
        return new string(reference.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
    }

    private static int ColumnNumber(string column)
    {
        int value = 0;
        foreach (char c in column.ToUpperInvariant())
            value = value * 26 + (c - 'A' + 1);
        return value;
    }

    private static string ColumnLetters(int number)
    {
        string result = "";
        while (number > 0)
        {
            number--;
            result = (char)('A' + (number % 26)) + result;
            number /= 26;
        }
        return result;
    }
}
