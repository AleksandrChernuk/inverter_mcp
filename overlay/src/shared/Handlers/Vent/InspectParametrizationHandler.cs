#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Collections;
using System.Reflection;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_inspect_parametrization</c> (read-only) — recon of the ACTIVE part/assembly's parametrization:
/// whether it is an iPart/iAssembly factory or member (table columns, key columns, member count),
/// the iLogic rules present (names), and the user parameters (name+expression). Use it FIRST to decide
/// how to drive a size variant (типорозмір): via an iLogic key parameter, by selecting an iPart/iAssembly
/// row, or (if none) that the part must be parametrized first. Phase 0 of the parametrization roadmap.
/// </summary>
public sealed class InspectParametrizationHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_inspect_parametrization";
    public bool IsReadOnly => true;

    // iLogic add-in ClientId (stable across Inventor versions).
    private const string ILogicAddInGuid = "{3BDD8D79-2179-4B11-8A5A-257B1C0263AC}";

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? doc;
        try { doc = app.ActiveDocument; } catch { doc = null; }
        if (doc is null)
            return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active Inventor document");

        var result = new JObject
        {
            ["document"] = doc.DisplayName,
            ["document_type"] = doc.DocumentType.ToString(),
        };
        try { result["path"] = doc.FullFileName; } catch { }

        JObject ipart = InspectIPart(doc);
        JObject ilogic = InspectILogic(app, doc);
        JObject uparams = InspectUserParams(doc);
        result["ipart"] = ipart;
        result["ilogic"] = ilogic;
        result["user_parameters"] = uparams;

        // Actionable hint: how can a size variant be driven?
        string hint;
        string ipKind = (string?)ipart["kind"] ?? "none";
        int ruleCount = (int?)ilogic["rule_count"] ?? 0;
        if (ipKind.EndsWith("factory", StringComparison.Ordinal))
            hint = "iPart/iAssembly-фабрика: типоразмір задаётся выбором строки (vent_select_ipart_member).";
        else if (ruleCount > 0)
            hint = "Есть iLogic-правила: типоразмір задаётся ключевым параметром + прогоном правила (vent_run_ilogic).";
        else if (((int?)uparams["count"] ?? 0) > 0)
            hint = "Нет iPart/iLogic, но есть пользовательские параметры: можно менять их напрямую (drive_dimension), связей может не быть.";
        else
            hint = "Деталь НЕ параметризована (нет iPart/iLogic/польз. параметров): сперва нужна параметризация.";
        result["hint"] = hint;

        return Ok(ctx, result);
    }

    // ---- iPart / iAssembly ----
    private static JObject InspectIPart(global::Inventor.Document doc)
    {
        var o = new JObject { ["kind"] = "none" };
        try
        {
            if (doc is PartDocument pd)
            {
                var def = (PartComponentDefinition)pd.ComponentDefinition;
                if (TryBool(def, "IsiPartFactory")) { o["kind"] = "ipart_factory"; FillFactory(o, GetProp(def, "iPartFactory")); }
                else if (TryBool(def, "IsiPartMember")) o["kind"] = "ipart_member";
            }
            else if (doc is AssemblyDocument ad)
            {
                var def = (AssemblyComponentDefinition)ad.ComponentDefinition;
                if (TryBool(def, "IsiAssemblyFactory")) { o["kind"] = "iassembly_factory"; FillFactory(o, GetProp(def, "iAssemblyFactory")); }
                else if (TryBool(def, "IsiAssemblyMember")) o["kind"] = "iassembly_member";
            }
        }
        catch { /* ignore */ }
        return o;
    }

    private static void FillFactory(JObject o, object? factory)
    {
        if (factory is null) return;
        // columns (= driving parameters); mark key columns
        try
        {
            object? cols = GetProp(factory, "TableColumns");
            int n = Count(cols);
            var arr = new JArray();
            for (int i = 1; i <= n; i++)
            {
                object? col = Item(cols, i);
                if (col is null) continue;
                string name = AsString(GetProp(col, "DisplayHeading"));
                if (string.IsNullOrEmpty(name)) name = AsString(GetProp(col, "Heading"));
                var c = new JObject { ["name"] = name };
                try { int key = Convert.ToInt32(GetProp(col, "KeyColumnOrder")); if (key > 0) c["key"] = key; } catch { }
                arr.Add(c);
            }
            if (arr.Count > 0) o["columns"] = arr;
        }
        catch { /* ignore */ }
        // rows (= members / типорозміри)
        try
        {
            object? rows = GetProp(factory, "TableRows");
            o["member_count"] = Count(rows);
        }
        catch { /* ignore */ }
    }

    // ---- iLogic (late-bound via reflection; iLogic assembly is not referenced) ----
    private static JObject InspectILogic(Application app, global::Inventor.Document doc)
    {
        var o = new JObject { ["available"] = false };
        object? autom = null;
        try
        {
            object addins = app.ApplicationAddIns;
            object ilogic = addins.GetType().InvokeMember(
                "ItemById", BindingFlags.InvokeMethod, null, addins, new object[] { ILogicAddInGuid })!;
            autom = GetProp(ilogic, "Automation");
        }
        catch { return o; }
        if (autom is null) return o;
        o["available"] = true;
        try
        {
            object? rules = autom.GetType().InvokeMember(
                "Rules", BindingFlags.InvokeMethod, null, autom, new object[] { doc });
            var arr = new JArray();
            if (rules is IEnumerable en)
                foreach (object r in en)
                {
                    try { arr.Add(AsString(GetProp(r, "Name"))); } catch { }
                }
            o["rules"] = arr;
            o["rule_count"] = arr.Count;
        }
        catch (Exception ex) { o["rules_error"] = ex.Message; }
        return o;
    }

    // ---- user parameters ----
    private static JObject InspectUserParams(global::Inventor.Document doc)
    {
        var o = new JObject { ["count"] = 0 };
        try
        {
            global::Inventor.Parameters? prms = null;
            if (doc is PartDocument pd) prms = ((PartComponentDefinition)pd.ComponentDefinition).Parameters;
            else if (doc is AssemblyDocument ad) prms = ((AssemblyComponentDefinition)ad.ComponentDefinition).Parameters;
            if (prms != null)
            {
                var arr = new JArray();
                foreach (Parameter par in prms.UserParameters)
                {
                    string name = ""; string expr = "";
                    try { name = par.Name; } catch { }
                    try { expr = par.Expression; } catch { }
                    arr.Add(new JObject { ["name"] = name, ["expression"] = expr });
                }
                o["count"] = arr.Count;
                if (arr.Count > 0) o["parameters"] = arr;
            }
        }
        catch { /* ignore */ }
        return o;
    }

    // ---- reflection helpers (COM RCWs) ----
    private static object? GetProp(object? obj, string name)
        => obj is null ? null : obj.GetType().InvokeMember(name, BindingFlags.GetProperty, null, obj, null);

    private static bool TryBool(object obj, string name)
    {
        try { return Convert.ToBoolean(GetProp(obj, name)); } catch { return false; }
    }

    private static int Count(object? coll)
    {
        try { return Convert.ToInt32(GetProp(coll, "Count")); } catch { return 0; }
    }

    private static object? Item(object? coll, int index)
    {
        try { return coll?.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, coll, new object[] { index }); }
        catch { return null; }
    }

    private static string AsString(object? v) => v?.ToString() ?? "";
}
#endif
