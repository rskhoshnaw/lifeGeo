using LifeDrop.Application.Places;
using Xunit;

namespace LifeDrop.Application.Tests.Places;

public class PlaceExportTests
{
    private static readonly Guid CityId = Guid.NewGuid(), HoodId = Guid.NewGuid(), StreetId = Guid.NewGuid(), AlleyId = Guid.NewGuid(), DirectAlleyId = Guid.NewGuid();

    private static PlaceDto P(Guid id, Guid? parent, string kind, string name, string status = "active", string? ckb = null) =>
        new(id, parent, kind, name, ckb, null, null, status);

    private static readonly PlaceDto[] Tree =
    [
        P(AlleyId, StreetId, "alley", "Alley 1"), P(StreetId, HoodId, "street", "Main Street"), P(HoodId, CityId, "neighbourhood", "Ankawa"),
        P(CityId, null, "city", "Erbil", ckb: "هەولێر"), P(DirectAlleyId, HoodId, "alley", "Alley 2")
    ];

    [Fact]
    public void Build_GivesEveryPlaceTheNamesOfItsPathAndSortsCityFirst()
    {
        var rows = PlaceExport.Build(Tree);

        Assert.Equal(new[] { "city", "neighbourhood", "street", "alley", "alley" }, rows.Select(r => r.Kind).ToArray());
        var city = rows[0];
        Assert.Equal(("Erbil", null, null, "هەولێر"), (city.City, city.Neighbourhood, city.Street, city.NameCkb));
        var street = rows.Single(r => r.Id == StreetId);
        Assert.Equal(("Erbil", "Ankawa", (string?)null), (street.City, street.Neighbourhood, street.Street));
        var alley = rows.Single(r => r.Id == AlleyId);
        Assert.Equal(("Erbil", "Ankawa", "Main Street"), (alley.City, alley.Neighbourhood, alley.Street));
        var direct = rows.Single(r => r.Id == DirectAlleyId);
        Assert.Equal(("Erbil", "Ankawa", (string?)null), (direct.City, direct.Neighbourhood, direct.Street));   // an alley straight under a neighbourhood has no street
    }

    [Fact]
    public void Build_ToleratesAMissingParentInTheList()
    {
        var orphan = Build(P(StreetId, HoodId, "street", "Main Street"));
        Assert.Equal(("", (string?)null), (orphan.City, orphan.Neighbourhood));
    }

    private static PlaceExportRow Build(PlaceDto place) => PlaceExport.Build([place]).Single();

    [Fact]
    public void ToCsv_HasTheHeaderQuotesSpecialCharactersAndKeepsFormulasAsText()
    {
        var csv = PlaceExport.ToCsv(new[]
        {
            new PlaceExportRow(CityId, null, "city", "Erbil", null, null, "Erbil, \"old\" city", null, "أربيل", null, "active"),
            new PlaceExportRow(StreetId, HoodId, "street", "Erbil", "Ankawa", null, "=1+1", null, null, null, "inactive")
        });

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(PlaceExport.CsvHeader, lines[0]);
        Assert.Equal($"{CityId},,city,Erbil,,,\"Erbil, \"\"old\"\" city\",,أربيل,,active", lines[1]);
        Assert.Equal($"{StreetId},{HoodId},street,Erbil,Ankawa,,'=1+1,,,,inactive", lines[2]);
    }

    [Theory]
    [InlineData("city", "Erbil", null, null, null, true)]
    [InlineData("city", "Erbil", "Erbil", null, null, true)]            // the city column of a city row (as in an export) is ignored
    [InlineData("city", "Erbil", null, "Ankawa", null, false)]          // but nothing below a city may be named
    [InlineData("neighbourhood", "Ankawa", "Erbil", null, null, true)]
    [InlineData("neighbourhood", "Ankawa", null, null, null, false)]
    [InlineData("street", "Main", "Erbil", "Ankawa", null, true)]
    [InlineData("street", "Main", "Erbil", null, null, false)]
    [InlineData("alley", "A1", "Erbil", "Ankawa", "Main", true)]
    [InlineData("alley", "A1", "Erbil", "Ankawa", null, true)]
    [InlineData("alley", "A1", "Erbil", null, "Main", false)]
    [InlineData("road", "X", "Erbil", null, null, false)]
    [InlineData("city", " ", null, null, null, false)]
    public void IsValidImportRow_NeedsTheLevelsAboveEachKind(string kind, string name, string? city, string? neighbourhood, string? street, bool expected) =>
        Assert.Equal(expected, PlaceService.IsValidImportRow(new ImportPlaceRow(kind, name, city, neighbourhood, street)));

    [Fact]
    public void IsValidImportRow_RefusesTooLongNames()
    {
        Assert.False(PlaceService.IsValidImportRow(new ImportPlaceRow("city", new string('x', PlaceService.MaxNameLength + 1))));
        Assert.False(PlaceService.IsValidImportRow(new ImportPlaceRow("city", "Erbil", NameCkb: new string('x', PlaceService.MaxNameLength + 1))));
        Assert.True(PlaceService.IsValidImportRow(new ImportPlaceRow("city", new string('x', PlaceService.MaxNameLength))));
        Assert.False(PlaceService.IsValidImportRow(null));
    }
}
