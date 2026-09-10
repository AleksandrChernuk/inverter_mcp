#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Collections.Generic;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_inspect_model</c> — read-only tour of the active PART's build tree: features, sketches and the
/// dimension constraints inside each sketch (name + expression + kind). Purpose: surface the sketch
/// dimensions that are NOT top-level model parameters (e.g. a blade-slot placement radius), so a size
/// change can target the real driver. Lengths in the expression are Inventor's own display units.
/// </summary>
public sealed class InspectModelHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_inspect_model";
    public bool IsReadOnly => true;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? active;
        try { active = app.ActiveDocument; } catch { active = null; }
        if (active is null)
            return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active Inventor document");
        if (active is not PartDocument doc)
            return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE, "vent_inspect_model requires an active part document");

        var pdef = doc.ComponentDefinition as PartComponentDefinition;
        if (pdef is null)
            return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE, "part has no PartComponentDefinition");

        int maxFeatures = p.Value<int?>("max_features") ?? 300;
        int maxDims = p.Value<int?>("max_dims") ?? 60;

        // ---- features ----
        var features = new JArray();
        int unhealthyFeatures = 0;
        try
        {
            foreach (PartFeature f in pdef.Features)
            {
                string fname, ftype, health;
                try { fname = f.Name; } catch { fname = "?"; }
                try { ftype = ShortType(f.Type.ToString()); } catch { ftype = "?"; }
                try { health = f.HealthStatus.ToString(); } catch { health = "unknown"; }
                bool supp = false;
                try { supp = f.Suppressed; } catch { }
                bool healthy = supp || string.Equals(health, "kUpToDateHealth", StringComparison.OrdinalIgnoreCase);
                if (!healthy) unhealthyFeatures++;
                features.Add(new JObject
                {
                    ["name"] = fname,
                    ["type"] = ftype,
                    ["suppressed"] = supp,
                    ["health"] = health,
                    ["healthy"] = healthy,
                });
                if (features.Count >= maxFeatures) break;
            }
        }
        catch { /* some part types expose features oddly; keep what we have */ }

        // ---- sketches + their dimension constraints ----
        var sketches = new JArray();
        try
        {
            foreach (PlanarSketch sk in pdef.Sketches)
            {
                var dims = new JArray();
                try
                {
                    foreach (DimensionConstraint dc in sk.DimensionConstraints)
                    {
                        string dname = "?", expr = "?", kind = "?";
                        try { dname = dc.Parameter.Name; } catch { }
                        try { expr = dc.Parameter.Expression; } catch { }
                        try { kind = ShortDim(dc.Type.ToString()); } catch { }
                        dims.Add(new JObject { ["name"] = dname, ["expression"] = expr, ["kind"] = kind });
                        if (dims.Count >= maxDims) break;
                    }
                }
                catch { /* ignore this sketch's dim quirks */ }

                string skname; try { skname = sk.Name; } catch { skname = "?"; }
                sketches.Add(new JObject
                {
                    ["name"] = skname,
                    ["dimension_count"] = dims.Count,
                    ["dimensions"] = dims,
                });
            }
        }
        catch { /* ignore */ }

        return Ok(ctx, new JObject
        {
            ["part"] = doc.DisplayName,
            ["is_sheet_metal"] = doc.ComponentDefinition is SheetMetalComponentDefinition,
            ["feature_count"] = features.Count,
            ["healthy"] = unhealthyFeatures == 0,
            ["unhealthy_feature_count"] = unhealthyFeatures,
            ["features"] = features,
            ["sketch_count"] = sketches.Count,
            ["sketches"] = sketches,
            ["note"] = "Ищите нужный драйвер по kind=radius/diameter/linear и значению в expression; меняйте его через vent_drive_dimension и после изменения снова проверяйте healthy/unhealthy_feature_count.",
        });
    }

    private static string ShortType(string t)
    {
        // "kExtrudeFeatureObject" -> "Extrude"
        if (t.StartsWith("k")) t = t.Substring(1);
        if (t.EndsWith("Object")) t = t.Substring(0, t.Length - "Object".Length);
        if (t.EndsWith("Feature")) t = t.Substring(0, t.Length - "Feature".Length);
        return t;
    }

    private static string ShortDim(string t)
    {
        // "kRadiusDimConstraintObject" -> "radius"
        t = ShortType(t);
        if (t.EndsWith("DimConstraint")) t = t.Substring(0, t.Length - "DimConstraint".Length);
        return t.ToLowerInvariant();
    }
}
#endif
