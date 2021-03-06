﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;
using Aquarius.Samples.Client.ServiceModel;
using Aquarius.TimeSeries.Client;
using log4net;
using Humanizer;
using NodaTime;
using NodaTime.Text;
using ServiceStack;
using ServiceStack.Logging.Log4Net;

namespace LabFileImporter
{
    public class Program
    {
        private static ILog _log;

        [STAThread]
        public static void Main(string[] args)
        {
            Environment.ExitCode = 1;

            try
            {
                ConfigureLogging();

                ServiceStackConfig.ConfigureServiceStack();

                var context = ParseArgs(args);
                new Program(context).Run();

                Environment.ExitCode = 0;
            }
            catch (Exception exception)
            {
                var (message, exceptionToLog) = HandleOuterException(exception);

                if (exceptionToLog != null)
                    _log.Error(message, exceptionToLog);
                else
                    _log.Error(message);
            }
        }

        private static (string Message, Exception Exception) HandleOuterException(Exception exception)
        {
            switch (exception)
            {
                case WebServiceException webServiceException:
                    return ($"API: ({webServiceException.StatusCode}) {string.Join(" ", webServiceException.StatusDescription, webServiceException.ErrorCode)}: {string.Join(" ", webServiceException.Message, webServiceException.ErrorMessage)}", webServiceException);

                case ExpectedException expectedException:
                    return (expectedException.Message, null);

                default:
                    return ("Unhandled exception", exception);
            }
        }

        private static void ConfigureLogging()
        {
            using (var stream = new MemoryStream(LoadEmbeddedResource("log4net.config")))
            using (var reader = new StreamReader(stream))
            {
                var xml = new XmlDocument();
                xml.LoadXml(reader.ReadToEnd());

                log4net.Config.XmlConfigurator.Configure(xml.DocumentElement);

                _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

                ServiceStack.Logging.LogManager.LogFactory = new Log4NetFactory();
            }
        }

        private static byte[] LoadEmbeddedResource(string path)
        {
            // ReSharper disable once PossibleNullReferenceException
            var resourceName = $"{MethodBase.GetCurrentMethod().DeclaringType.Namespace}.{path}";

            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new ExpectedException($"Can't load '{resourceName}' as embedded resource.");

                return stream.ReadFully();
            }
        }

