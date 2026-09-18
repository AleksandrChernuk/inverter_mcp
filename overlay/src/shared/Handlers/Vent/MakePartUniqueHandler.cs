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
/// <c>vent_make_part_unique</c> — localize a SHARED component so editing it cannot break other products.
/// A part that lives OUTSIDE the product folder (e.g. лопатка in a common «Библіотека»/Content Center folder,
/// referenced by many изделия) is copied INTO the current product's folder and the assembly reference is
/// rebound to that private copy (<c>ComponentOccurrence.Replace</c>). The original library file is never
/// modified. After this, drive_dimension / scale / set_component_parameter on that part change only THIS
/// product. Run this before editing any shared лопатка. Change is NOT saved to disk — call vent_save_product
/// to persist the rebind. This is the missing piece Pack-and-Go leaves out (it does not copy external/library
/// references).
/// </summary>
public sealed class MakePartUniqueHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_make_part_unique";
    public bool IsReadOnly => false;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? active;
        try { active = app.ActiveDocument; } catch { active = null; }
        if (active is null)
            return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active Inventor document");
        if (active is not AssemblyDocument asm)
            return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE,
                "vent_make_part_unique требует активную СБОРКУ (.iam) изделия");

        string? occName = p.Value<string>("occurrence");
        if (string.IsNullOrWhiteSpace(occName))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "occurrence обязателен (имя общей детали, напр. из inventor_get_assembly_bom)");

        var asmDef = (AssemblyComponentDefinition)asm.ComponentDefinition;

        ComponentOccurrence? occ = FindOccurrence(asmDef.Occurrences, occName!, out string? occErr);
        if (occ is null)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                occErr ?? $"компонент '{occName}' не найден (см. inventor_get_assembly_bom)");

        // Source part file (the shared library part).
        string source;
        try { source = ((global::Inventor.Document)occ.Definition.Document).FullFileName; }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, "не удалось получить файл детали: " + ex.Message); }
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return Fail(ctx, InventorErrorCodes.API_ERROR, "файл детали не найден на диске: " + source);

        // Product folder: the folder that owns this изделие. Default = folder of the active assembly.
        string? productRoot = p.Value<string>("product_root");
        if (string.IsNullOrWhiteSpace(productRoot))
        {
            try { productRoot = Path.GetDirectoryName(asm.FullFileName); } catch { productRoot = null; }
        }
        if (string.IsNullOrWhiteSpace(productRoot) || !Directory.Exists(productRoot))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "product_root не определён/не существует: сохраните сборку изделия или задайте product_root явно");

        // Already inside the product? Then it is already private — nothing to isolate.
        if (IsUnder(source, productRoot!))
            return Ok(ctx, new JObject
            {
                ["assembly"] = asm.DisplayName,
                ["component"] = occ.Name,
                ["source"] = source,
                ["localized"] = false,
                ["note"] = "Деталь уже внутри папки изделия — она не общая, изолировать нечего.",
            });

        // Destination: product_root [\ dest_subdir] \ <new_name|original>.ext
        string destDir = productRoot!;
        string? destSub = p.Value<string>("dest_subdir");
        if (!string.IsNullOrWhiteSpace(destSub)) destDir = Path.Combine(productRoot!, destSub!.Trim());
        string ext = Path.GetExtension(source);
        string baseName = p.Value<string>("new_name") is string nn && nn.Trim().Length > 0
            ? nn.Trim()
            : Path.GetFileNameWithoutExtension(source);
        if (baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "new_name содержит недопустимые символы");
        string target = Path.Combine(destDir, baseName + ext);

        bool overwrite = p.Value<bool?>("overwrite") ?? false;
        if (File.Exists(target) && !overwrite)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                $"целевой файл уже существует: {target}. Задайте new_name, либо overwrite=true если это и есть локальная копия.");

        bool allInstances = p.Value<bool?>("all_instances") ?? true;

        // Count how many instances in this assembly reference the same source (for the report).
        int refCount = CountReferences(asmDef.Occurrences, source);

        // Copy the library part into the product, then rebind the reference.
        try
        {
            Directory.CreateDirectory(destDir);
            File.Copy(source, target, overwrite);
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "не удалось скопировать деталь в изделие: " + ex.Message);
        }

        try
        {
            occ.Replace(target, allInstances);
        }
        catch (Exception ex)
        {
            // Roll back the copy we just made so we don't leave an orphan.
            try { if (File.Exists(target)) File.Delete(target); } catch { /* ignore */ }
            return Fail(ctx, InventorErrorCodes.API_ERROR,
                "деталь скопирована, но перепривязка ссылки (Replace) не удалась: " + ex.Message +
                " (копия удалена, изделие не изменено)");
        }

        try { asm.Update(); } catch { /* non-fatal for reference rebind */ }

        return Ok(ctx, new JObject
        {
            ["assembly"] = asm.DisplayName,
            ["component"] = occName,
            ["localized"] = true,
            ["source"] = source,
            ["local_copy"] = target,
            ["instances_rebound"] = allInstances ? refCount : 1,
            ["note"] = "Общая деталь скопирована внутрь изделия и ссылка перепривязана — правки этой детали больше НЕ трогают " +
                       "другие изделия. Оригинал в Библиотеке не изменён. На диск НЕ сохранено: вызовите vent_save_product, " +
                       "чтобы зафиксировать перепривязку. Если у детали есть свои под-детали из Библиотеки — их изолируйте отдельно.",
        });
    }

    private static bool IsUnder(string path, string root)
    {
        try
        {
            string p = Path.GetFullPath(path).TrimEnd('\\', '/');
            string r = Path.GetFullPath(root).TrimEnd('\\', '/');
            return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(r + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static int CountReferences(System.Collections.IEnumerable occurrences, string source)
    {
        int n = 0;
        foreach (ComponentOccurrence occ in occurrences)
        {
            try
            {
                string f = ((global::Inventor.Document)occ.Definition.Document).FullFileName;
                if (string.Equals(f, source, StringComparison.OrdinalIgnoreCase)) n++;
            }
            catch { /* ignore */ }
            try { if (occ.SubOccurrences != null && occ.SubOccurrences.Count > 0) n += CountReferences(occ.SubOccurrences, source); }
            catch { /* ignore */ }
        }
        return n;
    }

    // ---- occurrence lookup (exact name, else base name without ":N"; recurses sub-occurrences) ----
    private static ComponentOccurrence? FindOccurrence(System.Collections.IEnumerable occurrences, string wanted, out string? error)
    {
        error = null;
        var exact = new List<ComponentOccurrence>();
        var byBase = new List<ComponentOccurrence>();
        Collect(occurrences, wanted, exact, byBase);
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) { error = $"компонент '{wanted}' неоднозначен ({exact.Count} совпадений); используйте точное имя с ':N'"; return null; }
        if (byBase.Count == 1) return byBase[0];
        error = byBase.Count > 1
            ? $"компонент '{wanted}' неоднозначен ({byBase.Count} по базовому имени); используйте точное имя ':N'"
            : $"компонент '{wanted}' не найден (см. inventor_get_assembly_bom)";
        return null;
    }

    private static void Collect(System.Collections.IEnumerable occurrences, string wanted,
        List<ComponentOccurrence> exact, List<ComponentOccurrence> byBase)
    {
        foreach (ComponentOccurrence occ in occurrences)
        {
            string name;
            try { name = occ.Name; } catch { continue; }
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) exact.Add(occ);
            else if (string.Equals(Strip(name), Strip(wanted), StringComparison.OrdinalIgnoreCase)) byBase.Add(occ);
            try { if (occ.SubOccurrences != null && occ.SubOccurrences.Count > 0) Collect(occ.SubOccurrences, wanted, exact, byBase); }
            catch { /* ignore */ }
        }
    }

    private static string Strip(string n) { int i = n.LastIndexOf(':'); return i > 0 ? n.Substring(0, i) : n; }
}
#endif
