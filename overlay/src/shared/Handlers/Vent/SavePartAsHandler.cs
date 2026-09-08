#if INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_save_part_as</c> — save the active PART document as a new .ipt (derive a new part). Uses
/// SaveAs so the active document becomes the new file. For whole assemblies use the server-side
/// folder clone (vent_new_product) or Inventor Pack-and-Go, because assembly references need remapping.
/// </summary>
public sealed class SavePartAsHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_save_part_as";
    public bool IsReadOnly => false;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? active;
        try { active = app.ActiveDocument; } catch { active = null; }
        if (active is null)
            return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active Inventor document");
        if (active is not PartDocument doc)
            return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE,
                "vent_save_part_as requires an active part; for assemblies use vent_new_product / Pack-and-Go");

        string outputPath = (p["output_path"]?.Type == JTokenType.String) ? (string)p["output_path"]! : "";
        if (ExportPathPolicy.TryRejectPath(outputPath, out var rejection))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, rejection);
        if (!outputPath.EndsWith(".ipt", StringComparison.OrdinalIgnoreCase))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "output_path must end with .ipt");

        try
        {
            doc.SaveAs(outputPath, false); // false = SaveAs (not SaveCopyAs): active doc becomes the new file
            return Ok(ctx, new JObject
            {
                ["saved_as"] = outputPath,
                ["title"] = doc.DisplayName,
            });
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "save-as failed: " + ex.Message);
        }
    }
}
#endif
