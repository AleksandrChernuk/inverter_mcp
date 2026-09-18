#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Globalization;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_scale_part</c> — uniformly scale the active PART by a factor. Multiplies every INDEPENDENT
/// length dimension (numeric model/user parameters) by <c>factor</c>, leaving angles, counts,
/// sheet-metal reference parameters and (optionally) the sheet thickness untouched. Derived parameters
/// (expressions that reference other parameters, e.g. <c>d35 = d34*0,8</c>) follow automatically.
///
/// Purpose: enlarge a whole part PROPORTIONALLY so mating features stay aligned (e.g. the blade seat vs
/// the lower-disk slot). Driving a single diameter scales only that feature and breaks the fit; scaling
/// every independent length keeps the geometry similar, so a wheel scaled part-by-part still assembles.
/// Not saved to disk — call save_document to keep the result.
/// </summary>
public sealed class ScalePartHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_scale_part";
    public bool IsReadOnly => false;

    private const double CmToMm = 10.0;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? active;
        try { active = app.ActiveDocument; } catch { active = null; }
        if (active is null)
            return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active Inventor document");
        if (active is not PartDocument doc)
            return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE, "vent_scale_part requires an active part document");

        double factor = p.Value<double?>("factor") ?? 0.0;
        if (factor <= 0.0)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "factor is required and must be > 0 (e.g. 1.2 for +20%)");
        bool preserveThickness = p.Value<bool?>("preserve_thickness") ?? true;

        // Guardrail (copy-before-resize), identical policy to vent_drive_dimension.
        if (!(p.Value<bool?>("allow_in_place") ?? false))
        {
            string partPath = ""; try { partPath = doc.FullFileName; } catch { }
            if (!string.IsNullOrWhiteSpace(partPath) && ExportPathPolicy.TryRejectPath(partPath, out _))
                return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                    "деталь в защищённом/возможно общем расположении (мастер-каталог): масштабирование здесь поменяло бы её во ВСЕХ изделиях. " +
                    "Сначала склонируйте изделие (vent_new_product / vent_clone_recode_product) и меняйте копию, " +
                    "либо передайте allow_in_place=true, если деталь уникальна для одного изделия.");
        }

        var def = doc.ComponentDefinition;

        var scaled = new JArray();
        int skipped = 0;
        try
        {
            foreach (Parameter prm in def.Parameters)
            {
                string pname, units, expr, ptype;
                try { pname = prm.Name; } catch { continue; }
                try { units = System.Convert.ToString(prm.get_Units()) ?? ""; } catch { units = ""; }
                try { expr = prm.Expression; } catch { expr = ""; }
                try { ptype = prm.ParameterType.ToString(); } catch { ptype = ""; }

                if (!IsLength(units)) continue;                                   // angles / counts
                if (ptype.IndexOf("Reference", StringComparison.OrdinalIgnoreCase) >= 0) continue; // sheet-metal rule params
                if (preserveThickness && IsThickness(pname)) { skipped++; continue; }
                if (!IsNumericExpression(expr)) continue;                          // derived → follows automatically

                double before;
                try { before = (double)prm.Value; } catch { continue; }
                try { prm.Value = before * factor; } catch { skipped++; continue; }
                scaled.Add(new JObject
                {
                    ["name"] = pname,
                    ["units"] = units,
                    ["from_mm"] = Math.Round(before * CmToMm, 3),
                    ["to_mm"] = Math.Round(before * factor * CmToMm, 3),
                });
            }
        }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, $"scale failed: {ex.Message}"); }

        try { doc.Update(); }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, $"scaled but rebuild failed: {ex.Message}"); }

        JObject? bboxMm = null;
        try
        {
            Box b = def.RangeBox;
            bboxMm = new JObject
            {
                ["x_mm"] = Math.Round((b.MaxPoint.X - b.MinPoint.X) * CmToMm, 2),
                ["y_mm"] = Math.Round((b.MaxPoint.Y - b.MinPoint.Y) * CmToMm, 2),
                ["z_mm"] = Math.Round((b.MaxPoint.Z - b.MinPoint.Z) * CmToMm, 2),
            };
        }
        catch { /* ignore */ }

        double? massG = null;
        try { massG = Math.Round(def.MassProperties.Mass * 1000.0, 1); } catch { /* ignore */ }

        return Ok(ctx, new JObject
        {
            ["part"] = doc.DisplayName,
            ["factor"] = factor,
            ["scaled_count"] = scaled.Count,
            ["skipped_count"] = skipped,
            ["preserved_thickness"] = preserveThickness,
            ["scaled_parameters"] = scaled,
            ["bounding_box_mm"] = bboxMm,
            ["mass_g"] = massG,
            ["note"] = "Независимые длиновые размеры умножены на factor; углы/кол-во/reference-параметры и (опц.) толщина не тронуты; производные выражения пересчитались сами. НЕ сохранено на диск — save_document для записи.",
        });
    }

    private static bool IsLength(string units)
    {
        if (string.IsNullOrWhiteSpace(units)) return false;
        string u = units.Trim().ToLowerInvariant();
        if (u.Contains("deg") || u.Contains("rad") || u.Contains("град") || u == "ul" || u.Contains("бр")) return false;
        return u.Contains("mm") || u.Contains("cm") || u.Contains("мм") || u.Contains("см")
            || u == "m" || u == "м" || u.Contains("in") || u.Contains("ft");
    }

    private static bool IsThickness(string name)
    {
        string n = name.ToLowerInvariant();
        return n.Contains("толщ") || n.Contains("thick");
    }

    private static bool IsNumericExpression(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return false;
        string e = expr;
        foreach (var u in new[] { "mm", "cm", "мм", "см", "deg", "град", "rad", "ul", "бр", "in", "ft" })
            e = e.Replace(u, "");
        e = e.Replace(" ", "").Replace(",", ".");
        return double.TryParse(e, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
    }
}
#endif
