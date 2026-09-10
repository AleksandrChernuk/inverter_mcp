#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_set_component_parameter</c> — assembly editor. In the ACTIVE assembly, set a parameter of a named
/// component (occurrence) by name+value, rebuild, and report the assembly's new bounding box (mm) and mass (g).
/// Lets you drive a part's dimensions from the top assembly without opening each part separately. The change
/// applies to the referenced part document (shared by all its instances) and is NOT saved to disk.
/// Get occurrence names from inventor_get_assembly_bom (e.g. "КВЗ...Диск нижній:1"); the ":N" suffix is optional.
/// </summary>
public sealed class SetComponentParameterHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_set_component_parameter";
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
            return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE, "vent_set_component_parameter requires an active assembly document");

        string? occName = p.Value<string>("occurrence");
        string? name = p.Value<string>("name");
        string? value = p.Value<string>("value");
        if (string.IsNullOrWhiteSpace(occName))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "occurrence is required (component name, e.g. from get_assembly_bom)");
        if (string.IsNullOrWhiteSpace(name))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "name is required (parameter name)");
        if (string.IsNullOrWhiteSpace(value))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "value is required (e.g. '340 mm')");

        var asmDef = (AssemblyComponentDefinition)asm.ComponentDefinition;

        // find the occurrence by exact name, else by name without the ":N" instance suffix
        ComponentOccurrence? occ = FindOccurrence(asmDef.Occurrences, occName!, out string? occurrenceError);
        if (occ is null)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                occurrenceError ?? $"component '{occName}' not found (use inventor_get_assembly_bom to list names)");

        ComponentDefinition compDef;
        try { compDef = occ.Definition; }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, $"cannot access component definition: {ex.Message}"); }

        // the base ComponentDefinition interface has no .Parameters — resolve the concrete type
        global::Inventor.Parameters? prms = null;
        var pcd = compDef as PartComponentDefinition;
        if (pcd != null) prms = pcd.Parameters;
        else { var acd = compDef as AssemblyComponentDefinition; if (acd != null) prms = acd.Parameters; }
        if (prms is null)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "component type has no editable parameters");
        global::Inventor.Parameters pset = prms;

        Parameter? prm = null;
        try { prm = pset[name]; } catch { prm = null; }
        if (prm is null)
        {
            try
            {
                foreach (Parameter pr in pset)
                    if (string.Equals(pr.Name, name, StringComparison.OrdinalIgnoreCase)) { prm = pr; break; }
            }
            catch { /* ignore */ }
        }
        if (prm is null)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                $"parameter '{name}' not found on component '{occName}'");

        string oldExpr; try { oldExpr = prm.Expression; } catch { oldExpr = "?"; }
        try { prm.Expression = value; }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, $"failed to set '{name}': {ex.Message}"); }

        try { asm.Update(); }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, $"set ok but assembly rebuild failed: {ex.Message}"); }

        string newExpr; try { newExpr = prm.Expression; } catch { newExpr = value; }

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
            ["component"] = occ.Name,
            ["parameter"] = prm.Name,
            ["old_expression"] = oldExpr,
            ["new_expression"] = newExpr,
            ["assembly_bounding_box_mm"] = bboxMm,
            ["assembly_mass_g"] = massG,
            ["note"] = "Меняется общий файл детали (все её экземпляры). На диск НЕ сохранено — save_document при необходимости.",
        });
    }

    private static ComponentOccurrence? FindOccurrence(
        System.Collections.IEnumerable occurrences,
        string wanted,
        out string? error)
    {
        error = null;
        string[] path = wanted.Split(new[] { '/', '>' }, StringSplitOptions.RemoveEmptyEntries);
        if (path.Length > 1)
        {
            System.Collections.IEnumerable level = occurrences;
            ComponentOccurrence? current = null;
            foreach (string segment in path)
            {
                var matches = DirectMatches(level, segment);
                if (matches.Count != 1)
                {
                    error = $"component path segment '{segment}' matched {matches.Count} occurrences";
                    return null;
                }
                current = matches[0];
                level = current.SubOccurrences;
            }
            return current;
        }

        var exact = new System.Collections.Generic.List<ComponentOccurrence>();
        var byBase = new System.Collections.Generic.List<ComponentOccurrence>();
        CollectMatches(occurrences, wanted, exact, byBase);
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1)
        {
            error = $"component '{wanted}' is ambiguous ({exact.Count} matches); use a '/' occurrence path";
            return null;
        }
        if (byBase.Count == 1) return byBase[0];
        error = byBase.Count > 1
            ? $"component '{wanted}' is ambiguous ({byBase.Count} base-name matches); use exact ':N' names and a '/' path"
            : $"component '{wanted}' not found (use inventor_get_assembly_bom to list names)";
        return null;
    }

    private static System.Collections.Generic.List<ComponentOccurrence> DirectMatches(
        System.Collections.IEnumerable occurrences, string wanted)
    {
        var exact = new System.Collections.Generic.List<ComponentOccurrence>();
        var byBase = new System.Collections.Generic.List<ComponentOccurrence>();
        foreach (ComponentOccurrence occurrence in occurrences)
        {
            string occurrenceName;
            try { occurrenceName = occurrence.Name; } catch { continue; }
            if (string.Equals(occurrenceName, wanted, StringComparison.OrdinalIgnoreCase)) exact.Add(occurrence);
            else if (string.Equals(StripInstance(occurrenceName), StripInstance(wanted), StringComparison.OrdinalIgnoreCase)) byBase.Add(occurrence);
        }
        return exact.Count > 0 ? exact : byBase;
    }

    private static void CollectMatches(
        System.Collections.IEnumerable occurrences,
        string wanted,
        System.Collections.Generic.List<ComponentOccurrence> exact,
        System.Collections.Generic.List<ComponentOccurrence> byBase)
    {
        foreach (ComponentOccurrence occurrence in occurrences)
        {
            string occurrenceName;
            try { occurrenceName = occurrence.Name; } catch { continue; }
            if (string.Equals(occurrenceName, wanted, StringComparison.OrdinalIgnoreCase)) exact.Add(occurrence);
            else if (string.Equals(StripInstance(occurrenceName), StripInstance(wanted), StringComparison.OrdinalIgnoreCase)) byBase.Add(occurrence);
            try
            {
                if (occurrence.SubOccurrences != null && occurrence.SubOccurrences.Count > 0)
                    CollectMatches(occurrence.SubOccurrences, wanted, exact, byBase);
            }
            catch { }
        }
    }

    private static string StripInstance(string n)
    {
        int i = n.LastIndexOf(':');
        return i > 0 ? n.Substring(0, i) : n;
    }
}
#endif
