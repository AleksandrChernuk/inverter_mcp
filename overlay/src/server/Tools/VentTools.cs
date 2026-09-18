using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Ipt.Shared.Contracts;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Ipt.Server.Tools;

public sealed class VentPlanMutationDto
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("occurrence")] public string? Occurrence { get; set; }
    [JsonPropertyName("angle")] public int? Angle { get; set; }
    [JsonPropertyName("hand")] public string? Hand { get; set; }
    [JsonPropertyName("param_name")] public string? ParamName { get; set; }
    [JsonPropertyName("material_name")] public string? MaterialName { get; set; }
}

public sealed class VentPlanMeasureSideDto
{
    [JsonPropertyName("occurrence")] public string Occurrence { get; set; } = "";
    [JsonPropertyName("ref")] public string? Ref { get; set; }
}

public sealed class VentPlanCheckDto
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("occurrence")] public string? Occurrence { get; set; }
    [JsonPropertyName("allowed_unhealthy")] public int? AllowedUnhealthy { get; set; }
    [JsonPropertyName("max_pairs")] public int? MaxPairs { get; set; }
    [JsonPropertyName("occurrences")] public string[]? Occurrences { get; set; }
    [JsonPropertyName("a")] public VentPlanMeasureSideDto? A { get; set; }
    [JsonPropertyName("b")] public VentPlanMeasureSideDto? B { get; set; }
    [JsonPropertyName("min_mm")] public double? MinMm { get; set; }
    [JsonPropertyName("max_mm")] public double? MaxMm { get; set; }
    [JsonPropertyName("min_mass_g")] public double? MinMassG { get; set; }
    [JsonPropertyName("max_mass_g")] public double? MaxMassG { get; set; }
    [JsonPropertyName("min_x_mm")] public double? MinXmm { get; set; }
    [JsonPropertyName("max_x_mm")] public double? MaxXmm { get; set; }
    [JsonPropertyName("min_y_mm")] public double? MinYmm { get; set; }
    [JsonPropertyName("max_y_mm")] public double? MaxYmm { get; set; }
    [JsonPropertyName("min_z_mm")] public double? MinZmm { get; set; }
    [JsonPropertyName("max_z_mm")] public double? MaxZmm { get; set; }
    [JsonPropertyName("plane")] public string? Plane { get; set; }
    [JsonPropertyName("hole_count")] public int? HoleCount { get; set; }
    [JsonPropertyName("bolt_circle_diameter_mm")] public double? BoltCircleDiameterMm { get; set; }
    [JsonPropertyName("hole_diameter_mm")] public double? HoleDiameterMm { get; set; }
    [JsonPropertyName("center_mm")] public double[]? CenterMm { get; set; }
    [JsonPropertyName("tolerance_mm")] public double? ToleranceMm { get; set; }
    [JsonPropertyName("angle_tolerance_deg")] public double? AngleToleranceDeg { get; set; }
}

public sealed class VentSectionViewDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("start_mm")] public double[] StartMm { get; set; } = Array.Empty<double>();
    [JsonPropertyName("end_mm")] public double[] EndMm { get; set; } = Array.Empty<double>();
    [JsonPropertyName("position_mm")] public double[] PositionMm { get; set; } = Array.Empty<double>();
}

