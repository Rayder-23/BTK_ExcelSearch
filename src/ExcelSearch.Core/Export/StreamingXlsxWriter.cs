using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ExcelSearch.Core.Export;

/// <summary>
/// A forward-only .xlsx writer with flat memory use. The worksheet XML is written straight into a ZipArchive in Create
/// mode (so nothing but the current row is ever held), text is stored inline (no shared-strings table) and a small
/// stylesheet gives real dates a date format. Cell types are kept as given: numbers stay numbers, dates stay dates,
/// text stays text (so "007" keeps its zeros).
/// </summary>
internal sealed class StreamingXlsxWriter : IDisposable
{
    private const int DateStyle = 1;
    private const int HeaderStyle = 2;
    private const int MaxCellText = 32_767;   // Excel's limit for one cell

    private readonly ZipArchive _zip;
    private readonly StreamWriter _sheet;
    private int _row;
    private bool _completed;

    public StreamingXlsxWriter(Stream output, string sheetName)
    {
        _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        Add("[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
        Add("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Add("xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
            $"<sheet name=\"{Escape(sheetName)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Add("xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");

        // Style 0 = default, 1 = date (dd/mm/yyyy), 2 = bold header.
        Add("xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"dd/mm/yyyy\"/></numFmts>" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"3\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");

        // The sheet is the last entry and is streamed: rows are written as they arrive.
        var entry = _zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest);
        _sheet = new StreamWriter(entry.Open(), new UTF8Encoding(false), bufferSize: 65_536);
        _sheet.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                     "<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>" +
                     "<sheetData>");
    }

    /// <summary>Writes the first (bold) row. Call once, before any data row.</summary>
    public void WriteHeader(IReadOnlyList<string> headers)
    {
        _row++;
        _sheet.Write($"<row r=\"{_row}\">");
        for (var c = 0; c < headers.Count; c++) WriteText(c, headers[c], HeaderStyle);
        _sheet.Write("</row>");
    }

    /// <summary>Writes one data row. Null cells are left out (empty). Supported: string, double/int/decimal numbers, DateTime, bool.</summary>
    public void WriteRow(IReadOnlyList<object?> values)
    {
        _row++;
        _sheet.Write($"<row r=\"{_row}\">");
        for (var c = 0; c < values.Count; c++)
        {
            switch (values[c])
            {
                case null: break;
                case string s: WriteText(c, s, 0); break;
                case DateTime d:
                    _sheet.Write($"<c r=\"{Ref(c)}\" s=\"{DateStyle}\"><v>{d.ToOADate().ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                    break;
                case bool b:
                    _sheet.Write($"<c r=\"{Ref(c)}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>");
                    break;
                case double or float or decimal or int or long or short or byte:
                    _sheet.Write($"<c r=\"{Ref(c)}\"><v>{Convert.ToString(values[c], CultureInfo.InvariantCulture)}</v></c>");
                    break;
                default: WriteText(c, Convert.ToString(values[c], CultureInfo.InvariantCulture) ?? "", 0); break;
            }
        }
        _sheet.Write("</row>");
    }

    /// <summary>Closes the sheet and the zip. Without this the file is not a valid workbook.</summary>
    public void Complete()
    {
        if (_completed) return;
        _completed = true;
        _sheet.Write("</sheetData></worksheet>");
        _sheet.Dispose();
        _zip.Dispose();
    }

    public void Dispose()
    {
        if (_completed) return;
        _completed = true;
        _sheet.Dispose();
        _zip.Dispose();
    }

    private void WriteText(int column, string text, int style)
    {
        if (text.Length > MaxCellText) text = text[..MaxCellText];
        var s = style == 0 ? "" : $" s=\"{style}\"";
        _sheet.Write($"<c r=\"{Ref(column)}\" t=\"inlineStr\"{s}><is><t xml:space=\"preserve\">{Escape(text)}</t></is></c>");
    }

    private string Ref(int column) => ColumnName(column) + _row.ToString(CultureInfo.InvariantCulture);

    /// <summary>0 -> A, 25 -> Z, 26 -> AA.</summary>
    internal static string ColumnName(int column)
    {
        var name = new StringBuilder();
        for (var n = column + 1; n > 0; n = (n - 1) / 26)
            name.Insert(0, (char)('A' + (n - 1) % 26));
        return name.ToString();
    }

    /// <summary>XML-escapes text and drops characters XML 1.0 cannot hold (control characters other than tab, CR, LF).</summary>
    internal static string Escape(string text)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            string? replacement = ch switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&apos;",
                _ when ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r' => "",
                _ when ch is '￾' or '￿' => "",
                _ => null,
            };
            if (replacement is null)
            {
                sb?.Append(ch);
                continue;
            }
            sb ??= new StringBuilder(text, 0, i, text.Length + 16);
            sb.Append(replacement);
        }
        return sb?.ToString() ?? text;
    }

    private void Add(string name, string content)
    {
        var entry = _zip.CreateEntry(name, CompressionLevel.Fastest);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
