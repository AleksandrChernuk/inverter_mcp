#if INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Linq;
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
/// PDF (and optional DXF) to output_path. View locations are relative to the actual sheet, scale can be
/// calculated from model/sheet bounds, model dimensions are retrieved through GeneralDimensions.Retrieve,
/// assembly parts-list and balloons can be required as release gates, and technical notes are template inputs.
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
        bool retrieveDimensions = p["retrieve_dimensions"]?.Value<bool?>() ?? true;
        bool addOverallDimensions = p["add_overall_dimensions"]?.Value<bool?>() ?? true;
        bool requirePartsList = p["require_parts_list"]?.Value<bool?>() ?? false;
        bool requireBalloons = p["require_balloons"]?.Value<bool?>() ?? false;
        int minimumBalloons = p["minimum_balloons"]?.Value<int?>() ?? (requireBalloons ? 1 : 0);
        bool requireHoleTable = p["require_hole_table"]?.Value<bool?>() ?? false;
        int minimumDimensions = p["minimum_dimensions"]?.Value<int?>() ?? 0;
        string[] notes = p["technical_notes"] is JArray notesArray
            ? notesArray.Values<string>().Where(x => !string.IsNullOrWhiteSpace(x)).Take(64).ToArray()
            : Array.Empty<string>();
        JArray sections = p["section_views"] as JArray ?? new JArray();
        JArray details = p["detail_views"] as JArray ?? new JArray();
        if (sections.Count > 16 || details.Count > 32)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "at most 16 section and 32 detail views are allowed");
        if (minimumDimensions < 0 || minimumDimensions > 10000 || minimumBalloons < 0 || minimumBalloons > 10000)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "minimum_dimensions/minimum_balloons must be 0..10000");
        if ((requirePartsList || requireBalloons) && model is not AssemblyDocument)
            return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE,
                "parts list and balloons require an active assembly");

        DrawingDocument? dwg = null;
        try
        {
            var tg = app.TransientGeometry;
            dwg = (DrawingDocument)app.Documents.Add(
                DocumentTypeEnum.kDrawingDocumentObject,
                string.IsNullOrWhiteSpace(template) ? "" : template, // "" = default template
                true);

            Sheet sheet = dwg.Sheets[1];
            double s = scale ?? AutoScale(model, sheet);

            // Relative sheet placement works for A4..A0 and custom factory templates.
            DrawingView baseView = sheet.DrawingViews.AddBaseView(
                (_Document)model,
                tg.CreatePoint2d(sheet.Width * 0.28, sheet.Height * 0.34),
                s,
                ViewOrientationTypeEnum.kFrontViewOrientation,
                DrawingViewStyleEnum.kHiddenLineRemovedDrawingViewStyle);

            sheet.DrawingViews.AddProjectedView(baseView, tg.CreatePoint2d(sheet.Width * 0.28, sheet.Height * 0.70), DrawingViewStyleEnum.kFromBaseDrawingViewStyle);
            sheet.DrawingViews.AddProjectedView(baseView, tg.CreatePoint2d(sheet.Width * 0.61, sheet.Height * 0.34), DrawingViewStyleEnum.kFromBaseDrawingViewStyle);

            int sectionCount = 0;
            foreach (JObject section in sections.OfType<JObject>())
            {
                double[] start = Point2Mm(section["start_mm"], "section.start_mm");
                double[] end = Point2Mm(section["end_mm"], "section.end_mm");
                double[] position = Point2Mm(section["position_mm"], "section.position_mm");
                string name = section.Value<string>("name") ?? "";
                DrawingSketch lineSketch = baseView.Sketches.Add();
                lineSketch.Edit();
                lineSketch.SketchLines.AddByTwoPoints(
                    tg.CreatePoint2d(start[0] / 10.0, start[1] / 10.0),
                    tg.CreatePoint2d(end[0] / 10.0, end[1] / 10.0));
                lineSketch.ExitEdit();
                dynamic drawingViews = sheet.DrawingViews;
                drawingViews.AddSectionView(
                    baseView, lineSketch,
                    tg.CreatePoint2d(position[0] / 10.0, position[1] / 10.0),
                    DrawingViewStyleEnum.kHiddenLineRemovedDrawingViewStyle,
                    Type.Missing, true, name, true, true, Type.Missing);
                sectionCount++;
            }

            int detailCount = 0;
            foreach (JObject detail in details.OfType<JObject>())
            {
                double[] center = Point2Mm(detail["center_mm"], "detail.center_mm");
                double[] position = Point2Mm(detail["position_mm"], "detail.position_mm");
                double radius = detail.Value<double?>("radius_mm") ?? 0;
                double detailScale = detail.Value<double?>("scale") ?? s * 2;
                string name = detail.Value<string>("name") ?? "";
                if (radius <= 0 || detailScale <= 0)
                    throw new InvalidOperationException("detail radius_mm and scale must be positive");
                dynamic drawingViews = sheet.DrawingViews;
                drawingViews.AddDetailView(
                    baseView,
                    tg.CreatePoint2d(position[0] / 10.0, position[1] / 10.0),
                    DrawingViewStyleEnum.kFromBaseDrawingViewStyle,
                    true,
                    tg.CreatePoint2d(center[0] / 10.0, center[1] / 10.0),
                    radius / 10.0,
                    Type.Missing,
                    detailScale,
                    true,
                    name,
                    true);
                detailCount++;
            }

            int retrievedDimensionCount = 0;
            if (retrieveDimensions)
            {
                GeneralDimensionsEnumerator dimensions = sheet.DrawingDimensions.GeneralDimensions.Retrieve(baseView);
                retrievedDimensionCount = dimensions.Count;
            }
            int overallDimensionCount = addOverallDimensions ? AddOverallDimensions(sheet, baseView, tg) : 0;
            int dimensionCount = retrievedDimensionCount + overallDimensionCount;

            bool partsListCreated = false;
            int balloonCount = 0;
            var annotationWarnings = new JArray();
            if (model is AssemblyDocument assembly)
            {
                PartsList? partsList = null;
                if (requirePartsList || requireBalloons)
                {
                    Point2d placement = sheet.Border is not null
                        ? sheet.Border.RangeBox.MaxPoint
                        : tg.CreatePoint2d(sheet.Width, sheet.Height);
                    partsList = sheet.PartsLists.Add(baseView, placement);
                    partsListCreated = partsList is not null;
                }
                if (requireBalloons)
                {
                    int index = 0;
                    foreach (ComponentOccurrence occurrence in assembly.ComponentDefinition.Occurrences)
                    {
                        try
                        {
                            DrawingCurvesEnumerator curves = baseView.DrawingCurves[occurrence];
                            if (curves.Count < 1) throw new InvalidOperationException("no visible curve in base view");
                            DrawingCurve curve = curves[1];
                            Point2d? anchor = curve.CenterPoint;
                            if (anchor is null && curve.StartPoint is not null && curve.EndPoint is not null)
                                anchor = tg.CreatePoint2d(
                                    (curve.StartPoint.X + curve.EndPoint.X) / 2.0,
                                    (curve.StartPoint.Y + curve.EndPoint.Y) / 2.0);
                            if (anchor is null) throw new InvalidOperationException("curve has no usable anchor point");
                            double side = index++ % 2 == 0 ? -1 : 1;
                            var leader = app.TransientObjects.CreateObjectCollection();
                            leader.Add(tg.CreatePoint2d(anchor.X + side * 2.5, anchor.Y + 2.5));
                            leader.Add(tg.CreatePoint2d(anchor.X + side * 1.5, anchor.Y + 1.0));
                            leader.Add(sheet.CreateGeometryIntent(curve));
                            sheet.Balloons.Add(leader);
                            balloonCount++;
                        }
                        catch (Exception ex)
                        {
                            annotationWarnings.Add(new JObject
                            {
                                ["occurrence"] = occurrence.Name,
                                ["warning"] = ex.Message,
                            });
                        }
                    }
                }
            }

            if (notes.Length > 0)
            {
                string noteText = string.Join(Environment.NewLine, notes.Select((text, index) => $"{index + 1}. {text}"));
                sheet.DrawingNotes.GeneralNotes.AddFitted(
                    tg.CreatePoint2d(sheet.Width * 0.56, sheet.Height * 0.10), noteText);
            }

            bool holeTableCreated = false;
            if (requireHoleTable)
            {
                if (!baseView.HasOriginIndicator)
                {
                    DrawingCurve? originCurve = null;
                    foreach (DrawingCurve curve in baseView.DrawingCurves)
                    {
                        if (curve.StartPoint is not null) { originCurve = curve; break; }
                    }
                    if (originCurve is null)
                        throw new InvalidOperationException("cannot create a hole-table origin: no linear/arc curve is visible");
                    baseView.CreateOriginIndicator(sheet.CreateGeometryIntent(originCurve, PointIntentEnum.kStartPointIntent));
                }
                HoleTable holeTable = sheet.HoleTables.Add(
                    baseView, tg.CreatePoint2d(sheet.Width * 0.58, sheet.Height * 0.88));
                holeTableCreated = holeTable is not null && holeTable.HoleTableRows.Count > 0;
            }

            bool annotationsPass = dimensionCount >= minimumDimensions &&
                                   (!requirePartsList || partsListCreated) &&
                                   (!requireBalloons || balloonCount >= minimumBalloons) &&
                                   (!requireHoleTable || holeTableCreated);
            if (!annotationsPass)
                return Fail(ctx, InventorErrorCodes.API_ERROR,
                    $"drawing annotation gate failed: dimensions={dimensionCount}/{minimumDimensions}, " +
                    $"parts_list={partsListCreated}, balloons={balloonCount}/{minimumBalloons}, " +
                    $"hole_table={holeTableCreated}");

            // Title block is normally populated from the model iProperties by the template; save so it binds.
            string idwPath = System.IO.Path.ChangeExtension(outputPath, ".idw");
            string? directory = System.IO.Path.GetDirectoryName(idwPath);
            if (!string.IsNullOrWhiteSpace(directory)) System.IO.Directory.CreateDirectory(directory);
            dwg.SaveAs(idwPath, false);

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
                ["pass"] = true,
                ["scale"] = s,
                ["views"] = sheet.DrawingViews.Count,
                ["retrieved_dimensions"] = retrievedDimensionCount,
                ["overall_dimensions"] = overallDimensionCount,
                ["total_dimensions"] = dimensionCount,
                ["parts_list"] = partsListCreated,
                ["balloons"] = balloonCount,
                ["top_level_occurrences"] = assemblyOccurrenceCount(model),
                ["hole_table"] = holeTableCreated,
                ["technical_notes"] = notes.Length,
                ["section_views"] = sectionCount,
                ["detail_views"] = detailCount,
                ["annotation_warnings"] = annotationWarnings,
                ["idw"] = idwPath,
                ["pdf"] = pdfPath,
                ["dxf"] = dxfPath,
            });
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "failed to make drawing: " + ex.Message);
        }
        finally
        {
            // The output is already persisted; keep the source model active for the next batch stage.
            try { dwg?.Close(true); } catch { }
        }
    }

    private static double AutoScale(global::Inventor.Document model, Sheet sheet)
    {
        Box box = model is PartDocument part
            ? part.ComponentDefinition.RangeBox
            : ((AssemblyDocument)model).ComponentDefinition.RangeBox;
        double widthCm = Math.Max(0.001, box.MaxPoint.X - box.MinPoint.X);
        double heightCm = Math.Max(0.001, box.MaxPoint.Y - box.MinPoint.Y);
        double availableWidth = sheet.Width * 0.24;
        double availableHeight = sheet.Height * 0.24;
        double scale = Math.Min(availableWidth / widthCm, availableHeight / heightCm);
        return Math.Max(0.001, Math.Min(scale, 10.0));
    }

    private static int assemblyOccurrenceCount(global::Inventor.Document model)
    {
        return model is AssemblyDocument assembly ? assembly.ComponentDefinition.Occurrences.Count : 0;
    }

    private sealed class CurveEndpoint
    {
        public DrawingCurve Curve { get; set; } = null!;
        public PointIntentEnum Intent { get; set; }
        public Point2d Point { get; set; } = null!;
    }

    private static int AddOverallDimensions(Sheet sheet, DrawingView view, TransientGeometry tg)
    {
        var endpoints = new System.Collections.Generic.List<CurveEndpoint>();
        foreach (DrawingCurve curve in view.DrawingCurves)
        {
            if (curve.StartPoint is not null)
                endpoints.Add(new CurveEndpoint
                {
                    Curve = curve,
                    Intent = PointIntentEnum.kStartPointIntent,
                    Point = curve.StartPoint,
                });
            if (curve.EndPoint is not null)
                endpoints.Add(new CurveEndpoint
                {
                    Curve = curve,
                    Intent = PointIntentEnum.kEndPointIntent,
                    Point = curve.EndPoint,
                });
        }
        if (endpoints.Count < 2) return 0;

        CurveEndpoint left = endpoints.OrderBy(x => x.Point.X).First();
        CurveEndpoint right = endpoints.OrderByDescending(x => x.Point.X).First();
        CurveEndpoint bottom = endpoints.OrderBy(x => x.Point.Y).First();
        CurveEndpoint top = endpoints.OrderByDescending(x => x.Point.Y).First();
        GeneralDimensions general = sheet.DrawingDimensions.GeneralDimensions;
        int count = 0;

        if (right.Point.X - left.Point.X > 0.0001)
        {
            general.AddLinear(
                tg.CreatePoint2d((left.Point.X + right.Point.X) / 2.0, view.Bottom - 1.5),
                sheet.CreateGeometryIntent(left.Curve, left.Intent),
                sheet.CreateGeometryIntent(right.Curve, right.Intent),
                DimensionTypeEnum.kHorizontalDimensionType);
            count++;
        }
        if (top.Point.Y - bottom.Point.Y > 0.0001)
        {
            general.AddLinear(
                tg.CreatePoint2d(view.Left - 1.5, (bottom.Point.Y + top.Point.Y) / 2.0),
                sheet.CreateGeometryIntent(bottom.Curve, bottom.Intent),
                sheet.CreateGeometryIntent(top.Curve, top.Intent),
                DimensionTypeEnum.kVerticalDimensionType);
            count++;
        }
        return count;
    }

    private static double[] Point2Mm(JToken? token, string label)
    {
        if (token is not JArray array || array.Count != 2 ||
            array[0]?.Type is not (JTokenType.Float or JTokenType.Integer) ||
            array[1]?.Type is not (JTokenType.Float or JTokenType.Integer))
            throw new InvalidOperationException(label + " must be [x,y] in millimetres");
        return new[] { array[0]!.Value<double>(), array[1]!.Value<double>() };
    }
}
#endif
