#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_open_product</c> — opens a product's .iam/.ipt by absolute path and makes it active.
/// Thin wrapper around Documents.Open with a friendlier response for the vent workflow.
/// </summary>
public sealed class OpenProductHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_open_product";
    public bool IsReadOnly => false;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        string path = (p["path"]?.Type == JTokenType.String) ? (string)p["path"]! : "";
        if (string.IsNullOrWhiteSpace(path))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "path is required");
        if (!System.IO.File.Exists(path))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "file does not exist: " + path);

        // Auto-activate the product's .ipj BEFORE opening, so library references (ступиці,
        // покупні, материалы) resolve and Inventor does not prompt to locate/open files.
        // Inventor cannot switch the active project while documents are open, so this only
        // runs when nothing is open; otherwise we leave the current project untouched.
        string? projectNote = null;
        try
        {
            if (app.Documents.Count == 0)
            {
                string? ipj = VentSupport.FindProjectFile(path);
                if (!string.IsNullOrWhiteSpace(ipj))
                {
                    var (changed, _, err) = VentSupport.ActivateProject(app, ipj!);
                    projectNote = err != null
                        ? "проект не активирован: " + err
                        : (changed ? "активирован проект: " + ipj : null);
                }
            }
        }
        catch { /* best-effort; opening still proceeds */ }

        try
        {
            global::Inventor.Document doc = app.Documents.Open(path, true);
            return Ok(ctx, new JObject
            {
                ["title"] = doc.DisplayName,
                ["document_type"] = doc.DocumentType.ToString(),
                ["path"] = TryFullName(doc) ?? path,
                ["project"] = projectNote,
            });
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "failed to open product: " + ex.Message);
        }
    }

    private static string? TryFullName(global::Inventor.Document doc)
    {
        try { return doc.FullFileName; } catch { return null; }
    }
}
#endif
