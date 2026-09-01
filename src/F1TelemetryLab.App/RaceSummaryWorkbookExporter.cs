using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace F1TelemetryLab;

public static class RaceSummaryWorkbookExporter
{
    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    private sealed record Cell(string Value, bool Numeric = false);
    private sealed record Sheet(string Name, IReadOnlyList<IReadOnlyList<Cell>> Rows);

    public static string Export(string sessionFolder)
    {
        var database = Path.Combine(sessionFolder, "session.sqlite");
        if (!File.Exists(database)) throw new FileNotFoundException("session.sqlite not found", database);
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Cache=Private;Default Timeout=30");
        connection.Open();
        var sheets = new[]
        {
            Query(connection, "Laps", """
                SELECT lap_num AS Lap, lap_time_ms AS Lap_time_ms, sector1_ms AS Sector_1_ms,
                       sector2_ms AS Sector_2_ms, sector3_ms AS Sector_3_ms, clean_lap AS Clean,
                       position_start AS Position_start, position_end AS Position_end, pit_this_lap AS Pit
                FROM lap_state_summary WHERE is_player=1 ORDER BY lap_num
                """),
            Query(connection, "Tyres", """
                SELECT lap_num AS Lap, visual_tyre_compound_end AS Visual_compound, tyres_age_end AS Age_laps,
                       tyre_wear_fl_end AS Wear_FL_pct, tyre_wear_fr_end AS Wear_FR_pct,
                       tyre_wear_rl_end AS Wear_RL_pct, tyre_wear_rr_end AS Wear_RR_pct,
                       tyre_wear_fl_delta AS Lap_FL_pct, tyre_wear_fr_delta AS Lap_FR_pct,
                       tyre_wear_rl_delta AS Lap_RL_pct, tyre_wear_rr_delta AS Lap_RR_pct,
                       clean_lap AS Clean, pit_this_lap AS Pit
                FROM lap_state_summary WHERE is_player=1 ORDER BY lap_num
                """),
            Query(connection, "Pits", """
                SELECT car_idx AS Car, is_player AS Player, lap_num AS Lap, lap_time_ms AS Lap_time_ms,
                       position_start AS Position_start, position_end AS Position_end,
                       pit_stops_start AS Stops_start, pit_stops_end AS Stops_end,
                       visual_tyre_compound_start AS Compound_in, visual_tyre_compound_end AS Compound_out
                FROM lap_state_summary WHERE pit_this_lap=1 ORDER BY lap_num, position_end, car_idx
                """),
            Query(connection, "ERS", """
                SELECT lap_num AS Lap, ers_start / 40000.0 AS ERS_start_pct,
                       ers_end / 40000.0 AS ERS_end_pct, ers_delta / 40000.0 AS ERS_delta_pct,
                       ers_deployed_this_lap / 1000000.0 AS Deployed_MJ,
                       ers_harvest_mguk_this_lap / 1000000.0 AS Harvest_MGUK_MJ,
                       ers_harvest_mguh_this_lap / 1000000.0 AS Harvest_MGUH_MJ,
                       ers_deploy_mode_end AS Mode_end, clean_lap AS Clean, pit_this_lap AS Pit
                FROM lap_state_summary WHERE is_player=1 ORDER BY lap_num
                """),
            BuildQuality(connection)
        };

        var path = Path.Combine(sessionFolder, "race_summary.xlsx");
        var temporary = path + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
        using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            WriteContentTypes(archive, sheets.Length);
            WriteRootRelationships(archive);
            WriteWorkbook(archive, sheets);
            WriteWorkbookRelationships(archive, sheets.Length);
            WriteStyles(archive);
            for (var i = 0; i < sheets.Length; i++) WriteSheet(archive, i + 1, sheets[i]);
        }
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    private static Sheet Query(SqliteConnection connection, string name, string sql)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<IReadOnlyList<Cell>>();
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => new Cell(reader.GetName(i))).ToArray());
            while (reader.Read())
            {
                var cells = new Cell[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++) cells[i] = ToCell(reader, i);
                rows.Add(cells);
            }
            return new Sheet(name, rows);
        }
        catch (SqliteException ex)
        {
            return new Sheet(name, new[] { new[] { new Cell("Data unavailable"), new Cell(ex.Message) } });
        }
    }

    private static Sheet BuildQuality(SqliteConnection connection)
    {
        var rows = new List<IReadOnlyList<Cell>> { new[] { new Cell("Section"), new Cell("Metric"), new Cell("Value") } };
        Append(connection, rows, "Metadata", "SELECT key,value FROM session_metadata ORDER BY key");
        Append(connection, rows, "Quality", "SELECT dimension, rating || ': ' || summary FROM data_quality ORDER BY dimension");
        Append(connection, rows, "Analysis", "SELECT analyzed_at, summary FROM analysis_runs ORDER BY id DESC LIMIT 10");
        return new Sheet("Quality", rows);
    }

    private static void Append(SqliteConnection connection, List<IReadOnlyList<Cell>> rows, string section, string sql)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(new[]
            {
                new Cell(section),
                new Cell(reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? ""),
                new Cell(reader.IsDBNull(1) ? "" : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "")
            });
        }
        catch (SqliteException) { }
    }

    private static Cell ToCell(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return new Cell("");
        var value = reader.GetValue(index);
        return value switch
        {
            byte or short or int or long or float or double or decimal =>
                new Cell(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", true),
            _ => new Cell(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
        };
    }

    private static void WriteContentTypes(ZipArchive archive, int sheetCount)
    {
        WriteXml(archive, "[Content_Types].xml", writer =>
        {
            writer.WriteStartElement("Types", ContentTypesNs);
            WriteElement(writer, ContentTypesNs, "Default", ("Extension", "rels"), ("ContentType", "application/vnd.openxmlformats-package.relationships+xml"));
            WriteElement(writer, ContentTypesNs, "Default", ("Extension", "xml"), ("ContentType", "application/xml"));
            WriteElement(writer, ContentTypesNs, "Override", ("PartName", "/xl/workbook.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"));
            WriteElement(writer, ContentTypesNs, "Override", ("PartName", "/xl/styles.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"));
            for (var i = 1; i <= sheetCount; i++)
                WriteElement(writer, ContentTypesNs, "Override", ("PartName", $"/xl/worksheets/sheet{i}.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
            writer.WriteEndElement();
        });
    }

    private static void WriteRootRelationships(ZipArchive archive)
    {
        WriteXml(archive, "_rels/.rels", writer =>
        {
            writer.WriteStartElement("Relationships", RelationshipsNs);
            WriteElement(writer, RelationshipsNs, "Relationship", ("Id", "rId1"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"), ("Target", "xl/workbook.xml"));
            writer.WriteEndElement();
        });
    }

    private static void WriteWorkbook(ZipArchive archive, IReadOnlyList<Sheet> sheets)
    {
        WriteXml(archive, "xl/workbook.xml", writer =>
        {
            writer.WriteStartElement("workbook", SpreadsheetNs);
            writer.WriteAttributeString("xmlns", "r", null, "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            writer.WriteStartElement("sheets", SpreadsheetNs);
            for (var i = 0; i < sheets.Count; i++)
            {
                writer.WriteStartElement("sheet", SpreadsheetNs);
                writer.WriteAttributeString("name", sheets[i].Name);
                writer.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("r", "id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", $"rId{i + 1}");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
        });
    }

    private static void WriteWorkbookRelationships(ZipArchive archive, int sheetCount)
    {
        WriteXml(archive, "xl/_rels/workbook.xml.rels", writer =>
        {
            writer.WriteStartElement("Relationships", RelationshipsNs);
            for (var i = 1; i <= sheetCount; i++)
                WriteElement(writer, RelationshipsNs, "Relationship", ("Id", $"rId{i}"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"), ("Target", $"worksheets/sheet{i}.xml"));
            WriteElement(writer, RelationshipsNs, "Relationship", ("Id", $"rId{sheetCount + 1}"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"), ("Target", "styles.xml"));
            writer.WriteEndElement();
        });
    }

    private static void WriteStyles(ZipArchive archive)
    {
        WriteXml(archive, "xl/styles.xml", writer =>
        {
            writer.WriteStartElement("styleSheet", SpreadsheetNs);
            writer.WriteStartElement("fonts", SpreadsheetNs); writer.WriteAttributeString("count", "2");
            writer.WriteStartElement("font", SpreadsheetNs); writer.WriteStartElement("sz", SpreadsheetNs); writer.WriteAttributeString("val", "11"); writer.WriteEndElement(); writer.WriteStartElement("name", SpreadsheetNs); writer.WriteAttributeString("val", "Calibri"); writer.WriteEndElement(); writer.WriteEndElement();
            writer.WriteStartElement("font", SpreadsheetNs); writer.WriteStartElement("b", SpreadsheetNs); writer.WriteEndElement(); writer.WriteStartElement("color", SpreadsheetNs); writer.WriteAttributeString("rgb", "FFFFFFFF"); writer.WriteEndElement(); writer.WriteStartElement("sz", SpreadsheetNs); writer.WriteAttributeString("val", "11"); writer.WriteEndElement(); writer.WriteStartElement("name", SpreadsheetNs); writer.WriteAttributeString("val", "Calibri"); writer.WriteEndElement(); writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("fills", SpreadsheetNs); writer.WriteAttributeString("count", "3");
            writer.WriteStartElement("fill", SpreadsheetNs); writer.WriteStartElement("patternFill", SpreadsheetNs); writer.WriteAttributeString("patternType", "none"); writer.WriteEndElement(); writer.WriteEndElement();
            writer.WriteStartElement("fill", SpreadsheetNs); writer.WriteStartElement("patternFill", SpreadsheetNs); writer.WriteAttributeString("patternType", "gray125"); writer.WriteEndElement(); writer.WriteEndElement();
            writer.WriteStartElement("fill", SpreadsheetNs); writer.WriteStartElement("patternFill", SpreadsheetNs); writer.WriteAttributeString("patternType", "solid"); writer.WriteStartElement("fgColor", SpreadsheetNs); writer.WriteAttributeString("rgb", "FFF87F3A"); writer.WriteEndElement(); writer.WriteStartElement("bgColor", SpreadsheetNs); writer.WriteAttributeString("indexed", "64"); writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("borders", SpreadsheetNs); writer.WriteAttributeString("count", "1"); writer.WriteStartElement("border", SpreadsheetNs); foreach (var side in new[] { "left", "right", "top", "bottom", "diagonal" }) { writer.WriteStartElement(side, SpreadsheetNs); writer.WriteEndElement(); } writer.WriteEndElement(); writer.WriteEndElement();
            writer.WriteStartElement("cellStyleXfs", SpreadsheetNs); writer.WriteAttributeString("count", "1"); WriteElement(writer, SpreadsheetNs, "xf", ("numFmtId", "0"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0")); writer.WriteEndElement();
            writer.WriteStartElement("cellXfs", SpreadsheetNs); writer.WriteAttributeString("count", "2");
            WriteElement(writer, SpreadsheetNs, "xf", ("numFmtId", "0"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0"), ("xfId", "0"));
            WriteElement(writer, SpreadsheetNs, "xf", ("numFmtId", "0"), ("fontId", "1"), ("fillId", "2"), ("borderId", "0"), ("xfId", "0"), ("applyFill", "1"), ("applyFont", "1"));
            writer.WriteEndElement();
            writer.WriteStartElement("cellStyles", SpreadsheetNs); writer.WriteAttributeString("count", "1");
            WriteElement(writer, SpreadsheetNs, "cellStyle", ("name", "Normal"), ("xfId", "0"), ("builtinId", "0"));
            writer.WriteEndElement();
            writer.WriteEndElement();
        });
    }

    private static void WriteSheet(ZipArchive archive, int index, Sheet sheet)
    {
        WriteXml(archive, $"xl/worksheets/sheet{index}.xml", writer =>
        {
            writer.WriteStartElement("worksheet", SpreadsheetNs);
            writer.WriteStartElement("sheetViews", SpreadsheetNs); writer.WriteStartElement("sheetView", SpreadsheetNs); writer.WriteAttributeString("workbookViewId", "0"); writer.WriteStartElement("pane", SpreadsheetNs); writer.WriteAttributeString("ySplit", "1"); writer.WriteAttributeString("topLeftCell", "A2"); writer.WriteAttributeString("activePane", "bottomLeft"); writer.WriteAttributeString("state", "frozen"); writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
            writer.WriteStartElement("sheetData", SpreadsheetNs);
            for (var rowIndex = 0; rowIndex < sheet.Rows.Count; rowIndex++)
            {
                writer.WriteStartElement("row", SpreadsheetNs);
                writer.WriteAttributeString("r", (rowIndex + 1).ToString(CultureInfo.InvariantCulture));
                var row = sheet.Rows[rowIndex];
                for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
                {
                    var cell = row[columnIndex];
                    writer.WriteStartElement("c", SpreadsheetNs);
                    writer.WriteAttributeString("r", ColumnName(columnIndex + 1) + (rowIndex + 1).ToString(CultureInfo.InvariantCulture));
                    if (rowIndex == 0) writer.WriteAttributeString("s", "1");
                    if (cell.Numeric && rowIndex > 0)
                    {
                        writer.WriteStartElement("v", SpreadsheetNs); writer.WriteString(cell.Value); writer.WriteEndElement();
                    }
                    else
                    {
                        writer.WriteAttributeString("t", "inlineStr");
                        writer.WriteStartElement("is", SpreadsheetNs); writer.WriteStartElement("t", SpreadsheetNs); writer.WriteString(cell.Value); writer.WriteEndElement(); writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
        });
    }

    private static string ColumnName(int value)
    {
        var result = "";
        while (value > 0)
        {
            value--;
            result = (char)('A' + value % 26) + result;
            value /= 26;
        }
        return result;
    }

    private static void WriteXml(ZipArchive archive, string path, Action<XmlWriter> action)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = false, CloseOutput = false });
        writer.WriteStartDocument();
        action(writer);
        writer.WriteEndDocument();
    }

    private static void WriteElement(XmlWriter writer, string ns, string name, params (string Name, string Value)[] attributes)
    {
        writer.WriteStartElement(name, ns);
        foreach (var attribute in attributes) writer.WriteAttributeString(attribute.Name, attribute.Value);
        writer.WriteEndElement();
    }
}
