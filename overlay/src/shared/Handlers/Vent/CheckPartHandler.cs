#if INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Collections.Generic;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_check_part</c> — pre-cut sanity check of the active sheet-metal part ("проверка перед резкой").
/// Reports the model-side facts that matter before laser/plasma: sheet-metal or not, flat pattern present,
/// sheet thickness, and flat-pattern overall size. Each rule returns pass/fail with a message.
///
/// Deep contour geometry (closed outer profile, per-hole edge distance, minimum web) is validated
/// authoritatively by the cross-platform dxf_tools layer on the exported flat-pattern DXF — see
/// docs/ARCHITECTURE.md. This handler is the fast model-side gate.
/// </summary>
public sealed class CheckPartHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_check_part";
    public bool IsReadOnly => true;

    private const double CmToMm = 10.0;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? active;
        try { active = app.ActiveDocument; } catch { active = null; }
        if (active is null)
            return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active Inventor document");
        if (active is not PartDocument doc)
            return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE, "vent_check_part requires an active part document");

        var checks = new List<JObject>();
        void Rule(string name, bool ok, string msg) =>
            checks.Add(new JObject { ["rule"] = name, ["ok"] = ok, ["message"] = msg });

        bool isSheet = doc.ComponentDefinition is SheetMetalComponentDefinition;
        Rule("sheet_metal", isSheet, isSheet ? "деталь листовая" : "деталь НЕ листовая — flat pattern недоступен");

        double? flatW = null, flatH = null, thick = VentSupport.ThicknessMm(doc);
        if (isSheet)
        {
            var sm = (SheetMetalComponentDefinition)doc.ComponentDefinition;
            bool hasFp = false;
            try { hasFp = sm.HasFlatPattern; } catch { hasFp = false; }
            Rule("flat_pattern", hasFp, hasFp ? "flat pattern есть" : "нет flat pattern — создайте развёртку");

            if (hasFp)
            {
                try
                {
                    Box b = sm.FlatPattern.RangeBox;
                    flatW = Math.Round((b.MaxPoint.X - b.MinPoint.X) * CmToMm, 2);
                    flatH = Math.Round((b.MaxPoint.Y - b.MinPoint.Y) * CmToMm, 2);
                }
                catch { /* ignore */ }
            }
            Rule("thickness", thick.HasValue, thick.HasValue ? $"толщина {thick} мм" : "толщина не определена");
        }

        bool pass = true;
        foreach (var c in checks) if (!(bool)c["ok"]!) pass = false;

        return Ok(ctx, new JObject
        {
            ["part"] = doc.DisplayName,
            ["material"] = VentSupport.MaterialName(doc),
            ["thickness_mm"] = thick,
            ["flat_width_mm"] = flatW,
            ["flat_height_mm"] = flatH,
            ["pass"] = pass,
            ["checks"] = new JArray(checks),
            ["note"] = "Глубокая проверка контура/отверстий — через dxf_tools на выгруженном DXF.",
        });
    }
}
#endif
