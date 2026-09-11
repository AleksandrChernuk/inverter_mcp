#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_run_ilogic</c> — drive a size variant (типорозмір) the factory way (method 2): optionally set a
/// KEY parameter on the active part/assembly, then run an iLogic rule (by name, or all rules) that computes
/// the dependent parameters, rebuild, and report the resulting bounding box (mm) and mass (g). iLogic runs
/// late-bound (no assembly reference); requires the iLogic add-in to be loaded. Change is NOT saved to disk.
/// Guarded by copy-before-resize: refuses master-catalog files unless allow_in_place=true.
/// </summary>
public sealed class RunILogicHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_run_ilogic";
    public bool IsReadOnly => false;

    private const double CmToMm = 10.0;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? active;
        try { active = app.ActiveDocument; } catch { active = null; }
        if (active is null)
            return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active Inventor document");

        // Inventor 2021 COM interfaces do NOT inherit — resolve concrete definition types.
        PartComponentDefinition? pcd = null;
        AssemblyComponentDefinition? acd = null;
        global::Inventor.Parameters? pset = null;
        if (active is PartDocument pd) { pcd = (PartComponentDefinition)pd.ComponentDefinition; pset = pcd.Parameters; }
        else if (active is AssemblyDocument ad) { acd = (AssemblyComponentDefinition)ad.ComponentDefinition; pset = acd.Parameters; }
        else return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE, "vent_run_ilogic requires an active part or assembly document");

        string? rule = p.Value<string>("rule");
        bool runAll = p.Value<bool?>("run_all") ?? false;
        bool external = p.Value<bool?>("external") ?? false;
        string? setName = p.Value<string>("set_name");
        string? setValue = p.Value<string>("set_value");

        if (string.IsNullOrWhiteSpace(rule) && !runAll && string.IsNullOrWhiteSpace(setName))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "specify 'rule' (name), or run_all=true, or a 'set_name'+'set_value' to drive a key parameter");

        // Guardrail (copy-before-resize): running a rule / setting a parameter mutates the shared file.
        if (!(p.Value<bool?>("allow_in_place") ?? false))
        {
            string partPath = ""; try { partPath = active.FullFileName; } catch { }
            if (!string.IsNullOrWhiteSpace(partPath) && ExportPathPolicy.TryRejectPath(partPath, out _))
                return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                    "документ в защищённом/возможно общем расположении (мастер-каталог): прогон правила/смена параметра здесь " +
                    "поменяли бы деталь во ВСЕХ изделиях. Сначала склонируйте изделие (vent_new_product / " +
                    "vent_clone_recode_product) и меняйте копию, либо allow_in_place=true, если деталь продукт-уникальна.");
        }

        // 1) optionally set the key parameter first
        JObject? keyResult = null;
        if (!string.IsNullOrWhiteSpace(setName))
        {
            if (string.IsNullOrWhiteSpace(setValue))
                return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "set_value is required when set_name is given");
            Parameter? prm = null;
            try { prm = pset![setName]; } catch { prm = null; }
            if (prm is null)
                try { foreach (Parameter pr in pset!) if (string.Equals(pr.Name, setName, StringComparison.OrdinalIgnoreCase)) { prm = pr; break; } }
                catch { }
            if (prm is null)
                return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, $"key parameter '{setName}' not found (use vent_inspect_parametrization)");
            string oldExpr; try { oldExpr = prm.Expression; } catch { oldExpr = "?"; }
            try { prm.Expression = setValue!; }
            catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, $"failed to set key '{setName}': {ex.Message}"); }
            keyResult = new JObject { ["name"] = prm.Name, ["old_expression"] = oldExpr, ["new_expression"] = setValue };
        }

        // 2) run iLogic rule(s), if requested
        var rulesRun = new JArray();
        if (!string.IsNullOrWhiteSpace(rule) || runAll)
        {
            object? autom = VentSupport.GetILogicAutomation(app);
            if (autom is null)
                return Fail(ctx, InventorErrorCodes.API_ERROR, "iLogic add-in не загружен — правила недоступны (проверьте, что iLogic включён в Inventor)");

            var toRun = new List<string>();
            if (runAll)
            {
                try
                {
                    object? rules = autom.GetType().InvokeMember("Rules", BindingFlags.InvokeMethod, null, autom, new object[] { active });
                    if (rules is IEnumerable en)
                        foreach (object r in en) { try { toRun.Add((string)r.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, r, null)!); } catch { } }
                }
                catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, $"failed to list iLogic rules: {ex.Message}"); }
                if (toRun.Count == 0)
                    return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "run_all=true, но у документа нет iLogic-правил");
            }
            else
            {
                toRun.Add(rule!);
            }

            string method = external ? "RunExternalRule" : "RunRule";
            foreach (string rn in toRun)
            {
                try
                {
                    autom.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, autom, new object[] { active, rn });
                    rulesRun.Add(rn);
                }
                catch (Exception ex)
                {
                    return Fail(ctx, InventorErrorCodes.API_ERROR, $"iLogic rule '{rn}' failed: {ex.Message}");
                }
            }
        }

        // 3) rebuild + report (concrete types — no base ComponentDefinition members)
        try { if (pcd != null) ((PartDocument)active).Update(); else ((AssemblyDocument)active).Update(); }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, $"ran ok but rebuild failed: {ex.Message}"); }

        JObject? bboxMm = null;
        try
        {
            Box b = pcd != null ? pcd.RangeBox : acd!.RangeBox;
            bboxMm = new JObject
            {
                ["x_mm"] = Math.Round((b.MaxPoint.X - b.MinPoint.X) * CmToMm, 2),
                ["y_mm"] = Math.Round((b.MaxPoint.Y - b.MinPoint.Y) * CmToMm, 2),
                ["z_mm"] = Math.Round((b.MaxPoint.Z - b.MinPoint.Z) * CmToMm, 2),
            };
        }
        catch { /* ignore */ }

        double? massG = null;
        try { massG = Math.Round((pcd != null ? pcd.MassProperties : acd!.MassProperties).Mass * 1000.0, 1); } catch { /* ignore */ }

        return Ok(ctx, new JObject
        {
            ["document"] = active.DisplayName,
            ["key_parameter"] = keyResult,
            ["rules_run"] = rulesRun,
            ["bounding_box_mm"] = bboxMm,
            ["mass_g"] = massG,
            ["note"] = "Изменение НЕ сохранено на диск — save_document / vent_save_product при необходимости.",
        });
    }
}
#endif
