namespace SyncFactors.MockSuccessFactors;

public static class MetadataDocument
{
    public static string Build(string serviceRoot) =>
        $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <edmx:Edmx Version="1.0" xmlns:edmx="http://schemas.microsoft.com/ado/2007/06/edmx">
          <edmx:DataServices m:DataServiceVersion="2.0" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
            <Schema Namespace="SFOData" xmlns="http://schemas.microsoft.com/ado/2008/09/edm">
              <EntityType Name="PerPerson">
                <Key>
                  <PropertyRef Name="personIdExternal" />
                </Key>
                <Property Name="personIdExternal" Type="Edm.String" Nullable="false" />
                <NavigationProperty Name="personalInfoNav" Relationship="SFOData.PerPerson_PersonalInfo" ToRole="PersonalInfo" FromRole="PerPerson" />
                <NavigationProperty Name="employmentNav" Relationship="SFOData.PerPerson_Employment" ToRole="Employment" FromRole="PerPerson" />
                <NavigationProperty Name="emailNav" Relationship="SFOData.PerPerson_Email" ToRole="Email" FromRole="PerPerson" />
              </EntityType>
              <EntityContainer Name="SFODataContainer" m:IsDefaultEntityContainer="true">
                <EntitySet Name="PerPerson" EntityType="SFOData.PerPerson" />
              </EntityContainer>
              <Annotations Target="SFOData.SFODataContainer">
                <Annotation Term="Org.OData.Core.V1.Description" String="Mock SuccessFactors metadata for SyncFactors.Next" />
              </Annotations>
            </Schema>
          </edmx:DataServices>
        </edmx:Edmx>
        """;
}