        private static Context ParseArgs(string[] args)
        {
            var context = new Context();

            var resolvedArgs = args
                .SelectMany(ResolveOptionsFromFile)
                .ToArray();

            var options = new[]
            {
                new Option {Description = "AQSamples connection options: (if set, imported lab results will be uploaded)"},
                new Option {Key = nameof(context.ServerUrl), Setter = value => context.ServerUrl = value, Getter = () => context.ServerUrl, Description = "AQS server URL"},
                new Option {Key = nameof(context.ApiToken), Setter = value => context.ApiToken = value, Getter = () => context.ApiToken, Description = "AQS API Token"},

                new Option(), new Option{Description = "File parsing options:"},
                new Option {Key = nameof(context.Files).Singularize(), Setter = context.Files.Add, Description = "Parse the XLXS as lab file results."},
                new Option {Key = nameof(context.BulkImportIndicator), Setter = value => context.BulkImportIndicator = value, Getter = () => context.BulkImportIndicator, Description = "Cell A6 with this value indicates a bulk import format"},
                new Option {Key = nameof(context.FieldResultPrefix), Setter = value => context.FieldResultPrefix = value, Getter = () => context.FieldResultPrefix, Description = $"Row 5 methods beginning with this text indicate a {DataClassificationType.FIELD_RESULT}"},
                new Option {Key = nameof(context.StopOnFirstError), Setter = value => context.StopOnFirstError = ParseBoolean(value), Getter = () => $"{context.StopOnFirstError}", Description = "Stop on first error?"},
                new Option {Key = nameof(context.ErrorLimit), Setter = value =>  context.ErrorLimit = ParseInteger(value), Getter = () => $"{context.ErrorLimit}", Description = "Maximum number of errors shown."},
                new Option {Key = nameof(context.UtcOffset), Setter = value => context.UtcOffset = ParseOffset(value), Getter = () => string.Empty, Description = $"UTC offset for imported times [default: Use system timezone, currently {context.UtcOffset:m}]"},
                new Option {Key = nameof(context.StartTime), Setter = value => context.StartTime = ParseDateTimeOffset(value), Getter = () => string.Empty, Description = "When set, only include observations after this time."},
                new Option {Key = nameof(context.EndTime), Setter = value => context.EndTime = ParseDateTimeOffset(value), Getter = () => string.Empty, Description = "When set, only include observations before this time."},

                new Option(), new Option{Description = "Import options:"},
                new Option {Key = nameof(context.DryRun), Setter = value => context.DryRun = ParseBoolean(value), Getter = () => $"{context.DryRun}", Description = "Enable a dry-run of the import? /N is a shorthand."},
                new Option {Key = nameof(context.MaximumObservations), Setter = value =>  context.MaximumObservations = int.TryParse(value, out var number) ? (int?)number : null, Getter = () => $"{context.MaximumObservations}", Description = "When set, limit the number of imported observations."},
                new Option {Key = nameof(context.ResultGrade), Setter = value => context.ResultGrade = value, Getter = () => context.ResultGrade, Description = $"Result grade when value is not estimated."},
                new Option {Key = nameof(context.EstimatedGrade), Setter = value => context.EstimatedGrade = value, Getter = () => context.EstimatedGrade, Description = $"Result grade when estimated."},
                new Option {Key = nameof(context.FieldResultStatus), Setter = value => context.FieldResultStatus = value, Getter = () => context.FieldResultStatus, Description = $"Field result status."},
                new Option {Key = nameof(context.LabResultStatus), Setter = value => context.FieldResultStatus = value, Getter = () => context.FieldResultStatus, Description = $"Lab result status."},
                new Option {Key = nameof(context.DefaultLaboratory), Setter = value => context.DefaultLaboratory = value, Getter = () => context.DefaultLaboratory, Description = $"Default laboratory ID for lab results"},
                new Option {Key = nameof(context.DefaultMedium), Setter = value => context.DefaultMedium = value, Getter = () => context.DefaultMedium, Description = $"Default medium for results"},
                new Option {Key = nameof(context.NonDetectCondition), Setter = value => context.NonDetectCondition = value, Getter = () => context.NonDetectCondition, Description = $"Lab detect condition for non-detect events."},
                new Option {Key = nameof(context.LabSpecimenName), Setter = value => context.LabSpecimenName = value, Getter = () => context.LabSpecimenName, Description = $"Lab specimen name"},
                new Option {Key = nameof(context.VerboseErrors), Setter = value => context.VerboseErrors = ParseBoolean(value), Getter = () => $"{context.VerboseErrors}", Description = "Show row-by-row errors?"},

                new Option(), new Option{Description = "Alias options: (these help you map from your external system to AQUARIUS Samples)"},
                new Option {Key = nameof(context.LocationAliases).Singularize(), Setter = value => ParseLocationAlias(context, value), Description = "Set a location alias in aliasedLocation;SamplesLocationId format"},
                new Option {Key = nameof(context.ObservedPropertyAliases).Singularize(), Setter = value => ParseObservedPropertyAlias(context, value), Description = "Set an observed property alias in aliasedProperty;aliasedUnit;SamplesObservedPropertyId format"},
                new Option {Key = nameof(context.MethodAliases).Singularize(), Setter = value => ParseMethodAlias(context, value), Description = "Set a method alias in aliasedMethod;SamplesMethodId format"},
                new Option {Key = nameof(context.QCTypeAliases).Singularize(), Setter = value => ParseQcTypeAlias(context, value), Description = "Set a QC Type alias in aliasedQCType;SamplesQCType[;ActivitySuffix] format"},

                new Option(), new Option{Description = "CSV output options:"},
                new Option {Key = nameof(context.CsvOutputPath), Setter = value => context.CsvOutputPath = value, Getter = () => context.CsvOutputPath, Description = $"Path to output file. If not specified, no CSV will be output."},
                new Option {Key = nameof(context.Overwrite), Setter = value => context.Overwrite = ParseBoolean(value), Getter = () => $"{context.Overwrite}", Description = "Overwrite existing files?"},

                new Option(), new Option{Description = "GUI options:"},
                new Option {Key = nameof(context.LaunchGui), Setter = value => context.LaunchGui = ParseBoolean(value), Getter = () => $"{context.LaunchGui}", Description = "Launch in GUI mode? (Automatic when no Excel files are specified.)"},
            };

            var usageMessage
                    = $"Import lab file results as AQUARIUS Samples observations."
                      + $"\n"
                      + $"\nusage: {ExeHelper.ExeName} [-option=value] [@optionsFile] labFile ..."
                      + $"\n"
                      + $"\nSupported -option=value settings (/option=value works too):\n\n  {string.Join("\n  ", options.Select(o => o.UsageText()))}"
                      + $"\n"
                      + $"\nUse the @optionsFile syntax to read more options from a file."
                      + $"\n"
                      + $"\n  Each line in the file is treated as a command line option."
                      + $"\n  Blank lines and leading/trailing whitespace is ignored."
                      + $"\n  Comment lines begin with a # or // marker."
                ;

            var helpGuidance = "See /help screen for details.";

            foreach (var arg in resolvedArgs)
            {
                var match = ArgRegex.Match(arg);

                if (!match.Success)
                {
                    if (HelpKeyWords.Contains(arg))
                        throw new ExpectedException(usageMessage);

                    if (DryRunKeyWords.Contains(arg))
                    {
                        context.DryRun = true;
                        continue;
                    }

                    if (File.Exists(arg))
                    {
                        context.Files.Add(arg);
                        continue;
                    }

                    throw new ExpectedException($"Unknown argument: {arg}\n\n{helpGuidance}");
                }

                var key = match.Groups["key"].Value.ToLower();
                var value = match.Groups["value"].Value;

                var option =
                    options.FirstOrDefault(o => o.Key != null && o.Key.Equals(key, StringComparison.InvariantCultureIgnoreCase));

                if (option == null)
                {
                    throw new ExpectedException($"Unknown -option=value: {arg}\n\n{helpGuidance}");
                }

                option.Setter(value);
            }

            return context;
        }

