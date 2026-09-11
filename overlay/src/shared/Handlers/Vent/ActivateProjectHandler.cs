#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_activate_project</c> — make a product's Inventor project (.ipj) the active design project,
/// so its Workspace + Library paths resolve references (ступицы, покупні, материалы) and Inventor
/// stops prompting to locate/open files. Accepts a .ipj path directly, or a product folder / a file
/// inside the product (the nearest .ipj at or above it is used). Inventor cannot switch the active
/// project while documents are open — close all documents first (this is why auto-activation in
/// vent_open_product only runs when nothing is open).
/// </summary>
public sealed class ActivateProjectHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_activate_project";
    public bool IsReadOnly => false;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        string path = (p["path"]?.Type == JTokenType.String) ? (string)p["path"]! : "";
        if (string.IsNullOrWhiteSpace(path))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "path is required (.ipj file, product folder, or a file inside the product)");

        string? ipj = (path.EndsWith(".ipj", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(path))
            ? path
            : VentSupport.FindProjectFile(path);
        if (string.IsNullOrWhiteSpace(ipj))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "не найден .ipj в этой папке или выше: " + path);

        var (changed, active, error) = VentSupport.ActivateProject(app, ipj!);
        if (error != null)
            return Fail(ctx, InventorErrorCodes.API_ERROR, error + " (проект: " + ipj + ")");

        return Ok(ctx, new JObject
        {
            ["project"] = ipj,
            ["active_project"] = active,
            ["changed"] = changed,
            ["note"] = changed ? "проект активирован" : "проект уже был активен",
        });
    }
}
#endif
