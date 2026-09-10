#if INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Collections.Generic;
using System.Linq;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// Deterministically validates a motor/flange bolt-hole pattern from actual HoleFeature centers.
/// All public lengths are millimetres and the optional occurrence transform is applied before comparison.
/// </summary>
public sealed class CheckMountingPatternHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_check_mounting_pattern";
    public bool IsReadOnly => true;
    private const double CmToMm = 10.0;

    private sealed class HolePoint
    {
        public Point Point { get; set; } = null!;
        public double? DiameterMm { get; set; }
        public string Feature { get; set; } = "";
    }

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? active;
        try { active = app.ActiveDocument; } catch { active = null; }
        if (active is null) return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active Inventor document");

        string occurrenceName = (p["occurrence"]?.Value<string>() ?? "").Trim();
        string plane = (p["plane"]?.Value<string>() ?? "xy").Trim().ToLowerInvariant();
        int expectedCount = p["hole_count"]?.Value<int?>() ?? 0;
        double expectedBcd = p["bolt_circle_diameter_mm"]?.Value<double?>() ?? 0;
        double? expectedHoleDiameter = p["hole_diameter_mm"]?.Value<double?>();
        double tolerance = p["tolerance_mm"]?.Value<double?>() ?? 0.25;
        double angleTolerance = p["angle_tolerance_deg"]?.Value<double?>() ?? 1.0;
        if (plane is not ("xy" or "xz" or "yz"))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "plane must be xy|xz|yz");
        if (expectedCount < 2 || expectedCount > 128 || expectedBcd <= 0 || tolerance <= 0 || angleTolerance <= 0)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "hole_count 2..128, positive bolt_circle_diameter_mm/tolerance_mm/angle_tolerance_deg are required");

        PartDocument part;
        Matrix? transform = null;
        string target;
        if (active is PartDocument activePart && string.IsNullOrWhiteSpace(occurrenceName))
        {
            part = activePart;
            target = part.DisplayName;
        }
        else if (active is AssemblyDocument asm && !string.IsNullOrWhiteSpace(occurrenceName))
        {
            ComponentOccurrence? occurrence = FindOccurrence(asm.ComponentDefinition.Occurrences, occurrenceName);
            if (occurrence is null || occurrence.Definition.Document is not PartDocument occurrencePart)
                return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                    "occurrence was not found or is not a part: " + occurrenceName);
            part = occurrencePart;
            transform = occurrence.Transformation;
            target = occurrence.Name;
        }
        else
        {
            return Fail(ctx, InventorErrorCodes.WRONG_DOCUMENT_TYPE,
                "use an active part, or an active assembly plus occurrence");
        }

        var holes = new List<HolePoint>();
        try
        {
            foreach (HoleFeature feature in part.ComponentDefinition.Features.HoleFeatures)
            {
                double? diameter = null;
                try { diameter = feature.HoleDiameter.Value * CmToMm; } catch { }
                foreach (Point rawPoint in feature.HoleCenterPoints)
                {
                    Point point = app.TransientGeometry.CreatePoint(rawPoint.X, rawPoint.Y, rawPoint.Z);
                    if (transform is not null) point.TransformBy(transform);
                    holes.Add(new HolePoint { Point = point, DiameterMm = diameter, Feature = feature.Name });
                }
            }
        }
        catch (Exception ex)
        {
            return Fail(ctx, InventorErrorCodes.API_ERROR, "cannot inspect hole features: " + ex.Message);
        }

        var candidates = expectedHoleDiameter.HasValue
            ? holes.Where(h => h.DiameterMm.HasValue && Math.Abs(h.DiameterMm.Value - expectedHoleDiameter.Value) <= tolerance).ToList()
            : holes;
        if (candidates.Count == 0)
            return Ok(ctx, Failure(target, "no matching HoleFeature centers found", holes.Count));

        double[]? suppliedCenter = p["center_mm"] is JArray centerArray && centerArray.Count == 3
            ? centerArray.Select(x => x.Value<double>()).ToArray()
            : null;
        var projected = candidates.Select(h => Project(h.Point, plane)).ToList();
        double centerU = suppliedCenter is null ? projected.Average(x => x.U) : Project(suppliedCenter, plane).U;
        double centerV = suppliedCenter is null ? projected.Average(x => x.V) : Project(suppliedCenter, plane).V;
        double expectedRadius = expectedBcd / 2.0;

        // A mounting plate can contain unrelated holes with the same diameter. When the approved motor
        // recipe supplies its center, select only centers near the expected bolt circle; an off-circle
        // intended hole then appears as a count failure instead of corrupting the calculated center.
        if (suppliedCenter is not null)
        {
            var selected = candidates.Zip(projected, (hole, point) => new { Hole = hole, Point = point })
                .Where(item => Math.Abs(
                    Math.Sqrt(Math.Pow(item.Point.U - centerU, 2) + Math.Pow(item.Point.V - centerV, 2)) - expectedRadius) <= tolerance)
                .ToList();
            candidates = selected.Select(item => item.Hole).ToList();
            projected = selected.Select(item => item.Point).ToList();
            if (candidates.Count == 0)
                return Ok(ctx, Failure(target, "no matching holes lie on the supplied bolt circle", holes.Count));
        }

        var polar = projected.Select((pt, index) => new
        {
            Hole = candidates[index],
            U = pt.U,
            V = pt.V,
            Radius = Math.Sqrt(Math.Pow(pt.U - centerU, 2) + Math.Pow(pt.V - centerV, 2)),
            Angle = NormalizeDegrees(Math.Atan2(pt.V - centerV, pt.U - centerU) * 180.0 / Math.PI),
        }).OrderBy(x => x.Angle).ToList();

        double maxRadiusError = polar.Max(x => Math.Abs(x.Radius - expectedRadius));
        double expectedStep = 360.0 / expectedCount;
        double maxStepError = 0;
        if (polar.Count == expectedCount)
        {
            for (int i = 0; i < polar.Count; i++)
            {
                double next = i == polar.Count - 1 ? polar[0].Angle + 360 : polar[i + 1].Angle;
                maxStepError = Math.Max(maxStepError, Math.Abs((next - polar[i].Angle) - expectedStep));
            }
        }
        else maxStepError = 360;

        bool pass = polar.Count == expectedCount && maxRadiusError <= tolerance && maxStepError <= angleTolerance;
        var checks = new JArray
        {
            Check("hole_count", polar.Count == expectedCount, polar.Count, expectedCount),
            Check("bolt_circle_radius", maxRadiusError <= tolerance, Math.Round(maxRadiusError, 4), tolerance),
            Check("angular_spacing", maxStepError <= angleTolerance, Math.Round(maxStepError, 4), angleTolerance),
        };
        var points = new JArray(polar.Select(x => new JObject
        {
            ["feature"] = x.Hole.Feature,
            ["diameter_mm"] = x.Hole.DiameterMm,
            ["u_mm"] = Math.Round(x.U, 4),
            ["v_mm"] = Math.Round(x.V, 4),
            ["radius_mm"] = Math.Round(x.Radius, 4),
            ["angle_deg"] = Math.Round(x.Angle, 4),
        }));
        return Ok(ctx, new JObject
        {
            ["pass"] = pass,
            ["target"] = target,
            ["plane"] = plane,
            ["center_mm"] = new JArray(Math.Round(centerU, 4), Math.Round(centerV, 4)),
            ["bolt_circle_diameter_mm"] = expectedBcd,
            ["expected_hole_diameter_mm"] = expectedHoleDiameter,
            ["all_hole_centers"] = holes.Count,
            ["matching_hole_centers"] = candidates.Count,
            ["checks"] = checks,
            ["points"] = points,
        });
    }

    private static JObject Failure(string target, string message, int count) => new()
    {
        ["pass"] = false,
        ["target"] = target,
        ["all_hole_centers"] = count,
        ["checks"] = new JArray(Check("matching_holes", false, 0, 1)),
        ["message"] = message,
    };

    private static JObject Check(string rule, bool pass, double actual, double expected) => new()
    {
        ["rule"] = rule,
        ["pass"] = pass,
        ["actual"] = actual,
        ["expected"] = expected,
    };

    private static (double U, double V) Project(Point point, string plane) => plane switch
    {
        "xy" => (point.X * CmToMm, point.Y * CmToMm),
        "xz" => (point.X * CmToMm, point.Z * CmToMm),
        _ => (point.Y * CmToMm, point.Z * CmToMm),
    };

    private static (double U, double V) Project(double[] point, string plane) => plane switch
    {
        "xy" => (point[0], point[1]),
        "xz" => (point[0], point[2]),
        _ => (point[1], point[2]),
    };

    private static double NormalizeDegrees(double value)
    {
        value %= 360;
        return value < 0 ? value + 360 : value;
    }

    private static ComponentOccurrence? FindOccurrence(System.Collections.IEnumerable occurrences, string name)
    {
        foreach (ComponentOccurrence occurrence in occurrences)
        {
            if (string.Equals(occurrence.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(StripOccurrenceIndex(occurrence.Name), StripOccurrenceIndex(name), StringComparison.OrdinalIgnoreCase))
                return occurrence;
            try
            {
                if (occurrence.DefinitionDocumentType == DocumentTypeEnum.kAssemblyDocumentObject)
                {
                    var nested = FindOccurrence(occurrence.SubOccurrences, name);
                    if (nested is not null) return nested;
                }
            }
            catch { }
        }
        return null;
    }

    private static string StripOccurrenceIndex(string value)
    {
        int colon = value.LastIndexOf(':');
        return colon > 0 && int.TryParse(value.Substring(colon + 1), out _) ? value.Substring(0, colon) : value;
    }
}
#endif
