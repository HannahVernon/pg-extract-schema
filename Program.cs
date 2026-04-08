using System.CommandLine;
using System.Security;
using pg_extract_schema;

var hostOption = new Option<string>("--host", description: "PostgreSQL server hostname") { IsRequired = true };
hostOption.AddAlias("-h");

var portOption = new Option<int>("--port", getDefaultValue: () => 5432, description: "PostgreSQL server port");
portOption.AddAlias("-p");

var databaseOption = new Option<string>("--database", description: "Database name") { IsRequired = true };
databaseOption.AddAlias("-d");

var schemaOption = new Option<string?>("--schema", description: "Schema name (default: all non-system schemas)");
schemaOption.AddAlias("-s");

var outputOption = new Option<string>("--output", getDefaultValue: () => "output", description: "Output directory");
outputOption.AddAlias("-o");

var userOption = new Option<string>("--username", getDefaultValue: () => "postgres", description: "PostgreSQL username");
userOption.AddAlias("-U");

var passwordOption = new Option<string?>("--password", description: "PostgreSQL password (or set PGPASSWORD env var)");
passwordOption.AddAlias("-W");

var includeSystemOption = new Option<bool>(
    "--include-postgres-system-objects",
    getDefaultValue: () => false,
    description: "Include PostgreSQL system schemas (pg_catalog, pg_toast, information_schema, pg_temp) and the plpgsql extension");

var rootCommand = new RootCommand("Extract DDL from a PostgreSQL database into discrete .sql files")
{
    hostOption, portOption, databaseOption, schemaOption, outputOption, userOption, passwordOption, includeSystemOption
};

rootCommand.SetHandler(async (host, port, database, schema, output, username, password, includeSystem) =>
{
    password ??= Environment.GetEnvironmentVariable("PGPASSWORD");

    if (password == null)
    {
        Console.Write("Password: ");
        password = ReadPasswordMasked();
        Console.WriteLine();
    }

    var connString = $"Host={host};Port={port};Database={database};Username={username}"
        + (password != null ? $";Password={password}" : "");

    Console.WriteLine($"Connecting to {host}:{port}/{database} ...");

    try
    {
        var extractor = new SchemaExtractor(connString, output, schema, includeSystem);
        await extractor.ExtractAllAsync();
        Console.WriteLine($"\nDone. DDL written to: {Path.GetFullPath(output)}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.ExitCode = 1;
    }
}, hostOption, portOption, databaseOption, schemaOption, outputOption, userOption, passwordOption, includeSystemOption);

return await rootCommand.InvokeAsync(args);

static string ReadPasswordMasked()
{
    var password = new SecureString();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
            break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (password.Length > 0)
            {
                password.RemoveAt(password.Length - 1);
                Console.Write("\b \b");
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            password.AppendChar(key.KeyChar);
            Console.Write('*');
        }
    }

    // Convert SecureString to plain string only at the point of use
    var ptr = System.Runtime.InteropServices.Marshal.SecureStringToBSTR(password);
    try
    {
        return System.Runtime.InteropServices.Marshal.PtrToStringBSTR(ptr);
    }
    finally
    {
        System.Runtime.InteropServices.Marshal.ZeroFreeBSTR(ptr);
        password.Dispose();
    }
}
