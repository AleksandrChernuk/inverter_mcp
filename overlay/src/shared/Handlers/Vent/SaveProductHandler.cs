#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Collections.Generic;
using System.IO;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;
using Path = System.IO.Path;
using File = System.IO.File;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// Persist the active product and all dirty referenced native documents under an explicitly approved
/// product root. Plain Document.Save only saves the active assembly; Save2 is required for parameters
/// changed on referenced occurrences. Dirty external/library documents are blockers and are never saved.
/// </summary>
public sealed class SaveProductHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_save_product";
    public bool IsReadOnly => false;
    private const int MaxDocuments = 512;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? active;
        try { active = app.ActiveDocument; } catch { active = null; }
        if (active is null)
            return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active Inventor document");
        if (active.DocumentType != DocumentTypeEnum.kPartDocumentObject &&
            active.DocumentType != DocumentTypeEnum.kAssemblyDocumentObject)
            return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE,
                "vent_save_product requires an active part or assembly");

        string rawRoot = (p["product_root"]?.Value<string>() ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawRoot) || !Path.IsPathRooted(rawRoot))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "product_root must be an absolute directory");

        string productRoot;
        try { productRoot = Path.GetFullPath(rawRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "invalid product_root: " + ex.Message);
        }
        if (!Directory.Exists(productRoot))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "product_root does not exist: " + productRoot);
        if (ExportPathPolicy.TryRejectPath(Path.Combine(productRoot, "_save_probe.tmp"), out string rejection))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, rejection);

        string activePath;
        try { activePath = Path.GetFullPath(active.FullFileName); }
        catch { activePath = ""; }
        if (string.IsNullOrWhiteSpace(activePath) || !IsUnder(activePath, productRoot))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "active document must already be saved under product_root");

        var documents = new List<global::Inventor.Document> { active };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { activePath };
        try
        {
            foreach (global::Inventor.Document referenced in active.AllReferencedDocuments)
            {
                string path;
                try { path = Path.GetFullPath(referenced.FullFileName); }
                catch { path = ""; }
                string key = string.IsNullOrWhiteSpace(path) ? referenced.InternalName : path;
                if (seen.Add(key)) documents.Add(referenced);
                if (documents.Count > MaxDocuments)
                    return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                        $"product exceeds the {MaxDocuments}-document save limit");
            }
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "cannot enumerate product documents: " + ex.Message);
        }

        var dirtyInside = new List<global::Inventor.Document>();
        var dirtyPaths = new JArray();
        var blockedExternal = new JArray();
        foreach (var document in documents)
        {
            bool dirty;
            try { dirty = document.Dirty; } catch { dirty = false; }
            if (!dirty) continue;

            string path;
            try { path = Path.GetFullPath(document.FullFileName); }
            catch { path = ""; }
            if (string.IsNullOrWhiteSpace(path) || !IsUnder(path, productRoot))
            {
                blockedExternal.Add(string.IsNullOrWhiteSpace(path) ? document.DisplayName : path);
                continue;
            }
            dirtyPaths.Add(path);
            dirtyInside.Add(document);
        }

        if (blockedExternal.Count > 0)
        {
            return Ok(ctx, new JObject
            {
                ["pass"] = false,
                ["saved"] = false,
                ["product_root"] = productRoot,
                ["reason"] = "dirty_referenced_documents_outside_product_root",
                ["blocked_documents"] = blockedExternal,
                ["dirty_documents"] = dirtyPaths,
            });
        }

        try
        {
            if (!active.Update2(false))
                return Ok(ctx, new JObject
                {
                    ["pass"] = false,
                    ["saved"] = false,
                    ["reason"] = "final_update_failed",
                    ["dirty_documents"] = dirtyPaths,
                });

            ObjectCollection dependents = app.TransientObjects.CreateObjectCollection();
            foreach (var document in dirtyInside)
                if (!ReferenceEquals(document, active)) dependents.Add(document);
            active.Save2(true, dependents);
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "failed to save product tree: " + ex.Message);
        }

        var remainingDirty = new JArray();
        foreach (var document in dirtyInside)
        {
            try { if (document.Dirty) remainingDirty.Add(document.FullFileName); }
            catch { remainingDirty.Add(document.DisplayName + ": dirty state unavailable"); }
        }

        bool pass = remainingDirty.Count == 0;
        return Ok(ctx, new JObject
        {
            ["pass"] = pass,
            ["saved"] = pass,
            ["product_root"] = productRoot,
            ["active_document"] = activePath,
            ["document_count"] = documents.Count,
            ["dirty_before"] = dirtyPaths,
            ["saved_document_count"] = dirtyInside.Count,
            ["remaining_dirty"] = remainingDirty,
        });
    }

    private static bool IsUnder(string candidate, string root)
    {
        string path = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
#endif
