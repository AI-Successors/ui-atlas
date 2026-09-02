namespace UiAtlas.Core.Migration.Firebird;

internal static class Program
{
    private static readonly string[] ExcludedFields = ["CLIENT.PWD"];

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return args.Length == 0 || args[0] is "help" or "--help" or "-h"
                ? Help()
                : args[0].ToLowerInvariant() switch
                {
                    "inspect" => await InspectAsync(args[1..]).ConfigureAwait(false),
                    "export-customers" => await ExportCustomersAsync(args[1..]).ConfigureAwait(false),
                    _ => Fail("Unknown command.")
                };
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or FirebirdSql.Data.FirebirdClient.FbException)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 2;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            UiAtlas Firebird migration source

            INSPECT <database.fdb> <fbembed.dll>
              Verifies the Abacre AHMS schema and reports the customer count.

            EXPORT-CUSTOMERS <database.fdb> <fbembed.dll> <new-output-directory>
              Exports normalized customers to a checksummed JSONL package.

            Close the source application first or use a verified offline database copy.
            The source database is queried only through a snapshot transaction; CLIENT.PWD is never exported.
            """);
        return 0;
    }

    private static async Task<int> InspectAsync(string[] args)
    {
        if (args.Length != 2) return Fail("Usage: inspect <database.fdb> <fbembed.dll>");
        var reader = new AbacreAhmsReader(args[0], args[1]);
        var count = await reader.CountCustomersAsync().ConfigureAwait(false);
        Console.WriteLine($"source=abacre-ahms customer-count={count} excluded=CLIENT.PWD");
        return 0;
    }

    private static async Task<int> ExportCustomersAsync(string[] args)
    {
        if (args.Length != 3)
            return Fail("Usage: export-customers <database.fdb> <fbembed.dll> <new-output-directory>");

        var reader = new AbacreAhmsReader(args[0], args[1]);
        var result = await MigrationPackageWriter.WriteCustomersAsync(
            args[2],
            "abacre-ahms",
            args[0],
            reader.ReadCustomersAsync(),
            ExcludedFields).ConfigureAwait(false);
        Console.WriteLine($"exported={result.RecordCount} package={result.PackageDirectory} sha256={result.DataSha256}");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
