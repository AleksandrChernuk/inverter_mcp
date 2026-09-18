using System.IO.Pipes;
using Bimwright.Ipt.Server;
using Bimwright.Ipt.Server.Tools;
using Bimwright.Ipt.Shared.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Ipt.Tests;

/// <summary>
/// Inventor-free wire contract for the whole <c>vent</c> server surface: every vent tool that talks to the
/// add-in must emit the exact wire command name and the parameter keys the handler reads. We stand up a fake
/// named-pipe plugin, invoke each <see cref="VentTools"/> method and assert the captured envelope — no
/// Inventor needed (the COM handler bodies are verified on the 2026 machine, not here). Mirrors
/// AssemblyToolWireTests.
/// </summary>
public sealed class VentToolsWireTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ipt-vent-wire-" + Guid.NewGuid().ToString("N"));
    private readonly string _pipeName = "BimwrightVentTests-" + Guid.NewGuid().ToString("N");
    private readonly PluginClient _client;
    private readonly VentTools _tools;

    public VentToolsWireTests()
    {
        Directory.CreateDirectory(_dir);
        var descriptor = new TargetDescriptor
        {
            TargetId = "inventor-2027-" + Environment.ProcessId,
            InventorYear = 2027,
            ProcessId = Environment.ProcessId,
            HostApp = "Inventor",
            Transport = "pipe",
            PipeName = _pipeName,
            AuthToken = "test-token",
            LastHeartbeatUtc = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(Path.Combine(_dir, "target.json"), JsonConvert.SerializeObject(descriptor));
        _client = new PluginClient(new InventorMcpConfig { DescriptorDirectory = _dir, TimeoutMs = 5000 });
        _tools = new VentTools(_client);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task Clone_and_save_tools_emit_expected_wire()
    {
        AssertEnvelope(await CaptureAsync(() => _tools.CloneRecodeProduct(
                "C:\\src", "C:\\dst", "TOP.iam", "ВКР 6,3", "ВКР 7,1")),
            "vent_clone_recode_product",
            "source_root", "destination_root", "top_document", "source_code", "target_code",
            "update_iproperties", "dry_run");

        AssertEnvelope(await CaptureAsync(() => _tools.SavePartAs("C:\\dst\\part.ipt")),
            "vent_save_part_as", "output_path");
        AssertEnvelope(await CaptureAsync(() => _tools.SaveProduct("C:\\dst")),
            "vent_save_product", "product_root");
        AssertEnvelope(await CaptureAsync(() => _tools.ActivateProject("C:\\dst\\prod.ipj")),
            "vent_activate_project", "path");
        AssertEnvelope(await CaptureAsync(() => _tools.OpenProduct("C:\\dst\\TOP.iam")),
            "vent_open_product", "path");
    }

    [Fact]
    public async Task Check_and_drawing_and_export_tools_emit_expected_wire()
    {
        AssertEnvelope(await CaptureAsync(() => _tools.SetCasingDischarge(90, "right", "Rd")),
            "vent_set_discharge", "angle", "hand", "param_name");
        AssertEnvelope(await CaptureAsync(() => _tools.CheckPart()), "vent_check_part");
        AssertEnvelope(await CaptureAsync(() => _tools.CheckMountingPattern(4, 200.0, "xy")),
            "vent_check_mounting_pattern", "hole_count", "bolt_circle_diameter_mm", "plane");
        AssertEnvelope(await CaptureAsync(() => _tools.MakeGabarit("C:\\out\\g.pdf")),
            "vent_make_drawing", "output_path");
        AssertEnvelope(await CaptureAsync(() => _tools.BatchFlatDxf("C:\\out\\dxf")),
            "vent_batch_flat_dxf", "output_dir");
        AssertEnvelope(await CaptureAsync(() => _tools.BatchPdfDrawings("C:\\dwg", "C:\\out\\pdf")),
            "vent_batch_pdf_drawings", "drawings_dir", "output_dir", "recursive");
        AssertEnvelope(await CaptureAsync(() => _tools.BomReport()), "vent_bom_report");
    }

    [Fact]
    public async Task Parametrization_tools_emit_expected_wire()
    {
        AssertEnvelope(await CaptureAsync(() => _tools.InspectParametrization()), "vent_inspect_parametrization");
        AssertEnvelope(await CaptureAsync(() => _tools.RunILogic("MainRule", false, "Size", "7.1")),
            "vent_run_ilogic", "rule", "run_all", "set_name", "set_value", "external", "allow_in_place");
        AssertEnvelope(await CaptureAsync(() => _tools.SelectIPartMember(
                "ВКР 7,1", null, new Dictionary<string, string> { ["Розмір"] = "7,1" })),
            "vent_select_ipart_member", "member", "keys", "allow_in_place");
        AssertEnvelope(await CaptureAsync(() => _tools.MakeComponents(true)),
            "vent_make_components", "execute", "allow_in_place");
        AssertEnvelope(await CaptureAsync(() => _tools.ImportParams(
                new Dictionary<string, string> { ["D"] = "630 mm" }, null, "mm")),
            "vent_import_params", "parameters", "default_units", "allow_in_place");
    }

    [Fact]
    public async Task Make_part_unique_emits_expected_wire()
    {
        AssertEnvelope(await CaptureAsync(() => _tools.MakePartUnique(
                "Лопатка:1", "C:\\dst\\product", "Библиотека", "Лопатка_local", true, false)),
            "vent_make_part_unique",
            "occurrence", "product_root", "dest_subdir", "new_name", "all_instances", "overwrite");
    }

    [Fact]
    public async Task Edit_and_inspect_and_plan_tools_emit_expected_wire()
    {
        AssertEnvelope(await CaptureAsync(() => _tools.InspectModel()), "vent_inspect_model");
        AssertEnvelope(await CaptureAsync(() => _tools.DriveDimension("d0", "340 mm")),
            "vent_drive_dimension", "name", "value", "allow_in_place");
        AssertEnvelope(await CaptureAsync(() => _tools.ScalePart(1.2)),
            "vent_scale_part", "factor", "preserve_thickness", "allow_in_place");
        AssertEnvelope(await CaptureAsync(() => _tools.SetComponentParameter("Диск:1", "d0", "340 mm")),
            "vent_set_component_parameter", "occurrence", "name", "value", "allow_in_place");
        AssertEnvelope(await CaptureAsync(() => _tools.SetConstraint("Заподлицо:9", "5 mm")),
            "vent_set_constraint", "name", "value");
        AssertEnvelope(await CaptureAsync(() => _tools.InspectSketch("Эскиз3")),
            "vent_inspect_sketch", "sketch");

        var mutations = new[] { new VentPlanMutationDto { Kind = "drive_dimension", Name = "d0", Value = "10 mm" } };
        var checks = new[] { new VentPlanCheckDto { Kind = "model_health" } };
        AssertEnvelope(await CaptureAsync(() => _tools.ExecutePlan(mutations, checks)),
            "vent_execute_plan", "mutations", "checks", "dry_run");
    }

    // ---- pipe capture harness (mirrors AssemblyToolWireTests) ----
    private async Task<JObject> CaptureAsync(Func<Task<string>> invoke)
    {
        using var server = new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var receive = ReceiveOnceAsync(server);
        var invokeTask = invoke();
        var envelope = await receive;
        await invokeTask;
        return envelope;
    }

    private static async Task<JObject> ReceiveOnceAsync(NamedPipeServerStream server)
    {
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, leaveOpen: true);
        using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
        var envelope = JObject.Parse((await reader.ReadLineAsync())!);
        await writer.WriteLineAsync(new JObject
        {
            ["id"] = envelope["id"],
            ["ok"] = true,
            ["data"] = new JObject { ["accepted"] = true },
            ["meta"] = new JObject(),
        }.ToString(Formatting.None));
        return envelope;
    }

    private static void AssertEnvelope(JObject envelope, string command, params string[] keys)
    {
        Assert.Equal(command, (string?)envelope["command"]);
        var parameters = Assert.IsType<JObject>(envelope["params"]);
        foreach (var key in keys)
            Assert.True(parameters.ContainsKey(key), $"{command} missing wire key '{key}'");
    }
}
