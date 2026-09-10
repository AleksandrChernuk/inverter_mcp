#if INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Handlers.Export;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>Batch PDF export of all IDW/DWG documents in one product tree.</summary>
public sealed class BatchPdfDrawingsHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_batch_pdf_drawings";
    public bool IsReadOnly => false;
    private const string PdfTranslatorId = "{0AC6FD96-2F4D-42CE-8BE0-8AEA580399E4}";
    private const int MaxDrawings = 256;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        string drawingsDir = FullPath(p["drawings_dir"]?.Value<string>() ?? "");
        string outputDir = FullPath(p["output_dir"]?.Value<string>() ?? "");
        bool recursive = p["recursive"]?.Value<bool?>() ?? true;
        if (!Directory.Exists(drawingsDir))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "drawings_dir does not exist");
        if (ExportPathPolicy.TryRejectPath(Path.Combine(outputDir, "_probe.pdf"), out var rejection))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, rejection);

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        string[] drawings;
        try
        {
            drawings = Directory.GetFiles(drawingsDir, "*.idw", option)
                .Concat(Directory.GetFiles(drawingsDir, "*.dwg", option))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "cannot enumerate drawings: " + ex.Message);
        }
        if (drawings.Length == 0 || drawings.Length > MaxDrawings)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                $"drawing count must be 1..{MaxDrawings}; found {drawings.Length}");

        var pdf = ExportSupport.GetTranslator(app, PdfTranslatorId, "PDF");
        var rows = new JArray();
        int ok = 0, failed = 0;
        Directory.CreateDirectory(outputDir);
        foreach (string drawingPath in drawings)
        {
            global::Inventor.Document? document = null;
            bool wasOpen = false;
            try
            {
                document = FindOpen(app, drawingPath);
                wasOpen = document is not null;
                document ??= app.Documents.Open(drawingPath, false);
                if (document is not DrawingDocument drawing)
                    throw new InvalidOperationException("document is not an Inventor drawing");
                if (drawing.Dirty)
                    throw new InvalidOperationException("save or discard drawing changes before batch PDF export");

                string relative = RelativeUnder(drawingPath, drawingsDir);
                string outputPath = Path.Combine(outputDir, Path.ChangeExtension(relative, ".pdf"));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                ExportSupport.SaveCopyAs(app, pdf, drawing, outputPath);
                rows.Add(new JObject { ["drawing"] = drawingPath, ["pdf"] = outputPath, ["status"] = "ok" });
                ok++;
            }
            catch (Exception ex)
            {
                rows.Add(new JObject { ["drawing"] = drawingPath, ["status"] = "error", ["error"] = ex.Message });
                failed++;
            }
            finally
            {
                if (!wasOpen) { try { document?.Close(true); } catch { } }
            }
        }

        return Ok(ctx, new JObject
        {
            ["pass"] = failed == 0,
            ["drawings_dir"] = drawingsDir,
            ["output_dir"] = outputDir,
            ["ok"] = ok,
            ["failed"] = failed,
            ["files"] = rows,
        });
    }

    private static global::Inventor.Document? FindOpen(Application app, string path)
    {
        foreach (global::Inventor.Document document in app.Documents)
        {
            try
            {
                if (string.Equals(FullPath(document.FullFileName), path, StringComparison.OrdinalIgnoreCase))
                    return document;
            }
            catch { }
        }
        return null;
    }

    private static string FullPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return Path.GetFullPath(value); } catch { return ""; }
    }

    private static string RelativeUnder(string value, string root)
    {
        string full = FullPath(value);
        string prefix = FullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(prefix + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("drawing is outside drawings_dir: " + full);
        return full.Substring(prefix.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
#endif
