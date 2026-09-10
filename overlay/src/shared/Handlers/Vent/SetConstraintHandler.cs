#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_set_constraint</c> — assembly editor (part 2). In the ACTIVE assembly, change the driving value of a
/// named constraint — the offset of a mate/flush/insert, or the angle of an angle constraint — then rebuild and
/// report the assembly's new bounding box (mm) and mass (g). Constraint names come from inventor_list_constraints
/// (e.g. "Заподлицо:9", "Совмещение:3"). Value is an expression like "5 mm" or "30 deg". Not saved to disk.
/// </summary>
public sealed class SetConstraintHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_set_constraint";
    public bool IsReadOnly => false;

    private const double CmToMm = 10.0;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? active;
        try { active = app.ActiveDocument; } catch { active = null; }
        if (active is null)
            return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active Inventor document");
        if (active is not AssemblyDocument asm)
            return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE, "vent_set_constraint requires an active assembly document");

        string? cname = p.Value<string>("name");
        string? value = p.Value<string>("value");
        if (string.IsNullOrWhiteSpace(cname))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "name is required (constraint name, e.g. from inventor_list_constraints)");
        if (string.IsNullOrWhiteSpace(value))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "value is required (e.g. '5 mm' or '30 deg')");

        var asmDef = (AssemblyComponentDefinition)asm.ComponentDefinition;

        AssemblyConstraint? found = null;
        try
        {
            foreach (AssemblyConstraint c in asmDef.Constraints)
            {
                string cn; try { cn = c.Name; } catch { continue; }
                if (string.Equals(cn, cname, StringComparison.OrdinalIgnoreCase)) { found = c; break; }
            }
        }
        catch { /* ignore */ }
        if (found is null)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, $"constraint '{cname}' not found (use inventor_list_constraints)");

        // pick the driving parameter: offset for mate/flush/insert, angle for angle constraint
        Parameter? drive = null;
        string kind = "?";
        try
        {
            if (found is MateConstraint mc) { drive = mc.Offset; kind = "mate.offset"; }
            else if (found is FlushConstraint fc) { drive = fc.Offset; kind = "flush.offset"; }
            else if (found is AngleConstraint ac) { drive = ac.Angle; kind = "angle.angle"; }
        }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, $"cannot read constraint driver: {ex.Message}"); }
        if (drive is null)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, $"constraint '{cname}' has no editable offset/angle (type not supported)");

        string oldExpr; try { oldExpr = drive.Expression; } catch { oldExpr = "?"; }
        try { drive.Expression = value; }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, $"failed to set constraint '{cname}': {ex.Message}"); }

        try { asm.Update(); }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, $"set ok but assembly rebuild failed: {ex.Message}"); }

        string newExpr; try { newExpr = drive.Expression; } catch { newExpr = value; }

        JObject? bboxMm = null;
        try
        {
            Box b = asmDef.RangeBox;
            bboxMm = new JObject
            {
                ["x_mm"] = Math.Round((b.MaxPoint.X - b.MinPoint.X) * CmToMm, 2),
                ["y_mm"] = Math.Round((b.MaxPoint.Y - b.MinPoint.Y) * CmToMm, 2),
                ["z_mm"] = Math.Round((b.MaxPoint.Z - b.MinPoint.Z) * CmToMm, 2),
            };
        }
        catch { /* ignore */ }

        double? massG = null;
        try { massG = Math.Round(asmDef.MassProperties.Mass * 1000.0, 1); } catch { /* ignore */ }

        return Ok(ctx, new JObject
        {
            ["assembly"] = asm.DisplayName,
            ["constraint"] = found.Name,
            ["driver"] = kind,
            ["old_expression"] = oldExpr,
            ["new_expression"] = newExpr,
            ["assembly_bounding_box_mm"] = bboxMm,
            ["assembly_mass_g"] = massG,
            ["note"] = "На диск НЕ сохранено — save_document при необходимости.",
        });
    }
}
#endif
