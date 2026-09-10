using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Ipt.Shared.Contracts;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Ipt.Server.Tools;

/// <summary>
/// Vent-factory domain toolset (<c>vent</c>). A thin Domain-Specific Adapter over the base
/// parameter / feature / export / assembly tools: it speaks the constructor's language
/// ("изделие", "разворот корпуса", "габаритка", "проверка перед резкой") and maps it onto
/// wire commands handled by the add-in. All lengths are millimetres, all angles degrees.
///
/// Write-capable tools (set_casing_discharge, make_gabarit, batch_flat_dxf) are hidden under
/// --read-only; list_products / check_part / bom_report are read-only and always available.
/// </summary>
[McpServerToolType]
public sealed class VentTools
{
    private readonly PluginClient _client;
    public VentTools(PluginClient client) => _client = client;

    [McpServerTool(Name = "inventor_vent_list_products"),
     Description("List vent products (изделия) found under the catalog root: for each product folder, its name " +
                 "and the top assembly (.iam) plus part files (.ipt/.dxf) it contains. Pass catalog_root as an " +
                 "absolute folder path, or omit to use the VENT_CATALOG_ROOT env var. Read-only and server-side " +
                 "— works even before Inventor is running.")]
    public string ListProducts(string? catalogRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(catalogRoot)
            ? Environment.GetEnvironmentVariable("VENT_CATALOG_ROOT")
            : catalogRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return JsonConvert.SerializeObject(new { ok = false, error = new {
                code = "INVALID_ARGUMENT",
                message = "catalog_root not found; pass an absolute path or set VENT_CATALOG_ROOT" } }, Formatting.Indented);

        // A "product" = any folder that directly contains a .iam, or (fallback) that contains .ipt/.dxf.
        var products = new List<object>();
        foreach (var dir in EnumerateDirsSafe(root))
        {
            var iams = SafeFiles(dir, "*.iam");
            var ipts = SafeFiles(dir, "*.ipt");
            var dxfs = SafeFiles(dir, "*.dxf");
            if (iams.Length == 0 && ipts.Length == 0 && dxfs.Length == 0) continue;
            products.Add(new
            {
                name = Path.GetFileName(dir),
                path = dir,
                top_assembly = iams.FirstOrDefault(),
                assemblies = iams,
                parts = ipts,
                dxf = dxfs.Length,
            });
        }
        return JsonConvert.SerializeObject(new { ok = true, count = products.Count, products }, Formatting.Indented);
    }

