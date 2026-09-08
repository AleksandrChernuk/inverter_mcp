#if INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Collections.Generic;
using System.IO;
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
        try { Directory.CreateDirectory(outputDir); } catch { }

        string? partsDir = (p["parts_dir"]?.Type == JTokenType.String) ? (string)p["parts_dir"]! : null;

        var results = new JArray();
        int ok = 0, skipped = 0, failed = 0;

        void ProcessPart(PartDocument part, string label)
        {
            try
            {
                if (part.ComponentDefinition is not SheetMetalComponentDefinition sm)
                { results.Add(Row(label, "skipped", "не листовая")); skipped++; return; }
                if (!sm.HasFlatPattern)
                {
                    try { sm.Unfold(); } catch { }
                }
                if (!sm.HasFlatPattern)
                { results.Add(Row(label, "skipped", "нет flat pattern")); skipped++; return; }

                string outPath = Path.Combine(outputDir, SafeName(label) + ".dxf");
                sm.FlatPattern.DataIO.WriteDataToFile(FlatPatternDxfFormat, outPath);
                results.Add(Row(label, "ok", outPath)); ok++;
            }
            catch (Exception ex) { results.Add(Row(label, "error", ex.Message)); failed++; }
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(partsDir))
            {
                if (!Directory.Exists(partsDir))
                    return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "parts_dir not found: " + partsDir);
                foreach (var f in Directory.GetFiles(partsDir, "*.ipt", SearchOption.TopDirectoryOnly))
                {
                    global::Inventor.Document? d = null;
                    try { d = app.Documents.Open(f, false); ProcessPart((PartDocument)d, Path.GetFileNameWithoutExtension(f)); }
                    catch (Exception ex) { results.Add(Row(Path.GetFileNameWithoutExtension(f), "error", ex.Message)); failed++; }
                    finally { try { d?.Close(true); } catch { } }
                }
            }
            else
            {
                global::Inventor.Document? active;
                try { active = app.ActiveDocument; } catch { active = null; }
                if (active is not AssemblyDocument asm)
                    return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE,
                        "with no parts_dir, the active document must be an assembly");

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ComponentOccurrence occ in asm.ComponentDefinition.Occurrences)
                {
                    try
                    {
                        if (occ.Definition.Document is PartDocument part)
                        {
                            string key = part.FullFileName ?? part.DisplayName;
                            if (!seen.Add(key)) continue; // unique parts only
                            ProcessPart(part, Path.GetFileNameWithoutExtension(key));
                        }
                    }
                    catch (Exception ex) { results.Add(Row(occ.Name, "error", ex.Message)); failed++; }
                }
            }
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "batch failed: " + ex.Message);
        }

        return Ok(ctx, new JObject
        {
            ["output_dir"] = outputDir,
            ["ok"] = ok, ["skipped"] = skipped, ["failed"] = failed,
            ["parts"] = results,
        });
    }

    private static JObject Row(string part, string status, string detail) =>
        new() { ["part"] = part, ["status"] = status, ["detail"] = detail };

    private static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
#endif