        private static readonly Regex ArgRegex = new Regex(@"^([/-])(?<key>[^=]+)=(?<value>.*)$", RegexOptions.Compiled);

        private static readonly HashSet<string> HelpKeyWords =
            new HashSet<string>(
                new[] { "?", "h", "help" }
                    .SelectMany(keyword => new[] { "/", "-", "--" }.Select(prefix => prefix + keyword)),
                StringComparer.InvariantCultureIgnoreCase);

        private static readonly HashSet<string> DryRunKeyWords =
            new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
            {
                "/n",
                "-n",
            };

        private static IEnumerable<string> ResolveOptionsFromFile(string arg)
        {
            if (!arg.StartsWith("@"))
                return new[] { arg };

            var path = arg.Substring(1);

            if (!File.Exists(path))
                throw new ExpectedException($"Options file '{path}' does not exist.");

            return File.ReadAllLines(path)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Where(s => !s.StartsWith("#") && !s.StartsWith("//"));
        }

        private static bool ParseBoolean(string text)
        {
            if (bool.TryParse(text, out var value))
                return value;

            throw new ExpectedException($"'{text}' is not a valid boolean value. Try {true} or {false}");
        }

        private static int ParseInteger(string text)
        {
            if (int.TryParse(text, out var value))
                return value;

            throw new ExpectedException($"'{text}' is not a valid integer value.");
        }

        private static DateTimeOffset ParseDateTimeOffset(string text)
        {
            if (DateTimeOffset.TryParse(text, out var value))
                return value;

            throw new ExpectedException($"'{text}' is not a valid date-time. Try yyyy-MM-dd or yyyy-MM-dd HH:mm:ss");
        }

        public static Offset ParseOffset(string text)
        {
            try
            {
                var offset = text.FromJson<Offset>();

                return offset;
            }
            // ReSharper disable once EmptyGeneralCatchClause
            catch (Exception)
            {
            }

            var result = OffsetPattern.GeneralInvariantPattern.Parse(text);

            if (result.Success)
                return result.Value;

            throw new ExpectedException($"'{text}' is not a valid UTC offset {result.Exception.Message}");
        }

        private static void ParseLocationAlias(Context context, string text)
        {
            var parts = Split(text);

            if (parts.Length != 2)
                throw new ExpectedException($"'{text}' is not a valid location alias. Try /{nameof(context.LocationAliases).Singularize()}=aliasedLocation;SamplesLocationId");

            var aliasedLocation = parts[0];
            var samplesLocationId = parts[1];

            if (context.LocationAliases.TryGetValue(aliasedLocation, out var existingAlias))
                throw new ExpectedException($"Can't set location alias for '{aliasedLocation}' more than once. This location is already aliased to '{existingAlias}'");

            context.LocationAliases[aliasedLocation] = samplesLocationId;
        }

        private static void ParseMethodAlias(Context context, string text)
        {
            var parts = Split(text);

            if (parts.Length != 2)
                throw new ExpectedException($"'{text}' is not a valid method alias. Try /{nameof(context.LocationAliases).Singularize()}=aliasedMethod;SamplesMethodId");

            var aliasedMethod = parts[0];
            var samplesMethod = parts[1];

            if (context.MethodAliases.TryGetValue(aliasedMethod, out var existingAlias))
                throw new ExpectedException($"Can't set method alias for '{aliasedMethod}' more than once. This method is already aliased to '{existingAlias}'");

            context.MethodAliases[aliasedMethod] = samplesMethod;
        }

