using SyncForge.Core;
using Xunit;

namespace SyncForge.Tests;

public sealed class StockCsvTests
{
    [Fact]
    public void AcceptsBomQuotesAndComputesAvailableStock()
    {
        var preview = StockCsv.Parse("\uFEFFsku,physical_quantity,reserved_quantity\r\n\" demo-001 \",10,3\r\n");

        var row = Assert.Single(preview.Rows);
        Assert.Empty(preview.Errors);
        Assert.Equal("DEMO-001", row.Sku);
        Assert.Equal(7, row.AvailableQuantity);
        Assert.Equal(2, row.Line);
    }

    [Theory]
    [InlineData("sku,quantity\nABC,10", 1)]
    [InlineData("sku,physical_quantity,reserved_quantity\nABC,1,2", 2)]
    [InlineData("sku,physical_quantity,reserved_quantity\nABC,-1,0", 2)]
    [InlineData("sku,physical_quantity,reserved_quantity\nABC,1.2,0", 2)]
    [InlineData("sku,physical_quantity,reserved_quantity\nABC,1,0\nabc,0,0", 3)]
    [InlineData("sku,physical_quantity,reserved_quantity\n\"ABC,1,0", 2)]
    [InlineData("sku,physical_quantity,reserved_quantity\nABC,1,0,x", 2)]
    public void ReportsMalformedRowsWithSourceLine(string csv, int line)
    {
        var preview = StockCsv.Parse(csv);
        Assert.Contains(preview.Errors, error => error.Line == line);
    }

    [Fact]
    public void EnforcesLimits()
    {
        const string header = "sku,physical_quantity,reserved_quantity\n";
        var lines = string.Join('\n', Enumerable.Range(1, 26).Select(index => $"SKU-{index},1,0"));
        Assert.Equal(27, Assert.Single(StockCsv.Parse(header + lines).Errors).Line);
        Assert.Single(StockCsv.Parse(new string('á', StockCsv.MaxBytes)).Errors);
    }
}
