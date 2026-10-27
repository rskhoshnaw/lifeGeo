using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Customers;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests.Customers;

// Doctor list import (plan step 5.9): who may import, what is checked, and that a wrong row stops the whole import.
public class DoctorImportTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly UserId Manager = new(Guid.NewGuid());
    private static readonly Guid Visitor = Guid.NewGuid(), Region = Guid.NewGuid();

    private static DoctorImport Service(Store store, AccessScope? scope) => new(store, new Permissions(scope));
    private static AccessScope Wide => AccessScope.WholeTenant(Manager);

    [Fact]
    public async Task OnlyAWholeCompanyWriterWhoSeesDoctorsMayImport()
    {
        var request = new ImportDoctorsRequest([new DoctorImportRow("Dr. Ali")]);
        foreach (var scope in new AccessScope?[] { null, new AccessScope(DataScope.Region, Manager, Guid.NewGuid()), new AccessScope(DataScope.Own, Manager, null), Wide with { HideDoctors = true } })
        {
            var store = new Store();
            Assert.Equal(DoctorImportOutcome.Forbidden, (await Service(store, scope).ImportAsync(Tenant, Manager, request, CancellationToken.None)).Outcome);
            Assert.Equal(0, store.Imports);
        }
        Assert.Equal(DoctorImportOutcome.Success, (await Service(new Store(), Wide).ImportAsync(Tenant, Manager, request, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task EmptyOrTooBigFilesAreRefused()
    {
        var service = Service(new Store(), Wide);
        Assert.Equal(DoctorImportOutcome.InvalidData, (await service.ImportAsync(Tenant, Manager, new ImportDoctorsRequest(null), CancellationToken.None)).Outcome);
        Assert.Equal(DoctorImportOutcome.InvalidData, (await service.ImportAsync(Tenant, Manager, new ImportDoctorsRequest([]), CancellationToken.None)).Outcome);
        var tooMany = Enumerable.Range(0, DoctorImport.MaxRows + 1).Select(i => new DoctorImportRow("Dr " + i)).ToList();
        Assert.Equal(DoctorImportOutcome.InvalidData, (await service.ImportAsync(Tenant, Manager, new ImportDoctorsRequest(tooMany), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task EveryWrongRowIsReportedByItsNumberAndNothingIsImported()
    {
        var store = new Store();
        var rows = new List<DoctorImportRow>
        {
            new("Dr. Good"),
            new(" "),                                                                            // 2: name missing
            new("Dr. Spec", Specialty: "dentist", CustomerClass: "Z"),                           // 3: unknown specialty and class
            new("Dr. Place", Neighbourhood: "Ankawa"),                                           // 4: neighbourhood without city
            new("Dr. Street", City: "Erbil", Street: "Main"),                                    // 5: street without neighbourhood
            new("Dr. Pos", Latitude: 36.2),                                                      // 6: one coordinate only
            new("Dr. Far", Latitude: 99, Longitude: 44),                                         // 7: out of range
            new("Dr. Who", VisitorEmail: "nobody@t.test"),                                       // 8: unknown visitor
            new("Dr. Long", Notes: new string('n', 501)),                                        // 9
        };
        var (outcome, result) = await Service(store, Wide).ImportAsync(Tenant, Manager, new ImportDoctorsRequest(rows), CancellationToken.None);
        Assert.Equal(DoctorImportOutcome.Success, outcome);
        Assert.Equal(0, result!.Created);
        Assert.Equal(0, store.Imports);
        Assert.Equal(new[]
        {
            (2, "name", "required"), (3, "specialty", "invalid"), (3, "class", "invalid"), (4, "city", "required"), (5, "neighbourhood", "required"),
            (6, "latitude", "invalid"), (7, "latitude", "invalid"), (9, "notes", "too_long"), (8, "visitor_email", "not_found")
        }, result.Errors.Select(e => (e.Row, e.Field, e.Code)).ToArray());
    }

    [Fact]
    public async Task ACleanFileIsPassedOnTrimmedWithTheVisitorAsOwner()
    {
        var store = new Store();
        store.Users["rep@t.test"] = (Visitor, Region);
        var rows = new List<DoctorImportRow>
        {
            new("  Dr. Ali ", ClinicName: " Noor ", Specialty: " Neurology ", Cooperates: true, CustomerClass: "a", Phone: " 0750 ", City: "Erbil", VisitorEmail: " REP@t.test "),
            new("Dr. Two"),
        };
        var (_, result) = await Service(store, Wide).ImportAsync(Tenant, Manager, new ImportDoctorsRequest(rows, DryRun: true), CancellationToken.None);
        Assert.Equal((2, 0, true), (result!.Created, result.Errors.Count, result.DryRun));
        Assert.True(store.LastDryRun);
        var first = store.Rows[0];
        Assert.Equal(("Dr. Ali", "Noor", "neurology", "A", "0750", "rep@t.test"), (first.Data.Name, first.Data.ClinicName, first.Data.Specialty, first.Data.CustomerClass, first.Data.Phone, first.Data.VisitorEmail));
        Assert.Equal((Visitor, Region), (first.OwnerId, first.RegionId));
        Assert.Null(store.Rows[1].OwnerId);
        Assert.Equal(new[] { 1, 2 }, store.Rows.Select(r => r.Row).ToArray());
    }

    private sealed class Store : IDoctorImportStore
    {
        public Dictionary<string, (Guid UserId, Guid? RegionId)> Users { get; } = [];
        public List<DoctorToImport> Rows { get; } = [];
        public int Imports;
        public bool LastDryRun;

        public Task<IReadOnlyDictionary<string, (Guid UserId, Guid? RegionId)>> FindUsersAsync(TenantId tenantId, IReadOnlyCollection<string> emails, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, (Guid UserId, Guid? RegionId)>>(Users.Where(p => emails.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value));

        public Task<(int Created, int Duplicates)> ImportAsync(TenantId tenantId, UserId actorId, IReadOnlyList<DoctorToImport> rows, bool dryRun, CancellationToken cancellationToken)
        {
            Imports++;
            LastDryRun = dryRun;
            Rows.AddRange(rows);
            return Task.FromResult((rows.Count, 0));
        }
    }

    private sealed class Permissions(AccessScope? scope) : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) => Task.FromResult(scope is not null);

        public Task<AccessScope?> GetAccessScopeAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) => Task.FromResult(scope);
    }
}