        private static void ParseQcTypeAlias(Context context, string text)
        {
            var parts = Split(text);

            var aliasedQcType = parts.Length > 0 ? parts[0] : null;
            var samplesQcTypeText = parts.Length > 1 ? parts[1] : null;
            var activityNameSuffix = parts.Length > 2 ? parts[2] : null;

            var invalid = string.IsNullOrEmpty(aliasedQcType);

            var samplesQcType = (QualityControlType?) null;

            if (!string.IsNullOrEmpty(samplesQcTypeText))
            {
                if (!Enum.TryParse<QualityControlType>(samplesQcTypeText, true, out var qualityControlType))
                {
                    invalid = true;
                }
                else
                {
                    samplesQcType = qualityControlType;
                }
            }

            if (!samplesQcType.HasValue && !string.IsNullOrEmpty(activityNameSuffix))
                invalid = true;

            if (invalid)
                throw new ExpectedException($"'{text}' is not a valid QC Type alias. Try /{nameof(context.QCTypeAliases).Singularize()}=aliasedQCType;SamplesQCType[;ActivityNameSuffix]. Allowed Samples QC types are {string.Join(", ", Enum.GetNames(typeof(QualityControlType)))}");

            if (context.QCTypeAliases.TryGetValue(aliasedQcType, out var existingAlias))
                throw new ExpectedException($"Can't set QC Type alias for '{aliasedQcType}' more than once. This QC Type is already aliased to '{existingAlias.QualityControlType}' with an ActivityNameSuffix='{existingAlias.ActivityNameSuffix}'");

            context.QCTypeAliases[aliasedQcType] = new Context.QcTypeAlias
            {
                Alias = aliasedQcType,
                QualityControlType = samplesQcType,
                ActivityNameSuffix = activityNameSuffix
            };
        }

        private static void ParseObservedPropertyAlias(Context context, string text)
        {
            var parts = Split(text);

            if (parts.Length != 4)
                throw new ExpectedException($"'{text}' is not a valid observed property alias. Try /{nameof(context.ObservedPropertyAliases).Singularize()}=aliasedProperty;aliasedUnit;SamplesPropertyId;SamplesUnitId");

            var aliasedProperty = parts[0];
            var aliasedUnit = parts[1];

            var alias = new Context.AliasedProperty
            {
                PropertyId = aliasedProperty,
                UnitId = aliasedUnit
            };

            var samplesObservedPropertyId = parts[2];
            var samplesUnitId = parts[3];

            if (context.ObservedPropertyAliases.TryGetValue(alias.Key, out var existingAlias))
                throw new ExpectedException($"Can't set observed property alias for '{alias.Key}' more than once. This property;unit is already aliased to '{existingAlias.PropertyId};{existingAlias.UnitId}'");

            context.ObservedPropertyAliases[alias.Key] = new Context.AliasedProperty
            {
                PropertyId = samplesObservedPropertyId,
                UnitId = samplesUnitId,
                AliasedPropertyId = alias.PropertyId,
                AliasedUnitId = alias.UnitId,
            };
        }

        private static string[] Split(string text)
        {
            return text
                .Split(';')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
        }

        private readonly Context _context;

        public Program(Context context)
        {
            _context = context;
        }

        private void Run()
        {
            var launchGui = _context.LaunchGui ?? !_context.Files.Any();

            if (launchGui)
            {
                RunGuiImporter();
            }
            else
            {
                RunConsoleImporter();
            }
        }

        private void RunGuiImporter()
        {
            // ConfigureLogging();

            AppDomain.CurrentDomain.UnhandledException +=
                (sender, args) => HandleUnhandledGuiException(args.ExceptionObject as Exception);
            Application.ThreadException +=
                (sender, args) => HandleUnhandledGuiException(args.Exception);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            _log.Info($"Launching GUI mode for {ExeHelper.ExeNameAndVersion} ...");

            Application.Run(new MainForm
            {
                Context = _context
            });
        }

        private static void HandleUnhandledGuiException(Exception exception)
        {
            var (message, exceptionToLog) = HandleOuterException(exception);

            if (exceptionToLog != null)
                _log.Error(message, exceptionToLog);
            else
                _log.Error(message);

            var caption = "Oops. Better check the logs for details.";

            if (exception is WebServiceException)
            {
                caption = "AQUARIUS Samples API error";
            }
            else if (exception is ExpectedException)
            {
                caption = "An error occurred.";
            }

            MessageBox.Show(message, caption);
        }

        private void RunConsoleImporter()
        {
            new Importer(ServiceStack.Logging.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType), _context)
                .Import();
        }
    }
}
