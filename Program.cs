﻿/*
Copyright 2017 Regents of the University of California. Licensed under the
Educational Community License, Version 2.0 (the "License"); you may
not use this file except in compliance with the License. You may
obtain a copy of the License at

http://www.osedu.org/licenses/ECL-2.0

Unless required by applicable law or agreed to in writing,
software distributed under the License is distributed on an "AS IS"
BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express
or implied. See the License for the specific language governing
permissions and limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.IO;

namespace BuildSmarterContentPackage
{
    class Program
    {
        const string c_Help =
@"Builds a Smarter Balanced format test content package from a list of ids and
the item bank.

Syntax:
    BuildSmarterContentPackage <arguments>

Arguments:
    -h                    Display this help text.
    -ids <filename>       (Required) The name of a file containing a list of
                          item IDs to include in the test content package. See
                          details below about the file format.
    -at <token>           (Required) A GitLab access token valid on the item
                          bank. See below on how to generate the token.
    -o <filename>         (Required) The output filename. This will be a .zip
                          file and should have a .zip extension.
    -log <filename>       (Optional) the name of the log file to which
                          progress details and errors are written. If not
                          specified then this defaults to the output (-o)
                          filename with a suffix of "".log.csv"";
    -bank <url>           (Optional) The URL of the GitLab item bank from which
                          the items will be drawn. If not specified, defaults
                          to ""https://itembank.smarterbalanced.org"".
    -ns <namespace>       (Optional) The GitLab namespace to which the items
                          belong. This should be a username or a group name.
                          If not specified, defaults to ""itemreviewapp"".
    -bk <integer>         (Optional) The default bankKey to use for items if
                          not specified in the item id list. Default is 200.
                          Well-known values are 200 for production items and
                          187 for practice items.
    -notut                Do not automatically include tutorials. Without this
                          flag, the packager will automatically include in the
                          package that are referenced by the items. If the ID
                          of a tutorial is included in the IDs file it will
                          still be included in the package.
    -w                    (Optional) Wait for the user to press a key before
                          exiting. This is convenient when executing from a
                          debugger or icon to allow the user to read the
                          output.

Item ID File:
    The Item ID file specified by the '-ids' argument is a list of IDs for
    items that should be included in the file. It may be a flat list or in CSV
    format.

    Flat List Format:
    In this format there is one item ID per line. IDs may be the bare number
    (e.g. ""12345"") or they may be the full item name including
    the ""Item-"" prefix(e.g. ""Item-200-12345""). If the ID is a bare number
    than the bank key specified by the ""-bk"" parameter or default of ""200""
    will be used.

    CSV Format:
    A CSV file should comply with RFC 4180. The first line should be a list of
    field names. One column MUST be named ""ItemId"" and it will be the source
    of the item ids. Another column MAY be named ""BankKey"". If so, it will be
    the source of bank keys. If not included, the default bank key (either 200
    or the value specified in the ""-bk"") will be used. As with flat list
    format, the item ID may be a bare integer or a full name including prefix.

Access Token
    To generate an access token, do the following:
    1. Log into the GitLab item bank.
    2. Access your user profile (by clicking on your account icon in the upper-
       right).
    3. Edit your profile (by clicking on the pencil icon in the upper-right.
    4. Select ""access tokens"" from the menu.
    5. Give the token a name and expiration date. We recommend expiration no
       no longer than 3 months. Select ""API"" for the scope. Then click
       ""Create personal access token.""
";
        // Column 80                                                                   |

        const string c_DefaultItemBank = "https://itembank.smarterbalanced.org";
        const string c_DefaultNamespace = "itemreviewapp";
        const int c_DefaultBankKey = 200;

        public static CsvLogger ProgressLog { get; private set; }

        // Command-line arguments
        static bool s_showHelp = false;
        static string s_idFilename = null;
        static string s_accessToken = null;
        static string s_packageFilename = null;
        static string s_logFilename = null;
        static string s_itemBankUrl = c_DefaultItemBank;
        static string s_namespace = c_DefaultNamespace;
        static int s_bankKey = c_DefaultBankKey;
        static bool s_includeTutorials = true;
        static bool s_waitBeforeExit = false;

        static void Main(string[] args)
        {
            try
            {
                ParseCommandLine(args);

                if (s_showHelp)
                {
                    Console.WriteLine(c_Help);
                }
                else
                {
                    // Here's where the real work happens.
                    ProgressLog = new CsvLogger(s_logFilename);
#if DEBUG
                    ProgressLog.Trace = Console.Error;
#endif

                    var builder = new PackageBuilder();
                    builder.ItemBankUrl = s_itemBankUrl;
                    builder.ItemBankAccessToken = s_accessToken;
                    builder.ItemBankNamespace = s_namespace;
                    builder.IncludeTutorials = s_includeTutorials;
                    
                    // Load the queue with the inbound item IDs
                    using (var reader = new IdReader(s_idFilename, s_bankKey))
                    {
                        builder.AddIds(reader);
                    }

                    // Build the package
                    builder.ProducePackage(s_packageFilename);
                }
            }
            catch (Exception err)
            {
#if DEBUG
                Console.WriteLine(err.ToString());
#else
                Console.WriteLine(err.Message);
#endif
                Console.WriteLine("Use '-h' argument for help text.");
            }
            finally
            {
                if (ProgressLog != null)
                {
                    ProgressLog.Dispose();
                }
                ProgressLog = null;
            }

            if (s_waitBeforeExit)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Press any key to exit.");
                Console.ReadKey();
            }
        }

        static void ParseCommandLine(string[] args)
        {
#if DEBUG
            s_waitBeforeExit = true;
#endif

            for (int i = 0; i < args.Length; ++i)
            {
                switch (args[i])
                {
                    case "-h":
                    case "-?":
                        s_showHelp = true;
                        break;

                    case "-ids":
                        ++i;
                        if (i >= args.Length) throw new ArgumentException("Command line error: Value not supplied for '-ids' argument.");
                        s_idFilename = args[i];
                        break;

                    case "-at":
                        ++i;
                        if (i >= args.Length) throw new ArgumentException("Command line error: Value not supplied for '-at' argument.");
                        s_accessToken = args[i];
                        break;

                    case "-o":
                        ++i;
                        if (i >= args.Length) throw new ArgumentException("Command line error: Value not supplied for '-o' argument.");
                        s_packageFilename = args[i];
                        break;

                    case "-log":
                        ++i;
                        if (i >= args.Length) throw new ArgumentException("Command line error: Value not supplied for '-log' argument.");
                        s_logFilename = args[i];
                        break;

                    case "-bank":
                        ++i;
                        if (i >= args.Length) throw new ArgumentException("Command line error: Value not supplied for '-bank' argument.");
                        s_itemBankUrl = args[i];
                        break;

                    case "-ns":
                        ++i;
                        if (i >= args.Length) throw new ArgumentException("Command line error: Value not supplied for '-ns' argument.");
                        s_namespace = args[i];
                        break;

                    case "-bk":
                        ++i;
                        if (i >= args.Length) throw new ArgumentException("Command line error: Value not supplied for '-bk' argument.");
                        if (!int.TryParse(args[i], out s_bankKey))
                        {
                            throw new ArgumentException($"Command line error: Invalid BankKey. Must be integer. '-bk {args[i]}'");
                        }
                        break;

                    case "-notut":
                        s_includeTutorials = false;
                        break;

                    case "-w":
                        s_waitBeforeExit = true;
                        break;
                }
            }

            if (string.IsNullOrEmpty(s_idFilename))
            {
                throw new ArgumentException("Command Line Error: Missing '-ids' argument.");
            }
            s_idFilename = Path.GetFullPath(s_idFilename);
            if (!File.Exists(s_idFilename))
            {
                throw new ArgumentException($"Command Line Error: ID file '{s_idFilename}' not found! (-ids argument)");
            }

            if (string.IsNullOrEmpty(s_accessToken))
            {
                throw new ArgumentException("Command Line Error: Missing '-at' argument.");
            }

            if (string.IsNullOrEmpty(s_packageFilename))
            {
                throw new ArgumentException("Command Line Error: Missing '-o' argument.");
            }
            s_packageFilename = Path.GetFullPath(s_packageFilename);
            if (File.Exists(s_packageFilename))
            {
                File.Delete(s_packageFilename);
            }

            if (string.IsNullOrEmpty(s_logFilename))
            {
                s_logFilename = Path.Combine(Path.GetDirectoryName(s_packageFilename), Path.GetFileNameWithoutExtension(s_packageFilename) + ".log.csv");
            }
            if (File.Exists(s_logFilename))
            {
                File.Delete(s_logFilename);
            }

            Console.WriteLine($"ID File (input): {s_idFilename}");
            Console.WriteLine($"Package File (output): {s_packageFilename}");
            Console.WriteLine($"Log File (output): {s_logFilename}");
            Console.WriteLine($"Item Bank URL: {s_itemBankUrl}");
            Console.WriteLine($"Namespace: {s_namespace}");
            Console.WriteLine($"Default Bank Key: {s_bankKey}");
            Console.WriteLine("Auto Include Tutorials: {0}", s_includeTutorials ? "Yes" : "No");
            Console.WriteLine();
        }
    }
}