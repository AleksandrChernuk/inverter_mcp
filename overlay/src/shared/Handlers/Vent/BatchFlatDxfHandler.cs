#if INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_batch_flat_dxf</c> — export flat-pattern DXF for every sheet-metal part, either of the active
/// assembly's referenced documents or of every .ipt in <c>parts_dir</c>, into <c>output_dir</c>. Non
/// sheet-metal parts and parts without a flat pattern are skipped (reported). Returns per-part status.
///
/// This is a long-running batch: run it per product rather than across the whole catalog in one call.
/// </summary>
public sealed class BatchFlatDxfHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_batch_flat_dxf";
    public bool IsReadOnly => false;

    private const string FlatPatternDxfFormat = "FLAT PATTERN DXF";

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;

        string outputDir = (p["output_dir"]?.Type == JTokenType.String) ? (string)p["output_dir"]! : "";
        if (ExportPathPolicy.TryRejectPath(Path.Combine(outputDir, "_probe.dxf"), out var rejection))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, rejection);
        try { Directory.CreateDirectory(outputDir); }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, "cannot create output_dir: " + ex.Message); }
        string incompletePath = Path.Combine(outputDir, "INCOMPLETE.json");
        string manifestPath = Path.Combine(outputDir, "dxf-export-manifest.json");
        try
        {
            File.WriteAllText(incompletePath, new JObject
            {
                ["state"] = "running",
                ["started_at_utc"] = DateTime.UtcNow.ToString("O"),
            }.ToString());
        }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, "cannot create DXF batch marker: " + ex.Message); }

        string? partsDir = (p["parts_dir"]?.Type == JTokenType.String) ? (string)p["parts_dir"]! : null;
        var materialAliases = p["material_aliases"] as JObject ?? new JObject();
        bool createMissingFlatPatterns = p["create_missing_flat_patterns"]?.Value<bool?>() ?? false;

        var results = new JArray();
        int ok = 0, skipped = 0, failed = 0;
        var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void ProcessPart(PartDocument part, string label, int quantity)
        {
            try
            {
                if (part.Dirty)
                    throw new InvalidOperationException("save or discard part changes before DXF export");
                if (part.ComponentDefinition is not SheetMetalComponentDefinition sm)
                { results.Add(Row(label, "skipped", "не листовая")); skipped++; return; }
                if (!sm.HasFlatPattern)
                {
                    if (!createMissingFlatPatterns)
                        throw new InvalidOperationException("нет flat pattern; create it explicitly or enable create_missing_flat_patterns");
                    sm.Unfold();
                    if (!part.Update2(false))
                        throw new InvalidOperationException("part rebuild failed after creating flat pattern");
                    part.Save();
                }
                if (!sm.HasFlatPattern)
                    throw new InvalidOperationException("flat pattern creation did not succeed");

                string designation = ReadIProperty(part, "Design Tracking Properties", "Part Number");
                if (string.IsNullOrWhiteSpace(designation)) designation = label;
                string description = ReadIProperty(part, "Design Tracking Properties", "Description");
                string rawMaterial = "";
                try { rawMaterial = part.ComponentDefinition.Material.Name; } catch { }
                string material = materialAliases.Value<string>(rawMaterial) ?? rawMaterial;
                double thicknessMm = sm.Thickness.Value * 10.0;
                string exportName = string.Join(" ", new[]
                {
                    thicknessMm.ToString("0.###", CultureInfo.InvariantCulture) + "мм",
                    material,
                    Math.Max(1, quantity).ToString(CultureInfo.InvariantCulture) + "шт",
                    designation,
                    description,
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
                string outPath = Path.Combine(outputDir, SafeName(exportName) + ".dxf");
                if (!outputNames.Add(outPath))
                {
                    results.Add(Row(label, "error", "duplicate DXF output name: " + outPath));
                    failed++;
                    return;
                }
                sm.FlatPattern.DataIO.WriteDataToFile(FlatPatternDxfFormat, outPath);
                results.Add(new JObject
                {
                    ["part"] = label,
                    ["status"] = "ok",
                    ["detail"] = outPath,
                    ["designation"] = designation,
                    ["material"] = material,
                    ["source_material"] = rawMaterial,
                    ["thickness_mm"] = Math.Round(thicknessMm, 4),
                    ["quantity"] = Math.Max(1, quantity),
                });
                ok++;
            }
            catch (Exception ex) { results.Add(Row(label, "error", ex.Message)); failed++; }
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(partsDir))
            {
                if (!Directory.Exists(partsDir))
                    return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "parts_dir not found: " + partsDir);
                foreach (var f in Directory.GetFiles(partsDir, "*.ipt", SearchOption.AllDirectories))
                {
                    global::Inventor.Document? d = null;
                    bool openedHere = false;
                    try
                    {
                        d = FindOpen(app, f);
                        if (d is null) { d = app.Documents.Open(f, false); openedHere = true; }
                        ProcessPart((PartDocument)d, Path.GetFileNameWithoutExtension(f), 1);
                    }
                    catch (Exception ex) { results.Add(Row(Path.GetFileNameWithoutExtension(f), "error", ex.Message)); failed++; }
                    finally { if (openedHere) try { d?.Close(true); } catch { } }
                }
            }
            else
            {
                global::Inventor.Document? active;
                try { active = app.ActiveDocument; } catch { active = null; }
                if (active is not AssemblyDocument asm)
                    return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE,
                        "with no parts_dir, the active document must be an assembly");

                var parts = new Dictionary<string, PartDocument>(StringComparer.OrdinalIgnoreCase);
                var quantities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                void WalkOccurrences(System.Collections.IEnumerable occurrences)
                {
                    foreach (ComponentOccurrence occ in occurrences)
                    {
                        try
                        {
                            if (occ.Suppressed) continue;
                            if (occ.Definition.Document is PartDocument part)
                            {
                                string key = part.FullFileName ?? part.DisplayName;
                                parts[key] = part;
                                quantities[key] = quantities.TryGetValue(key, out int current) ? current + 1 : 1;
                            }
                            if (occ.SubOccurrences != null && occ.SubOccurrences.Count > 0)
                                WalkOccurrences(occ.SubOccurrences);
                        }
                        catch (Exception ex) { results.Add(Row(occ.Name, "error", ex.Message)); failed++; }
                    }
                }
                WalkOccurrences(asm.ComponentDefinition.Occurrences);
                foreach (var item in parts.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                    ProcessPart(item.Value, Path.GetFileNameWithoutExtension(item.Key), quantities[item.Key]);
            }
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "batch failed: " + ex.Message);
        }

        bool pass = failed == 0 && ok > 0;
        var manifest = new JObject
        {
            ["state"] = pass ? "complete" : "failed",
            ["completed_at_utc"] = DateTime.UtcNow.ToString("O"),
            ["pass"] = pass,
            ["output_dir"] = outputDir,
            ["ok"] = ok, ["skipped"] = skipped, ["failed"] = failed,
            ["parts"] = results,
            ["dxf_files"] = new JArray(results.OfType<JObject>()
                .Where(row => row.Value<string>("status") == "ok")
                .Select(row => Path.GetFileName(row.Value<string>("detail") ?? ""))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
        };
        try
        {
            File.WriteAllText(manifestPath, manifest.ToString());
            if (pass) File.Delete(incompletePath);
            else File.WriteAllText(incompletePath, manifest.ToString());
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "cannot finalize DXF batch manifest: " + ex.Message);
        }
        manifest["manifest"] = manifestPath;
        return Ok(ctx, manifest);
    }

    private static JObject Row(string part, string status, string detail) =>
        new() { ["part"] = part, ["status"] = status, ["detail"] = detail };

    private static string ReadIProperty(global::Inventor.Document document, string setName, string propertyName)
    {
        try { return Convert.ToString(document.PropertySets[setName][propertyName].Value) ?? ""; }
        catch { return ""; }
    }

    private static global::Inventor.Document? FindOpen(Application app, string path)
    {
        string fullPath = Path.GetFullPath(path);
        foreach (global::Inventor.Document document in app.Documents)
        {
            try
            {
                if (string.Equals(Path.GetFullPath(document.FullFileName), fullPath, StringComparison.OrdinalIgnoreCase))
                    return document;
            }
            catch { }
        }
        return null;
    }

    private static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Trim().TrimEnd('.');
        return s.Length <= 180 ? s : s.Substring(0, 180).TrimEnd();
    }
}
#endif
