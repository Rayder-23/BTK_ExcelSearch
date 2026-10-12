using System.IO.Compression;
using System.Xml;

namespace ExcelSearch.Core.Import;

/// <summary>
/// Finds the cells of one worksheet that hold an Excel error value (#DIV/0!, #N/A, #REF!...).
/// ExcelDataReader reports such a cell as null, so without this a row with an error value looks like a blank
/// cell and could be imported as valid. The error type is only visible in the sheet XML (<c>t="e"</c>).
/// </summary>
internal static class ErrorCellScanner
{
    private static readonly byte[] Marker = "t=\"e\""u8.ToArray();

    /// <summary>(1-based row, 0-based column) to the error text. Empty when the sheet has none, or on any read problem.</summary>
    public static IReadOnlyDictionary<(int Row, int Col), string> Find(string path, string sheetName)
    {
        var result = new Dictionary<(int, int), string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

            var partName = FindSheetPart(zip, sheetName);
            var entry = partName is null ? null : zip.GetEntry(partName);
            if (entry is null) return result;

            // Cheap pre-check: a plain byte search for t="e". Almost every file has none, and then we stop here.
            if (!ContainsMarker(entry)) return result;

            using var stream = entry.Open();
            using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "c" || xml.GetAttribute("t") != "e") continue;
                var reference = xml.GetAttribute("r");
                if (reference is null || !TryParseReference(reference, out var row, out var col)) continue;

                var text = "#ERROR!";
                if (!xml.IsEmptyElement)
                {
                    var depth = xml.Depth;
                    while (xml.Read() && xml.Depth > depth)
                        if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "v")
                            text = xml.ReadElementContentAsString();
                }
                result[(row, col)] = text;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException or UnauthorizedAccessException)
        {
            // Not being able to look is no worse than before this check existed; the normal parse still runs.
        }
        return result;
    }

    private static bool ContainsMarker(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var buffer = new byte[81920];
        var carry = 0;   // the last bytes of the previous block, in case the marker straddles two reads
        int read;
        while ((read = stream.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            var total = carry + read;
            if (buffer.AsSpan(0, total).IndexOf(Marker) >= 0) return true;
            carry = Math.Min(Marker.Length - 1, total);
            buffer.AsSpan(total - carry, carry).CopyTo(buffer);
        }
        return false;
    }

    /// <summary>workbook.xml gives the sheet's relationship id, workbook.xml.rels gives the part it points to.</summary>
    private static string? FindSheetPart(ZipArchive zip, string sheetName)
    {
        string? relId = null;
        var workbook = zip.GetEntry("xl/workbook.xml");
        if (workbook is null) return null;
        using (var s = workbook.Open())
        using (var xml = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
        {
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "sheet" && xml.GetAttribute("name") == sheetName)
                {
                    relId = xml.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    break;
                }
            }
        }
        if (relId is null) return null;

        var rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (rels is null) return null;
        using (var s = rels.Open())
        using (var xml = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
        {
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "Relationship" && xml.GetAttribute("Id") == relId)
                {
                    var target = xml.GetAttribute("Target");
                    if (target is null) return null;
                    return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                }
            }
        }
        return null;
    }

    /// <summary>"AB12" to row 12, column 27 (0-based).</summary>
    private static bool TryParseReference(string reference, out int row, out int col)
    {
        row = 0;
        col = 0;
        var i = 0;
        for (; i < reference.Length && char.IsAsciiLetter(reference[i]); i++)
            col = col * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        if (i == 0 || i == reference.Length || !int.TryParse(reference.AsSpan(i), out row)) return false;
        col -= 1;
        return true;
    }
}
