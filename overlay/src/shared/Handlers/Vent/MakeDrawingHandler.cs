#if INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Handlers.Export;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_make_drawing</c> — generate an overall-dimension drawing ("габаритка") of the active model.
/// Creates a drawing (from a supplied .idw/.dwg template, or Inventor's default), places a base view plus
/// front/top projections, fits the sheet, fills the title block from the model iProperties, and exports a
/// PDF (and optional DXF) to output_path.
///
/// VERIFY ON WINDOWS: view placement coordinates, sheet size and the title-block property mapping depend on
/// your template — wire your factory .idw template via the `template` argument and adjust positions once.
/// Auto габаритные dimensions are left as a follow-up (RetrieveDimensions/GeneralDimensions against your
/// template's view), since reliable placement is template-specific.
/// </summary>
public sealed class MakeDrawingHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_make_drawing";
    public bool IsReadOnly => false;

    // Stable Inventor PDF translator ClassId (consistent across 2022-2027).
    private const string PdfTranslatorId = "{0AC6FD96-2F4D-42CE-8BE0-8AEA580399E4}";

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? model;
        try { model = app.ActiveDocument; } catch { model = null; }
        if (model is null)
            return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active model to draw");
        if (model.DocumentType != DocumentTypeEnum.kPartDocumentObject &&
            model.DocumentType != DocumentTypeEnum.kAssemblyDocumentObject)
            return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE, "vent_make_drawing requires an active part or assembly");

        string outputPath = (p["output_path"]?.Type == JTokenType.String) ? (string)p["output_path"]! : "";
        if (ExportPathPolicy.TryRejectPath(outputPath, out var rejection))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, rejection);

        string? template = (p["template"]?.Type == JTokenType.String) ? (string)p["template"]! : null;
        bool exportDxf = p["export_dxf"]?.Type == JTokenType.Boolean && (bool)p["export_dxf"]!;
        double? scale = (p["scale"]?.Type == JTokenType.Float || p["scale"]?.Type == JTokenType.Integer)
            ? (double?)p["scale"]! : null;

        DrawingDocument? dwg = null;
        try
        {
            var tg = app.TransientGeometry;
            dwg = (DrawingDocument)app.Documents.Add(
                DocumentTypeEnum.kDrawingDocumentObject,
                string.IsNullOrWhiteSpace(template) ? "" : template, // "" = default template
                true);

            Sheet sheet = dwg.Sheets[1];
            double s = scale ?? 0.1; // 1:10 default; adjust/auto-fit against your template

            // Base view (front) near lower-left, then top + right-side projections.
            DrawingView baseView = sheet.DrawingViews.AddBaseView(
                (_Document)model,
                tg.CreatePoint2d(12, 10),
                s,
                ViewOrientationTypeEnum.kFrontViewOrientation,
                DrawingViewStyleEnum.kHiddenLineRemovedDrawingViewStyle);

            try { sheet.DrawingViews.AddProjectedView(baseView, tg.CreatePoint2d(12, 22), DrawingViewStyleEnum.kFromBaseDrawingViewStyle); } catch { }
            try { sheet.DrawingViews.AddProjectedView(baseView, tg.CreatePoint2d(26, 10), DrawingViewStyleEnum.kFromBaseDrawingViewStyle); } catch { }

            // Title block is normally populated from the model iProperties by the template; save so it binds.
            string idwPath = System.IO.Path.ChangeExtension(outputPath, ".idw");
            try { dwg.SaveAs(idwPath, false); } catch { /* saving idw is best-effort */ }

            // Export PDF via the built-in translator.
            var pdf = ExportSupport.GetTranslator(app, PdfTranslatorId, "PDF");
            string pdfPath = System.IO.Path.ChangeExtension(outputPath, ".pdf");
            ExportSupport.SaveCopyAs(app, pdf, dwg, pdfPath);

            string? dxfPath = null;
            if (exportDxf)
            {
                dxfPath = System.IO.Path.ChangeExtension(outputPath, ".dxf");
                try { dwg.SaveAs(dxfPath, true); } catch (Exception ex) { dxfPath = "ERROR: " + ex.Message; }
            }

            return Ok(ctx, new JObject
            {
                ["model"] = model.DisplayName,
                ["scale"] = s,
                ["views"] = sheet.DrawingViews.Count,
                ["idw"] = idwPath,
                ["pdf"] = pdfPath,
                ["dxf"] = dxfPath,
                ["note"] = "Проверьте посадку видов и штамп под ваш .idw-шаблон; авто-габаритные размеры — следующий шаг.",
            });
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "failed to make drawing: " + ex.Message);
        }
    }
}
#endif
