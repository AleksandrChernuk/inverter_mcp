#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Reflection;
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

    // ---- Inventor project (.ipj) resolution & activation ----

    /// <summary>
    /// Find the nearest Inventor project (.ipj) by walking up from a file or folder path.
    /// Returns the first .ipj found in a directory at or above <paramref name="startPath"/>
    /// (stops after <paramref name="maxUp"/> levels), or null if none.
    /// </summary>
    public static string? FindProjectFile(string startPath, int maxUp = 8)
    {
        try
        {
            string? dir = System.IO.Directory.Exists(startPath)
                ? startPath
                : System.IO.Path.GetDirectoryName(startPath);
            for (int i = 0; i < maxUp && !string.IsNullOrEmpty(dir); i++)
            {
                string[] ipjs;
                try { ipjs = System.IO.Directory.GetFiles(dir!, "*.ipj", System.IO.SearchOption.TopDirectoryOnly); }
                catch { ipjs = Array.Empty<string>(); }
                if (ipjs.Length > 0) return ipjs[0];
                dir = System.IO.Path.GetDirectoryName(dir!);
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>Full path of the currently active design project, or "" if none/unavailable.</summary>
    public static string ActiveProjectPath(Application app)
    {
        try { return app.DesignProjectManager.ActiveDesignProject?.FullFileName ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Activate the given .ipj as the active design project. Inventor cannot switch the active
    /// project while any document is open, so this returns an error in that case (unless the
    /// project is already active). Returns (changed, activePath, error): error is null on success.
    /// </summary>
    public static (bool changed, string active, string? error) ActivateProject(Application app, string ipjPath)
    {
        try
        {
            var dpm = app.DesignProjectManager;
            string current = ActiveProjectPath(app);
            if (string.Equals(current, ipjPath, StringComparison.OrdinalIgnoreCase))
                return (false, current, null); // already active

            if (app.Documents.Count > 0)
                return (false, current, "нельзя сменить активный проект, пока открыты документы (закройте все документы и повторите)");

            DesignProject? target = null;
            try
            {
                foreach (DesignProject dp in dpm.DesignProjects)
                {
                    string f = ""; try { f = dp.FullFileName; } catch { }
                    if (string.Equals(f, ipjPath, StringComparison.OrdinalIgnoreCase)) { target = dp; break; }
                }
            }
            catch { /* ignore enumeration issues */ }

            if (target == null)
            {
                try { target = dpm.DesignProjects.AddExisting(ipjPath); }
                catch (Exception ex) { return (false, current, "AddExisting не удалось: " + ex.Message); }
            }

            target.Activate();
            return (true, ActiveProjectPath(app), null);
        }
        catch (Exception ex)
        {
            return (false, ActiveProjectPath(app), ex.Message);
        }
    }

    // ---- iLogic access (late-bound; the iLogic assembly is not referenced) ----

    /// <summary>iLogic add-in ClientId (stable across Inventor versions).</summary>
    public const string ILogicAddInGuid = "{3BDD8D79-2179-4B11-8A5A-257B1C0263AC}";

    /// <summary>
    /// Return the iLogic Automation object (late-bound), or null if the iLogic add-in is not loaded.
    /// Call members via reflection: <c>Rules(doc)</c>, <c>RunRule(doc, name)</c>, <c>RunExternalRule(doc, name)</c>.
    /// </summary>
    public static object? GetILogicAutomation(Application app)
    {
        try
        {
            object addins = app.ApplicationAddIns;
            object ilogic = addins.GetType().InvokeMember(
                "ItemById", BindingFlags.InvokeMethod, null, addins, new object[] { ILogicAddInGuid })!;
            return ilogic.GetType().InvokeMember("Automation", BindingFlags.GetProperty, null, ilogic, null);
        }
        catch { return null; }
    }
}
#endif