    private static string[] SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> EnumerateDirsSafe(string root)
    {
        yield return root;
        Stack<string> stack = new(); stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            string[] subs;
            try { subs = Directory.GetDirectories(d); } catch { continue; }
            foreach (var s in subs) { yield return s; stack.Push(s); }
        }
    }

    [McpServerTool(Name = "inventor_vent_new_product"),
     Description("Create a NEW product (изделие) by cloning an existing template product FOLDER to a new " +
                 "folder under the catalog. Copies every file (assembly + parts + drawings) so relative " +
                 "references stay intact. Args: template_dir (absolute path of the product to clone), " +
                 "new_name (folder name for the copy). Optional dest_root (defaults to the template's parent). " +
                 "Returns the new folder and its top .iam. Server-side (no Inventor). AFTER cloning, open the " +
                 "new top assembly and set the size parameters, then regenerate DXF.\n" +
                 "CAVEAT: if the template assembly stores ABSOLUTE part references, the copy will still point " +
                 "at the originals — such families need Inventor Pack-and-Go (see vent_save_part_as / docs).")]
    public string NewProduct(string templateDir, string newName, string? destRoot = null)
    {
        if (string.IsNullOrWhiteSpace(templateDir) || !Directory.Exists(templateDir))
            return Err("INVALID_ARGUMENT", "template_dir not found: " + templateDir);
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Err("INVALID_ARGUMENT", "new_name is empty or has invalid characters");

        var parent = string.IsNullOrWhiteSpace(destRoot) ? Path.GetDirectoryName(templateDir.TrimEnd('\\', '/')) : destRoot;
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            return Err("INVALID_ARGUMENT", "dest_root not found: " + parent);

        var dest = Path.Combine(parent!, newName);
        if (Directory.Exists(dest))
            return Err("INVALID_ARGUMENT", "destination already exists: " + dest);

        try
        {
            CopyDir(templateDir, dest);
        }
        catch (Exception ex)
        {
            return Err("API_ERROR", "copy failed: " + ex.Message);
        }

        var topIam = Directory.GetFiles(dest, "*.iam", SearchOption.TopDirectoryOnly);
        return JsonConvert.SerializeObject(new
        {
            ok = true,
            product_dir = dest,
            top_assembly = topIam.Length > 0 ? topIam[0] : null,
            parts = Directory.GetFiles(dest, "*.ipt", SearchOption.TopDirectoryOnly).Length,
            note = "Откройте top_assembly, задайте параметры, перегенерируйте DXF. ВАЖНО: если в папке есть .ipj — " +
                   "активируйте НОВЫЙ .ipj в Inventor (Pack-and-Go делает ссылки относительными проекту), иначе " +
                   "ссылки укажут на шаблон. Для семейств с абсолютными ссылками используйте полноценный Pack-and-Go.",
        }, Formatting.Indented);
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), false);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    private static string Err(string code, string message)
        => JsonConvert.SerializeObject(new { ok = false, error = new { code, message } }, Formatting.Indented);

    [McpServerTool(Name = "inventor_vent_save_part_as"),
     Description("Save the ACTIVE part document as a NEW .ipt at output_path (derive a new part from the " +
                 "current one). output_path must be an absolute path under an allowed output root. Use for " +
                 "single-part derivations; for whole assemblies use vent_new_product (folder clone) or Pack-and-Go.")]
    public Task<string> SavePartAs(string outputPath, CancellationToken ct = default)
        => Call("vent_save_part_as", new JObject { ["output_path"] = outputPath }, ct);

    [McpServerTool(Name = "inventor_vent_open_product"),
     Description("Open a product's top assembly (.iam) or a specific part (.ipt) in Inventor by absolute " +
                 "path (get paths from inventor_vent_list_products). Makes it the active document so the " +
                 "other vent tools operate on it.")]
    public Task<string> OpenProduct(string path, CancellationToken ct = default)
        => Call("vent_open_product", new JObject { ["path"] = path }, ct);

    [McpServerTool(Name = "inventor_vent_set_casing_discharge"),
     Description("Set the spiral-casing discharge orientation of the active fan model (\"разворот корпуса\"): " +
                 "angle is one of 0/45/90/135/180/225/270/315 degrees, hand is 'right' (правое) or 'left' (левое). " +
                 "Drives the model's discharge parameter(s), updates the document, and reports the new bounding " +
                 "box so you can verify. param_name overrides the auto-detected parameter name if your model differs.")]
    public Task<string> SetCasingDischarge(
        [Description("Discharge angle in degrees: 0,45,90,135,180,225,270,315.")] int angle,
        [Description("Rotation hand: 'right' (правое) or 'left' (левое). Optional.")] string? hand = null,
        [Description("Override the driving parameter name if it is not the standard 'Rd'/'discharge_angle'.")] string? paramName = null,
        CancellationToken ct = default)
        => Call("vent_set_discharge", new JObject
        {
            ["angle"] = angle,
            ["hand"] = hand,
            ["param_name"] = paramName,
        }, ct);

    [McpServerTool(Name = "inventor_vent_check_part"),
     Description("Pre-cut check of the active sheet-metal part's flat pattern (\"проверка контуров перед резкой\"): " +
                 "closed outer profile, hole diameters, minimum hole-to-edge distance and minimum web vs sheet " +
                 "thickness, and overall flat size. Returns a pass/fail list per rule. Read-only.")]
    public Task<string> CheckPart(CancellationToken ct = default)
        => Call("vent_check_part", new JObject(), ct);

    [McpServerTool(Name = "inventor_vent_make_gabarit"),
     Description("Generate an overall-dimension drawing (\"габаритка\") for the active part/assembly: creates a " +
                 "drawing from the idw template, places a base view + projections, retrieves overall dimensions, " +
                 "fills the title block from iProperties, and exports to output_path (.pdf; add export_dxf=true for " +
                 ".dxf too). output_path must be an absolute path under an allowed output root.")]
    public Task<string> MakeGabarit(
        string outputPath,
        [Description("Drawing scale, e.g. 0.1 for 1:10. Omit to auto-fit.")] double? scale = null,
        [Description("Also export the drawing to DXF next to the PDF. Default false.")] bool exportDxf = false,
        [Description("Absolute path to a custom .idw/.dwg template with your title block. Optional.")] string? template = null,
        CancellationToken ct = default)
        => Call("vent_make_drawing", new JObject
        {
            ["output_path"] = outputPath,
            ["scale"] = scale,
            ["export_dxf"] = exportDxf,
            ["template"] = template,
        }, ct);

    [McpServerTool(Name = "inventor_vent_batch_flat_dxf"),
     Description("Export flat-pattern DXF for every sheet-metal part of the active assembly (or of a folder of " +
                 ".ipt files) into output_dir. Skips non-sheet-metal parts. Returns per-part status. output_dir " +
                 "must be an absolute path under an allowed output root. Long-running: prefer running per product.")]
    public Task<string> BatchFlatDxf(
        string outputDir,
        [Description("Folder of .ipt files to process instead of the active assembly's parts. Optional.")] string? partsDir = null,
        CancellationToken ct = default)
        => Call("vent_batch_flat_dxf", new JObject
        {
            ["output_dir"] = outputDir,
            ["parts_dir"] = partsDir,
        }, ct);

    [McpServerTool(Name = "inventor_vent_bom_report"),
     Description("Material/cut report for the active assembly (\"спецификация\"): per part the designation, material, " +
                 "thickness, quantity, flat size, area and cut length, plus totals by thickness. Read-only.")]
    public Task<string> BomReport(CancellationToken ct = default)
        => Call("vent_bom_report", new JObject(), ct);

    [McpServerTool(Name = "inventor_vent_inspect_model"),
     Description("Inspect the ACTIVE part's build tree: features, sketches and every sketch dimension " +
                 "(name, expression, kind=radius/diameter/linear/angle). Use it to find size drivers that " +
                 "are NOT top-level parameters (e.g. a blade-slot placement radius) before changing them. Read-only.")]
    public Task<string> InspectModel(CancellationToken ct = default)
        => Call("vent_inspect_model", new JObject(), ct);

    [McpServerTool(Name = "inventor_vent_drive_dimension"),
     Description("Set a part parameter / named sketch dimension by name and value (e.g. name=\"d0\", " +
                 "value=\"340 mm\"), rebuild the active part, and report the new bounding box and mass — for " +
                 "parametric studies. Change is NOT saved to disk (use inventor_save_document to keep it).")]
    public Task<string> DriveDimension(
        string name,
        [Description("New expression, e.g. \"340 mm\" or a numeric expression.")] string value,
        CancellationToken ct = default)
        => Call("vent_drive_dimension", new JObject { ["name"] = name, ["value"] = value }, ct);

    // ---- helper (mirrors ExportTools.Call) ----
    private async Task<string> Call(string command, JObject p, CancellationToken ct)
    {
        try
        {
            var data = await _client.SendAsync(command, p, ct);
            return JsonConvert.SerializeObject(data, Formatting.Indented);
        }
        catch (InventorGatewayException ex)
        {
            return JsonConvert.SerializeObject(new { ok = false, error = new { code = ex.Code, message = ex.Message } }, Formatting.Indented);
        }
    }
}