public sealed class VentDetailViewDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("center_mm")] public double[] CenterMm { get; set; } = Array.Empty<double>();
    [JsonPropertyName("position_mm")] public double[] PositionMm { get; set; } = Array.Empty<double>();
    [JsonPropertyName("radius_mm")] public double RadiusMm { get; set; }
    [JsonPropertyName("scale")] public double? Scale { get; set; }
}

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

    [McpServerTool(Name = "inventor_vent_clone_recode_product"),
     Description("Pack-and-Go equivalent for a complete Inventor product tree. In dry_run mode (default) " +
                 "returns the exact source→destination map and collision checks without writing. On execution, " +
                 "native documents are SaveCopyAs copies (preserving InternalName), copied assembly/drawing " +
                 "references are rebound with FileDescriptor.ReplaceReference, filenames and core iProperties " +
                 "are recoded, and external shared references are reported but not copied. destination_root must " +
                 "be a NEW folder under an allowed output root; maximum 512 files. A failed filesystem stage leaves " +
                 "INCOMPLETE.json for recovery and never saves the source documents.")]
    public Task<string> CloneRecodeProduct(
        string sourceRoot,
        string destinationRoot,
        string topDocument,
        string sourceCode,
        string targetCode,
        bool updateIproperties = true,
        bool dryRun = true,
        CancellationToken ct = default)
        => Call("vent_clone_recode_product", new JObject
        {
            ["source_root"] = sourceRoot,
            ["destination_root"] = destinationRoot,
            ["top_document"] = topDocument,
            ["source_code"] = sourceCode,
            ["target_code"] = targetCode,
            ["update_iproperties"] = updateIproperties,
            ["dry_run"] = dryRun,
        }, ct);

    [McpServerTool(Name = "inventor_vent_save_part_as"),
     Description("Save the ACTIVE part document as a NEW .ipt at output_path (derive a new part from the " +
                 "current one). output_path must be an absolute path under an allowed output root. Use for " +
                 "single-part derivations; for whole assemblies use vent_new_product (folder clone) or Pack-and-Go.")]
    public Task<string> SavePartAs(string outputPath, CancellationToken ct = default)
        => Call("vent_save_part_as", new JObject { ["output_path"] = outputPath }, ct);

    [McpServerTool(Name = "inventor_vent_save_product"),
     Description("Save the active part/assembly plus every dirty referenced Inventor document under the " +
                 "approved product_root. Uses Document.Save2; refuses dirty external/library documents and " +
                 "returns the exact save ledger. Use after all transactional checks pass.")]
    public Task<string> SaveProduct(string productRoot, CancellationToken ct = default)
        => Call("vent_save_product", new JObject { ["product_root"] = productRoot }, ct);

    [McpServerTool(Name = "inventor_vent_activate_project"),
     Description("Activate a product's Inventor project (.ipj) as the active design project so its Workspace " +
                 "+ Library paths resolve references (ступиці, покупні, материалы) and Inventor stops prompting " +
                 "to locate/open files. Pass a .ipj path, a product folder, or any file inside the product " +
                 "(the nearest .ipj at or above it is used). Inventor cannot switch the active project while " +
                 "documents are open, so call this with NOTHING open (vent_open_product also auto-activates the " +
                 ".ipj when nothing is open). Returns the active project path and whether it changed.")]
    public Task<string> ActivateProject(
        [Description(".ipj path, product folder, or a file inside the product.")] string path,
        CancellationToken ct = default)
        => Call("vent_activate_project", new JObject { ["path"] = path }, ct);

    [McpServerTool(Name = "inventor_vent_open_product"),
     Description("Open a product's top assembly (.iam) or a specific part (.ipt) in Inventor by absolute " +
                 "path (get paths from inventor_vent_list_products). Makes it the active document so the " +
                 "other vent tools operate on it. If nothing is open, the product's .ipj is auto-activated " +
                 "first so library references resolve without prompts.")]
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

    [McpServerTool(Name = "inventor_vent_check_mounting_pattern"),
     Description("Validate the actual motor/flange mounting-hole pattern from HoleFeature centers. Checks hole " +
                 "count, bolt-circle radius and equal angular spacing on plane xy|xz|yz, optionally filtering by " +
                 "hole diameter. For an assembly pass occurrence; for a part omit it. All lengths are mm. Read-only.")]
    public Task<string> CheckMountingPattern(
        int holeCount,
        double boltCircleDiameterMm,
        string plane = "xy",
        string? occurrence = null,
        double? holeDiameterMm = null,
        double[]? centerMm = null,
        double toleranceMm = 0.25,
        double angleToleranceDeg = 1.0,
        CancellationToken ct = default)
        => Call("vent_check_mounting_pattern", new JObject
        {
            ["hole_count"] = holeCount,
            ["bolt_circle_diameter_mm"] = boltCircleDiameterMm,
            ["plane"] = plane,
            ["occurrence"] = occurrence,
            ["hole_diameter_mm"] = holeDiameterMm,
            ["center_mm"] = centerMm is null ? null : new JArray(centerMm),
            ["tolerance_mm"] = toleranceMm,
            ["angle_tolerance_deg"] = angleToleranceDeg,
        }, ct);

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
        [Description("Retrieve model dimensions into the base view.")] bool retrieveDimensions = true,
        [Description("Create associative overall width/height dimensions from visible base-view geometry.")] bool addOverallDimensions = true,
        [Description("For an assembly, require a Parts List.")] bool requirePartsList = false,
        [Description("For an assembly, require automatic balloons and enforce minimumBalloons.")] bool requireBalloons = false,
        [Description("Minimum balloons required when requireBalloons=true.")] int minimumBalloons = 1,
        [Description("Require a non-empty associative hole table for the base view.")] bool requireHoleTable = false,
        [Description("Minimum retrieved dimensions required for this drawing to pass.")] int minimumDimensions = 0,
        [Description("Numbered technical requirements placed on the sheet.")] string[]? technicalNotes = null,
        [Description("Explicit section-line recipes in sheet coordinates (mm).")] VentSectionViewDto[]? sectionViews = null,
        [Description("Explicit detail-view recipes in sheet coordinates (mm).")] VentDetailViewDto[]? detailViews = null,
        CancellationToken ct = default)
        => Call("vent_make_drawing", new JObject
        {
            ["output_path"] = outputPath,
            ["scale"] = scale,
            ["export_dxf"] = exportDxf,
            ["template"] = template,
            ["retrieve_dimensions"] = retrieveDimensions,
            ["add_overall_dimensions"] = addOverallDimensions,
            ["require_parts_list"] = requirePartsList,
            ["require_balloons"] = requireBalloons,
            ["minimum_balloons"] = minimumBalloons,
            ["require_hole_table"] = requireHoleTable,
            ["minimum_dimensions"] = minimumDimensions,
            ["technical_notes"] = technicalNotes is null ? new JArray() : new JArray(technicalNotes),
            ["section_views"] = sectionViews is null ? new JArray() : JArray.FromObject(sectionViews),
            ["detail_views"] = detailViews is null ? new JArray() : JArray.FromObject(detailViews),
        }, ct);

    [McpServerTool(Name = "inventor_vent_batch_flat_dxf"),
     Description("Export flat-pattern DXF for every sheet-metal part of the active assembly (or of a folder of " +
                 ".ipt files) into output_dir. Skips non-sheet-metal parts. Returns per-part status. output_dir " +
                 "must be an absolute path under an allowed output root. Long-running: prefer running per product.")]
    public Task<string> BatchFlatDxf(
        string outputDir,
        [Description("Folder of .ipt files to process instead of the active assembly's parts. Optional.")] string? partsDir = null,
        [Description("Optional mapping from Inventor material names to factory filename aliases such as Ст3.")] Dictionary<string, string>? materialAliases = null,
        [Description("Create, rebuild and save missing flat patterns before export. Default false; explicit opt-in because this changes parts.")] bool createMissingFlatPatterns = false,
        CancellationToken ct = default)
        => Call("vent_batch_flat_dxf", new JObject
        {
            ["output_dir"] = outputDir,
            ["parts_dir"] = partsDir,
            ["material_aliases"] = materialAliases is null ? new JObject() : JObject.FromObject(materialAliases),
            ["create_missing_flat_patterns"] = createMissingFlatPatterns,
        }, ct);

    [McpServerTool(Name = "inventor_vent_batch_pdf_drawings"),
     Description("Export every .idw/.dwg in a product drawing tree to PDF, preserving relative subfolders. " +
                 "Returns a per-drawing ledger and pass=false if any translation failed. output_dir must be under " +
                 "an allowed export root; maximum 256 drawings.")]
    public Task<string> BatchPdfDrawings(
        string drawingsDir,
        string outputDir,
        bool recursive = true,
        CancellationToken ct = default)
        => Call("vent_batch_pdf_drawings", new JObject
        {
            ["drawings_dir"] = drawingsDir,
            ["output_dir"] = outputDir,
            ["recursive"] = recursive,
        }, ct);

    [McpServerTool(Name = "inventor_vent_bom_report"),
     Description("Material/cut report for the active assembly (\"спецификация\"): per part the designation, material, " +
                 "thickness, quantity, flat size, area and cut length, plus totals by thickness. Read-only.")]
    public Task<string> BomReport(CancellationToken ct = default)
        => Call("vent_bom_report", new JObject(), ct);

    [McpServerTool(Name = "inventor_vent_inspect_parametrization"),
     Description("Read-only recon of the ACTIVE part/assembly's parametrization: whether it is an " +
                 "iPart/iAssembly factory or member (with table columns, key columns and member count), the " +
                 "iLogic rules present (names), and the user parameters (name+expression). Use it FIRST to " +
                 "decide how to drive a size variant (типорозмір): by an iLogic key parameter, by selecting an " +
                 "iPart/iAssembly row, or that the part is not parametrized yet. Works on Inventor 2021+.")]
    public Task<string> InspectParametrization(CancellationToken ct = default)
        => Call("vent_inspect_parametrization", new JObject(), ct);

    [McpServerTool(Name = "inventor_vent_run_ilogic"),
     Description("Drive a size variant (типорозмір) via iLogic (method 2): optionally set a KEY parameter " +
                 "(set_name + set_value) on the active part/assembly, then run an iLogic rule that computes the " +
                 "dependent parameters, rebuild, and report the new bounding box (mm) and mass (g). Give 'rule' " +
                 "(rule name from inventor_vent_inspect_parametrization) or run_all=true; external=true for an " +
                 "external rule. Requires the iLogic add-in loaded. NOT saved to disk. Guarded by copy-before-resize: " +
                 "master-catalog files are refused unless allow_in_place=true (clone the product first).")]
    public Task<string> RunILogic(
        [Description("iLogic rule name to run (from inventor_vent_inspect_parametrization). Omit to only set a key parameter or with run_all.")] string? rule = null,
        [Description("Run every iLogic rule on the document, in order. Default false.")] bool runAll = false,
        [Description("Key parameter to set before running (e.g. the типорозмір driver). Optional.")] string? setName = null,
        [Description("Value/expression for set_name, e.g. \"400 mm\" or \"3.15\". Required if set_name is given.")] string? setValue = null,
        [Description("Treat 'rule' as an external rule (RunExternalRule). Default false.")] bool external = false,
        [Description("Allow running on a part/assembly in a protected/master location in place. Default false.")] bool allowInPlace = false,
        CancellationToken ct = default)
        => Call("vent_run_ilogic", new JObject
        {
            ["rule"] = rule,
            ["run_all"] = runAll,
            ["set_name"] = setName,
            ["set_value"] = setValue,
            ["external"] = external,
            ["allow_in_place"] = allowInPlace,
        }, ct);

    [McpServerTool(Name = "inventor_vent_select_ipart_member"),
     Description("Drive a size variant (типорозмір) via an iPart/iAssembly table (method 1): select a row of " +
                 "the ACTIVE factory document by 'member' name, by 'keys' (key column -> value), or by 1-based " +
                 "'row' index, and generate/activate that member. On a factory CreateMember spawns the concrete " +
                 "member (типорозмір); on a member document it switches the row (ChangeRow). Returns the selected " +
                 "row cells and the resulting bounding box (mm) + mass (g). Use inventor_vent_inspect_parametrization " +
                 "FIRST to see the columns, key columns and member names. NOT saved to disk. Guarded by " +
                 "copy-before-resize: master-catalog factories are refused unless allow_in_place=true.")]
    public Task<string> SelectIPartMember(
        [Description("Member name to select (from inventor_vent_inspect_parametrization). Optional.")] string? member = null,
        [Description("1-based table row index to select. Optional.")] int? row = null,
        [Description("Key column -> value map to find the row, e.g. {\"Розмір\":\"7,1\"}. Units/decimal comma tolerant. Optional.")] Dictionary<string, string>? keys = null,
        [Description("Allow generating a member for a factory in a protected/master location. Default false.")] bool allowInPlace = false,
        CancellationToken ct = default)
        => Call("vent_select_ipart_member", new JObject
        {
            ["member"] = member,
            ["row"] = row,
            ["keys"] = keys is null ? null : JObject.FromObject(keys),
            ["allow_in_place"] = allowInPlace,
        }, ct);

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
        [Description("Allow editing a part that lives in a protected/master location in place. Default false " +
                     "(safe): parts in the master catalog are refused because a shared/library part would change " +
                     "in every product — clone the product first and resize the copy. Set true only for a product-unique part.")]
        bool allowInPlace = false,
        CancellationToken ct = default)
        => Call("vent_drive_dimension",
                new JObject { ["name"] = name, ["value"] = value, ["allow_in_place"] = allowInPlace }, ct);

    [McpServerTool(Name = "inventor_vent_scale_part"),
     Description("Uniformly scale the ACTIVE part by a factor (e.g. 1.2 = +20%): multiplies every INDEPENDENT " +
                 "length dimension, leaving angles, counts, sheet-metal reference parameters and (by default) the " +
                 "sheet thickness untouched; derived parameters follow automatically. Use to enlarge a whole part " +
                 "PROPORTIONALLY so mating features stay aligned (e.g. blade seat vs disk slot) — unlike " +
                 "drive_dimension which changes one dimension and can break the fit. allow_in_place is required for " +
                 "master-catalog parts (clone the product first). Rebuilds and reports bbox+mass. NOT saved to disk.")]
    public Task<string> ScalePart(
        [Description("Scale factor, e.g. 1.2 for +20%, 0.95 for -5%.")] double factor,
        [Description("Keep the sheet-metal thickness unscaled (default true).")] bool preserveThickness = true,
        [Description("Allow scaling a part in a protected/master location (only if unique to one product).")] bool allowInPlace = false,
        CancellationToken ct = default)
        => Call("vent_scale_part", new JObject
        {
            ["factor"] = factor,
            ["preserve_thickness"] = preserveThickness,
            ["allow_in_place"] = allowInPlace,
        }, ct);

    [McpServerTool(Name = "inventor_vent_set_component_parameter"),
     Description("Assembly editor: in the ACTIVE assembly, set a parameter of a named component (occurrence, " +
                 "e.g. \"КВЗ...Диск нижній:1\" from inventor_get_assembly_bom; the \":N\" suffix is optional) by " +
                 "name+value, rebuild, and report the assembly's new bounding box and mass. Drive part dimensions " +
                 "from the top assembly without opening each part. Changes the shared part file; NOT saved to disk.")]
    public Task<string> SetComponentParameter(
        [Description("Component/occurrence name, e.g. from inventor_get_assembly_bom.")] string occurrence,
        string name,
        [Description("New expression, e.g. \"340 mm\".")] string value,
        [Description("Allow editing a component whose part is in a protected/master location in place. Default " +
                     "false (safe): master-catalog parts are refused because a shared/library part would change in " +
                     "every product — clone the product first and edit the copy. Set true only for a product-unique part.")]
        bool allowInPlace = false,
        CancellationToken ct = default)
        => Call("vent_set_component_parameter",
                new JObject { ["occurrence"] = occurrence, ["name"] = name, ["value"] = value, ["allow_in_place"] = allowInPlace }, ct);

    [McpServerTool(Name = "inventor_vent_set_constraint"),
     Description("Assembly editor (constraints): in the ACTIVE assembly, change a named constraint's driving " +
                 "value — offset of a mate/flush/insert, or angle of an angle constraint — then rebuild and " +
                 "report the assembly's new bounding box and mass. Names from inventor_list_constraints " +
                 "(e.g. \"Заподлицо:9\"); value like \"5 mm\" or \"30 deg\". NOT saved to disk.")]
    public Task<string> SetConstraint(
        [Description("Constraint name, e.g. from inventor_list_constraints.")] string name,
        [Description("New offset/angle expression, e.g. \"5 mm\" or \"30 deg\".")] string value,
        CancellationToken ct = default)
        => Call("vent_set_constraint", new JObject { ["name"] = name, ["value"] = value }, ct);

    [McpServerTool(Name = "inventor_vent_inspect_sketch"),
     Description("Look INSIDE one sketch of the active part: points, lines, arcs, circles (coords in mm + radial " +
                 "distance from the sketch origin) and geometric-constraint counts by type. Use to judge whether a " +
                 "driving dimension can be added (needs a free DOF + a target). Pass sketch=name (from inventor_vent_inspect_model). Read-only.")]
    public Task<string> InspectSketch(
        [Description("Sketch name, e.g. \"Эскиз3\" from inventor_vent_inspect_model.")] string sketch,
        CancellationToken ct = default)
        => Call("vent_inspect_sketch", new JObject { ["sketch"] = sketch }, ct);

    [McpServerTool(Name = "inventor_vent_execute_plan"),
     Description("Execute a bounded autonomous engineering plan against the ACTIVE part/assembly. Mutations " +
                 "(drive_dimension, set_component_parameter, set_constraint, set_casing_discharge, set_material) " +
                 "run in one Inventor transaction, followed by Update2(false) and objective checks " +
                 "(model_health, part_cut_ready, constraints_healthy, no_interference, min_distance, physical_bounds, mounting_pattern). " +
                 "Mutations may be empty for an independent validation-only pass. Any failed mutation/rebuild/check " +
                 "aborts the active-document transaction and restores the referenced-document snapshot. A passing run " +
                 "does NOT save; call inventor_vent_save_product only after approval. dryRun defaults true and only " +
                 "validates the plan. Limits: 256 mutations and 64 checks; arbitrary code is not supported.")]
    public Task<string> ExecutePlan(
        [Description("Typed mutations in dependency order; every item needs a supported kind and its kind-specific fields.")]
        VentPlanMutationDto[] mutations,
        [Description("Objective acceptance checks evaluated after the final rebuild. At least one check is required.")]
        VentPlanCheckDto[] checks,
        [Description("When true (default), validate only and do not touch the model. Pass false to execute.")]
        bool dryRun = true,
        CancellationToken ct = default)
        => Call("vent_execute_plan", new JObject
        {
            ["mutations"] = JArray.FromObject(mutations ?? Array.Empty<VentPlanMutationDto>()),
            ["checks"] = JArray.FromObject(checks ?? Array.Empty<VentPlanCheckDto>()),
            ["dry_run"] = dryRun,
        }, ct);

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
