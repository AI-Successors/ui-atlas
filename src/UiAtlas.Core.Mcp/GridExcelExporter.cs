using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Mcp;

public sealed record GridExportReceipt(string Path, string Sha256, int RowCount, int ColumnCount,
    GridDataStatus Status, bool Verified, string DatasetId);

/// <summary>A deterministic XLSX export of retained data; never starts or controls Excel.</summary>
public sealed class GridExcelExporter(Func<string>? downloads = null)
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";
    private readonly Func<string> _downloads = downloads ?? Downloads;

    public GridExportReceipt Export(string id, string name, GridReadResult data, bool allowPartial = false)
    {
        if (data.Status == GridDataStatus.Failed || data.Rows.Count == 0)
            throw new GridOperationException("no_exportable_data", "No successfully read rows are available to export.");
        if (data.Status != GridDataStatus.Complete && !allowPartial)
            throw new GridOperationException("partial_data", "The extraction is partial. Review its reasons before explicitly allowing a partial export.");
        if (data.Schema.Columns.Count is < 1 or > 64 || data.Rows.Count > 1000)
            throw new GridOperationException("export_size_limit", "This dataset exceeds the bounded workbook export size.");
        var columns = data.Schema.Columns.OrderBy(c => c.Ordinal).ToArray();
        var outputRows = new List<string[]> { new[] { name },
            new[] { $"{data.Status} · Current reachable table · Captured {data.CaptureEndedUtc:yyyy-MM-dd HH:mm:ss zzz}" }, Array.Empty<string>(),
            columns.Select(c => c.Label).ToArray() };
        foreach (var row in data.Rows)
        {
            if (row.Cells.Count != columns.Length || row.Cells.Select(c => c.ColumnKey).Distinct().Count() != columns.Length ||
                !row.Cells.Select(c => c.ColumnKey).ToHashSet(StringComparer.Ordinal).SetEquals(columns.Select(c => c.ColumnKey)))
                throw new GridOperationException("invalid_dataset", "Dataset cells do not match its columns.");
            outputRows.Add(columns.Select(c => row.Cells.Single(v => v.ColumnKey == c.ColumnKey))
                .Select(c => c.Status == GridCellReadStatus.Unreadable ? "[Unreadable]" : c.Text ?? "").ToArray());
        }
        string[][] details = [
            ["Extraction details", "Value"], ["Table", name], ["Status", data.Status.ToString()],
            ["Scope", "All reachable rows and columns under current application filters"],
            ["Rows", data.Rows.Count.ToString(CultureInfo.InvariantCulture)], ["Columns", columns.Length.ToString(CultureInfo.InvariantCulture)],
            ["Columns complete", (data.Coverage?.ColumnCoverageComplete == true).ToString()],
            ["Rows complete", (data.Coverage?.RowCoverageComplete == true).ToString()],
            ["Capture", data.CaptureStatus.ToString()], ["Extraction", data.ExtractionStatus.ToString()],
            ["Restoration", data.Restoration.Status.ToString()], ["Restoration detail", data.Restoration.Reason],
            ["Captured", data.CaptureEndedUtc?.ToString("O") ?? ""], ["Reader", data.ReadingMechanism],
            ["Dataset", id], ["Limitations", string.Join("; ", data.Reasons)],
            ["Cell format", "Literal source text. Empty cells are blank; unreadable cells show [Unreadable]."]
        ];
        var root = System.IO.Path.GetFullPath(_downloads());
        Directory.CreateDirectory(root);
        var stem = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').Take(60).ToArray()).Trim();
        if (stem.Length == 0) stem = "Table";
        stem += data.Status == GridDataStatus.Complete ? "" : "-PARTIAL";
        stem += "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var temporary = System.IO.Path.Combine(root, ".ui-atlas-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
                Add(zip, "[Content_Types].xml", new XElement(ct + "Types",
                    new XElement(ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                    new XElement(ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                    Override("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
                    Override("/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"),
                    Override("/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"),
                    Override("/xl/worksheets/sheet2.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
                Add(zip, "_rels/.rels", new XElement(P + "Relationships", Relationship("rId1", "officeDocument", "xl/workbook.xml")));
                Add(zip, "xl/workbook.xml", new XElement(S + "workbook", new XAttribute(XNamespace.Xmlns + "r", R),
                    new XElement(S + "sheets", Sheet(SheetName(name), 1), Sheet("Extraction details", 2))));
                Add(zip, "xl/_rels/workbook.xml.rels", new XElement(P + "Relationships",
                    Relationship("rId1", "worksheet", "worksheets/sheet1.xml"), Relationship("rId2", "worksheet", "worksheets/sheet2.xml"),
                    Relationship("rId3", "styles", "styles.xml")));
                Add(zip, "xl/styles.xml", Styles());
                Add(zip, "xl/worksheets/sheet1.xml", Worksheet(outputRows, columns.Length, true));
                Add(zip, "xl/worksheets/sheet2.xml", Worksheet(details, 2, false));
                XElement Override(string path, string type) => new(ct + "Override", new XAttribute("PartName", path), new XAttribute("ContentType", type));
            }
            Verify(temporary, outputRows, details);
            string destination = "";
            for (var index = 0; index < 100; index++)
            {
                destination = System.IO.Path.Combine(root, stem + (index == 0 ? "" : "-" + index) + ".xlsx");
                try { File.Move(temporary, destination, overwrite: false); break; }
                catch (IOException) when (File.Exists(destination) && index < 99) { }
            }
            return new(destination, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))).ToLowerInvariant(),
                data.Rows.Count, columns.Length, data.Status, true, id);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static XElement Worksheet(IReadOnlyList<string[]> rows, int columns, bool main)
    {
        var sheet = new XElement(S + "worksheet",
            new XElement(S + "dimension", new XAttribute("ref", $"A1:{Column(columns)}{rows.Count}")),
            new XElement(S + "sheetViews", new XElement(S + "sheetView", new XAttribute("workbookViewId", 0),
                new XElement(S + "pane", new XAttribute("ySplit", main ? 4 : 1), new XAttribute("topLeftCell", main ? "A5" : "A2"),
                    new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))),
            new XElement(S + "cols", Enumerable.Range(1, columns).Select(c => new XElement(S + "col",
                new XAttribute("min", c), new XAttribute("max", c), new XAttribute("width", main ? 22 : c == 1 ? 24 : 88), new XAttribute("customWidth", 1)))),
            new XElement(S + "sheetData", rows.Select((row, i) => new XElement(S + "row", new XAttribute("r", i + 1),
                new XAttribute("ht", i == 0 ? 30 : main ? 23 : 34), new XAttribute("customHeight", 1),
                row.Select((value, c) => new XElement(S + "c", new XAttribute("r", Column(c + 1) + (i + 1)),
                    new XAttribute("t", "inlineStr"), new XAttribute("s", i == 0 || main && i == 3 ? 1 : value == "[Unreadable]" ? 3 : 0),
                    new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), ValidateText(value)))))))));
        if (main)
        {
            sheet.Add(new XElement(S + "autoFilter", new XAttribute("ref", $"A4:{Column(columns)}{rows.Count}")));
            if (columns > 1) sheet.Add(new XElement(S + "mergeCells", new XAttribute("count", 2),
                new XElement(S + "mergeCell", new XAttribute("ref", $"A1:{Column(columns)}1")),
                new XElement(S + "mergeCell", new XAttribute("ref", $"A2:{Column(columns)}2"))));
        }
        return sheet;
    }

    private static XElement Styles() => XElement.Parse("""
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="12"/><name val="Calibri"/></font></fonts>
          <fills count="4"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF203B55"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFFFE4B5"/><bgColor indexed="64"/></patternFill></fill></fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="4"><xf numFmtId="49" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="center"/></xf><xf numFmtId="49" fontId="1" fillId="2" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="49" fontId="0" fillId="3" borderId="0" xfId="0"/></cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """);

    private static string ValidateText(string value)
    {
        if (value.Length > 32767) throw new GridOperationException("cell_too_long", "A source cell exceeds Excel's text limit.");
        XmlConvert.VerifyXmlChars(value); return value;
    }
    private static string SheetName(string name)
    {
        var clean = new string(name.Where(c => !"[]:*?/\\".Contains(c) && !char.IsControl(c)).Take(31).ToArray()).Trim('\'');
        return string.IsNullOrWhiteSpace(clean) || clean.Equals("Extraction details", StringComparison.OrdinalIgnoreCase) ? "Table" : clean;
    }
    private static XElement Sheet(string name, int id) => new(S + "sheet", new XAttribute("name", name), new XAttribute("sheetId", id), new XAttribute(R + "id", "rId" + id));
    private static XElement Relationship(string id, string type, string target) => new(P + "Relationship", new XAttribute("Id", id), new XAttribute("Type", R.NamespaceName + "/" + type), new XAttribute("Target", target));
    private static void Add(ZipArchive zip, string name, XElement xml)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        new XDocument(new XDeclaration("1.0", "utf-8", "yes"), xml).Save(writer);
    }
    internal static void Verify(string path, params IReadOnlyList<string[]>[] sheets)
    {
        using var zip = ZipFile.OpenRead(path);
        for (var i = 0; i < sheets.Length; i++)
        {
            using var stream = (zip.GetEntry($"xl/worksheets/sheet{i + 1}.xml") ?? throw new InvalidDataException("Missing worksheet.")).Open();
            var xml = XDocument.Load(stream);
            var rows = xml.Descendants(S + "row").ToArray();
            if (rows.Length != sheets[i].Count) throw new InvalidDataException("Workbook row count mismatch.");
            for (var row = 0; row < rows.Length; row++)
            {
                var values = rows[row].Elements(S + "c").Select(c => (string?)c.Element(S + "is")?.Element(S + "t") ?? "");
                if (!values.SequenceEqual(sheets[i][row], StringComparer.Ordinal) || rows[row].Descendants(S + "f").Any())
                    throw new InvalidDataException("Workbook cell fidelity failed.");
            }
        }
    }
    private static string Column(int index)
    {
        var value = "";
        while (index > 0) { index--; value = (char)('A' + index % 26) + value; index /= 26; }
        return value;
    }
    private static string Downloads()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        var result = SHGetKnownFolderPath(ref id, 0, 0, out var pointer);
        if (result != 0) Marshal.ThrowExceptionForHR(result);
        try { return Marshal.PtrToStringUni(pointer) ?? throw new IOException("Downloads folder is unavailable."); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, nint token, out nint path);
}
