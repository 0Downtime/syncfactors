using SyncFactors.MockSuccessFactors;
using System.Xml.Linq;

namespace SyncFactors.MockSuccessFactors.Tests;

public sealed class MetadataDocumentTests
{
    [Fact]
    public void Build_ReturnsValidODataMetadataDocument()
    {
        var xml = MetadataDocument.Build("http://localhost:5005/odata/v2");

        var document = XDocument.Parse(xml);
        XNamespace edmx = "http://schemas.microsoft.com/ado/2007/06/edmx";
        XNamespace edm = "http://schemas.microsoft.com/ado/2008/09/edm";

        Assert.Equal("Edmx", document.Root?.Name.LocalName);
        Assert.Equal(edmx, document.Root?.Name.Namespace);
        Assert.Contains(
            document.Descendants(edm + "EntityType"),
            entity => entity.Attribute("Name")?.Value == "PerPerson");
        Assert.Contains(
            document.Descendants(edm + "EntityType"),
            entity => entity.Attribute("Name")?.Value == "EmpJob");
        Assert.Contains(
            document.Descendants(edm + "EntitySet"),
            entitySet => entitySet.Attribute("Name")?.Value == "PerPerson" &&
                entitySet.Attribute("EntityType")?.Value == "SFOData.PerPerson");
        Assert.Contains(
            document.Descendants(edm + "NavigationProperty"),
            property => property.Attribute("Name")?.Value == "employmentNav");
        Assert.Contains("Mock SuccessFactors metadata for SyncFactors", xml, StringComparison.Ordinal);
    }
}
