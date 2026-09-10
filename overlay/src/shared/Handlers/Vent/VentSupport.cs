#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// Shared helpers for the vent-factory handlers: bounding box (in mm), driving-parameter lookup for
/// "разворот корпуса", and material / sheet-thickness readout. Inventor's internal length unit is
/// centimetres, so every length crossing this boundary is multiplied by 10 to return millimetres.
/// </summary>
internal static class VentSupport
{
    private const double CmToMm = 10.0;

    /// <summary>Overall bounding box of a part or assembly component definition, in millimetres.</summary>
    public static JObject BBoxMm(ComponentDefinition def)
    {
        Box b = def.RangeBox;
        double w = (b.MaxPoint.X - b.MinPoint.X) * CmToMm;
        double h = (b.MaxPoint.Y - b.MinPoint.Y) * CmToMm;
        double d = (b.MaxPoint.Z - b.MinPoint.Z) * CmToMm;
        return new JObject
        {
            ["x_mm"] = Math.Round(w, 2),
            ["y_mm"] = Math.Round(h, 2),
            ["z_mm"] = Math.Round(d, 2),
        };
    }

    /// <summary>
    /// Candidate parameter names for the spiral-casing discharge angle ("разворот корпуса"),
    /// tried in order. Override from the tool call if your models use a different name.
    /// </summary>
    public static readonly string[] DischargeParamCandidates =
    {
        "Rd", "discharge_angle", "DischargeAngle", "Razvorot", "Разворот", "razvorot",
        "casing_angle", "Ugol", "Угол",
    };

    /// <summary>Find a user/model parameter by name (exact, case-insensitive), or null.</summary>
    public static Parameter? FindParameter(PartDocument doc, string name)
    {
        try
        {
            foreach (Parameter prm in doc.ComponentDefinition.Parameters)
                if (string.Equals(prm.Name, name, StringComparison.OrdinalIgnoreCase))
                    return prm;
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>Read a document's material name (iProperty "Material"), or null.</summary>
    public static string? MaterialName(global::Inventor.Document doc)
    {
        try
        {
            PropertySet ps = doc.PropertySets["Design Tracking Properties"];
            return (string)ps["Material"].Value;
        }
        catch { return null; }
    }

    /// <summary>Sheet thickness in mm for a sheet-metal part, or null if not sheet metal.</summary>
    public static double? ThicknessMm(PartDocument doc)
    {
        try
        {
            if (doc.ComponentDefinition is SheetMetalComponentDefinition sm)
            {
                double cm = (double)sm.Thickness.Value; // cm
                return Math.Round(cm * CmToMm, 3);
            }
        }
        catch { /* ignore */ }
        return null;
    }
}
#endif
