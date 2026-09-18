using Bimwright.Ipt.Server;
using Bimwright.Ipt.Server.Tools;
using Newtonsoft.Json.Linq;

namespace Bimwright.Ipt.Tests;

/// <summary>
/// The two server-side vent tools (no Inventor, no wire): <c>list_products</c> scans a catalog folder and
/// <c>new_product</c> clones a product folder. Both run entirely on the filesystem, so they are fully unit
/// tested here.
/// </summary>
public sealed class VentToolsServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipt-vent-srv-" + Guid.NewGuid().ToString("N"));
    private readonly VentTools _tools = new VentTools(new PluginClient(new InventorMcpConfig()));

    public VentToolsServerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void ListProducts_finds_a_product_folder_with_an_assembly()
    {
        string prod = Path.Combine(_root, "ВКР 7,1");
        Directory.CreateDirectory(prod);
        File.WriteAllText(Path.Combine(prod, "00.00.000 СК Вентилятор.iam"), "");
        File.WriteAllText(Path.Combine(prod, "01.01.000 Диск.ipt"), "");

        var r = JObject.Parse(_tools.ListProducts(_root));

        Assert.True((bool)r["ok"]!);
        Assert.True((int)r["count"]! >= 1);
        var products = (JArray)r["products"]!;
        Assert.Contains(products, p => (string?)p["name"] == "ВКР 7,1");
        var found = products.First(p => (string?)p["name"] == "ВКР 7,1");
        Assert.EndsWith(".iam", (string?)found["top_assembly"] ?? "");
        Assert.Single((JArray)found["parts"]!);
    }

    [Fact]
    public void ListProducts_rejects_a_missing_root()
    {
        var r = JObject.Parse(_tools.ListProducts(Path.Combine(_root, "does-not-exist")));

        Assert.False((bool)r["ok"]!);
        Assert.Equal("INVALID_ARGUMENT", (string?)r["error"]!["code"]);
    }

    [Fact]
    public void NewProduct_clones_the_template_folder_with_its_files()
    {
        string template = Path.Combine(_root, "template");
        Directory.CreateDirectory(template);
        File.WriteAllText(Path.Combine(template, "top.iam"), "iam");
        File.WriteAllText(Path.Combine(template, "part.ipt"), "ipt");

        var r = JObject.Parse(_tools.NewProduct(template, "ВКР 8,0"));

        Assert.True((bool)r["ok"]!);
        string dest = (string)r["product_dir"]!;
        Assert.True(Directory.Exists(dest));
        Assert.True(File.Exists(Path.Combine(dest, "top.iam")));
        Assert.True(File.Exists(Path.Combine(dest, "part.ipt")));
        Assert.EndsWith(".iam", (string?)r["top_assembly"] ?? "");
    }

    [Fact]
    public void NewProduct_refuses_to_overwrite_an_existing_destination()
    {
        string template = Path.Combine(_root, "tpl2");
        Directory.CreateDirectory(template);
        File.WriteAllText(Path.Combine(template, "top.iam"), "iam");
        // pre-create the destination so the clone must refuse
        Directory.CreateDirectory(Path.Combine(_root, "dup"));

        var r = JObject.Parse(_tools.NewProduct(template, "dup", _root));

        Assert.False((bool)r["ok"]!);
        Assert.Equal("INVALID_ARGUMENT", (string?)r["error"]!["code"]);
    }

    [Fact]
    public void NewProduct_rejects_a_missing_template()
    {
        var r = JObject.Parse(_tools.NewProduct(Path.Combine(_root, "nope"), "X"));

        Assert.False((bool)r["ok"]!);
        Assert.Equal("INVALID_ARGUMENT", (string?)r["error"]!["code"]);
    }
}
