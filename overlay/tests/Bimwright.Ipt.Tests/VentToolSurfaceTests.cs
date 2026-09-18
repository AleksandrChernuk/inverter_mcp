using System.Reflection;
using Bimwright.Ipt.Server.Tools;
using ModelContextProtocol.Server;

namespace Bimwright.Ipt.Tests;

/// <summary>
/// Locks the <c>vent</c> tool surface: the exact set of <c>inventor_vent_*</c> tool names on
/// <see cref="VentTools"/>, that they are all prefixed and unique. Reflection-only — independent of the base
/// integration edits (ToolsetFilter / Program), so it stays green whether or not the fork has wired the
/// toolset in yet. Update the frozen list deliberately when adding a vent tool.
/// </summary>
public sealed class VentToolSurfaceTests
{
    private static string[] VentToolNames() =>
        typeof(VentTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), false)
                          .Cast<McpServerToolAttribute>().FirstOrDefault()?.Name)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToArray();

    private static readonly string[] Expected =
    {
        "inventor_vent_list_products",
        "inventor_vent_new_product",
        "inventor_vent_clone_recode_product",
        "inventor_vent_save_part_as",
        "inventor_vent_save_product",
        "inventor_vent_activate_project",
        "inventor_vent_open_product",
        "inventor_vent_set_casing_discharge",
        "inventor_vent_check_part",
        "inventor_vent_check_mounting_pattern",
        "inventor_vent_make_gabarit",
        "inventor_vent_batch_flat_dxf",
        "inventor_vent_batch_pdf_drawings",
        "inventor_vent_bom_report",
        "inventor_vent_inspect_parametrization",
        "inventor_vent_run_ilogic",
        "inventor_vent_select_ipart_member",
        "inventor_vent_make_part_unique",
        "inventor_vent_make_components",
        "inventor_vent_import_params",
        "inventor_vent_inspect_model",
        "inventor_vent_drive_dimension",
        "inventor_vent_scale_part",
        "inventor_vent_set_component_parameter",
        "inventor_vent_set_constraint",
        "inventor_vent_inspect_sketch",
        "inventor_vent_execute_plan",
    };

    [Fact]
    public void Vent_tool_surface_is_exactly_the_frozen_27()
    {
        var names = VentToolNames();

        // no duplicates
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        // exact set (order-independent)
        Assert.Equal(
            Expected.OrderBy(x => x, StringComparer.Ordinal),
            names.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(27, names.Length);
    }

    [Fact]
    public void Every_vent_tool_is_prefixed_inventor_vent()
    {
        foreach (var n in VentToolNames())
            Assert.StartsWith("inventor_vent_", n);
    }
}
