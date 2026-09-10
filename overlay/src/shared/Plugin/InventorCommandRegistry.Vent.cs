#if INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
namespace Bimwright.Ipt.Shared.Plugin;

using System;
using System.Collections.Generic;
using Bimwright.Ipt.Shared.Infrastructure;
using Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// Vent-factory registrar. Wire commands behind the <c>vent</c> toolset. <c>list_products</c> has
/// no wire command (it is server-side in <see cref="Bimwright.Ipt.Server.Tools.VentTools"/>).
/// </summary>
public static partial class InventorCommandRegistry
{
    static partial void AddVent(Dictionary<string, IInventorCommand> d, Action<IInventorCommand> add)
    {
        add(new OpenProductHandler());
        add(new SavePartAsHandler());
        add(new SetDischargeHandler());
        add(new CheckPartHandler());
        add(new MakeDrawingHandler());
        add(new BatchFlatDxfHandler());
        add(new BomReportHandler());
        add(new InspectModelHandler());
        add(new DriveDimensionHandler());
        add(new SetComponentParameterHandler());
        add(new SetConstraintHandler());
    }
}
#endif
