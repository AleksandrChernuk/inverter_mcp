#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_drive_dimension</c> — set a model/user parameter (incl. a named sketch dimension like d0) by
/// name on the active PART, rebuild, and report the resulting overall bounding box (mm) and mass (g),
/// plus the old/new expression. The companion to <c>vent_inspect_model</c>: inspect to find the driver,
/// drive it here and see the size/mass effect at once. Lengths returned in millimetres.
/// </summary>
public sealed class DriveDimensionHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_drive_dimension";
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
            return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE, "vent_drive_dimension requires an active part document");

        string? name = p.Value<string>("name");
        string? value = p.Value<string>("value");
        if (string.IsNullOrWhiteSpace(name))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "name is required (parameter/dimension name, e.g. 'd0')");
        if (string.IsNullOrWhiteSpace(value))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "value is required (e.g. '340 mm')");

        var def = doc.ComponentDefinition;

        Parameter? prm = null;
        try { prm = def.Parameters[name]; } catch { prm = null; }
        if (prm is null)
        {
            try
            {
                foreach (Parameter pr in def.Parameters)
                    if (string.Equals(pr.Name, name, StringComparison.OrdinalIgnoreCase)) { prm = pr; break; }
            }
            catch { /* ignore */ }
        }
        if (prm is null)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                $"parameter '{name}' not found (use vent_inspect_model / list_parameters to see names)");

        string oldExpr;
        try { oldExpr = prm.Expression; } catch { oldExpr = "?"; }

        try { prm.Expression = value; }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, $"failed to set '{name}': {ex.Message}"); }

        try { doc.Update(); }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, $"set ok but rebuild failed: {ex.Message}"); }

        string newExpr; try { newExpr = prm.Expression; } catch { newExpr = value; }

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
        try { massG = Math.Round(def.MassProperties.Mass * 1000.0, 1); } catch { /* mass may be unavailable */ }

        return Ok(ctx, new JObject
        {
            ["part"] = doc.DisplayName,
            ["parameter"] = prm.Name,
            ["old_expression"] = oldExpr,
            ["new_expression"] = newExpr,
            ["bounding_box_mm"] = bboxMm,
            ["mass_g"] = massG,
            ["note"] = "Изменение НЕ сохранено на диск — сохраните через save_document, если нужно оставить.",
        });
    }
}
#endif
