#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_select_ipart_member</c> — drive a size variant (типорозмір) the factory way (method 1):
/// select a row of an iPart/iAssembly FACTORY by member name, by key-column values, or by 1-based row
/// index, and generate/activate that member. On a FACTORY document <c>CreateMember(row)</c> spawns the
/// concrete member (типорозмір); on a MEMBER document <c>ChangeRow</c> switches which row it represents.
/// Reports the selected row cells and the resulting bounding box (mm) + mass (g). Phase 2 of the
/// parametrization roadmap. Everything is late-bound (iPart types are awkward to strong-type) and each
/// step fails gracefully. Guarded by copy-before-resize: refuses master-catalog files unless
/// allow_in_place=true. Change is NOT saved to disk.
/// </summary>
public sealed class SelectIPartMemberHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_select_ipart_member";
    public bool IsReadOnly => false;

    private const double CmToMm = 10.0;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? active;
        try { active = app.ActiveDocument; } catch { active = null; }
        if (active is null)
            return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active Inventor document");

        // Resolve the iPart/iAssembly factory (or member) object of the active document.
        object? factory = null, member = null;
        try
        {
            if (active is PartDocument pd)
            {
                var def = (PartComponentDefinition)pd.ComponentDefinition;
                if (TryBool(def, "IsiPartFactory")) factory = GetProp(def, "iPartFactory");
                else if (TryBool(def, "IsiPartMember")) member = GetProp(def, "iPartMember");
            }
            else if (active is AssemblyDocument ad)
            {
                var def = (AssemblyComponentDefinition)ad.ComponentDefinition;
                if (TryBool(def, "IsiAssemblyFactory")) factory = GetProp(def, "iAssemblyFactory");
                else if (TryBool(def, "IsiAssemblyMember")) member = GetProp(def, "iAssemblyMember");
            }
            else
                return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE,
                    "vent_select_ipart_member requires an active part or assembly document");
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "failed to inspect iPart/iAssembly: " + ex.Message);
        }

        if (factory is null && member is null)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "активный документ не является iPart/iAssembly (ни фабрикой, ни членом). Сначала параметризуйте его " +
                "как таблицу iPart/iAssembly (способ 1) или используйте vent_run_ilogic (способ 2). См. vent_inspect_parametrization.");

        // The table (columns/rows) lives on the factory; a member exposes its parent factory too.
        object? table = factory ?? GetProp(member, "ParentFactory") ?? GetProp(member, "Factory");
        if (table is null)
            return Fail(ctx, InventorErrorCodes.API_ERROR, "не удалось получить таблицу iPart/iAssembly (ParentFactory)");

        // Inputs.
        string? memberName = p.Value<string>("member");
        int? rowIndex = p.Value<int?>("row");
        JObject? keys = p["keys"] as JObject;
        if (string.IsNullOrWhiteSpace(memberName) && rowIndex is null && (keys is null || !keys.HasValues))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "specify 'member' (name), 'row' (1-based index), or 'keys' (key column -> value)");

        // Guardrail: generating a member near a master-catalog factory touches shared files.
        if (!(p.Value<bool?>("allow_in_place") ?? false))
        {
            string path = ""; try { path = active.FullFileName; } catch { }
            if (!string.IsNullOrWhiteSpace(path) && ExportPathPolicy.TryRejectPath(path, out _))
                return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                    "документ в защищённом/возможно общем расположении (мастер-каталог): генерация члена iPart здесь " +
                    "затронула бы общий файл. Сначала склонируйте изделие (vent_new_product / vent_clone_recode_product), " +
                    "либо allow_in_place=true, если деталь продукт-уникальна.");
        }

        object? cols = GetProp(table, "TableColumns");
        object? rows = GetProp(table, "TableRows");
        int colN = Count(cols), rowN = Count(rows);
        if (rowN <= 0)
            return Fail(ctx, InventorErrorCodes.API_ERROR, "у фабрики iPart/iAssembly нет строк таблицы");

        // Column headings + the special "Member" column index.
        var colNames = new List<string>();
        int memberCol = -1;
        for (int i = 1; i <= colN; i++)
        {
            object? col = Item(cols, i);
            string name = AsString(GetProp(col, "DisplayHeading"));
            if (string.IsNullOrEmpty(name)) name = AsString(GetProp(col, "Heading"));
            colNames.Add(name);
            if (memberCol < 0 && name.IndexOf("member", StringComparison.OrdinalIgnoreCase) >= 0) memberCol = i;
        }

        // Resolve the target row (1-based).
        int target = -1;
        if (rowIndex is int ri)
        {
            if (ri < 1 || ri > rowN)
                return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, $"row {ri} out of range 1..{rowN}");
            target = ri;
        }
        else
        {
            for (int r = 1; r <= rowN && target < 0; r++)
            {
                object? rowObj = Item(rows, r);
                if (rowObj is null) continue;
                bool match = true;

                if (!string.IsNullOrWhiteSpace(memberName))
                {
                    string mv = memberCol > 0 ? CellValue(rowObj, memberCol) : "";
                    if (string.IsNullOrEmpty(mv)) mv = AsString(GetProp(rowObj, "MemberName"));
                    match = string.Equals(mv.Trim(), memberName!.Trim(), StringComparison.OrdinalIgnoreCase);
                }

                if (match && keys != null)
                {
                    foreach (var kv in keys)
                    {
                        int ci = colNames.FindIndex(n => string.Equals(n, kv.Key, StringComparison.OrdinalIgnoreCase));
                        if (ci < 0) { match = false; break; }
                        if (!CellEquals(CellValue(rowObj, ci + 1), kv.Value)) { match = false; break; }
                    }
                }

                if (match) target = r;
            }
            if (target < 0)
                return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                    "нет строки, соответствующей member/keys (см. vent_inspect_parametrization)");
        }

        object? targetRow = Item(rows, target);

        // Snapshot of the selected row for the report.
        var rowReport = new JObject { ["index"] = target };
        try
        {
            var cells = new JObject();
            for (int i = 1; i <= colN; i++) cells[colNames[i - 1]] = CellValue(targetRow, i);
            rowReport["cells"] = cells;
        }
        catch { /* ignore */ }

        // Activate the member: CreateMember on a factory, ChangeRow on a member.
        global::Inventor.Document? memberDoc = null;
        string mode = factory != null ? "create_member" : "change_row";
        try
        {
            if (factory != null)
            {
                object? res = factory.GetType().InvokeMember(
                    "CreateMember", BindingFlags.InvokeMethod, null, factory, new object[] { targetRow! });
                memberDoc = res as global::Inventor.Document;
            }
            else
            {
                try { member!.GetType().InvokeMember("ChangeRow", BindingFlags.InvokeMethod, null, member, new object[] { targetRow! }); }
                catch { member!.GetType().InvokeMember("ChangeRow", BindingFlags.InvokeMethod, null, member, new object[] { target }); }
                memberDoc = active;
            }
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "не удалось активировать член iPart/iAssembly: " + ex.Message);
        }

        // Rebuild + report bbox/mass of the resulting member (all late-bound).
        JObject? bboxMm = null; double? massG = null;
        try
        {
            if (memberDoc != null)
            {
                try { memberDoc.GetType().InvokeMember("Update", BindingFlags.InvokeMethod, null, memberDoc, null); } catch { /* ignore */ }
                object? def = GetProp(memberDoc, "ComponentDefinition");
                object? box = GetProp(def, "RangeBox");
                object? mn = GetProp(box, "MinPoint"); object? mx = GetProp(box, "MaxPoint");
                double x = (ToD(GetProp(mx, "X")) - ToD(GetProp(mn, "X"))) * CmToMm;
                double y = (ToD(GetProp(mx, "Y")) - ToD(GetProp(mn, "Y"))) * CmToMm;
                double z = (ToD(GetProp(mx, "Z")) - ToD(GetProp(mn, "Z"))) * CmToMm;
                bboxMm = new JObject { ["x_mm"] = Math.Round(x, 2), ["y_mm"] = Math.Round(y, 2), ["z_mm"] = Math.Round(z, 2) };
                try { massG = Math.Round(ToD(GetProp(GetProp(def, "MassProperties"), "Mass")) * 1000.0, 1); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        string? memberFile = null; try { memberFile = memberDoc?.FullFileName; } catch { /* ignore */ }

        return Ok(ctx, new JObject
        {
            ["document"] = active.DisplayName,
            ["mode"] = mode,
            ["selected_row"] = rowReport,
            ["member_document"] = memberFile,
            ["bounding_box_mm"] = bboxMm,
            ["mass_g"] = massG,
            ["note"] = "Изменение НЕ сохранено на диск — save_document / vent_save_product при необходимости. " +
                       "CreateMember мог создать файл члена рядом с фабрикой.",
        });
    }

    // ---- cell helpers ----
    private static string CellValue(object? row, int col1)
    {
        try
        {
            object? cell = Item(row, col1);
            object? v = GetProp(cell, "Value");
            return v?.ToString() ?? cell?.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static bool CellEquals(string cell, JToken want)
    {
        string w = want.Type == JTokenType.String ? (want.Value<string>() ?? "") : want.ToString();
        if (string.Equals(cell.Trim(), w.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        // numeric compare ignoring units / decimal comma
        if (double.TryParse(NumOnly(cell), out double a) && double.TryParse(NumOnly(w), out double b))
            return Math.Abs(a - b) < 1e-6;
        return false;
    }

    private static string NumOnly(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s ?? "")
            if (char.IsDigit(c) || c == '.' || c == '-' || c == ',') sb.Append(c);
        return sb.ToString().Replace(',', '.');
    }

    // ---- reflection helpers (COM RCWs) ----
    private static double ToD(object? v) { try { return Convert.ToDouble(v); } catch { return 0; } }
    private static object? GetProp(object? obj, string name)
        => obj is null ? null : obj.GetType().InvokeMember(name, BindingFlags.GetProperty, null, obj, null);
    private static bool TryBool(object obj, string name) { try { return Convert.ToBoolean(GetProp(obj, name)); } catch { return false; } }
    private static int Count(object? coll) { try { return Convert.ToInt32(GetProp(coll, "Count")); } catch { return 0; } }
    private static object? Item(object? coll, int index)
    {
        try { return coll?.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, coll, new object[] { index }); }
        catch { return null; }
    }
    private static string AsString(object? v) => v?.ToString() ?? "";
}
#endif
