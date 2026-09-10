#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_set_discharge</c> — sets the spiral-casing discharge orientation ("разворот корпуса").
/// Drives a model parameter (auto-detected from <see cref="VentSupport.DischargeParamCandidates"/>,
/// or the caller-supplied <c>param_name</c>) to the given angle in degrees, optionally a <c>hand</c>
/// parameter, then updates the document and returns the new bounding box for verification.
///
/// NOTE: the exact driving parameter name is model-specific. If auto-detection fails, the handler
/// lists the available user parameters so the agent can pick one and pass <c>param_name</c>.
/// </summary>
public sealed class SetDischargeHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_set_discharge";
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
                "vent_set_discharge requires the casing PART document to be active");

        if (p["angle"] is null || p["angle"]!.Type == JTokenType.Null)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "angle (degrees) is required");
        double angle = (double)p["angle"]!;

        // Resolve the driving parameter.
        string? given = (p["param_name"]?.Type == JTokenType.String) ? (string)p["param_name"]! : null;
        Parameter? prm = null;
        if (!string.IsNullOrWhiteSpace(given))
            prm = VentSupport.FindParameter(doc, given!);
        else
            foreach (var cand in VentSupport.DischargeParamCandidates)
                if ((prm = VentSupport.FindParameter(doc, cand)) != null) break;

        if (prm is null)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "discharge parameter not found; pass param_name. Available: " + ListUserParams(doc));

        try
        {
            prm.Expression = angle.ToString(System.Globalization.CultureInfo.InvariantCulture) + " deg";
            // optional hand parameter (0/1 or a text parameter), best-effort
            string? hand = (p["hand"]?.Type == JTokenType.String) ? (string)p["hand"]! : null;
            if (!string.IsNullOrWhiteSpace(hand))
            {
                var handPrm = VentSupport.FindParameter(doc, "hand") ?? VentSupport.FindParameter(doc, "Hand");
                if (handPrm != null)
                {
                    try { handPrm.Expression = hand!.Equals("left", StringComparison.OrdinalIgnoreCase) ? "1" : "0"; }
                    catch { /* hand param not numeric; ignore */ }
                }
            }
            doc.Update();
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "failed to set discharge: " + ex.Message);
        }

        return Ok(ctx, new JObject
        {
            ["parameter"] = prm.Name,
            ["angle_deg"] = angle,
            ["bounding_box_mm"] = VentSupport.BBoxMm((global::Inventor.ComponentDefinition)doc.ComponentDefinition),
        });
    }

    private static string ListUserParams(PartDocument doc)
    {
        try
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (Parameter prm in doc.ComponentDefinition.Parameters.UserParameters)
                names.Add(prm.Name);
            return string.Join(", ", names);
        }
        catch { return "(unavailable)"; }
    }
}
#endif
