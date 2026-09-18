#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Reflection;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_make_components</c> — multibody .ipt → .iam (method: build the assembly as ONE part with a body
/// per detail, then Make Components to explode it into an .iam + per-body .ipt files). Phase 3 of the
/// parametrization roadmap.
///
/// Reality check: Inventor exposes NO reliable HEADLESS API for the Make Components operation itself — it is
/// an interactive command (the user picks target folder / assembly / templates in a dialog). So this handler
/// is primarily RECON: it confirms the active part is multibody, lists the solid bodies (the future
/// components) and reports readiness. With execute=true it makes a guarded, best-effort attempt to run the
/// Make Components UI command (dialog suppression may or may not allow it depending on the Inventor build),
/// and reports exactly what happened — feeding the 2026 acceptance. Read the solid-body report first.
/// </summary>
public sealed class MakeComponentsHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_make_components";
    public bool IsReadOnly => false;

    // Candidate internal command ids for "Make Components" (differ across Inventor builds/locales).
    private static readonly string[] MakeComponentsCmdIds =
    {
        "PartMakeComponentsCmd", "AssemblyMakeComponentsCmd", "MakeComponentsCmd", "PartMakePartCmd",
    };

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? active;
        try { active = app.ActiveDocument; } catch { active = null; }
        if (active is null)
            return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active Inventor document");
        if (active is not PartDocument pd)
            return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE,
                "vent_make_components требует активную многотельную ДЕТАЛЬ (.ipt). Соберите узел как одну " +
                "деталь (каждая деталь = отдельное тело), затем вызовите Make Components.");

        var def = (PartComponentDefinition)pd.ComponentDefinition;

        // Recon: enumerate solid bodies (= future components).
        var bodies = new JArray();
        int solidCount = 0, total = 0;
        try
        {
            foreach (SurfaceBody b in def.SurfaceBodies)
            {
                total++;
                string bname = ""; try { bname = b.Name; } catch { }
                bool isSolid = true;
                try { object? v = b.GetType().InvokeMember("IsSolid", BindingFlags.GetProperty, null, b, null); if (v != null) isSolid = Convert.ToBoolean(v); }
                catch { /* property may not exist on this build — assume solid */ }
                if (isSolid) solidCount++;
                bodies.Add(new JObject { ["name"] = bname, ["is_solid"] = isSolid });
            }
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "не удалось прочитать тела детали: " + ex.Message);
        }

        var recon = new JObject
        {
            ["document"] = active.DisplayName,
            ["body_count"] = total,
            ["solid_body_count"] = solidCount,
            ["bodies"] = bodies,
        };
        try { recon["path"] = active.FullFileName; } catch { }

        if (solidCount < 2)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                $"для Make Components нужно ≥2 сплошных тел, а найдено {solidCount}. Достройте многотельную деталь.");

        bool execute = p.Value<bool?>("execute") ?? false;
        if (!execute)
        {
            recon["ready"] = true;
            recon["note"] = "Разведка (dry-run): деталь многотельная и готова к Make Components. Передайте execute=true, " +
                            "чтобы попытаться запустить операцию. ВНИМАНИЕ: Make Components — интерактивная команда, " +
                            "надёжного headless-API нет; фактическое разбиение обычно выполняется в UI. Точный способ " +
                            "под Inventor 2026 подтверждается на приёмке.";
            return Ok(ctx, recon);
        }

        // execute=true: guardrail, then a best-effort attempt via the UI command.
        if (!(p.Value<bool?>("allow_in_place") ?? false))
        {
            string path = ""; try { path = active.FullFileName; } catch { }
            if (!string.IsNullOrWhiteSpace(path) && ExportPathPolicy.TryRejectPath(path, out _))
                return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                    "документ в защищённом/мастер-расположении: Make Components создаёт файлы рядом. Склонируйте изделие " +
                    "или allow_in_place=true.");
        }

        string? triedId = null; string? execError = null; bool executed = false;
        try
        {
            var cds = app.CommandManager.ControlDefinitions;
            foreach (string id in MakeComponentsCmdIds)
            {
                ControlDefinition? cd = null;
                try { cd = cds[id]; } catch { cd = null; }
                if (cd == null) continue;
                triedId = id;
                try { cd.Execute(); executed = true; break; }
                catch (Exception ex) { execError = ex.Message; }
            }
        }
        catch (Exception ex) { execError = ex.Message; }

        recon["executed"] = executed;
        recon["command_id"] = triedId;
        if (executed)
        {
            recon["note"] = "Команда Make Components запущена. Если открылся диалог — операция требует ручного выбора " +
                            "цели/шаблона (headless-запуск не гарантирован). Проверьте результат и сохраните.";
            return Ok(ctx, recon);
        }

        return Fail(ctx, InventorErrorCodes.API_ERROR,
            "не удалось запустить Make Components программно" +
            (triedId == null ? " (команда не найдена среди известных id — уточнить на Inventor 2026)" : $" (id={triedId})") +
            (execError == null ? "" : ": " + execError) +
            ". Разведка тел выполнена (см. bodies). Операцию выполнить в UI Inventor.");
    }
}
#endif
