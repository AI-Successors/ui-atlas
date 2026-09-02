using System.Data;
using System.Runtime.CompilerServices;
using FirebirdSql.Data.FirebirdClient;

namespace UiAtlas.Core.Migration.Firebird;

public sealed class AbacreAhmsReader(string databasePath, string clientLibraryPath)
{
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly string _clientLibraryPath = Path.GetFullPath(clientLibraryPath);

    public async Task<long> CountCustomersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginReadOnlyTransactionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (FbTransaction)transaction;
        command.CommandText = "select count(*) from CLIENT";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async IAsyncEnumerable<NormalizedCustomer> ReadCustomersAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginReadOnlyTransactionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (FbTransaction)transaction;
        command.CommandText = CustomerQuery;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new NormalizedCustomer(
                SourceRecordId: GetRequiredInt32(reader, "CLIENTID").ToString(System.Globalization.CultureInfo.InvariantCulture),
                Code: GetString(reader, "CODE"),
                Barcode: GetString(reader, "BARCODE"),
                FirstName: GetString(reader, "FNAME"),
                LastName: GetString(reader, "LNAME"),
                Company: GetString(reader, "COMPANY"),
                JobTitle: GetString(reader, "JOBTITLE"),
                Email: GetString(reader, "EMAIL"),
                CcEmail: GetString(reader, "CCEMAIL"),
                Phone: GetString(reader, "PHONE"),
                Mobile: GetString(reader, "MOBILE"),
                Fax: GetString(reader, "FAX"),
                Website: GetString(reader, "WEBSITE"),
                VatId: GetString(reader, "VATID"),
                TaxId: GetString(reader, "TAXID"),
                BirthDate: GetDateTime(reader, "BIRTHDATE"),
                AccountBalance: GetDecimal(reader, "ACCOUNTBALANCE"),
                AccountLimit: GetDecimal(reader, "ACCOUNTLIMIT"),
                DiscountPercent: GetDecimal(reader, "DISC_PERC"),
                SendNews: GetBoolean(reader, "SENDNEWS"),
                Notes: GetString(reader, "NOTES"),
                Address: new MigrationAddress(
                    GetString(reader, "STREET1"),
                    GetString(reader, "STREET2"),
                    GetString(reader, "CITY"),
                    GetString(reader, "STATE_NAME"),
                    GetString(reader, "ZIP"),
                    GetString(reader, "COUNTRY_NAME")),
                Source: new SourceReference(
                    GetInt32(reader, "CLIENTTYPEID"),
                    GetString(reader, "CLIENTTYPE_CODE"),
                    GetString(reader, "CLIENTTYPE_NAME"),
                    GetInt32(reader, "CLIENTACTID"),
                    GetString(reader, "CLIENTACT_CODE"),
                    GetString(reader, "CLIENTACT_NAME"),
                    GetInt32(reader, "PRICELEVELID")),
                CustomFields: new SortedDictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["custom1"] = GetString(reader, "CFIELD1"),
                    ["custom2"] = GetString(reader, "CFIELD2"),
                    ["custom3"] = GetString(reader, "CFIELD3"),
                    ["custom4"] = GetString(reader, "CFIELD4"),
                    ["custom5"] = GetString(reader, "CFIELD5")
                });
        }

        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    private FbConnection CreateConnection()
    {
        if (!File.Exists(_databasePath)) throw new FileNotFoundException("Source database was not found.", _databasePath);
        if (!File.Exists(_clientLibraryPath)) throw new FileNotFoundException("Firebird client library was not found.", _clientLibraryPath);
        var builder = new FbConnectionStringBuilder
        {
            Database = _databasePath,
            ClientLibrary = _clientLibraryPath,
            UserID = "SYSDBA",
            Password = "embedded-local-access",
            ServerType = FbServerType.Embedded,
            Charset = "NONE",
            Dialect = 3,
            Pooling = false
        };
        return new FbConnection(builder.ConnectionString);
    }

    private static Task<FbTransaction> BeginReadOnlyTransactionAsync(
        FbConnection connection,
        CancellationToken cancellationToken) =>
        connection.BeginTransactionAsync(
            new FbTransactionOptions
            {
                TransactionBehavior = FbTransactionBehavior.Read |
                    FbTransactionBehavior.Concurrency |
                    FbTransactionBehavior.Wait
            },
            cancellationToken);

    private static string? GetString(IDataRecord reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));

    private static int GetRequiredInt32(IDataRecord reader, string name) =>
        Convert.ToInt32(reader.GetValue(reader.GetOrdinal(name)), System.Globalization.CultureInfo.InvariantCulture);

    private static int? GetInt32(IDataRecord reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name))
            ? null
            : Convert.ToInt32(reader.GetValue(reader.GetOrdinal(name)), System.Globalization.CultureInfo.InvariantCulture);

    private static decimal? GetDecimal(IDataRecord reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name))
            ? null
            : Convert.ToDecimal(reader.GetValue(reader.GetOrdinal(name)), System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime? GetDateTime(IDataRecord reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetDateTime(reader.GetOrdinal(name));

    private static bool? GetBoolean(IDataRecord reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name))
            ? null
            : Convert.ToBoolean(reader.GetValue(reader.GetOrdinal(name)), System.Globalization.CultureInfo.InvariantCulture);

    private const string CustomerQuery = """
        select
            c.CLIENTID, c.CODE, c.BARCODE, c.FNAME, c.LNAME, c.COMPANY,
            c.JOBTITLE, c.EMAIL, c.CCEMAIL, c.PHONE, c.MOBILE, c.FAX,
            c.WEBSITE, c.VATID, c.TAXID, c.BIRTHDATE, c.ACCOUNTBALANCE,
            c.ACCOUNTLIMIT, c.DISC_PERC, c.SENDNEWS, c.NOTES,
            c.STREET1, c.STREET2, c.CITY, c.ZIP,
            c.CLIENTTYPEID, ct.CODE as CLIENTTYPE_CODE, ct.NAME as CLIENTTYPE_NAME,
            c.CLIENTACTID, ca.CODE as CLIENTACT_CODE, ca.NAME as CLIENTACT_NAME,
            c.PRICELEVELID, c.CFIELD1, c.CFIELD2, c.CFIELD3, c.CFIELD4, c.CFIELD5,
            s.NAME as STATE_NAME, co.NAME as COUNTRY_NAME
        from CLIENT c
        left join CLIENTTYPE ct on ct.CLIENTTYPEID = c.CLIENTTYPEID
        left join CLIENTACT ca on ca.CLIENTACTID = c.CLIENTACTID
        left join STATE s on s.STATEID = c.STATEID
        left join COUNTRY co on co.COUNTRYID = c.COUNTRYID
        order by c.CLIENTID
        """;
}
