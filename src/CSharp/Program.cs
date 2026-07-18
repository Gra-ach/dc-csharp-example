using InterSystems.Data.IRISClient;
using InterSystems.XEP;
using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Gaia;

namespace XepGaia
{
    public class Program
    {
        private const string XepClassName = "Gaia.EpochPhotometry";

        private static int Main(string[] args)
        {
            AppConfig config = new()
            {
                Host = GetEnvironmentVariable("IRIS_HOST", "localhost"),
                Port = GetEnvironmentVariableAsInt("IRIS_PORT", 1972),
                Namespace = GetEnvironmentVariable("IRIS_NAMESPACE", "USER"),
                Username = GetEnvironmentVariable("IRIS_USERNAME", "_SYSTEM"),
                Password = GetEnvironmentVariable("IRIS_PASSWORD", "SYS"),

                InputCsvPath = args.Length >= 1
                    ? args[0]
                    : "EpochPhotometry.csv",

                OutputCsvPath = args.Length >= 2
                    ? args[1]
                    : "EpochPhotometryChanges.csv"
            };

            try
            {
                ValidateConfiguration(config);

                Console.WriteLine($"Reading {config.InputCsvPath}...");

                List<EpochPhotometry> events =
                    ReadEpochPhotometryCsv(config.InputCsvPath);

                Console.WriteLine(
                    $"Parsed {events.Count:N0} rows from the source file.");

                ImportWithXep(config, events);

                Console.WriteLine(
                    $"Executing the ADO.NET query and writing {config.OutputCsvPath}...");

                int exportedRows = QueryAndExport(config);

                Console.WriteLine(
                    $"Finished. Exported {exportedRows:N0} rows.");

                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Processing failed.");
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        // ---------------------------------------------------------------------
        // Step 2: Read CSV and construct XEP event objects
        // ---------------------------------------------------------------------

        private static List<EpochPhotometry> ReadEpochPhotometryCsv(
            string inputPath)
        {
            CsvConfiguration csvConfiguration =
                new(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true,
                    BadDataFound = null,
                    MissingFieldFound = null,
                    HeaderValidated = null,
                    TrimOptions = TrimOptions.Trim
                };

            List<EpochPhotometry> result = new();

            using StreamReader streamReader =
                new(inputPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            using CsvReader csv = new(streamReader, csvConfiguration);

            if (!csv.Read() || !csv.ReadHeader())
            {
                throw new InvalidDataException(
                    "The input CSV does not contain a header row.");
            }

            ValidateRequiredColumns(csv.HeaderRecord);

            long rowNumber = 1;

            while (csv.Read())
            {
                rowNumber++;

                string? sourceIdText = csv.GetField("source_id");
                string? bpFluxText = csv.GetField("bp_flux");
                string? rpFluxText = csv.GetField("rp_flux");

                if (!long.TryParse(
                        sourceIdText,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long sourceId))
                {
                    Console.Error.WriteLine(
                        $"Skipping CSV row {rowNumber}: " +
                        $"invalid source_id '{sourceIdText}'.");

                    continue;
                }

                double[] bpFlux = ParseNumericArray(bpFluxText);
                double[] rpFlux = ParseNumericArray(rpFluxText);

                EpochPhotometry item =
                    new(sourceId, bpFlux, rpFlux);

                result.Add(item);
            }

            return result;
        }

        private static void ValidateRequiredColumns(string[]? headers)
        {
            if (headers == null)
            {
                throw new InvalidDataException(
                    "The input CSV header could not be read.");
            }

            HashSet<string> headerSet =
                new(headers, StringComparer.OrdinalIgnoreCase);

            string[] requiredColumns =
            {
                "source_id",
                "bp_flux",
                "rp_flux"
            };

            string[] missingColumns = requiredColumns
                .Where(column => !headerSet.Contains(column))
                .ToArray();

            if (missingColumns.Length > 0)
            {
                throw new InvalidDataException(
                    "The following required columns are missing: " +
                    string.Join(", ", missingColumns));
            }
        }

        private static double[] ParseNumericArray(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return Array.Empty<double>();
            }

            string value = input.Trim();

            if (value.Length >= 2 &&
                value[0] == '[' &&
                value[^1] == ']')
            {
                value = value[1..^1];
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<double>();
            }

            List<double> numbers = new();

            foreach (string rawElement in value.Split(','))
            {
                string element = rawElement.Trim();

                if (string.IsNullOrWhiteSpace(element) ||
                    element.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                    element.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                    element.Equals("nan", StringComparison.OrdinalIgnoreCase) ||
                    element.Equals("+nan", StringComparison.OrdinalIgnoreCase) ||
                    element.Equals("-nan", StringComparison.OrdinalIgnoreCase) ||
                    element.Equals("infinity", StringComparison.OrdinalIgnoreCase) ||
                    element.Equals("+infinity", StringComparison.OrdinalIgnoreCase) ||
                    element.Equals("-infinity", StringComparison.OrdinalIgnoreCase) ||
                    element.Equals("inf", StringComparison.OrdinalIgnoreCase) ||
                    element.Equals("+inf", StringComparison.OrdinalIgnoreCase) ||
                    element.Equals("-inf", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!double.TryParse(
                        element,
                        NumberStyles.Float |
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out double number))
                {
                    continue;
                }

                if (!double.IsFinite(number))
                {
                    continue;
                }

                numbers.Add(number);
            }

            return numbers.ToArray();
        }

        // ---------------------------------------------------------------------
        // Step 1 and Step 2:
        // Create the IRIS class schema and persist the objects through XEP
        // ---------------------------------------------------------------------

        private static void ImportWithXep(
            AppConfig config,
            IReadOnlyCollection<EpochPhotometry> events)
        {
            EventPersister? persister = null;
            Event? irisEvent = null;

            try
            {
                persister = PersisterFactory.CreatePersister();

                persister.Connect(
                    config.Host,
                    config.Port,
                    config.Namespace,
                    config.Username,
                    config.Password);

                persister.DeleteExtent(XepClassName);

                try
                {
                    persister.ImportSchema(XepClassName);
                    Console.WriteLine(
                        $"Imported XEP schema {XepClassName}.");
                }
                catch (XEPException exception)
                {
                    /*
                    * A schema-import exception commonly means that the class
                    * already exists. For a production application, inspect the
                    * exception code rather than accepting every import error.
                    */
                    Console.WriteLine(
                        "XEP schema was not imported. " +
                        "It may already exist.");

                    Console.WriteLine(exception.Message);
                }                

                irisEvent = persister.GetEvent(XepClassName);

                const int batchSize = 5_000;

                foreach (EpochPhotometry[] batch in
                        events.Chunk(batchSize))
                {
                    irisEvent.Store(batch);                    
                }
            }
            catch (XEPException exception)
            {
                throw new InvalidOperationException(
                    "The XEP import failed.",
                    exception);
            }
            finally
            {
                irisEvent?.Close();
                persister?.Close();
            }
        }

        // ---------------------------------------------------------------------
        // Step 3 and Step 4:
        // Query through ADO.NET and save the result to CSV
        // ---------------------------------------------------------------------

        private static int QueryAndExport(AppConfig config)
        {
            string connectionString =
                $"Server={config.Host};" +
                $"Port={config.Port};" +
                $"Namespace={config.Namespace};" +
                $"User ID={config.Username};" +
                $"Password={config.Password};";

            using IRISConnection connection =
                new(connectionString);

            connection.Open();

            using IRISCommand command =
                connection.CreateCommand();

            command.CommandText = BuildQuery();

            using IRISDataReader reader =
                command.ExecuteReader();

            CsvConfiguration outputConfiguration =
                new(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true
                };

            using StreamWriter streamWriter =
                new(config.OutputCsvPath, false, new UTF8Encoding(false));

            using CsvWriter csv =
                new(streamWriter, outputConfiguration);

            WriteOutputHeader(csv);

            int rowCount = 0;

            while (reader.Read())
            {
                WriteOutputRow(csv, reader);
                rowCount++;
            }

            return rowCount;
        }

        private static string BuildQuery()
        {
            return """
                SELECT
                    q.source_id,
                    q.bp_min_flux,
                    q.bp_max_flux,
                    q.rp_min_flux,
                    q.rp_max_flux,
                    q.percentage_change_bp,
                    q.percentage_change_rp,

                    CASE
                        WHEN q.percentage_change_bp IS NULL
                            THEN q.percentage_change_rp
                        WHEN q.percentage_change_rp IS NULL
                            THEN q.percentage_change_bp
                        WHEN q.percentage_change_bp >= q.percentage_change_rp
                            THEN q.percentage_change_bp
                        ELSE q.percentage_change_rp
                    END AS max_percentage_change

                FROM
                (
                    SELECT
                        source_id,
                        bp_min_flux,
                        bp_max_flux,
                        rp_min_flux,
                        rp_max_flux,

                        CASE
                            WHEN bp_min_flux IS NULL
                            OR bp_max_flux IS NULL
                            OR bp_min_flux = 0
                                THEN NULL
                            ELSE
                                (
                                    (bp_max_flux - bp_min_flux)
                                    / bp_min_flux
                                ) * 100
                        END AS percentage_change_bp,

                        CASE
                            WHEN rp_min_flux IS NULL
                            OR rp_max_flux IS NULL
                            OR rp_min_flux = 0
                                THEN NULL
                            ELSE
                                (
                                    (rp_max_flux - rp_min_flux)
                                    / rp_min_flux
                                ) * 100
                        END AS percentage_change_rp

                    FROM Gaia.EpochPhotometry
                ) q

                WHERE
                    CASE
                        WHEN q.percentage_change_bp IS NULL
                            THEN q.percentage_change_rp
                        WHEN q.percentage_change_rp IS NULL
                            THEN q.percentage_change_bp
                        WHEN q.percentage_change_bp >= q.percentage_change_rp
                            THEN q.percentage_change_bp
                        ELSE q.percentage_change_rp
                    END > 100
                """;
        }

        private static void WriteOutputHeader(CsvWriter csv)
        {
            csv.WriteField("source_id");
            csv.WriteField("bp_min_flux");
            csv.WriteField("bp_max_flux");
            csv.WriteField("rp_min_flux");
            csv.WriteField("rp_max_flux");
            csv.WriteField("percentage_change_bp");
            csv.WriteField("percentage_change_rp");
            csv.WriteField("max_percentage_change");
            csv.NextRecord();
        }

        private static void WriteOutputRow(
            CsvWriter csv,
            IRISDataReader reader)
        {
            csv.WriteField(
                GetInt64(reader, "source_id"));

            csv.WriteField(
                GetNullableDouble(reader, "bp_min_flux"));

            csv.WriteField(
                GetNullableDouble(reader, "bp_max_flux"));

            csv.WriteField(
                GetNullableDouble(reader, "rp_min_flux"));

            csv.WriteField(
                GetNullableDouble(reader, "rp_max_flux"));

            csv.WriteField(
                GetNullableDouble(reader, "percentage_change_bp"));

            csv.WriteField(
                GetNullableDouble(reader, "percentage_change_rp"));

            csv.WriteField(
                GetNullableDouble(reader, "max_percentage_change"));

            csv.NextRecord();
        }

        private static long GetInt64(
            IRISDataReader reader,
            string columnName)
        {
            int ordinal = reader.GetOrdinal(columnName);

            return Convert.ToInt64(
                reader.GetValue(ordinal),
                CultureInfo.InvariantCulture);
        }

        private static double? GetNullableDouble(
            IRISDataReader reader,
            string columnName)
        {
            int ordinal = reader.GetOrdinal(columnName);

            if (reader.IsDBNull(ordinal))
            {
                return null;
            }

            return Convert.ToDouble(
                reader.GetValue(ordinal),
                CultureInfo.InvariantCulture);
        }

        private static void ValidateConfiguration(AppConfig config)
        {
            if (!File.Exists(config.InputCsvPath))
            {
                throw new FileNotFoundException(
                    "The input CSV file was not found.",
                    config.InputCsvPath);
            }

            if (config.Port <= 0 || config.Port > 65_535)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(config.Port),
                    "The IRIS superserver port is invalid.");
            }

            string? outputDirectory =
                Path.GetDirectoryName(
                    Path.GetFullPath(config.OutputCsvPath));

            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }
        }

        private static string GetEnvironmentVariable(
            string name,
            string defaultValue)
        {
            string? value = Environment.GetEnvironmentVariable(name);

            return string.IsNullOrWhiteSpace(value)
                ? defaultValue
                : value;
        }

        private static int GetEnvironmentVariableAsInt(
            string name,
            int defaultValue)
        {
            string? value = Environment.GetEnvironmentVariable(name);

            return int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsedValue)
                ? parsedValue
                : defaultValue;
        }

        private sealed class AppConfig
        {
            public required string Host { get; init; }
            public required int Port { get; init; }
            public required string Namespace { get; init; }
            public required string Username { get; init; }
            public required string Password { get; init; }
            public required string InputCsvPath { get; init; }
            public required string OutputCsvPath { get; init; }
        }
    }
}