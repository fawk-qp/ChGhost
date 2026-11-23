using System.Text.Json;
using ChGhost.Core.Models;

namespace ChGhost.Tests;

public class SavedConfigTests
{
    [Fact]
    public void SerializationKeepsLegacyPropertyNamesAndValues()
    {
        var config = new SavedConfig
        {
            TopLeftX = 12,
            TopLeftY = 34,
            BottomRightX = 650,
            BottomRightY = 672,
            Signatures = new Dictionary<char, List<PieceTemplate>>
            {
                ['P'] = new List<PieceTemplate>
                {
                    new() { EdgePatch = new[] { 1.0, 2.0 }, IsWhite = true }
                }
            }
        };

        string json = JsonSerializer.Serialize(config);
        using JsonDocument document = JsonDocument.Parse(json);
        string[] propertyNames = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[]
        {
            "BlackBodyB", "BlackBodyG", "BlackBodyR", "BottomRightX", "BottomRightY",
            "Signatures", "TopLeftX", "TopLeftY", "WhiteBodyB", "WhiteBodyG", "WhiteBodyR"
        }, propertyNames);

        SavedConfig restored = JsonSerializer.Deserialize<SavedConfig>(json)!;
        Assert.Equal(12, restored.TopLeftX);
        Assert.Equal(672, restored.BottomRightY);
        Assert.Equal(2.0, restored.Signatures['P'][0].EdgePatch[1]);
        Assert.True(restored.Signatures['P'][0].IsWhite);
    }
}
