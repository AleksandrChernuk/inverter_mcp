#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Collections.Generic;
using System.IO;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_import_params</c> — Excel/CSV parameter table → model user parameters (method 3). Bring a shared
/// parameter table (same parameter names across a family) INTO the active part/assembly: for each row it
/// updates the matching user parameter's expression, or creates it via <c>UserParameters.AddByExpression</c>.
/// Source is an inline name→expression map ('parameters') and/or a 'csv' file with columns
/// name,expression[,units] (inline wins on conflicts). This is the reliable, headless half of способ 3
/// (native Inventor "linked spreadsheet" is a separate, UI-driven feature). Rebuilds and reports the changed
/// parameters + new bounding box (mm) and mass (g). NOT saved to disk. Guarded by copy-before-resize.
/// </summary>
public sealed class ImportParamsHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_import_params";
    public bool IsReadOnly => false;

    private const double CmToMm = 10.0;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? active;
        try { active = app.ActiveDocument; } catch { active = null; }
        if (active is null)
            return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active Inventor document");

        PartDocument? pd = active as PartDocument;
        AssemblyDocument? ad = active as AssemblyDocument;
        global::Inventor.Parameters? pset;
        if (pd != null) pset = ((PartComponentDefinition)pd.ComponentDefinition).Parameters;
        else if (ad != null) pset = ((AssemblyComponentDefinition)ad.ComponentDefinition).Parameters;
        else return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE, "vent_import_params требует активную деталь или сборку");

        string defaultUnits = p.Value<string>("default_units") ?? "ul";

        // Collect rows: name -> (expression, units). CSV first, inline map overrides.
        var rows = new Dictionary<string, (string expr, string units)>(StringComparer.OrdinalIgnoreCase);

        string? csv = p.Value<string>("csv");
        if (!string.IsNullOrWhiteSpace(csv))
        {
            if (!File.Exists(csv))
                return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "csv не найден: " + csv);
            string[] lines;
            try { lines = File.ReadAllLines(csv); }
            catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, "не удалось прочитать csv: " + ex.Message); }
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] c = line.Split(',');
                string name = c[0].Trim();
                if (name.Length == 0) continue;
                // skip a header row like "name,expression"
                if (i == 0 && name.Equals("name", StringComparison.OrdinalIgnoreCase)) continue;
                if (c.Length < 2) continue;
                string expr = c[1].Trim();
                string units = c.Length >= 3 && c[2].Trim().Length > 0 ? c[2].Trim() : defaultUnits;
                rows[name] = (expr, units);
            }
        }

        if (p["parameters"] is JObject inline)
        {
            foreach (var kv in inline)
            {
                string name = kv.Key.Trim();
                if (name.Length == 0 || kv.Value == null) continue;
                string expr = kv.Value.Type == JTokenType.String ? (kv.Value.Value<string>() ?? "") : kv.Value.ToString();
                string units = rows.TryGetValue(name, out var prev) ? prev.units : defaultUnits;
                rows[name] = (expr.Trim(), units);
            }
        }

        if (rows.Count == 0)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "нечего импортировать: передайте 'parameters' (карта имя→выражение) и/или 'csv' (name,expression[,units])");

        // Guardrail: changing parameters mutates the shared file.
        if (!(p.Value<bool?>("allow_in_place") ?? false))
        {
            string path = ""; try { path = active.FullFileName; } catch { }
            if (!string.IsNullOrWhiteSpace(path) && ExportPathPolicy.TryRejectPath(path, out _))
                return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                    "документ в защищённом/мастер-расположении: импорт параметров поменял бы общий файл. " +
                    "Склонируйте изделие или allow_in_place=true, если деталь продукт-уникальна.");
        }

        UserParameters ups = pset!.UserParameters;
        var changed = new JArray();
        var errors = new JArray();
        foreach (var kv in rows)
        {
            string name = kv.Key; string expr = kv.Value.expr; string units = kv.Value.units;
            try
            {
                Parameter? existing = null;
                try { existing = pset![name]; } catch { existing = null; }
                if (existing != null)
                {
                    string old; try { old = existing.Expression; } catch { old = "?"; }
                    existing.Expression = expr;
                    changed.Add(new JObject { ["name"] = name, ["old_expression"] = old, ["new_expression"] = expr, ["action"] = "update" });
                }
                else
                {
                    Parameter np = ups.AddByExpression(name, expr, units);
                    changed.Add(new JObject { ["name"] = np.Name, ["new_expression"] = expr, ["units"] = units, ["action"] = "add" });
                }
            }
            catch (Exception ex)
            {
                errors.Add(new JObject { ["name"] = name, ["expression"] = expr, ["units"] = units, ["error"] = ex.Message });
            }
        }

        if (changed.Count == 0)
            return Fail(ctx, InventorErrorCodes.API_ERROR,
                "ни один параметр не импортирован (см. errors). Проверьте имена/единицы (units напр. \"mm\", \"deg\", \"ul\").");

        // Rebuild + report.
        try { if (pd != null) pd.Update(); else ad!.Update(); }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, $"параметры записаны, но перестроение не удалось: {ex.Message}"); }

        JObject? bboxMm = null; double? massG = null;
        try
        {
            Box b = pd != null ? ((PartComponentDefinition)pd.ComponentDefinition).RangeBox
                               : ((AssemblyComponentDefinition)ad!.ComponentDefinition).RangeBox;
            bboxMm = new JObject
            {
                ["x_mm"] = Math.Round((b.MaxPoint.X - b.MinPoint.X) * CmToMm, 2),
                ["y_mm"] = Math.Round((b.MaxPoint.Y - b.MinPoint.Y) * CmToMm, 2),
                ["z_mm"] = Math.Round((b.MaxPoint.Z - b.MinPoint.Z) * CmToMm, 2),
            };
        }
        catch { /* ignore */ }
        try
        {
            massG = Math.Round((pd != null ? ((PartComponentDefinition)pd.ComponentDefinition).MassProperties.Mass
                                            : ((AssemblyComponentDefinition)ad!.ComponentDefinition).MassProperties.Mass) * 1000.0, 1);
        }
        catch { /* ignore */ }

        return Ok(ctx, new JObject
        {
            ["document"] = active.DisplayName,
            ["changed"] = changed,
            ["changed_count"] = changed.Count,
            ["errors"] = errors,
            ["bounding_box_mm"] = bboxMm,
            ["mass_g"] = massG,
            ["note"] = "Изменение НЕ сохранено на диск — save_document / vent_save_product при необходимости.",
        });
    }
}
#endif
