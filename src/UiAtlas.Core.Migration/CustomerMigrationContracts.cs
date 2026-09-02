namespace UiAtlas.Core.Migration;

public sealed record MigrationAddress(
    string? Street1,
    string? Street2,
    string? City,
    string? State,
    string? PostalCode,
    string? Country);

public sealed record SourceReference(
    int? ClientTypeId,
    string? ClientTypeCode,
    string? ClientTypeName,
    int? ClientActivityId,
    string? ClientActivityCode,
    string? ClientActivityName,
    int? PriceLevelId);

public sealed record NormalizedCustomer(
    string SourceRecordId,
    string? Code,
    string? Barcode,
    string? FirstName,
    string? LastName,
    string? Company,
    string? JobTitle,
    string? Email,
    string? CcEmail,
    string? Phone,
    string? Mobile,
    string? Fax,
    string? Website,
    string? VatId,
    string? TaxId,
    DateTime? BirthDate,
    decimal? AccountBalance,
    decimal? AccountLimit,
    decimal? DiscountPercent,
    bool? SendNews,
    string? Notes,
    MigrationAddress Address,
    SourceReference Source,
    IReadOnlyDictionary<string, string?> CustomFields);

public sealed record MigrationPackageManifest(
    string FormatVersion,
    string SourceSystem,
    bool ContainsSensitiveData,
    string SourceDatabaseName,
    string SourceDatabaseSha256,
    string DataFile,
    long RecordCount,
    string DataSha256,
    IReadOnlyList<string> ExcludedSourceFields);

public sealed record MigrationPackageResult(
    string PackageDirectory,
    string DataFile,
    string ManifestFile,
    long RecordCount,
    string DataSha256);
