#if INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Collections.Generic;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_bom_report</c> — material/cut report ("спецификация") for the active assembly. Walks the
/// assembly occurrences, and per unique part reports designation (display name), material, sheet
/// thickness, quantity, and flat-pattern size. Totals quantity by thickness. Read-only.
///
/// Cut length / area per part are best measured on the exported flat DXF (dxf_tools bom); this handler
/// gives the model-side rollup (material + thickness + qty + flat size).
/// </summary>
public sealed class BomReportHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_bom_report";
    public bool IsReadOnly => true;

    private const double CmToMm = 10.0;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? active;
        try { active = app.ActiveDocument; } catch { active = null; }
        if (active is not AssemblyDocument asm)
            return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE, "vent_bom_report requires an active assembly");

        var byKey = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        var qtyByThickness = new Dictionary<string, int>();

        try
        {
            foreach (ComponentOccurrence occ in asm.ComponentDefinition.Occurrences)
            {
                if (occ.Definition.Document is not PartDocument part) continue;
                string key = part.FullFileName ?? part.DisplayName;

                if (byKey.TryGetValue(key, out var existing))
                {
                    existing["qty"] = (int)existing["qty"]! + 1;
                }
                else
                {
                    double? thick = VentSupport.ThicknessMm(part);
                    double? fw = null, fh = null;
                    if (part.ComponentDefinition is SheetMetalComponentDefinition sm)
                    {
                        try
                        {
                            if (sm.HasFlatPattern)
                            {
                                Box b = sm.FlatPattern.RangeBox;
                                fw = Math.Round((b.MaxPoint.X - b.MinPoint.X) * CmToMm, 1);
                                fh = Math.Round((b.MaxPoint.Y - b.MinPoint.Y) * CmToMm, 1);
                            }
                        }
                        catch { }
                    }
                    byKey[key] = new JObject
                    {
                        ["part"] = part.DisplayName,
                        ["material"] = VentSupport.MaterialName(part),
                        ["thickness_mm"] = thick,
                        ["flat_width_mm"] = fw,
                        ["flat_height_mm"] = fh,
                        ["qty"] = 1,
                    };
                }

                var t = VentSupport.ThicknessMm(part);
                string tk = t.HasValue ? t.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "n/a";
                qtyByThickness[tk] = qtyByThickness.TryGetValue(tk, out var c) ? c + 1 : 1;
            }
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "bom failed: " + ex.Message);
        }

        var rows = new JArray();
        foreach (var v in byKey.Values) rows.Add(v);
        var totals = new JObject();
        foreach (var kv in qtyByThickness) totals[kv.Key] = kv.Value;

        return Ok(ctx, new JObject
        {
            ["assembly"] = asm.DisplayName,
            ["unique_parts"] = byKey.Count,
            ["rows"] = rows,
            ["qty_by_thickness_mm"] = totals,
        });
    }
}
#endif
