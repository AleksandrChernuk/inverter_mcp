#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_inspect_sketch</c> — read-only look INSIDE one sketch of the active part: its points, lines, arcs,
/// circles (coords in mm + radial distance from the sketch origin) and a count of geometric constraints by
/// type. Purpose: decide whether a driving dimension (e.g. a slot-placement radius) can be added — that needs
/// a free degree of freedom and a target entity. Pass sketch=name (from vent_inspect_model).
/// </summary>
public sealed class SketchGeometryHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_inspect_sketch";
    public bool IsReadOnly => true;

    private const double CmToMm = 10.0;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? active;
        try { active = app.ActiveDocument; } catch { active = null; }
        if (active is null) return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active Inventor document");
        if (active is not PartDocument doc) return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE, "vent_inspect_sketch requires an active part document");

        var pdef = doc.ComponentDefinition as PartComponentDefinition;
        if (pdef is null) return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE, "part has no PartComponentDefinition");

        string? want = p.Value<string>("sketch");
        if (string.IsNullOrWhiteSpace(want)) return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "sketch name is required (see vent_inspect_model)");

        PlanarSketch? sk = null;
        try { foreach (PlanarSketch s in pdef.Sketches) { if (string.Equals(s.Name, want, StringComparison.OrdinalIgnoreCase)) { sk = s; break; } } }
        catch { }
        if (sk is null) return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, $"sketch '{want}' not found");

        static double R(double xcm, double ycm) => Math.Round(Math.Sqrt(xcm * xcm + ycm * ycm) * CmToMm, 2);
        static JObject XY(double xcm, double ycm) => new JObject { ["x_mm"] = Math.Round(xcm * CmToMm, 2), ["y_mm"] = Math.Round(ycm * CmToMm, 2), ["r_mm"] = R(xcm, ycm) };

        int cap = p.Value<int?>("max") ?? 60;

        var points = new JArray();
        try { foreach (SketchPoint pt in sk.SketchPoints) { var g = pt.Geometry; var o = XY(g.X, g.Y); o["construction"] = SafeBool(() => pt.Construction); points.Add(o); if (points.Count >= cap) break; } } catch { }

        var circles = new JArray();
        try { foreach (SketchCircle c in sk.SketchCircles) { var g = c.CenterSketchPoint.Geometry; var o = XY(g.X, g.Y); o["radius_mm"] = Math.Round(c.Radius * CmToMm, 2); o["construction"] = SafeBool(() => c.Construction); circles.Add(o); if (circles.Count >= cap) break; } } catch { }

        var arcs = new JArray();
        try { foreach (SketchArc a in sk.SketchArcs) { var g = a.CenterSketchPoint.Geometry; var o = XY(g.X, g.Y); o["radius_mm"] = Math.Round(a.Radius * CmToMm, 2); o["construction"] = SafeBool(() => a.Construction); arcs.Add(o); if (arcs.Count >= cap) break; } } catch { }

        int lineCount = 0; try { lineCount = sk.SketchLines.Count; } catch { }

        // geometric constraints grouped by type
        var consByType = new System.Collections.Generic.Dictionary<string, int>();
        int consTotal = 0;
        try
        {
            foreach (GeometricConstraint gc in sk.GeometricConstraints)
            {
                consTotal++;
                string t; try { t = ShortCon(gc.Type.ToString()); } catch { t = "?"; }
                consByType[t] = consByType.TryGetValue(t, out var n) ? n + 1 : 1;
            }
        }
        catch { }
        var cons = new JObject();
        foreach (var kv in consByType) cons[kv.Key] = kv.Value;

        return Ok(ctx, new JObject
        {
            ["part"] = doc.DisplayName,
            ["sketch"] = sk.Name,
            ["points"] = points,
            ["circles"] = circles,
            ["arcs"] = arcs,
            ["line_count"] = lineCount,
            ["geometric_constraint_count"] = consTotal,
            ["geometric_constraints_by_type"] = cons,
            ["note"] = "r_mm — расстояние от центра эскиза (кандидат в «радиус пазов»). Много геометрических связей + 0 размеров ⇒ эскиз, скорее всего, полностью связан, и добавить драйв-размер без снятия связи нельзя.",
        });
    }

    private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }

    private static string ShortCon(string t)
    {
        if (t.StartsWith("k")) t = t.Substring(1);
        if (t.EndsWith("ConstraintObject")) t = t.Substring(0, t.Length - "ConstraintObject".Length);
        else if (t.EndsWith("Object")) t = t.Substring(0, t.Length - "Object".Length);
        return t;
    }
}
#endif
